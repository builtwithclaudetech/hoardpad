// EditorView.axaml.cs — the editor-surface wiring (design §3). Framework wiring only (the testable
// editor logic lives in HoardPad.App.Editor): install TextMate against the FR-7 custom registry overlay,
// register the document's grammar lazily on bind, install the FoldingManager + the FR-9 debounced
// FoldingService (M8.2 indentation + brace folding by TextMate scope), bind the FR-6 line-op keybindings,
// and push the caret/EOL/encoding facts to the EditorViewModel for the status bar. All of this runs on
// the UI thread (architecture §3.1).

using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Folding;
using AvaloniaEdit.TextMate;
using HoardPad.App.Editor;
using HoardPad.App.Markdown;
using HoardPad.App.ViewModels;

namespace HoardPad.App.Views;

/// <summary>The editor surface (AvaloniaEdit + TextMate + line ops + brace folding) for the active document.</summary>
public sealed partial class EditorView : UserControl
{
    private TextEditor _editor = null!;
    private ContentControl _previewHost = null!;
    private MarkdownPreviewControl? _preview;
    private TextMate.Installation? _textMate;
    private FoldingManager? _foldingManager;
    private FoldingService? _foldingService;
    private bool _suppressTextSync;

    /// <summary>
    /// QA3-22: per-tab undo. Each <see cref="EditorViewModel"/> (one per open tab, see <c>DocumentTab</c>)
    /// owns its own AvaloniaEdit <see cref="TextDocument"/> — and therefore its own <c>UndoStack</c> — so a
    /// tab switch reattaches that document instead of mutating the ONE shared document every prior tab used
    /// (which is what wiped undo on every switch). A <see cref="ConditionalWeakTable{TKey,TValue}"/> keys
    /// off the VM instance without holding a strong reference, so a closed tab's document is collected
    /// naturally once its <see cref="EditorViewModel"/> is no longer referenced from <c>Tabs</c> — no manual
    /// eviction needed.
    /// </summary>
    private readonly ConditionalWeakTable<EditorViewModel, TextDocument> _tabDocuments = new();

    public EditorView()
    {
        InitializeComponent();
        _editor = this.FindControl<TextEditor>("Editor")!;
        _previewHost = this.FindControl<ContentControl>("PreviewHost")!;

        // FR-9 folding (M8.2). The FoldingManager is the UI-thread commit target; the FoldingService owns
        // the 300 ms debounce, the off-UI recompute via the FoldingStrategySelector (indentation vs brace by
        // TextMate scope), and the snapshot-version guard on the UI-thread UpdateFoldings() commit
        // (architecture §3.3). TimeProvider.System is the production wall clock (the service is the
        // TimeProvider-driven unit, exercised with a FakeTimeProvider in tests); commits + the recompute
        // kickoff marshal back onto the Avalonia UI thread via Dispatcher.UIThread.Post.
        _foldingManager = FoldingManager.Install(_editor.TextArea);
        _foldingService = new FoldingService(
            TimeProvider.System,
            getDocument: () => _editor.Document,
            getScope: () => _textMate is null ? null : ViewModel?.Document.GrammarScope,
            commit: foldings => _foldingManager?.UpdateFoldings(foldings, firstErrorOffset: -1),
            postToUi: action => Dispatcher.UIThread.Post(action));

        WireLineOperationKeyBindings();
        WireRedoRebindKeyBindings();

        // BUG-2 root cause: TextEditor.OnApplyTemplate unconditionally installs AvaloniaEdit's OWN stock
        // SearchPanel (AvaloniaEdit.Search.SearchPanel.Install), which HoardPad never shows (design §3.5 —
        // FindReplaceView supersedes it) but which still wires a SearchInputHandler onto the TextArea's
        // DefaultInputHandler. That handler registers a CommandBinding for SearchCommands.CloseSearchPanel,
        // whose DEFAULT gesture is bare Escape — and TextAreaInputHandler's KeyDown dispatch matches any
        // CommandBinding's gesture and unconditionally marks the event Handled once executed, even though
        // ExecuteCloseSearchPanel/Close() is a no-op when that (permanently invisible) panel isn't open. The
        // net effect: Escape is silently swallowed at the TextArea level whenever the editor has focus,
        // before it can ever bubble to MainWindow's OnWindowKeyDown — so HoardPad's own Find/Replace panel
        // could never be dismissed with Esc while the editor (not the panel) held focus. Uninstalling the
        // stock panel the moment its template is applied removes that phantom handler entirely; TemplateApplied
        // fires right after TextEditor.OnApplyTemplate assigns its private searchPanel field, so SearchPanel
        // is guaranteed non-null here.
        _editor.TemplateApplied += (_, _) => _editor.SearchPanel?.Uninstall();

        _editor.TextChanged += OnEditorTextChanged;
        _editor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;

        // F-EDIT2: AvaloniaEdit's built-in caret-follow horizontal auto-scroll — decompiled AvaloniaEdit
        // 11.4.1's TextArea.PerformTextInput calls Caret.BringCaretToView() exactly once after every
        // committed keystroke — settles a CONSTANT ~7.83px short of true max scroll during a real
        // per-character typing loop, hard-clipping the just-typed trailing glyph against the editor's right
        // edge. It does not self-correct on idle and is independent of editor width.
        //
        // Execution order (decompiled): PerformTextInput calls ReplaceSelectionWithText(text), THEN raises
        // OnTextEntered(e) (this hook runs here — BEFORE the built-in call), THEN calls its own
        // Caret.BringCaretToView() (the built-in call, second/last). So this hook's call is the FIRST of the
        // two BringCaretToView() calls per keystroke, not a follow-up after the built-in one.
        //
        // Mechanism: UNCONFIRMED. MakeVisible computes its scroll target purely from the caret rectangle and
        // viewport width, so calling it twice against an unchanged rectangle should be idempotent — yet two
        // calls per keystroke empirically closes the undershoot from ~7.83px to ~2.5px, which only makes
        // sense if the two calls see a DIFFERENT caret rectangle (one plausible source: TextView's cached
        // visible-line set being stale/incomplete on the first access within a keystroke and corrected once
        // the first MakeVisible mutates the scroll offset). That has not been verified against the
        // AvaloniaEdit source — do not trust it as the mechanism. What IS verified, empirically and
        // repeatedly, is the OUTCOME: a steady ~2.5px undershoot (well under one glyph width) with the hook
        // present vs. a steady ~7.83px undershoot without it, clean across a font-size / Padding /
        // window-width / prefix-length sweep (EditorRightEdgeClipTests). That test file is what actually
        // guards this — if a future AvaloniaEdit point release changes the internal timing this reasoning
        // rests on, the test will catch the regression even though the mechanism was never nailed down.
        //
        // Scope decision: only TextArea.TextEntered is hooked. BringCaretToView() has exactly two call
        // sites in THIS file: this hook, and SelectRange (Find/Replace navigation) — SelectRange is left
        // unhooked because its own single call already settles at the same ~2.5px baseline (spot-checked),
        // not because it lacks a call site. OnExternalWriteApplied (the AI-write caret restore) is NOT one
        // of the four BringCaretToView() call sites at all — it sets CaretOffset after an AI-applied on-disk
        // edit but never calls BringCaretToView(), so the viewport is never told to follow the caret. That
        // gap is pre-existing, orthogonal to F-EDIT2 (a missing call, not an undershooting one), and
        // deliberately NOT fixed here. The other two call sites live in AvaloniaEdit itself
        // (CaretNavigationCommandHandler's End/Ctrl+End/Shift+End/Shift+Ctrl+End, and
        // SelectionMouseHandler's mouse-drag-select auto-scroll) — spot-checks of the End-key path and the
        // bulk-insert (paste / AI-write) shape both settle at the same ~2.5px baseline on their own single
        // BringCaretToView() call, with no visible clip, WITHOUT this hook — so hooking them too would add
        // nothing measurable. Known residual, left uncovered: IME composition-commit was never spot-checked
        // and could in principle re-enter PerformTextInput repeatedly, closer to the typing shape than a
        // single call — if that path does undershoot the same way, it likely already goes through the same
        // TextEntered event this hook covers (same underlying call site), but that is unconfirmed.
        _editor.TextArea.TextEntered += OnEditorTextEntered;

        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private EditorViewModel? ViewModel => DataContext as EditorViewModel;

    private EditorViewModel? _subscribedViewModel;

    /// <summary>
    /// QA5-1 fix (fast-tab-cycle scroll-loss CRITICAL): mirrors <see cref="Views.ChecklistView"/>'s
    /// <c>_settledVm</c> (QA4-19) — the vm whose SCROLL <see cref="_editor"/> currently, authoritatively
    /// reflects, set once that vm's own deferred scroll-restore Post in <see cref="RestoreViewState"/> has
    /// actually run (i.e. a layout pass has settled for it). Null until the very first restore-Post fires.
    /// <see cref="CaptureViewState"/>'s scroll capture only trusts the live Horizontal/VerticalOffset when the
    /// vm being switched away from is THIS vm — otherwise the switch happened too fast for that vm's own
    /// restore-Post to run, and the live offset is a TRANSIENT value AvaloniaEdit reset to on the Document
    /// reassignment (<c>TextView.OnDocumentChanged -> ClearScrollData()</c>), not a real reading for the vm
    /// actually being deactivated — capturing it would permanently overwrite that vm's true prior scroll with
    /// a stale/zero value. Caret and selection need no such guard: their restore is synchronous (see
    /// <see cref="RestoreViewState"/>), so there is no unsettled window for them.
    /// </summary>
    private EditorViewModel? _scrollSettledVm;

    /// <summary>
    /// QA3-22: the single unsubscribe path for a previously-bound <see cref="EditorViewModel"/>, shared by
    /// the DataContext-changed-to-null path, the tab-switch resubscribe path, and detach — so all three stay
    /// in lockstep (a divergence here is exactly what let the null-transition undo-wipe bug hide).
    /// </summary>
    private void UnsubscribeFrom(EditorViewModel vm)
    {
        vm.GrammarChangeRequested -= OnGrammarChangeRequested;
        vm.BufferMutationRequested -= OnBufferMutationRequested;
        vm.PropertyChanged -= OnViewModelPropertyChanged;
        vm.UndoRequested -= OnUndoRequested;
        vm.RedoRequested -= OnRedoRequested;
        vm.CutRequested -= OnCutRequested;
        vm.CopyRequested -= OnCopyRequested;
        vm.PasteRequested -= OnPasteRequested;
        vm.FlushUndoPointRequested -= OnFlushUndoPointRequested;
        vm.ExternalWriteApplied -= OnExternalWriteApplied;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // QA5-1: capture the OUTGOING tab's caret/selection/scroll before anything below reassigns
        // _editor.Document — the moment that clears all three (see EditorViewState's doc comment for the
        // decompiled AvaloniaEdit mechanism). Keyed off _subscribedViewModel (the vm the editor CURRENTLY
        // reflects), not the new DataContext, and unconditional — this also covers the A -> null step of an
        // A -> null -> A round-trip (QA3-22), which is the only chance to capture A's state before the null
        // branch below clears _subscribedViewModel to null.
        if (_subscribedViewModel is { } outgoing)
            outgoing.ViewState = CaptureViewState(outgoing);

        if (ViewModel is not { } vm)
        {
            // QA3-22: DataContext went null — MainWindowViewModel.ActivateTab unconditionally sets
            // Editor = tab?.Editor, which is null for a checklist/Settings tab, so switching text-tab A ->
            // checklist/Settings -> back to A drives DataContext A -> null -> A. Reset _subscribedViewModel
            // (with a matching unsubscribe) here so that final re-bind to A is correctly detected as a
            // genuine tab switch below (isTabSwitch = !ReferenceEquals(_subscribedViewModel, vm)) and routed
            // through SwitchToTabDocument, instead of being mistaken for a same-VM re-mirror that would wipe
            // A's undo stack via MirrorDocumentIntoEditor.
            if (_subscribedViewModel is not null)
            {
                UnsubscribeFrom(_subscribedViewModel);
                _subscribedViewModel = null;
            }
            return;
        }

        // Re-subscribe per DataContext swap (a tab switch) so a stale editor VM never re-mirrors into this
        // view. GrammarChangeRequested (QA2-13 language-menu switch, no text change) and
        // BufferMutationRequested (M6.7/QA3-5 find/replace, an undo-preserving text mutation) are distinct
        // signals from a brand-new document load — see EditorViewModel's doc comments on each.
        bool isTabSwitch = !ReferenceEquals(_subscribedViewModel, vm);
        if (isTabSwitch)
        {
            if (_subscribedViewModel is not null)
                UnsubscribeFrom(_subscribedViewModel);
            vm.GrammarChangeRequested += OnGrammarChangeRequested;
            vm.BufferMutationRequested += OnBufferMutationRequested;
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.UndoRequested += OnUndoRequested;
            vm.RedoRequested += OnRedoRequested;
            vm.CutRequested += OnCutRequested;
            vm.CopyRequested += OnCopyRequested;
            vm.PasteRequested += OnPasteRequested; // FR-15: render the preview when the toggle flips.
            vm.FlushUndoPointRequested += OnFlushUndoPointRequested; // PANEL-4 (FR-MD-7): seal a pre-flush undo point.
            vm.ExternalWriteApplied += OnExternalWriteApplied; // QA3-9: an AI-applied on-disk edit refreshes this tab.
            _subscribedViewModel = vm;
        }

        // TextMate syntax highlighting (FR-7), installed against the VM's shared registry overlay so
        // the VM's language resolution and the view's lazy grammar loading use one instance. The One
        // Dark base theme matches brand.md §2; the brand-retinted theme + lockstep switch is FR-37
        // (M6.x, not M6.2). Grammars register lazily per language via SetGrammar — never all 20 up
        // front (NFR-1 / architecture §7.2). Installed on first bind (the VM is not set at ctor time).
        if (_textMate is null)
        {
            _textMate = _editor.InstallTextMate(vm.RegistryOptions, initCurrentDocument: true);
            // FR-37 (M7.1): register with the lockstep theme service so this editor follows theme switches.
            // RegisterEditor applies the ACTIVE palette immediately (the brand-retinted TextMate theme +
            // the §13 surface background), so an editor opened after a switch is correctly themed. Null in
            // the editor-only test path (no app singleton) — the editor keeps the stock install default.
            HoardPad.App.Theme.ThemeService.Current?.RegisterEditor(_textMate, _editor);
        }

        // QA3-22: a genuine tab switch attaches this tab's OWN TextDocument/UndoStack (SwitchToTabDocument)
        // rather than mutating the previously-attached one — that is what preserves per-tab undo. A re-fire
        // with the SAME vm (DataContext re-set to itself, or the explicit MirrorActiveDocument() re-load
        // path below) is a genuine new-document load on THIS tab and keeps clearing undo via the original
        // whole-buffer mirror (QA3-5 gap 2 — a Ctrl+Z must never bleed a previous document into this one).
        if (isTabSwitch)
            SwitchToTabDocument(vm);
        else
            MirrorDocumentIntoEditor(vm);

        // FR-15 (QA4-11 fix): if this VM is already in preview state on bind (a tab switch back to a doc
        // left in preview), render the current buffer into the preview surface so the swap shows live
        // content immediately. This MUST run AFTER SwitchToTabDocument/MirrorDocumentIntoEditor above —
        // RenderPreview reads _editor.Text, and until the document swap above runs, _editor.Document still
        // holds whatever tab was PREVIOUSLY attached (e.g. a blank tab switched away from). Rendering
        // before the swap silently rendered the wrong (often blank) tab's content into the preview, which
        // is exactly the QA4-11 "MD Preview never loads" regression: toggle preview on, switch to another
        // tab, switch back — the preview showed nothing because it rendered the other tab's empty buffer.
        if (vm.IsPreviewVisible)
            RenderPreview(vm);
    }

    /// <summary>
    /// QA3-22: attach THIS <paramref name="vm"/>'s own <see cref="TextDocument"/> — creating one (a fresh
    /// document starts with an empty <c>UndoStack</c>) the first time this tab is bound, or reattaching the
    /// one created on an earlier visit — so a tab switch never touches another tab's undo history. If the
    /// VM's text diverged from the cached document while this tab was inactive (e.g. a Find/Replace "All
    /// Open Files" pass, which mutates <see cref="EditorViewModel.Document"/> on a background tab with no
    /// view subscriber to react), the divergence is applied via <c>Document.Replace</c> — the same
    /// undo-preserving mechanism <see cref="OnBufferMutationRequested"/> uses — rather than silently showing
    /// stale content or discarding this tab's accumulated undo history.
    /// <see cref="FoldingManager"/> is permanently bound to the <see cref="TextDocument"/> it was installed
    /// against (it does not follow <c>TextEditor.Document</c> reassignment), so it is uninstalled and
    /// reinstalled against the newly-attached document on every switch.
    /// </summary>
    private void SwitchToTabDocument(EditorViewModel vm)
    {
        if (_foldingManager is not null)
            FoldingManager.Uninstall(_foldingManager);

        bool isNewDocument = !_tabDocuments.TryGetValue(vm, out TextDocument? document);
        if (isNewDocument)
        {
            document = new TextDocument(vm.Document.Text);
            _tabDocuments.Add(vm, document);
        }

        _suppressTextSync = true;
        try
        {
            _editor.Document = document;

            if (!isNewDocument && !string.Equals(document!.Text, vm.Document.Text, StringComparison.Ordinal))
            {
                document.UndoStack.StartUndoGroup();
                try
                {
                    document.Replace(0, document.TextLength, vm.Document.Text);
                }
                finally
                {
                    document.UndoStack.EndUndoGroup();
                }
            }
        }
        finally
        {
            _suppressTextSync = false;
        }

        _foldingManager = FoldingManager.Install(_editor.TextArea);

        ApplyGrammarFor(vm.Document.GrammarScope);
        _foldingService?.SetDocument();
        vm.UpdateCursor(_editor.TextArea.Caret.Line, _editor.TextArea.Caret.Column);

        RestoreViewState(vm);
    }

    /// <summary>
    /// QA5-1: read this view's current caret/selection/scroll straight off the live AvaloniaEdit controls —
    /// the values <see cref="_editor"/> shows for whichever tab is CURRENTLY attached (called just before
    /// that attachment changes). Caret and selection are captured unconditionally (their restore is
    /// synchronous, so the live value always genuinely belongs to <paramref name="outgoing"/>). Scroll is
    /// captured ONLY when <paramref name="outgoing"/>'s own deferred scroll-restore has settled (see
    /// <see cref="_scrollSettledVm"/>'s doc comment) — otherwise the live offset is a transient post-Document-
    /// reassignment value that does not belong to <paramref name="outgoing"/> at all, and <paramref name="outgoing"/>'s
    /// previously-stored scroll is carried forward unchanged rather than being overwritten with that transient
    /// (frequently zero) reading.
    /// </summary>
    private EditorViewState CaptureViewState(EditorViewModel outgoing)
    {
        bool scrollSettled = ReferenceEquals(outgoing, _scrollSettledVm);
        double scrollX = scrollSettled ? _editor.HorizontalOffset : outgoing.ViewState.ScrollX;
        double scrollY = scrollSettled ? _editor.VerticalOffset : outgoing.ViewState.ScrollY;
        return new EditorViewState(_editor.CaretOffset, _editor.SelectionStart, _editor.SelectionLength, scrollX, scrollY);
    }

    /// <summary>
    /// QA5-1: restore <paramref name="vm"/>'s saved caret/selection synchronously — neither reads document
    /// layout, only the document's own text offsets (already valid the instant <see cref="SwitchToTabDocument"/>
    /// reattaches <paramref name="vm"/>'s <c>TextDocument</c>), so no dispatcher deferral is needed (verified
    /// empirically in PerTabCaretTests). Scroll restore IS deferred (DispatcherPriority.Loaded, identity-guarded
    /// against <see cref="_subscribedViewModel"/>, mirroring ChecklistView's QA4-19 pattern, and — once it fires —
    /// marks <paramref name="vm"/> as <see cref="_scrollSettledVm"/> so a later <see cref="CaptureViewState"/>
    /// knows this vm's scroll is now a real, trustworthy reading rather than a transient post-reassignment
    /// value): AvaloniaEdit clears
    /// its scroll extent on the same Document reassignment (TextView.OnDocumentChanged -> ClearScrollData()),
    /// which is not rebuilt until the next layout pass, so restoring immediately after the reassignment lands on
    /// a stale (zero) extent (verified empirically) — restoring it a layout pass later, after the extent is live
    /// again, is not. Sets the internal ScrollViewer's Offset directly (found via the visual tree) rather than
    /// <c>TextEditor.ScrollToVerticalOffset</c>/<c>ScrollToHorizontalOffset</c> — decompiling the shipped
    /// AvaloniaEdit 11.4.1 binary shows both of those public methods are genuinely no-ops in this pinned
    /// version (body is just <c>ApplyTemplate();</c>, the <c>offset</c> parameter is never used), confirmed
    /// empirically in PerTabCaretTests before this was written this way.
    /// </summary>
    private void RestoreViewState(EditorViewModel vm)
    {
        EditorViewState state = vm.ViewState;
        int docLength = _editor.Document?.TextLength ?? 0;
        int selectionStart = Math.Clamp(state.SelectionStart, 0, docLength);
        int selectionLength = Math.Clamp(state.SelectionLength, 0, docLength - selectionStart);
        // Select() BEFORE the explicit CaretOffset set below: TextEditor.Select(start, length) repositions the
        // caret to the selection's end as a side effect, which would silently clobber a caret restored to a
        // position DECOUPLED from the selection end if this ran the other way around (verified empirically —
        // an earlier draft of this method had the order reversed and failed PerTabCaretTests with the caret one
        // character off from its captured value).
        if (selectionLength > 0)
            _editor.Select(selectionStart, selectionLength);
        _editor.CaretOffset = Math.Clamp(state.CaretOffset, 0, docLength);

        Dispatcher.UIThread.Post(
            () =>
            {
                if (!ReferenceEquals(_subscribedViewModel, vm))
                    return;
                if (_editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scrollViewer)
                    scrollViewer.Offset = new Vector(state.ScrollX, state.ScrollY);
                // QA5-1 fix: this vm's scroll is now settled — CaptureViewState may trust the live offset as
                // genuinely belonging to it from this point on (see _scrollSettledVm's doc comment).
                _scrollSettledVm = vm;
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Re-mirror the active document's decoded text AND re-apply the TextMate grammar from the live VM
    /// (carry-forward B). Called on an IN-PLACE open/reinterpret — the VM raised its document change WITHOUT
    /// swapping the DataContext (e.g. <c>EditorViewModel.OpenAsync</c> repurposing the same tab) — without
    /// this, opening a file into the already-bound editor would leave the buffer + grammar stale. (QA3-22: a
    /// genuine DataContext swap — an actual tab switch — no longer routes here; it goes through
    /// <see cref="SwitchToTabDocument"/>, which reattaches that tab's own <c>TextDocument</c>/<c>UndoStack</c>
    /// instead.) This remains a NEW-DOCUMENT LOAD on the CURRENTLY-ATTACHED tab document (QA3-5 gap 2): it
    /// deliberately clears its AvaloniaEdit undo stack (via the <c>TextEditor.Text</c> setter) so a Ctrl+Z
    /// can never bleed a previous document's content into the one just loaded on this tab. Safe to call
    /// repeatedly.
    /// </summary>
    public void MirrorActiveDocument()
    {
        if (ViewModel is { } vm)
            MirrorDocumentIntoEditor(vm);
    }

    /// <summary>
    /// QA2-13: the VM changed ONLY the grammar scope (a language-menu override) — text is unchanged. Re-apply
    /// the TextMate grammar in place and reschedule the FR-9 fold recompute (fold strategy is scope-dependent)
    /// WITHOUT touching <c>TextEditor.Text</c>, so the undo stack is left completely untouched.
    /// </summary>
    private void OnGrammarChangeRequested()
    {
        if (ViewModel is not { } vm)
            return;
        ApplyGrammarFor(vm.Document.GrammarScope);
        _foldingService?.Schedule();
    }

    /// <summary>
    /// QA3-5: the VM performed an in-session content mutation that must remain undoable (a find/replace
    /// current-file replace or replace-all, M6.7). Apply the new text via <c>Document.Replace</c> — verified
    /// (FindReplaceUndoTests, against the pinned AvaloniaEdit 11.4.1 behavior) to push exactly ONE undo entry
    /// per call, so a replace-all is one Ctrl+Z — and, unlike the <c>TextEditor.Text</c> setter the
    /// new-document-load path uses, it does NOT call <c>UndoStack.ClearAll()</c>. The undo group start/end is
    /// decorative future-proofing (a single Replace is already one undo entry) — it is not the mechanism that
    /// preserves undo.
    /// </summary>
    private void OnBufferMutationRequested()
    {
        if (ViewModel is not { } vm)
            return;
        if (_editor.Document is not { } document)
            return;

        _suppressTextSync = true;
        try
        {
            document.UndoStack.StartUndoGroup();
            try
            {
                document.Replace(0, document.TextLength, vm.Document.Text);
            }
            finally
            {
                document.UndoStack.EndUndoGroup();
            }
        }
        finally
        {
            _suppressTextSync = false;
        }

        // Caret decision (QA3-5): snap to the start, matching the TextEditor.Text setter's prior behavior —
        // the simplest choice that preserves today's observed caret position after a buffer-wide replace.
        _editor.CaretOffset = 0;
        // No explicit fold-recompute kick here: document.Replace raises the editor's TextChanged event
        // (OnEditorTextChanged), which unconditionally calls _foldingService.Schedule() regardless of
        // _suppressTextSync — an extra call here would just restart the same debounce a second time.
        vm.UpdateCursor(_editor.TextArea.Caret.Line, _editor.TextArea.Caret.Column);
    }

    /// <summary>
    /// FR-15: when the VM's <see cref="EditorViewModel.IsPreviewVisible"/> flips on, render the CURRENT
    /// editor buffer into the read-only preview surface (the swap-in-place). Rendering reads the editor's
    /// live text (never the reverse), so the buffer is untouched (SC-4). Toggling back to source needs no
    /// work — the TextEditor was only hidden, so its scroll position is intact (design §4.4).
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorViewModel.IsPreviewVisible)
            && ViewModel is { IsPreviewVisible: true } vm)
        {
            RenderPreview(vm);
        }
    }

    // ----- Edit menu event handlers (BUG-9): route EditorViewModel events to the TextEditor. -----

    private void OnUndoRequested() => _editor.Undo();

    private void OnRedoRequested() => _editor.Redo();

    private void OnCutRequested() => _editor.Cut();

    private void OnCopyRequested() => _editor.Copy();

    private void OnPasteRequested() => _editor.Paste();

    /// <summary>
    /// PANEL-4 (feature-ai-multidoc-panel FR-MD-7): seal an editor undo point after a pre-turn AI flush. An
    /// empty undo group closes any in-progress typing group so the pre-flush buffer state is a discrete,
    /// undoable boundary — a Ctrl+Z after the AI reloads the file (MirrorActiveDocument pushes the reload as
    /// an undoable whole-text replace) restores the pre-flush content, independent of the AI undo journal.
    /// </summary>
    private void OnFlushUndoPointRequested()
    {
        if (_editor.Document is not { } document)
            return;
        document.UndoStack.StartUndoGroup();
        document.UndoStack.EndUndoGroup();
    }

    /// <summary>
    /// QA3-9: the VM detected an AI-applied on-disk edit to THIS tab's file (outside the editor) and re-read
    /// it via <see cref="EditorViewModel.RefreshFromDiskAsync"/>. Apply the new text via <c>Document.Replace</c>
    /// — the SAME undo-preserving mechanism <see cref="OnBufferMutationRequested"/> uses (Ctrl+Z reverts it,
    /// QA3-20) — but PRESERVE the caret's character offset (clamped to the new, possibly shorter, document
    /// length) instead of resetting it to 0: unlike a find/replace whole-buffer swap, an AI edit is typically
    /// small relative to the whole document and the user's editing position should survive it where possible.
    /// </summary>
    private void OnExternalWriteApplied()
    {
        if (ViewModel is not { } vm)
            return;
        if (_editor.Document is not { } document)
            return;

        int priorOffset = _editor.CaretOffset;

        _suppressTextSync = true;
        try
        {
            document.UndoStack.StartUndoGroup();
            try
            {
                document.Replace(0, document.TextLength, vm.Document.Text);
            }
            finally
            {
                document.UndoStack.EndUndoGroup();
            }
        }
        finally
        {
            _suppressTextSync = false;
        }

        _editor.CaretOffset = Math.Min(priorOffset, document.TextLength);
        vm.UpdateCursor(_editor.TextArea.Caret.Line, _editor.TextArea.Caret.Column);
    }

    /// <summary>Build (lazily) the preview surface and render the editor's current text into it (FR-15/FR-16).</summary>
    private void RenderPreview(EditorViewModel vm)
    {
        _ = vm; // parameter kept for future per-doc preview options driven from the VM (e.g. image base path).
        _preview ??= new MarkdownPreviewControl();
        if (!ReferenceEquals(_previewHost.Content, _preview))
            _previewHost.Content = _preview;
        // Render from the live editor text so the preview reflects unsaved edits (the buffer stays read-only).
        _preview.Render(_editor.Text);
    }

    private void MirrorDocumentIntoEditor(EditorViewModel vm)
    {
        // Mirror the document's decoded text into the editor without re-marking it dirty. try/finally so a
        // throw from the Text setter (or a synchronous TextChanged/Caret subscriber) can never leave
        // _suppressTextSync stuck true — which would silently stop every later keystroke from syncing to
        // the VM (IsDirty would never flip again).
        _suppressTextSync = true;
        try
        {
            _editor.Text = vm.Document.Text;
        }
        finally
        {
            _suppressTextSync = false;
        }

        ApplyGrammarFor(vm.Document.GrammarScope);
        // A document replace (tab switch / open / reinterpret / in-place re-mirror): cancel any in-flight
        // recompute against the OLD document and schedule a fresh debounced recompute for the new one (FR-9).
        _foldingService?.SetDocument();
        vm.UpdateCursor(_editor.TextArea.Caret.Line, _editor.TextArea.Caret.Column);
    }

    /// <summary>
    /// Dispose the per-view TextMate installation on detach (carry-forward B): once multiple documents
    /// open/close, each EditorView's <see cref="TextMate.Installation"/> holds a grammar/theme registry —
    /// leaving it undisposed leaks per tab. Re-installed lazily on the next DataContext bind.
    /// </summary>
    private void OnDetachedFromVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        // FR-37 (M7.1): unregister from the theme service BEFORE disposing the installation so a disposed
        // installation is never re-themed on a later Switch (no leak, no use-after-dispose).
        if (_textMate is not null)
            HoardPad.App.Theme.ThemeService.Current?.UnregisterEditor(_textMate);
        _textMate?.Dispose();
        _textMate = null;

        // FR-9: cancel any pending debounce / in-flight recompute so a detached view never commits folds.
        _foldingService?.Stop();
        _foldingService = null;

        // Drop the QA2-13/QA3-5 grammar/buffer-mutation + FR-15 PropertyChanged subscriptions so a detached
        // view does not re-mirror or re-render the preview (no leak).
        if (_subscribedViewModel is not null)
        {
            UnsubscribeFrom(_subscribedViewModel);
            _subscribedViewModel = null;
        }
    }

    /// <summary>
    /// Select <paramref name="length"/> chars at <paramref name="offset"/> and scroll the match into view
    /// (M6.7 find/replace navigation — the ◂ ▸ / F3 cursor moves the editor selection to the current match).
    /// A null/empty span clears the selection. Offsets are clamped to the document so a stale span is safe.
    /// </summary>
    public void SelectRange(int offset, int length)
    {
        if (_editor.Document is null)
            return;
        if (length <= 0)
        {
            _editor.TextArea.ClearSelection();
            return;
        }

        int docLength = _editor.Document.TextLength;
        int start = Math.Clamp(offset, 0, docLength);
        int end = Math.Clamp(offset + length, start, docLength);
        _editor.Select(start, end - start);
        _editor.CaretOffset = end;
        _editor.TextArea.Caret.BringCaretToView();
    }

    /// <summary>Set the editor grammar for a scope (FR-7 lazy registration), or clear it for Plain Text.</summary>
    public void ApplyGrammarFor(string? scope)
    {
        if (_textMate is null)
            return;
        // SetGrammar triggers the registry's GetGrammar(scope) — for source.toml that lazily parses
        // the embedded supplemental grammar on first use. null/empty leaves the buffer un-highlighted.
        _textMate.SetGrammar(string.IsNullOrEmpty(scope) ? null : scope);
    }

    /// <summary>
    /// M9.1 perf-bench orchestration (architecture §3.5; SPIKE-1 §B/§C). Drives the layout-aware synthetic
    /// keystroke that yields the SC-1/SC-2 stop instant — the moment the editor has RENDERED the change
    /// (first <c>LayoutUpdated</c> after the keystroke's <c>TextChanged</c>), matching SPIKE-1's
    /// layout-as-paint-proxy stop. Sequence:
    ///   1. Wait for the editor's first <c>LayoutUpdated</c> (the loaded document is now rendered = SC-1
    ///      "first render ready").
    ///   2. Invoke <paramref name="markKeystroke"/> (the harness stamps the SC-2 start), then inject one
    ///      character — the synthetic startup keystroke — producing a genuine <c>TextChanged</c>.
    ///   3. On the NEXT <c>LayoutUpdated</c> (the keystroke is now rendered), invoke
    ///      <paramref name="onRendered"/> (the harness stamps the stop and emits the echo).
    /// Bench-only: nothing calls this outside HOARDPAD_BENCH_PIPE, so it adds no normal/test behavior. The
    /// app-injected keystroke replaces an OS-level key because the headless X11 backend rejects programmatic
    /// keyboard focus (PRD R3-mi10 reduced-scope input leg) — the editor's real TextChanged→layout/render
    /// path is still exercised end-to-end.
    /// </summary>
    internal void RunBenchKeystroke(Action markKeystroke, Action onRendered)
    {
        void OnFirstLayout(object? _, EventArgs __)
        {
            _editor.LayoutUpdated -= OnFirstLayout;

            // The keystroke is rendered when the next layout pass completes.
            void OnKeystrokeRendered(object? _, EventArgs __)
            {
                _editor.LayoutUpdated -= OnKeystrokeRendered;
                onRendered();
            }
            _editor.LayoutUpdated += OnKeystrokeRendered;

            markKeystroke();
            _editor.Document?.Insert(_editor.Document.TextLength, "a"); // synthetic startup keystroke → TextChanged.
        }

        _editor.LayoutUpdated += OnFirstLayout;
        _editor.InvalidateArrange(); // ensure a layout pass is scheduled so OnFirstLayout fires promptly.
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (ViewModel is { } vm && !_suppressTextSync)
        {
            vm.Document.Text = _editor.Text;
            vm.IsDirty = true;
        }
        // FR-9: debounce the fold recompute 300 ms after the last keystroke (the snapshot-version guard in
        // FoldingService drops any commit whose offsets were computed against a now-stale document version).
        _foldingService?.Schedule();
    }

    private void OnCaretPositionChanged(object? sender, EventArgs e) =>
        ViewModel?.UpdateCursor(_editor.TextArea.Caret.Line, _editor.TextArea.Caret.Column);

    /// <summary>F-EDIT2: see the wiring comment on the TextEntered subscription in the constructor.</summary>
    private void OnEditorTextEntered(object? sender, TextInputEventArgs e) =>
        _editor.TextArea.Caret.BringCaretToView();

    // ----- FR-6 line ops: move-line-up/down + duplicate-line (design §3.3 keymap, ODQ-2). -----

    private void WireLineOperationKeyBindings()
    {
        AddLineOpBinding(Key.Up, KeyModifiers.Control | KeyModifiers.Shift, MoveLinesUp);
        AddLineOpBinding(Key.Down, KeyModifiers.Control | KeyModifiers.Shift, MoveLinesDown);
        AddLineOpBinding(Key.D, KeyModifiers.Control, DuplicateLines);
        // FR-6 delete-line — the one FR-6 op without a home until M6.3 (carry-forward B). Ctrl+Shift+K.
        AddLineOpBinding(Key.K, KeyModifiers.Control | KeyModifiers.Shift, DeleteCurrentLines);
    }

    private void AddLineOpBinding(Key key, KeyModifiers modifiers, Action action) =>
        _editor.TextArea.KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(key, modifiers),
            Command = new RelayAction(action),
        });

    // ----- FEAT-5: Redo rebind Ctrl+Y -> Ctrl+R. -----

    /// <summary>
    /// FEAT-5. AvaloniaEdit's <c>TextAreaDefaultInputHandler</c> wires <c>ApplicationCommands.Redo</c> to
    /// Avalonia's default hotkey gesture (Ctrl+Y) via its OWN <c>RoutedCommand</c>/<c>CommandBindings</c>
    /// mechanism — entirely independent of any Window-level <c>KeyBinding</c>, so removing/not-adding a
    /// Window-level Ctrl+Y binding (as MainWindow.axaml already did) does NOT stop Ctrl+Y from redoing while
    /// the editor's TextArea has focus. Confirmed empirically: an explicit <see cref="AddLineOpBinding"/>
    /// entry on <c>TextArea.KeyBindings</c> for a gesture pre-empts that built-in RoutedCommand handler
    /// before it ever executes (the same precedence AddLineOpBinding's existing entries already rely on to
    /// coexist with AvaloniaEdit's stock bindings). So:
    ///   - Ctrl+R is wired here to <c>_editor.Redo()</c> — the real, functioning accelerator when the editor
    ///     has focus (mirrors MainWindow.axaml's new Window-level Ctrl+R entry, which only fires when focus is
    ///     OFF the editor — the same split QA3-5 established for Ctrl+Z/Undo).
    ///   - Ctrl+Y is wired to a no-op, which pre-empts and disarms AvaloniaEdit's built-in Redo binding, so
    ///     Ctrl+Y no longer redoes anywhere the editor has focus.
    /// </summary>
    private void WireRedoRebindKeyBindings()
    {
        AddLineOpBinding(Key.R, KeyModifiers.Control, () => _editor.Redo());
        AddLineOpBinding(Key.Y, KeyModifiers.Control, static () => { });
    }

    private void MoveLinesUp() => RunLineOp(LineOperations.MoveLinesUp);

    private void MoveLinesDown() => RunLineOp(LineOperations.MoveLinesDown);

    private void DuplicateLines() => RunLineOp(LineOperations.DuplicateLines);

    private void DeleteCurrentLines() => RunLineOp(LineOperations.DeleteLines);

    private void RunLineOp(Func<TextDocument, LineSpan, LineSpan> op)
    {
        TextDocument? document = _editor.Document;
        if (document is null)
            return;

        LineSpan span = CurrentLineSpan();
        LineSpan result;
        using (document.RunUpdate())
            result = op(document, span);

        SelectLineSpan(document, result);
    }

    /// <summary>The 1-based line span the caret/selection currently covers.</summary>
    private LineSpan CurrentLineSpan()
    {
        Selection selection = _editor.TextArea.Selection;
        if (selection.IsEmpty)
        {
            int line = _editor.TextArea.Caret.Line;
            return new LineSpan(line, line);
        }

        int first = _editor.Document.GetLineByOffset(selection.SurroundingSegment.Offset).LineNumber;
        int last = _editor.Document.GetLineByOffset(selection.SurroundingSegment.EndOffset).LineNumber;
        return first <= last ? new LineSpan(first, last) : new LineSpan(last, first);
    }

    /// <summary>Re-select a line span after a line op so the caret/selection follows the moved/copied text.</summary>
    private void SelectLineSpan(TextDocument document, LineSpan span)
    {
        int firstLine = Math.Clamp(span.FirstLine, 1, document.LineCount);
        int lastLine = Math.Clamp(span.LastLine, 1, document.LineCount);
        DocumentLine first = document.GetLineByNumber(firstLine);
        DocumentLine last = document.GetLineByNumber(lastLine);
        _editor.Select(first.Offset, last.EndOffset - first.Offset);
        _editor.CaretOffset = last.EndOffset;
    }

    /// <summary>A tiny ICommand wrapper so the editor's KeyBindings can invoke a parameterless action.</summary>
    private sealed class RelayAction : System.Windows.Input.ICommand
    {
        private readonly Action _action;
        public RelayAction(Action action) => _action = action;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _action();
    }
}
