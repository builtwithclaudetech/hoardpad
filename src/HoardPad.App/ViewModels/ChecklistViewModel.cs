// ChecklistViewModel.cs — the `.hoard` checklist document VM (FR-30…FR-34, FR-41, FR-42, design §5).
//
// Binds to the PURE HoardPad.Core.Checklist functions — it embeds NO hierarchy/display/lock/depth-cap
// arithmetic (architecture §5.1/§6.5): VisibleRows for the row projection, EffectiveEnabled for the lock,
// TryIndent/TryOutdent + RenormalizeSiblingOrders + MapDropToStoredOrder for the mutations. Every TERMINAL
// mutation persists immediately (zero-debounce) through the serialized HoardWriteQueue; item-TEXT edits
// coalesce ≤ 150 ms. A depth-cap no-op enqueues ZERO writes (a null pure-fn result never calls EnqueueTerminal).
// C-1: a parent toggle re-raises IsEnabled on EVERY descendant VM via the effectiveEnabled fan-out.
//
// All model mutation is on the UI thread; the atomic write runs off the dispatcher inside the queue.
//
// LOCK DISCIPLINE:
//   _modelGate guards: _items, _title, _checkedItemMode, _rootExtra, _settingsExtra, _hasPersisted, _path.
//   All reads/writes of those fields must be inside lock(_modelGate). The gate is never held while
//   awaiting (the snapshot is taken inside the gate, the awaited write runs outside it).
//
// QA3-7 (Save-As): _path is mutated exactly once per relocate, by RelocateAsync, AFTER the new file is
// already durable on disk (write-new-first) — so every write that reads _path under the gate (including
// one racing in from the queue) sees either the consistent OLD path/OLD-persisted state or the consistent
// NEW path/persisted state, never a half-flipped mix.

using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using HoardPad.App.Checklist;
using HoardPad.Core.Checklist;

namespace HoardPad.App.ViewModels;

/// <summary>The active <c>.hoard</c> document's view-model: visible-row projection + durable mutations.</summary>
internal sealed partial class ChecklistViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ChecklistFileService _fileService;
    private readonly HoardWriteQueue _writeQueue;
    /// <summary>The current on-disk path. Mutable (QA3-7 Save-As): flipped exactly once per relocate by
    /// <see cref="RelocateAsync"/>, guarded by <see cref="_modelGate"/> like every other model field.</summary>
    private string _path;
    private readonly object _modelGate = new();

    // The authoritative flat model (Core's immutable item list). Every mutation produces a new list via a
    // pure function, is swapped here under the gate, then the visible-row projection + write are driven off it.
    // ALL of these are read and written under _modelGate.
    private IReadOnlyList<HoardItem> _items;
    private string _title;
    private string _checkedItemMode;
    private IDictionary<string, System.Text.Json.JsonElement>? _rootExtra;
    private IDictionary<string, System.Text.Json.JsonElement>? _settingsExtra;
    /// <summary>Tracks whether we've persisted at least once — avoids the check-then-act TOCTOU in SaveAsync.</summary>
    private bool _hasPersisted;

    /// <summary>The optional delete-non-empty-parent prompt (C-3). The view supplies a real modal
    /// <c>ShowDialog(owner)</c>; null in headless/unit tests where the choice is passed directly.</summary>
    public Func<ChecklistItemViewModel, int, Task<DeleteParentChoice>>? DeleteParentPrompt { get; set; }

    /// <summary>
    /// QA4-19: the last known vertical scroll offset of this document's row list, in pixels. The single
    /// shared <see cref="Views.ChecklistView"/> instance is reused across every open <c>.hoard</c> tab (its
    /// DataContext is repointed on tab switch, see <see cref="Views.MainWindow"/>), so scroll position can't
    /// live on the view — it is captured here (per-document, per-tab) on deactivate and restored on
    /// reactivate. Not an <c>[ObservableProperty]</c>: it is never bound in XAML, only read/written by the
    /// view's own save/restore wiring.
    /// </summary>
    public double ScrollOffsetY { get; set; }

    [ObservableProperty]
    private string _displayTitle = string.Empty;

    /// <summary>
    /// Set when the write queue catches a non-cancellation exception. The view can bind/observe this to
    /// surface a "save failed" status banner. Null means no error (or the last error was cleared).
    /// </summary>
    [ObservableProperty]
    private string? _saveFailedMessage;

    /// <summary>The visible-row sequence (pre-order walk under the active mode + collapse), the ItemsControl source.</summary>
    public ObservableCollection<ChecklistItemViewModel> VisibleRows { get; } = [];

    /// <summary>
    /// Raised after Indent/Outdent/MoveWithinGroup reproject the visible rows, naming the id of the item
    /// that just moved (QA3-6/QA3-19). <see cref="RebuildVisibleRows"/> clears and re-adds every row, which
    /// drops keyboard focus entirely — without this, a repeated Indent/Outdent/Move has no stable focus
    /// target and silently acts on whatever the user's pointer happens to land on next (the QA3-6 3-tier
    /// symptom is this focus loss, not a hierarchy-math bug). The view re-focuses the moved item's row via
    /// the same mechanism <c>OnItemTextKeyDown</c> already uses.
    /// </summary>
    public event Action<string>? FocusRequested;

    private ChecklistViewModel(
        ChecklistFileService fileService,
        TimeProvider timeProvider,
        string path,
        HoardDocument document,
        bool fileAlreadyExists)
    {
        _fileService = fileService;
        _path = path;
        _items = document.Items;
        _title = document.Title;
        _checkedItemMode = document.Settings.CheckedItemMode;
        _rootExtra = document.Extra;
        _settingsExtra = document.Settings.Extra;
        _hasPersisted = fileAlreadyExists;
        _displayTitle = document.Title;
        _writeQueue = new HoardWriteQueue(WriteCurrentStateAsync, timeProvider);
        _writeQueue.WriteError += OnWriteError;
        RebuildVisibleRows();
    }

    /// <summary>Open an existing <c>.hoard</c> into a new VM (FR-30). Loads via the pure Core loader.</summary>
    public static async Task<ChecklistViewModel> OpenAsync(
        ChecklistFileService fileService,
        TimeProvider timeProvider,
        string path,
        CancellationToken ct)
    {
        HoardLoadResult result = await ChecklistFileService.LoadAsync(path, ct).ConfigureAwait(true);
        HoardDocument document = result.Document
            ?? new HoardDocument(); // a rejected (higher-version) file opens as an empty document, never throws.
        return new ChecklistViewModel(fileService, timeProvider, path, document, fileAlreadyExists: true);
    }

    /// <summary>Create a VM around an in-memory document (FR-34 new checklist / tests).</summary>
    public static ChecklistViewModel ForDocument(
        ChecklistFileService fileService,
        TimeProvider timeProvider,
        string path,
        HoardDocument document,
        bool fileAlreadyExists = false) =>
        new(fileService, timeProvider, path, document, fileAlreadyExists);

    /// <summary>
    /// The file path of this <c>.hoard</c> document (used by the session/recovery engine to persist the tab's
    /// location in <c>session.json</c> so the tab round-trips across a clean save+close+restart, Critical-2 fix).
    /// Null for a never-saved in-memory document.
    /// </summary>
    internal string? FilePath
    {
        get
        {
            string path;
            lock (_modelGate)
                path = _path;
            return string.IsNullOrEmpty(path) ? null : path;
        }
    }

    /// <summary>Test/telemetry hook: completed atomic writes — proves a no-op enqueued zero writes.</summary>
    public long CompletedWrites => _writeQueue.CompletedWrites;

    /// <summary>The active checked-item display mode (FR-33), parsed from the stored string.</summary>
    public CheckedItemDisplayMode Mode
    {
        get
        {
            string mode;
            lock (_modelGate)
                mode = _checkedItemMode;
            return HoardDisplayModeExtensions.ParseCheckedItemMode(mode);
        }
    }

    // ----- Terminal mutations (FR-31 zero-debounce). Each: pure-fn mutate → swap → reproject → enqueue. -----

    /// <summary>Toggle an item's check (FR-30). C-1: re-raise IsEnabled on the whole subtree (lock fan-out).</summary>
    public void ToggleChecked(string itemId)
    {
        var mutated = Mutate(items => items
            .Select(i => i.Id == itemId ? i with { Checked = !i.Checked } : i)
            .ToList());
        if (mutated)
            EnqueueTerminal();
    }

    /// <summary>Toggle a parent's collapse state (FR-42 — durable, zero-debounce terminal mutation).</summary>
    public void ToggleCollapsed(string itemId)
    {
        var mutated = Mutate(items => items
            .Select(i => i.Id == itemId ? i with { Collapsed = !i.Collapsed } : i)
            .ToList());
        if (mutated)
            EnqueueTerminal();
    }

    /// <summary>Indent (demote) an item one tier carrying its subtree (FR-32/FR-41). Depth-cap no-op = ZERO writes.</summary>
    public void Indent(string itemId)
    {
        IReadOnlyList<HoardItem>? result = HoardHierarchy.TryIndent(itemId, Snapshot(), Mode);
        if (result is null)
            return; // depth-cap / no-preceding-sibling no-op — enqueue NOTHING (C-4).
        Swap(result);
        EnqueueTerminal();
        FocusRequested?.Invoke(itemId);
    }

    /// <summary>Outdent (promote) an item one tier carrying its subtree (FR-32/FR-41). Tier-0 no-op = ZERO writes.</summary>
    public void Outdent(string itemId)
    {
        IReadOnlyList<HoardItem>? result = HoardHierarchy.TryOutdent(itemId, Snapshot(), Mode);
        if (result is null)
            return; // already tier 0 — enqueue NOTHING.
        Swap(result);
        EnqueueTerminal();
        FocusRequested?.Invoke(itemId);
    }

    /// <summary>Move an item up/down one position within its sibling group (FR-32 keyboard reorder, B4).</summary>
    public void MoveWithinGroup(string itemId, int direction)
    {
        var snapshot = Snapshot();
        HoardItem? item = snapshot.FirstOrDefault(i => i.Id == itemId);
        if (item is null)
            return;

        var siblings = HoardHierarchy.ChildrenOf(item.ParentId, snapshot);
        int index = -1;
        for (int i = 0; i < siblings.Count; i++)
        {
            if (siblings[i].Id == itemId) { index = i; break; }
        }
        int target = index + direction;
        if (index < 0 || target < 0 || target >= siblings.Count)
            return; // at an edge — no-op, ZERO writes.

        // Swap the two siblings' stored order, then renormalize the group (Core pure fn).
        HoardItem other = siblings[target];
        var reordered = snapshot
            .Select(i =>
                i.Id == itemId ? i with { Order = other.Order } :
                i.Id == other.Id ? i with { Order = item.Order } : i)
            .ToList();
        Swap(HoardHierarchy.RenormalizeSiblingOrders(reordered));
        EnqueueTerminal();
        FocusRequested?.Invoke(itemId);
    }

    /// <summary>
    /// Apply a drag-drop within a sibling group (FR-32/FR-33). The drop is a display gap; the pure Core
    /// mapping resolves it to a stored-order anchor, which we assign and renormalize (the control embeds no
    /// partition arithmetic — architecture §5.1). A same-position drop is a no-op (ZERO writes, C-4).
    /// </summary>
    public void DropWithinGroup(string draggedId, int displayGap)
    {
        var snapshot = Snapshot();
        int anchor = HoardDisplay.MapDropToStoredOrder(draggedId, displayGap, snapshot, Mode);
        var reanchored = snapshot
            .Select(i => i.Id == draggedId ? i with { Order = anchor } : i)
            .ToList();
        var renormalized = HoardHierarchy.RenormalizeSiblingOrders(reanchored);

        // C-4: if the renormalized result is identical to the current state, it's a same-position
        // drop — enqueue NOTHING (zero writes). SequenceEqual uses HoardItem's record Equals, which
        // compares all fields including Extra (IDictionary<string,JsonElement>). Extra compares by
        // REFERENCE: `with`-expressions that don't touch Extra copy the reference, so the same dict
        // instance is preserved across the reorder transforms — the no-op guard is correct and won't
        // produce a false positive. If Extra ever becomes value-compared (e.g. custom Equals), this
        // comment should be revisited to ensure the no-op guard still holds.
        if (renormalized.SequenceEqual(snapshot))
            return;

        Swap(renormalized);
        EnqueueTerminal();
    }

    /// <summary>Append a new item to a sibling group (FR-30 add). Tier of the parent (null = tier 0).</summary>
    public string AddItem(string? parentId, string text)
    {
        string id = NewId();
        var snapshot = Snapshot();
        int nextOrder = HoardHierarchy.ChildrenOf(parentId, snapshot).Count;
        var added = snapshot.Append(new HoardItem
        {
            Id = id,
            Text = text,
            ParentId = parentId,
            Order = nextOrder,
        }).ToList();
        Swap(added);
        EnqueueTerminal();
        return id;
    }

    /// <summary>
    /// Insert a new sibling item immediately below <paramref name="afterItemId"/>, at the SAME hierarchy
    /// level (QA2-6 — Enter on a row's text creates a new sibling below it, mirroring the add-item row's
    /// tier-0 append). A no-op (returns null) when <paramref name="afterItemId"/> no longer exists (e.g. it
    /// was deleted between the keypress and this call). Terminal — persists immediately.
    /// </summary>
    public string? AddSiblingAfter(string afterItemId, string text)
    {
        var snapshot = Snapshot();
        HoardItem? after = snapshot.FirstOrDefault(i => i.Id == afterItemId);
        if (after is null)
            return null;

        // Bump every later sibling's order by 1 to open a gap, then insert the new item into that gap
        // (order = after.Order + 1). RenormalizeSiblingOrders below collapses this to a contiguous 0..n-1
        // ranking, so the exact gap value only has to be unambiguous, not contiguous.
        string id = NewId();
        var bumped = snapshot
            .Select(i => i.ParentId == after.ParentId && i.Order > after.Order
                ? i with { Order = i.Order + 1 }
                : i)
            .Append(new HoardItem
            {
                Id = id,
                Text = text,
                ParentId = after.ParentId,
                Order = after.Order + 1,
            })
            .ToList();

        Swap(HoardHierarchy.RenormalizeSiblingOrders(bumped));
        EnqueueTerminal();
        return id;
    }

    /// <summary>Delete a leaf (FR-30). A non-empty parent must go through <see cref="DeleteAsync"/>.</summary>
    public void DeleteLeaf(string itemId)
    {
        var snapshot = Snapshot();
        if (snapshot.Any(i => i.ParentId == itemId))
            return; // not a leaf — caller must use DeleteAsync for the subtree-vs-promote prompt.
        Swap(HoardHierarchy.RenormalizeSiblingOrders(snapshot.Where(i => i.Id != itemId).ToList()));
        EnqueueTerminal();
    }

    /// <summary>
    /// Delete an item (FR-30, C-3). A leaf is removed directly. A NON-EMPTY parent prompts the MODAL
    /// subtree-vs-promote choice (the view supplies <see cref="DeleteParentPrompt"/>); Cancel is a no-op
    /// (ZERO writes). Both branches are terminal mutations persisted zero-debounce.
    /// </summary>
    public async Task DeleteAsync(string itemId)
    {
        var snapshot = Snapshot();
        HoardItem? item = snapshot.FirstOrDefault(i => i.Id == itemId);
        if (item is null)
            return;

        int childCount = snapshot.Count(i => i.ParentId == itemId);
        if (childCount == 0)
        {
            DeleteLeaf(itemId);
            return;
        }

        ChecklistItemViewModel? row = VisibleRows.FirstOrDefault(r => r.Id == itemId);
        DeleteParentChoice choice = DeleteParentPrompt is { } prompt && row is not null
            ? await prompt(row, childCount).ConfigureAwait(true)
            : DeleteParentChoice.Cancel; // no prompt wired → fail safe (no destructive default).

        switch (choice)
        {
            case DeleteParentChoice.DeleteSubtree:
                DeleteSubtree(itemId);
                break;
            case DeleteParentChoice.PromoteChildren:
                PromoteChildrenThenDelete(itemId);
                break;
            case DeleteParentChoice.Cancel:
            default:
                return; // Cancel — ZERO writes.
        }
    }

    /// <summary>Delete a parent and its whole subtree (FR-30 delete-subtree branch).</summary>
    public void DeleteSubtree(string itemId)
    {
        var snapshot = Snapshot();
        var toRemove = SubtreeIds(itemId, snapshot);
        Swap(HoardHierarchy.RenormalizeSiblingOrders(
            snapshot.Where(i => !toRemove.Contains(i.Id)).ToList()));
        EnqueueTerminal();
    }

    /// <summary>Delete a parent but reparent its direct children up one tier (FR-30 promote-children branch).</summary>
    public void PromoteChildrenThenDelete(string itemId)
    {
        var snapshot = Snapshot();
        HoardItem? parent = snapshot.FirstOrDefault(i => i.Id == itemId);
        if (parent is null)
            return;
        string? grandParentId = parent.ParentId;
        var promoted = snapshot
            .Where(i => i.Id != itemId)
            .Select(i => i.ParentId == itemId ? i with { ParentId = grandParentId } : i)
            .ToList();
        Swap(HoardHierarchy.RenormalizeSiblingOrders(promoted));
        EnqueueTerminal();
    }

    /// <summary>Set the per-list checked-item display mode (FR-33 — terminal mutation).</summary>
    public void SetMode(CheckedItemDisplayMode mode)
    {
        string modeStr = mode switch
        {
            CheckedItemDisplayMode.MoveToBottom => "move-to-bottom",
            CheckedItemDisplayMode.HideWhenChecked => "hide-when-checked",
            _ => "strikethrough-in-place",
        };
        lock (_modelGate)
            _checkedItemMode = modeStr;
        RebuildVisibleRows();
        EnqueueTerminal();
    }

    /// <summary>Set the document title (FR-30 inline title edit — coalesced like item text).</summary>
    public void SetTitle(string title)
    {
        lock (_modelGate)
            _title = title;
        DisplayTitle = title;
        _writeQueue.EnqueueTextEdit();
    }

    // ----- Item-TEXT edit (FR-31 ≤ 150 ms coalesce). -----

    /// <summary>Edit an item's text (FR-30). Coalesced ≤ 150 ms (TimeProvider-driven), not zero-debounce.</summary>
    public void EditText(string itemId, string text)
    {
        var mutated = Mutate(items => items
            .Select(i => i.Id == itemId ? i with { Text = text } : i)
            .ToList());
        if (mutated)
            _writeQueue.EnqueueTextEdit();
    }

    // ----- The C-1 completion-lock fan-out + visible-row projection. -----

    /// <summary>
    /// True while <see cref="RebuildVisibleRowsCore"/> is synchronously reconciling <see cref="VisibleRows"/>
    /// (its <c>Clear()</c>/<c>Add()</c> cycle). QA3-21: <c>Clear()</c> detaches every realized row container —
    /// including the currently keyboard-focused item TextBox — from the visual tree, and Avalonia raises
    /// <c>LostFocus</c> on that TextBox SYNCHRONOUSLY as part of the detach. The checklist wires
    /// <c>TextBox.LostFocus</c> to <see cref="ChecklistView.OnTextCommitted"/> → <see cref="EditText"/>, which
    /// (whenever the TextBox holds characters not yet committed to the model — the normal state between any
    /// two fast keystrokes, since a keystroke only updates the control's own <c>Text</c>, not the model, until
    /// Enter/LostFocus/the 150 ms coalesce fires) finds a genuine diff and calls <see cref="Mutate"/> →
    /// <see cref="Swap"/> → <see cref="RebuildVisibleRows"/> AGAIN — reentrantly, mid-<c>Clear()</c>, before
    /// the outer cycle has finished tearing down/regenerating containers. Re-entering the ItemsControl's
    /// container-generation cycle while it is already mid-cycle corrupts Avalonia's internal generator state:
    /// a NullReferenceException deep inside <c>PanelContainerGenerator.ClearItemsControlLogicalChildren</c> /
    /// <c>ContentPresenter.ContentChanged</c>. Decisively reproduced (with this guard temporarily disabled)
    /// via <c>ChecklistFastTypingCrashTests</c>: type into a row's TextBox, then — with NO settle — press
    /// Alt+Shift+Right/Left while that row is still focused; the resulting stack trace matches this
    /// mechanism frame-for-frame. The originally reported gesture (type → Enter → immediate Alt+Shift+arrow
    /// on the just-created row) lands in the same reentrancy window for the same underlying reason: a fast
    /// typist's very next keystroke routinely lands on a row whose most recent edit was never separately
    /// committed first.
    /// </summary>
    private bool _visibleRowsRebuildInFlight;

    /// <summary>Set when a reentrant call arrives while <see cref="_visibleRowsRebuildInFlight"/> is true.</summary>
    private bool _visibleRowsRebuildRequestedWhileInFlight;

    /// <summary>
    /// Test-only visibility into the QA3-21 guard: how many times a reentrant <see cref="RebuildVisibleRows"/>
    /// call was coalesced instead of being allowed to re-enter the ItemsControl's container-generation cycle.
    /// Zero in the overwhelmingly common non-reentrant path; every increment is a prevented crash.
    /// </summary>
    internal int ReentrantVisibleRowsRebuildCoalesceCount { get; private set; }

    /// <summary>
    /// Rebuild the visible-row VM list from the pure VisibleRows walk + refresh every row's lock. Reentrancy-
    /// safe (QA3-21): a call that arrives while the ObservableCollection reconciliation below is already in
    /// flight is coalesced rather than re-entering it — see <see cref="_visibleRowsRebuildInFlight"/>. Every
    /// caller still observes <see cref="VisibleRows"/> synchronously reflecting the LATEST model state by the
    /// time this method returns: the coalesced follow-up runs immediately after the in-flight cycle completes,
    /// within the same top-level call.
    /// </summary>
    private void RebuildVisibleRows()
    {
        if (_visibleRowsRebuildInFlight)
        {
            _visibleRowsRebuildRequestedWhileInFlight = true;
            ReentrantVisibleRowsRebuildCoalesceCount++;
            return;
        }

        _visibleRowsRebuildInFlight = true;
        try
        {
            RebuildVisibleRowsCore();
        }
        finally
        {
            _visibleRowsRebuildInFlight = false;
        }

        if (_visibleRowsRebuildRequestedWhileInFlight)
        {
            _visibleRowsRebuildRequestedWhileInFlight = false;
            RebuildVisibleRows(); // catch up now that the in-flight cycle has fully settled.
        }
    }

    /// <summary>The actual ObservableCollection reconciliation — only ever entered non-reentrantly (guarded above).</summary>
    private void RebuildVisibleRowsCore()
    {
        var snapshot = Snapshot();
        IReadOnlyList<HoardItem> rows = HoardDisplay.VisibleRows(snapshot, Mode);

        // Reconcile the ObservableCollection in place by id so existing row VMs (and their bindings) persist.
        var byId = VisibleRows.ToDictionary(r => r.Id);
        VisibleRows.Clear();
        foreach (HoardItem item in rows)
        {
            ChecklistItemViewModel vm = byId.TryGetValue(item.Id, out var existing)
                ? existing
                : new ChecklistItemViewModel(item.Id);
            ProjectRow(vm, item, snapshot);
            VisibleRows.Add(vm);
        }
    }

    /// <summary>
    /// Project one row VM's bound facts from the model. <b>C-1 fan-out:</b> IsEnabled is recomputed from
    /// <see cref="HoardHierarchy.EffectiveEnabled"/> over the FULL ancestor chain on every projection, so a
    /// parent toggle (which re-projects the whole visible list) re-raises IsEnabled on every descendant —
    /// not just direct children — via the ObservableProperty setter's PropertyChanged.
    /// </summary>
    private static void ProjectRow(ChecklistItemViewModel vm, HoardItem item, IReadOnlyList<HoardItem> snapshot)
    {
        var siblings = HoardHierarchy.ChildrenOf(item.ParentId, snapshot);
        int position = 0;
        for (int i = 0; i < siblings.Count; i++)
        {
            if (siblings[i].Id == item.Id) { position = i + 1; break; }
        }

        vm.Text = item.Text;
        vm.Checked = item.Checked;
        vm.Collapsed = item.Collapsed;
        vm.Depth = HoardHierarchy.Depth(item, snapshot);
        vm.IsParent = HoardHierarchy.IsParent(item, snapshot);
        vm.PositionInSet = position;
        vm.SizeOfSet = siblings.Count;
        vm.IsEnabled = HoardHierarchy.EffectiveEnabled(item, snapshot); // the completion-lock fan-out.
    }

    // ----- Plumbing. -----

    private IReadOnlyList<HoardItem> Snapshot()
    {
        lock (_modelGate)
            return _items;
    }

    private void Swap(IReadOnlyList<HoardItem> next)
    {
        lock (_modelGate)
            _items = next;
        RebuildVisibleRows();
    }

    /// <summary>Apply a pure transform; swap + reproject only when it actually changed the list (else no-op).</summary>
    private bool Mutate(Func<IReadOnlyList<HoardItem>, IReadOnlyList<HoardItem>> transform)
    {
        var current = Snapshot();
        var next = transform(current);
        if (ReferenceEquals(next, current) || next.SequenceEqual(current))
            return false;
        Swap(next);
        return true;
    }

    private void EnqueueTerminal() => _writeQueue.EnqueueTerminal();

    /// <summary>Persist the document's CURRENT state atomically (the write-queue snapshot callback).</summary>
    private async Task WriteCurrentStateAsync(CancellationToken ct)
    {
        HoardDocument document;
        bool hasPersisted;
        string path;
        lock (_modelGate)
        {
            hasPersisted = _hasPersisted;
            path = _path; // QA3-7: read under the gate — _path is mutable (RelocateAsync flips it).
            document = new HoardDocument
            {
                SchemaVersion = HoardSchema.CurrentVersion,
                Title = _title,
                Settings = new HoardSettings { CheckedItemMode = _checkedItemMode, Extra = _settingsExtra },
                Items = _items,
                Extra = _rootExtra,
            };
        }

        await _fileService.SaveAsync(path, document, hasPersisted, ct).ConfigureAwait(false);

        // Mark as persisted AFTER a successful write so the next write takes the replace path.
        lock (_modelGate)
            _hasPersisted = true;
    }

    /// <summary>
    /// QA3-7: force an immediate durable write of the current state (File ▸ Save / toolbar Save on a
    /// checklist tab — otherwise a silent no-op, since `.hoard` already auto-saves zero-debounce). Also
    /// the write-quiesce step of <see cref="RelocateAsync"/>.
    /// </summary>
    public Task FlushAsync(CancellationToken ct) => _writeQueue.QuiesceAsync(ct);

    /// <summary>
    /// QA3-7 Save-As (Option B — auto-save preserved, additive relocate): move this document to
    /// <paramref name="requestedPath"/>, FORCING the <c>.hoard</c> extension regardless of what was typed
    /// or picked (a wrong extension would silently reopen this file as garbled text — the QA3-7-reverse
    /// failure mode). Executes the safe order up through the durable flip:
    ///   1. write-quiesce  — drain the queue (<see cref="HoardWriteQueue.QuiesceAsync"/>) so nothing is
    ///      still writing the OLD path by the time this returns.
    ///   2. write-new-first — serialize the CURRENT state and write it to the NEW path atomically; this
    ///      must succeed (be durable on disk) BEFORE the old file is ever touched.
    ///   3. flip _path/_hasPersisted — every subsequent write (queue-driven or explicit) now targets the
    ///      new path.
    /// Steps 4 (immediate session.json index update) and 5 (delete the OLD file LAST) are NOT this VM's
    /// concern — they live in <see cref="Session.WorkspaceService"/>, which owns the session index and
    /// must persist it before the old file is removed (orphan-not-loss bias: a crash between step 3 and
    /// the caller's step 5 leaves BOTH files on disk and the tab still resolvable, never a lost list).
    /// Returns the prior on-disk path for the caller to delete last, or <c>null</c> when this document had
    /// never actually been persisted yet (nothing to delete) or the requested path resolves to the same
    /// file it is already at.
    /// </summary>
    public async Task<string?> RelocateAsync(string requestedPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestedPath);
        string newPath = ForceHoardExtension(requestedPath);

        // Step 1: write-quiesce.
        await _writeQueue.QuiesceAsync(ct).ConfigureAwait(false);

        HoardDocument document;
        string? priorPath;
        lock (_modelGate)
        {
            priorPath = _hasPersisted && !string.IsNullOrEmpty(_path) ? _path : null;
            document = new HoardDocument
            {
                SchemaVersion = HoardSchema.CurrentVersion,
                Title = _title,
                Settings = new HoardSettings { CheckedItemMode = _checkedItemMode, Extra = _settingsExtra },
                Items = _items,
                Extra = _rootExtra,
            };
        }

        // Step 2: write-new-first. fileAlreadyExists reflects the NEW path (the native Save dialog already
        // confirmed an overwrite via its own prompt when the picked path collides with an existing file).
        bool newPathAlreadyExists = System.IO.File.Exists(newPath);
        await _fileService.SaveAsync(newPath, document, newPathAlreadyExists, ct).ConfigureAwait(false);

        // Step 3: flip — only after the new file is confirmed durable.
        lock (_modelGate)
        {
            _path = newPath;
            _hasPersisted = true;
        }

        return string.Equals(priorPath, newPath, StringComparison.Ordinal) ? null : priorPath;
    }

    /// <summary>FORCE the <c>.hoard</c> extension (case-insensitive match kept as-is; anything else replaced).</summary>
    private static string ForceHoardExtension(string path) =>
        string.Equals(System.IO.Path.GetExtension(path), ".hoard", StringComparison.OrdinalIgnoreCase)
            ? path
            : System.IO.Path.ChangeExtension(path, ".hoard");

    /// <summary>Raised on the pump thread when a write throws — surfaces "save failed" to the UI.</summary>
    private void OnWriteError(Exception ex)
    {
        // Post back to the UI thread so bound controls can react (the pump thread is not the dispatcher).
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            SaveFailedMessage = $"Save failed: {ex.Message}");
    }

    private static HashSet<string> SubtreeIds(string rootId, IReadOnlyList<HoardItem> items)
    {
        var ids = new HashSet<string> { rootId };
        bool grew;
        do
        {
            grew = false;
            foreach (HoardItem item in items)
            {
                if (item.ParentId is { } p && ids.Contains(p) && ids.Add(item.Id))
                    grew = true;
            }
        }
        while (grew);
        return ids;
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    public ValueTask DisposeAsync() => _writeQueue.DisposeAsync();
}

/// <summary>The delete-non-empty-parent choice (C-3 modal): delete the whole subtree, promote the children
/// up one tier, or cancel (the default-focused, non-destructive choice).</summary>
internal enum DeleteParentChoice
{
    /// <summary>Cancel — no mutation (the Cancel button has initial focus, design §5.2).</summary>
    Cancel,

    /// <summary>Delete the parent and its entire subtree.</summary>
    DeleteSubtree,

    /// <summary>Delete the parent but reparent its children up one tier (un-nest).</summary>
    PromoteChildren,
}
