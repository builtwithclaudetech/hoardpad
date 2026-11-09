// AiPanelView.axaml.cs — the M6.6 AI-panel overlay code-behind (design §6.1, FR-22).
//
// Wiring only (house MVVM rule). Exposes the confirm-overlay host Panel so the composition root can
// construct the production AiConfirmService over it (the FR-10 confirms show in-panel, never a separate
// window). No logic beyond InitializeComponent + the overlay-host accessor.

using System;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using HoardPad.App.Ai;
using HoardPad.App.ViewModels;

namespace HoardPad.App.Views;

/// <summary>The FR-22 in-window AI panel overlay (design §6.1) — hosted in the editor's right Grid column.</summary>
public sealed partial class AiPanelView : UserControl
{
    // QA3-18: how close (in DIPs) the vertical offset must sit to the maximum scrollable offset to still
    // count as "at the bottom" — small enough that trivial layout rounding doesn't false-negative, large
    // enough to absorb the sub-pixel offset drift the render pipeline produces.
    private const double StickToBottomToleranceDip = 4;

    // QA4-16: the prompt input never shrinks below this height when the divider is dragged down.
    private const double MinPromptInputHeightDip = 34;

    // C2: the flexible Transcript row is never squeezed below this — the ceiling on how far the prompt
    // input may grow (see OnPromptSplitterDragDelta) is derived FROM this floor, so the Transcript stays
    // usable and the StatusBar/every other chrome row can never be pushed off the panel by an aggressive drag.
    private const double MinTranscriptHeightDip = 60;

    private AiPanelViewModel? _subscribedVm;
    private bool _stickToBottom = true;

    public AiPanelView()
    {
        InitializeComponent();

        // FEAT-4: Ctrl+Enter sends; plain Enter / Shift+Enter stay soft-enter (AcceptsReturn's default
        // newline insertion). Attached as a Tunnel handler directly on PromptBox so it runs BEFORE the
        // TextBox's own OnKeyDown (which inserts the newline) — the same pattern ChecklistView.axaml.cs
        // uses for its row-level Tunnel handler. Scoping to PromptBox (not the whole panel) keeps this to
        // "in the AI prompt box" per the deliverable, not a panel-wide gesture.
        PromptBox.AddHandler(KeyDownEvent, OnPromptKeyDown, RoutingStrategies.Tunnel);

        // QA4-14: give PromptBox its OWN local Cut/Copy/Paste KeyBindings so it — not the document editor —
        // handles Ctrl+X/C/V while it has focus. Root cause (confirmed against Avalonia 11.3.6's
        // KeyboardDevice.ProcessRawEvent): a KeyDown's KeyBindings are walked from the FOCUSED element UP the
        // visual tree to the Window BEFORE the routed KeyDownEvent is even raised, and the first ancestor
        // whose Gesture matches AND whose Command.CanExecute is true wins outright — it marks the event
        // Handled and no descendant ever gets its own native key handling. MainWindow's Ctrl+X/C/V
        // KeyBindings (FEAT-3) forward UNCONDITIONALLY to the active editor's Cut/Copy/PasteCommand, so with
        // no closer-scoped binding they always won this walk, even with focus in this plain TextBox — the
        // exact mechanism FEAT-5's TextArea-level Ctrl+R rebind already exploits for Redo (see
        // RedoRebindTests.cs). Placing the bindings on PromptBox itself means they are checked FIRST (it IS
        // the focused element) and always win while it has focus, so MainWindow's forwarding bindings are
        // never reached; when focus moves elsewhere (e.g. the editor), PromptBox's bindings simply are not
        // on the walked path and this is a no-op — the editor's own paste is completely unaffected.
        PromptBox.KeyBindings.Add(new KeyBinding { Gesture = KeyGesture.Parse("Ctrl+X"), Command = new RelayCommand(PromptBox.Cut) });
        PromptBox.KeyBindings.Add(new KeyBinding { Gesture = KeyGesture.Parse("Ctrl+C"), Command = new RelayCommand(PromptBox.Copy) });
        PromptBox.KeyBindings.Add(new KeyBinding { Gesture = KeyGesture.Parse("Ctrl+V"), Command = new RelayCommand(PromptBox.Paste) });

        // QA4-16: PromptResizeSplitter's built-in auto-resize (Avalonia's GridSplitter.SetupDefinitionsToResize)
        // only ever adjusts whichever of its two row neighbors is NOT the flexible "*" row — here NEITHER the
        // Activity-log/Pending tabs row (above) nor the prompt row (below) is the panel's Transcript "*" row,
        // so the built-in behavior always resizes the tabs row and never the prompt row itself (confirmed
        // against Avalonia 11.3.6's decompiled GridSplitter — IsStar(Definition1) alone picks Resize1 vs
        // Resize2, and Definition1 here is the tabs row). This handler grows/shrinks the prompt row directly;
        // the built-in behavior's harmless side effect (a small tabs-row nudge) plus this handler's prompt-row
        // change both net out against the Transcript row above, which absorbs the difference.
        PromptResizeSplitter.DragDelta += OnPromptSplitterDragDelta;
    }

    /// <summary>
    /// QA4-16: grow/shrink the prompt input's row as the divider is dragged (see the ctor comment). C2: the
    /// growth ceiling is derived from the Transcript row's OWN live rendered height (<see cref="MinTranscriptHeightDip"/>)
    /// rather than a fixed pixel cap — since Transcript is the panel's sole flexible "*" row and absorbs
    /// whatever the prompt row takes, this guarantees Transcript (and everything below it — caps, tabs,
    /// StatusBar) can never be squeezed to nothing by an aggressive upward drag, however large.
    /// </summary>
    private void OnPromptSplitterDragDelta(object? sender, VectorEventArgs e)
    {
        if (PromptResizeSplitter.GetVisualParent() is not Grid grid)
            return;

        int promptRowIndex = Grid.GetRow(PromptResizeSplitter) + 1;
        RowDefinition promptRow = grid.RowDefinitions[promptRowIndex];
        double current = promptRow.Height.IsAbsolute ? promptRow.Height.Value : PromptBox.Bounds.Height;

        // Dragging the divider UP (negative Y) grows the prompt input below it, taken from Transcript's
        // current headroom above MinTranscriptHeightDip; dragging DOWN shrinks it back toward MinPromptInputHeightDip.
        double growthCeiling = current + Math.Max(0, TranscriptScroll.Bounds.Height - MinTranscriptHeightDip);
        double next = Math.Clamp(current - e.Vector.Y, MinPromptInputHeightDip, Math.Max(MinPromptInputHeightDip, growthCeiling));
        promptRow.Height = new GridLength(next, GridUnitType.Pixel);
    }

    /// <summary>FEAT-4: Ctrl+Enter triggers Send and consumes the key so no newline is inserted.</summary>
    private void OnPromptKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.Control)
            return;

        e.Handled = true;
        if (DataContext is AiPanelViewModel vm && vm.SendCommand.CanExecute(null))
            vm.SendCommand.Execute(null);
    }

    /// <summary>
    /// The Panel the inline safety confirms (<see cref="AiConfirmView"/>) are shown over — an in-panel
    /// overlay, never a separate OS window (FR-22). The production
    /// <see cref="HoardPad.App.Ai.AiConfirmService"/> is built over this host.
    /// </summary>
    public Panel ConfirmOverlay => ConfirmOverlayHost;

    /// <summary>
    /// On attach, swap the panel view-model's fail-closed default confirm service for the production
    /// inline-confirm service hosted over this view's overlay Panel (the FR-10 confirms show in-panel,
    /// never a separate OS window). The overlay host only exists once the view is built, so this is the
    /// earliest correct point to bind the real service. Also wires the QA3-18 transcript auto-scroll.
    /// </summary>
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is AiPanelViewModel vm)
        {
            vm.SetConfirmService(new AiConfirmService(ConfirmOverlayHost));
            AttachTranscriptAutoScroll(vm);
        }
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        DetachTranscriptAutoScroll();
        base.OnUnloaded(e);
    }

    /// <summary>
    /// QA3-18: scroll the transcript to the newest line whenever one is appended, unless the user has
    /// manually scrolled up to read earlier history. "At the bottom" is tracked off the ScrollViewer's own
    /// <see cref="ScrollViewer.ScrollChanged"/> (which fires on every user/programmatic scroll and reports
    /// the current Offset/Extent/Viewport) rather than computed inside the collection-changed handler,
    /// because CollectionChanged fires before the new line's layout pass — Extent hasn't grown yet there.
    /// </summary>
    private void AttachTranscriptAutoScroll(AiPanelViewModel vm)
    {
        DetachTranscriptAutoScroll();
        _subscribedVm = vm;
        _stickToBottom = true;
        TranscriptScroll.ScrollChanged += OnTranscriptScrollChanged;
        vm.Transcript.CollectionChanged += OnTranscriptCollectionChanged;
    }

    private void DetachTranscriptAutoScroll()
    {
        TranscriptScroll.ScrollChanged -= OnTranscriptScrollChanged;
        if (_subscribedVm is { } vm)
            vm.Transcript.CollectionChanged -= OnTranscriptCollectionChanged;
        _subscribedVm = null;
    }

    private void OnTranscriptScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        double maxOffsetY = Math.Max(0, TranscriptScroll.Extent.Height - TranscriptScroll.Viewport.Height);
        _stickToBottom = TranscriptScroll.Offset.Y >= maxOffsetY - StickToBottomToleranceDip;
    }

    private void OnTranscriptCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || !_stickToBottom)
            return;

        // Defer past the layout pass the new line triggers, so Extent reflects the appended content by
        // the time ScrollToEnd runs.
        Dispatcher.UIThread.Post(() => TranscriptScroll.ScrollToEnd(), DispatcherPriority.Loaded);
    }
}
