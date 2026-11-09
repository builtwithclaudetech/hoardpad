// ChecklistView.axaml.cs — the `.hoard` checklist control wiring (FR-30…FR-34, FR-41, FR-42, design §5).
//
// Framework wiring only (the durable mutation logic lives in ChecklistViewModel + the Core pure functions):
//   - checkbox / twisty / delete clicks route to the VM (terminal mutations, zero-debounce persist);
//   - item-text LostFocus + the add-item Enter route to the VM (text coalesces ≤ 150 ms; add is terminal);
//   - the mode selector drives FR-33;
//   - title LostFocus / Enter → vm.SetTitle (coalesced; Fix 4);
//   - keyboard ops on a focused row: Alt+Shift+→ / Alt+Shift+← indent/outdent (subtree travels, §8.3),
//     Alt+Up / Alt+Down reorder within the sibling group, ← / → collapse/expand a focused parent (FR-42);
//   - drag handle PointerPressed → StartDragDrop → DragDrop.DoDragDrop → Drop → vm.DropWithinGroup;
//   - row context menu: Indent / Outdent / Move up / Move down (§5.1/§5.2, C-2a discoverability);
//   - CollapseToggleRequested subscription on each ChecklistItemControl so AT expand/collapse actions
//     route to vm.ToggleCollapsed (C-5 screen-reader expand/collapse wire-up);
//   - delete-non-empty-parent wires the MODAL ShowDialog(owner) prompt (C-3) into the VM.
// All mutation runs on the UI thread; the atomic write runs off the dispatcher inside the write queue.

using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HoardPad.App.ViewModels;

namespace HoardPad.App.Views;

/// <summary>The custom <c>.hoard</c> checklist control (ItemsControl over the visible-row projection).</summary>
internal sealed partial class ChecklistView : UserControl
{
    /// <summary>The DataObject format key for the dragged item id (intra-list row reorder).</summary>
    private const string DragItemIdFormat = "HoardPad.ChecklistItemId";

    /// <summary>
    /// The ItemsControl whose ContainerPrepared/ContainerClearing events are currently subscribed.
    /// Tracked so a DataContext swap unsubscribes the OLD handlers before re-subscribing — avoids
    /// a double-subscribe leak if the same view instance is reused with a different document (M6.3).
    /// </summary>
    private ItemsControl? _subscribedRows;

    /// <summary>
    /// The ChecklistViewModel whose FocusRequested is currently subscribed (QA3-6/QA3-19). Tracked so a
    /// DataContext swap unsubscribes the OLD VM's handler before subscribing to the new one — mirrors the
    /// _subscribedRows pattern above and avoids leaking a `this`-capturing delegate on a stale VM.
    /// </summary>
    private ChecklistViewModel? _focusSubscribedVm;

    /// <summary>
    /// QA4-3 fix 3: the id of the MOST RECENT row to raise <see cref="GotFocusEvent"/>, tracked
    /// independently of the shell's <c>FocusedChecklistItemId</c> so <see cref="OnRowKeyDown"/> has a
    /// fallback target when <see cref="FocusedRow"/> resolves to nothing genuinely inside a checklist row
    /// (e.g. Avalonia's own Alt-mnemonic access-key handling steals the chord before the keystroke lands).
    /// Always reflects the LAST row that was genuinely focused — a real focus change to a different row
    /// updates this immediately, so the fallback can never act on a stale id once the user has moved on.
    /// </summary>
    private string? _lastFocusedRowId;

    /// <summary>
    /// QA4-19: the document whose scroll state <see cref="RowsScroller"/>'s <c>Offset</c> CURRENTLY,
    /// authoritatively reflects — set once that document's deferred restore-Post in
    /// <see cref="OnDataContextChanged"/> has actually run (i.e. a layout pass has settled for it). Null
    /// until the very first restore-Post fires. <see cref="OnPropertyChanged"/>'s outgoing-offset capture
    /// only trusts the live <c>Offset</c> value when the vm being switched away from is THIS vm — otherwise
    /// the switch happened too fast for a layout pass to catch up, and <c>Offset</c> is stale leftover from
    /// whatever vm settled last, not a real reading for the vm actually being deactivated.
    /// </summary>
    private ChecklistViewModel? _settledVm;

    public ChecklistView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnRowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DropEvent, OnRowDrop, RoutingStrategies.Bubble);
        AddHandler(DragDrop.DragOverEvent, OnRowDragOver, RoutingStrategies.Bubble);
        AddHandler(GotFocusEvent, OnRowGotFocus, RoutingStrategies.Bubble);
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private ChecklistViewModel? ViewModel => DataContext as ChecklistViewModel;

    // Named "RowsScroller" (not "RowsScrollViewer") because the Avalonia name-source-generator already
    // declares its OWN field literally named "RowsScrollViewer" for the x:Name'd ScrollViewer below — a
    // same-named member here would collide with it (CS0102) even though that generated field is never
    // populated (this class's own InitializeComponent() overload is the plain AvaloniaXamlLoader.Load(this)
    // one, not the generator's field-populating overload — matches the this.FindControl<T>("Name") pattern
    // used everywhere else in this file, e.g. Rows/TitleBox/ModeSelector/AddItemBox).
    private ScrollViewer? RowsScroller => this.FindControl<ScrollViewer>("RowsScrollViewer");

    // QA4-19: this single ChecklistView instance is reused across every open .hoard tab (its DataContext is
    // repointed on tab switch — see MainWindow.axaml), so scroll position can't live on the view; it is
    // captured onto the OUTGOING ChecklistViewModel here. AvaloniaProperty.OldValue gives us the vm that was
    // attached a moment ago. The capture must run synchronously before THIS OVERRIDE RETURNS — not
    // specifically before/after the base.OnPropertyChanged() call below, whose relative position doesn't
    // matter here (Avalonia.Base 11.3.6: DataContextChanged is raised by a static class-handler invoked
    // from AvaloniaObject.RaisePropertyChanged AFTER the whole OnPropertyChanged virtual chain returns —
    // StyledElement.OnPropertyChanged never special-cases DataContextProperty). What DOES matter: the
    // DataContextChanged cascade (which flows into OnDataContextChanged below and eventually a layout pass
    // that can move ScrollViewer.Offset) only happens once this method has fully returned, so reading
    // RowsScroller.Offset.Y anywhere in this method body is safe — reading it after this method returns
    // would not be.
    //
    // Guarded by _settledVm (see field doc) against a rapid A -> B -> C -> B switch chain with no
    // intervening layout pass: without the guard, a hop away from a vm whose own restore-Post never got a
    // chance to run (so ScrollViewer.Offset still visually reflects an EARLIER vm, not `outgoing`) would
    // stamp that earlier vm's leftover offset onto `outgoing` — corrupting it for whenever it's reactivated,
    // even though `outgoing` itself was never actually scrolled to that position.
    protected override void OnPropertyChanged(Avalonia.AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == DataContextProperty
            && change.OldValue is ChecklistViewModel outgoing
            && ReferenceEquals(outgoing, _settledVm)
            && RowsScroller is { } scrollViewer)
        {
            outgoing.ScrollOffsetY = scrollViewer.Offset.Y;
        }
        base.OnPropertyChanged(change);
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (ViewModel is not { } vm)
            return;

        // Wire the MODAL delete-non-empty-parent prompt (C-3): a real ShowDialog(owner) over this window.
        vm.DeleteParentPrompt = async (row, childCount) =>
        {
            if (this.GetVisualRoot() is not Window owner)
                return DeleteParentChoice.Cancel;
            return await DeleteParentDialog.ShowAsync(owner, row.Text, childCount);
        };

        // Reflect the stored mode into the selector without re-triggering a write.
        var selector = this.FindControl<ComboBox>("ModeSelector")!;
        selector.SelectedIndex = vm.Mode switch
        {
            HoardPad.Core.Checklist.CheckedItemDisplayMode.MoveToBottom => 1,
            HoardPad.Core.Checklist.CheckedItemDisplayMode.HideWhenChecked => 2,
            _ => 0,
        };

        // Subscribe CollapseToggleRequested on row containers so AT expand/collapse actions
        // route to vm.ToggleCollapsed (C-5 screen-reader expand/collapse).
        // Unsubscribe from the previous rows first to prevent a double-subscribe leak when the
        // same ChecklistView instance is reused for a second document (M6.3).
        var rows = this.FindControl<ItemsControl>("Rows")!;
        UnsubscribeVisibleRows();
        SubscribeVisibleRows(rows);

        // QA3-6/QA3-19: re-focus the moved item's row after Indent/Outdent/MoveWithinGroup (whichever path
        // triggered it — row keyboard shortcut, row context menu, or the top _Checklist menu, since all
        // three route through these same VM methods). Unsubscribe the previous VM first (mirrors the rows
        // subscription above) so a document swap on this reused view doesn't leak a stale handler.
        if (_focusSubscribedVm is { } previous)
            previous.FocusRequested -= OnChecklistFocusRequested;
        vm.FocusRequested += OnChecklistFocusRequested;
        _focusSubscribedVm = vm;

        // Carry-forward A: when a .hoard DataContext attaches, make this VM the shell's ActiveChecklist so
        // the top _Checklist menu enables + routes (the workspace also sets it; setting the same VM is an
        // idempotent no-op). Idle a stale focused-row id from a previous document (QA4-3 fix 2/3: both the
        // shell's id and this view's own keyboard-fallback id).
        _lastFocusedRowId = null;
        if (Shell is { } shell)
        {
            shell.ActiveChecklist = vm;
            shell.FocusedChecklistItemId = null;
        }

        // QA4-19: restore this document's saved scroll offset. The row containers are only realized once the
        // ItemsSource binding above has flowed through and a layout pass has run, so this must be deferred —
        // Loaded priority mirrors FocusRowTextBox's post below, the same "wait for containers" pattern.
        //
        // Identity-guarded: a rapid A -> B -> C -> B switch (Ctrl+Tab-repeat / fast clicks, no intervening
        // layout) stacks multiple Posts. By the time the dispatcher drains them the live DataContext may no
        // longer be the vm THIS Post was queued for (e.g. a C-targeted Post draining while B is active again)
        // — applying it anyway would blindly overwrite whatever the CURRENT tab's restore already set,
        // exactly the stale-last-write-wins bug FocusRowTextBox's own post avoids by re-resolving the live
        // ViewModel (via FocusedRowId lookups) rather than trusting a captured snapshot. Re-check identity at
        // execution time and no-op on mismatch — only the Post whose vm is STILL live may touch Offset.
        double restoreOffsetY = vm.ScrollOffsetY;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!ReferenceEquals(ViewModel, vm) || RowsScroller is not { } scrollViewer)
                    return;
                scrollViewer.Offset = new Vector(0, restoreOffsetY);
                _settledVm = vm;
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>The shell view-model that owns ActiveChecklist (the window's DataContext), or null.</summary>
    private MainWindowViewModel? Shell =>
        (this.GetVisualRoot() as Window)?.DataContext as MainWindowViewModel;

    // Carry-forward A: on row focus, publish the focused row id to the shell so the top _Checklist menu
    // commands route to the right item (the command bodies guard on FocusedChecklistItemId != null).
    private void OnRowGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (e.Source is not Visual focused)
            return;

        foreach (Visual ancestor in focused.GetSelfAndVisualAncestors())
        {
            if (ancestor is ChecklistItemControl rowControl && rowControl.Item is { } item)
            {
                // QA4-3 fix 3: tracked regardless of Shell — the view-level keyboard fallback in
                // OnRowKeyDown needs this even when no shell is attached (headless/test hosting).
                _lastFocusedRowId = item.Id;
                if (Shell is { } shell)
                    shell.FocusedChecklistItemId = item.Id;
                return;
            }
        }
    }

    // Carry-forward A: on detach (tab switch / close), clear the shell's ActiveChecklist if it still points
    // at THIS view's VM, so the _Checklist menu disables (no stale active checklist on a now-hidden control).
    private void OnDetachedFromVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        // GetVisualRoot is null after detach; the workspace owns the authoritative clear on tab switch, so
        // here we only unsubscribe the row containers to avoid the leak. The shell's ActiveChecklist is
        // repointed by ActivateTab when another tab becomes active.
        UnsubscribeVisibleRows();

        // REVISE-6 fix: null DeleteParentPrompt on detach so the `this`-capturing lambda released by the
        // VM. Without this, the session-scoped ChecklistViewModel holds a reference to the view (via the
        // closure) for the rest of the app run — a per-tab leak.
        if (ViewModel is { } vm)
            vm.DeleteParentPrompt = null;

        // Same leak shape as above (REVISE-6) for the QA3-6/QA3-19 FocusRequested subscription.
        if (_focusSubscribedVm is { } subscribed)
        {
            subscribed.FocusRequested -= OnChecklistFocusRequested;
            _focusSubscribedVm = null;
        }
    }

    // ----- CollapseToggleRequested subscription for AT expand/collapse (C-5). -----
    //
    // Avalonia 11.x uses ContainerPrepared / ContainerClearing on the ItemsControl itself
    // (not Materialized/Dematerialized on ItemContainerGenerator — that is an older API).
    // We track _subscribedRows so OnDataContextChanged can unsubscribe before re-subscribing,
    // preventing a double-subscribe accumulation across DataContext changes.

    private void UnsubscribeVisibleRows()
    {
        if (_subscribedRows is null)
            return;
        _subscribedRows.ContainerPrepared -= OnRowContainerPrepared;
        _subscribedRows.ContainerClearing -= OnRowContainerClearing;
        _subscribedRows = null;
    }

    private void SubscribeVisibleRows(ItemsControl rows)
    {
        // ContainerPrepared fires when a container is realized (row materialized).
        // ContainerClearing fires when a container is about to be recycled/removed.
        rows.ContainerPrepared += OnRowContainerPrepared;
        rows.ContainerClearing += OnRowContainerClearing;
        _subscribedRows = rows;
    }

    private void OnRowContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is ChecklistItemControl control)
            control.CollapseToggleRequested += OnCollapseToggleRequested;
    }

    private void OnRowContainerClearing(object? sender, ContainerClearingEventArgs e)
    {
        if (e.Container is ChecklistItemControl control)
            control.CollapseToggleRequested -= OnCollapseToggleRequested;
    }

    private void OnCollapseToggleRequested(ChecklistItemViewModel item)
    {
        // Routed from ChecklistItemAutomationPeer.Expand/Collapse — the AT expand/collapse action.
        ViewModel?.ToggleCollapsed(item.Id);
    }

    // ----- Title wiring (Fix 4 — coalesced title persist). -----

    private void OnTitleLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox box && ViewModel is { } vm)
            vm.SetTitle(box.Text ?? string.Empty);
    }

    private void OnTitleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box && ViewModel is { } vm)
        {
            vm.SetTitle(box.Text ?? string.Empty);
            e.Handled = true;
        }
    }

    // ----- Row actions (terminal mutations). -----

    private void OnCheckboxClick(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row && ViewModel is { } vm)
            vm.ToggleChecked(row.Id);
    }

    private void OnTwistyClick(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row && ViewModel is { } vm)
            vm.ToggleCollapsed(row.Id);
    }

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row && ViewModel is { } vm)
            await vm.DeleteAsync(row.Id);
    }

    private void OnTextCommitted(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox box && box.Tag is ChecklistItemViewModel row && ViewModel is { } vm)
            vm.EditText(row.Id, box.Text ?? string.Empty);
    }

    // QA2-6: Enter while editing a row's text commits the text, then inserts a new sibling item
    // immediately below it (same hierarchy level) and moves keyboard focus to the new row.
    private void OnItemTextKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box || box.Tag is not ChecklistItemViewModel row
            || ViewModel is not { } vm)
            return;

        vm.EditText(row.Id, box.Text ?? string.Empty); // commit the coalesced edit before inserting.
        string? newId = vm.AddSiblingAfter(row.Id, string.Empty);
        e.Handled = true;

        if (newId is null)
            return;

        // The new row's container is not yet realized synchronously after Swap — focus once layout catches up.
        Dispatcher.UIThread.Post(() => FocusRowTextBox(newId), DispatcherPriority.Loaded);
    }

    // QA3-6/QA3-19: RebuildVisibleRows (driven by Indent/Outdent/MoveWithinGroup) clears and re-adds every
    // row VM, which drops keyboard focus entirely. Re-focus the moved item's row the same way
    // OnItemTextKeyDown already does, so a repeated Indent/Outdent/Move keeps acting on the same item
    // instead of silently falling through to whatever row focus happens to land on next.
    private void OnChecklistFocusRequested(string itemId) =>
        Dispatcher.UIThread.Post(() => FocusRowTextBox(itemId), DispatcherPriority.Loaded);

    /// <summary>Find the realized row container for <paramref name="itemId"/> and focus its text TextBox.</summary>
    private void FocusRowTextBox(string itemId)
    {
        if (this.FindControl<ItemsControl>("Rows") is not { } rows || ViewModel is not { } vm)
            return;

        int index = -1;
        for (int i = 0; i < vm.VisibleRows.Count; i++)
        {
            if (vm.VisibleRows[i].Id == itemId) { index = i; break; }
        }
        if (index < 0)
            return;

        // ContainerFromIndex returns the ItemsControl's own generated container — a ContentPresenter that
        // hosts the DataTemplate's root (ChecklistItemControl), not the ChecklistItemControl itself. Search
        // its visual descendants for the row's TextBox rather than casting the container directly.
        if (rows.ContainerFromIndex(index) is not Control container)
            return;

        var textBox = container.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
        textBox?.Focus();
    }

    private void OnModeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || ViewModel is not { } vm)
            return;
        var mode = combo.SelectedIndex switch
        {
            1 => HoardPad.Core.Checklist.CheckedItemDisplayMode.MoveToBottom,
            2 => HoardPad.Core.Checklist.CheckedItemDisplayMode.HideWhenChecked,
            _ => HoardPad.Core.Checklist.CheckedItemDisplayMode.StrikethroughInPlace,
        };
        if (mode != vm.Mode)
            vm.SetMode(mode);
    }

    private void OnAddItemKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box || ViewModel is not { } vm)
            return;
        string text = box.Text ?? string.Empty;
        if (text.Length == 0)
            return;
        vm.AddItem(parentId: null, text); // tier-0 append (the active sibling group at the add row, §5.1).
        box.Text = string.Empty;
        e.Handled = true;
    }

    // ----- Context menu handlers (Fix 3: Indent/Outdent/Move up/Move down, §5.1/§5.2, C-2a). -----

    private void OnContextIndentClick(object? sender, RoutedEventArgs e)
    {
        if (MenuItemRow(sender) is { } row && ViewModel is { } vm)
            vm.Indent(row.Id);
    }

    private void OnContextOutdentClick(object? sender, RoutedEventArgs e)
    {
        if (MenuItemRow(sender) is { } row && ViewModel is { } vm)
            vm.Outdent(row.Id);
    }

    private void OnContextMoveUpClick(object? sender, RoutedEventArgs e)
    {
        if (MenuItemRow(sender) is { } row && ViewModel is { } vm)
            vm.MoveWithinGroup(row.Id, -1);
    }

    private void OnContextMoveDownClick(object? sender, RoutedEventArgs e)
    {
        if (MenuItemRow(sender) is { } row && ViewModel is { } vm)
            vm.MoveWithinGroup(row.Id, +1);
    }

    private static ChecklistItemViewModel? MenuItemRow(object? sender) =>
        (sender as MenuItem)?.Tag as ChecklistItemViewModel;

    // ----- Drag-reorder within a sibling group (Fix 2: FR-32/§5.2). -----

    private async void OnDragHandlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border handle || handle.Tag is not ChecklistItemViewModel row)
            return;
        if (ViewModel is null)
            return;

        var data = new DataObject();
        data.Set(DragItemIdFormat, row.Id);

        // DoDragDrop runs the OS drag loop — it returns when the user drops or cancels.
        await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
    }

    private void OnRowDragOver(object? sender, DragEventArgs e)
    {
        // Accept a move if it carries our format.
        if (e.Data.Contains(DragItemIdFormat))
            e.DragEffects = DragDropEffects.Move;
        else
            e.DragEffects = DragDropEffects.None;
    }

    private void OnRowDrop(object? sender, DragEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;
        if (e.Data.Get(DragItemIdFormat) is not string draggedId)
            return;

        // Determine which display gap the pointer stopped on. Each row in VisibleRows occupies one
        // display slot; the gap index (0 = before first row, N = after last row) maps to stored order
        // via HoardDisplay.MapDropToStoredOrder (the Core pure function).
        var rows = this.FindControl<ItemsControl>("Rows");
        if (rows is null)
            return;

        int gap = ComputeDropGap(rows, e.GetPosition(rows));
        vm.DropWithinGroup(draggedId, gap);
        e.Handled = true;
    }

    /// <summary>
    /// Compute the display-gap index (0 = before row 0, N = after last row) for a drop at
    /// <paramref name="positionInList"/>. The gap is the slot between rows closest to the pointer Y.
    /// </summary>
    private static int ComputeDropGap(ItemsControl rows, Point positionInList)
    {
        // Measure the realized item containers. Avalonia ItemsControl stacks vertically by default.
        int count = rows.ItemCount;
        if (count == 0)
            return 0;

        double y = positionInList.Y;
        double cumulativeHeight = 0;

        for (int i = 0; i < count; i++)
        {
            // Avalonia 11.x: use ItemsControl.ContainerFromIndex (ItemContainerGenerator overload is obsolete).
            var container = rows.ContainerFromIndex(i) as Control;
            double rowHeight = container?.Bounds.Height ?? 28; // fallback to nominal row height.
            double rowMid = cumulativeHeight + rowHeight / 2;
            if (y < rowMid)
                return i; // drop before this row.
            cumulativeHeight += rowHeight;
        }

        return count; // drop after all rows.
    }

    // ----- Keyboard ops on a focused row (design §8.3). -----

    private void OnRowKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;

        bool altShift = e.KeyModifiers == (KeyModifiers.Alt | KeyModifiers.Shift);
        bool altOnly = e.KeyModifiers == KeyModifiers.Alt;

        ChecklistItemViewModel? row = FocusedRow();

        // QA4-3 fix 3: Alt+Shift+arrows / Alt+Up/Down can arrive while FocusManager.GetFocusedElement()
        // resolves to nothing genuinely inside a checklist row (Avalonia's own Alt-mnemonic access-key
        // handling steals the chord before it lands). Fall back to the most-recently-focused row — but
        // ONLY for the indent/outdent/move dispatch below, and ONLY when FocusedRow() found no row
        // genuinely focused. A real focus change to a different row is always picked up by FocusedRow()
        // itself and takes priority — the fallback can never act on a stale id once the user has moved on.
        if (row is null && (altShift || altOnly) && _lastFocusedRowId is { } fallbackId)
            row = vm.VisibleRows.FirstOrDefault(r => r.Id == fallbackId);

        if (row is null)
            return;

        // QA5-B6: a bare ← / → while the row's item-text TextBox has focus must move the caret, not
        // collapse/expand — even when the caret is already at the very start/end of the text. Leave the
        // event unhandled so it flows through to the TextBox's own caret handling. Collapse/expand by
        // keyboard stays reachable once focus has left that TextBox (twisty, context menu, top menu).
        if (e.KeyModifiers == KeyModifiers.None
            && (e.Key == Key.Left || e.Key == Key.Right)
            && IsFocusInItemTextBox())
            return;

        switch (e.Key)
        {
            // Alt+Shift+→ / Alt+Shift+← — indent/outdent, subtree travels (FR-32/FR-41).
            case Key.Right when altShift:
                vm.Indent(row.Id);
                e.Handled = true;
                break;
            case Key.Left when altShift:
                vm.Outdent(row.Id);
                e.Handled = true;
                break;

            // Alt+Up / Alt+Down — reorder within the sibling group (FR-32, B4).
            case Key.Up when altOnly:
                vm.MoveWithinGroup(row.Id, -1);
                e.Handled = true;
                break;
            case Key.Down when altOnly:
                vm.MoveWithinGroup(row.Id, +1);
                e.Handled = true;
                break;

            // ← / → — collapse/expand a focused parent (FR-42). Only when no modifier (Alt-combos above).
            case Key.Left when e.KeyModifiers == KeyModifiers.None && row is { IsParent: true, Collapsed: false }:
                vm.ToggleCollapsed(row.Id);
                e.Handled = true;
                break;
            case Key.Right when e.KeyModifiers == KeyModifiers.None && row is { IsParent: true, Collapsed: true }:
                vm.ToggleCollapsed(row.Id);
                e.Handled = true;
                break;
        }
    }

    private static ChecklistItemViewModel? RowOf(object? sender) =>
        (sender as Control)?.Tag as ChecklistItemViewModel;

    /// <summary>The view-model of the currently keyboard-focused row, or null.</summary>
    private ChecklistItemViewModel? FocusedRow()
    {
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not Visual focused)
            return null;

        // Walk up from the focused element to the row's ChecklistItemControl, then read its item.
        foreach (Visual ancestor in focused.GetSelfAndVisualAncestors())
        {
            if (ancestor is ChecklistItemControl rowControl && rowControl.Item is { } item)
                return item;
        }
        return null;
    }

    /// <summary>
    /// True when the keyboard-focused element is (or is inside) a row's item-text TextBox — as opposed to
    /// some other focusable part of the row (the checkbox, the twisty Button, the delete "✕" Button). The
    /// item-text TextBox is the only TextBox nested inside a ChecklistItemControl, so finding one before
    /// reaching the row's ChecklistItemControl ancestor is unambiguous (QA5-B6).
    /// </summary>
    private bool IsFocusInItemTextBox()
    {
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not Visual focused)
            return false;

        foreach (Visual ancestor in focused.GetSelfAndVisualAncestors())
        {
            if (ancestor is TextBox)
                return true;
            if (ancestor is ChecklistItemControl)
                return false;
        }
        return false;
    }
}
