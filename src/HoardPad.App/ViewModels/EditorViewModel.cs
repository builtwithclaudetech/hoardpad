// EditorViewModel.cs — the active-document view-model (FR-3…FR-8, design §3). Owns one EditorDocument,
// exposes the FR-6 status-bar segments (language / encoding / EOL / cursor) + the FR-5 dirty marker, and
// the FR-6 manual reinterpret command. Open/save go through EditorFileService -> IAtomicFileStore. The
// EditorView (code-behind) keeps the AvaloniaEdit TextDocument <-> this VM's Text in sync and drives the
// cursor segment; all editor mutation is on the UI thread (architecture §3.1), file I/O is async.

using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HoardPad.App.Editor;

namespace HoardPad.App.ViewModels;

/// <summary>
/// QA5-1: this tab's caret/selection/scroll snapshot. AvaloniaEdit 11.4.1 unconditionally clears all three on
/// every <c>TextEditor.Document</c> reassignment (confirmed by decompile: <c>TextArea.OnDocumentChanged</c>
/// sets <c>Caret.Location = new TextLocation(1, 1)</c> and calls <c>ClearSelection()</c>; <c>TextView.OnDocumentChanged</c>
/// calls <c>ClearScrollData()</c>) — since one shared <see cref="AvaloniaEdit.TextEditor"/> is reused across
/// tabs, a tab switch reassigns <c>Document</c> and wipes all three regardless of document identity or
/// content. <see cref="HoardPad.App.Views.EditorView"/> captures this record from the outgoing tab right
/// before the swap and restores it right after.
/// </summary>
internal sealed record EditorViewState(int CaretOffset, int SelectionStart, int SelectionLength, double ScrollX, double ScrollY)
{
    internal static readonly EditorViewState Initial = new(0, 0, 0, 0, 0);
}

/// <summary>The active document's view-model — the FR-6 status segments, dirty state, and open/save/reinterpret.</summary>
internal sealed partial class EditorViewModel : ObservableObject
{
    private readonly EditorFileService _fileService;
    private readonly LanguageCatalog _languageCatalog;

    /// <summary>The open document this VM backs (FR-1). The view binds its text to the editor control.</summary>
    public EditorDocument Document { get; }

    /// <summary>
    /// QA5-1: this tab's last-known caret/selection/scroll position, captured by <see cref="HoardPad.App.Views.EditorView"/>
    /// right before switching away and restored right after switching back. Plain settable property (matches
    /// <c>ChecklistViewModel.ScrollOffsetY</c>'s shape) — purely view-facing UI state that this VM never reads.
    /// </summary>
    internal EditorViewState ViewState { get; set; } = EditorViewState.Initial;

    /// <summary>
    /// The FR-7 grammar registry the editor view installs against. Shared (not service-located by the
    /// view) so the VM's language resolution and the view's lazy grammar loading use one instance.
    /// </summary>
    public HoardPadRegistryOptions RegistryOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirtyMarker))]
    private bool _isDirty;

    [ObservableProperty]
    private string _documentName = "untitled";

    [ObservableProperty]
    private string _languageMode = "Plain Text";

    [ObservableProperty]
    private string _encodingLabel = "UTF-8";

    [ObservableProperty]
    private string _lineEndingLabel = "LF";

    [ObservableProperty]
    private string _cursorLabel = "Ln 1, Col 1";

    /// <summary>
    /// Whether the FR-15 Markdown rendered-preview is currently shown (true) instead of the colorized
    /// source (false). Only a Markdown document can flip this; for a non-Markdown doc the toggle is
    /// disabled (<see cref="IsMarkdown"/> / <see cref="ToggleMarkdownPreviewCommand"/>). The preview is a
    /// separate read-only visual tree built from the parse — the buffer is untouched (SC-4).
    /// </summary>
    [ObservableProperty]
    private bool _isPreviewVisible;

    /// <summary>
    /// FEAT-6: whether the View ▸ Word Wrap toggle is on for THIS tab's document. Scope is per-tab (one
    /// EditorViewModel per open document, same as <see cref="IsPreviewVisible"/> above) — the simplest
    /// scope that fits the existing per-document VM shape, with no cross-tab state to reconcile and no
    /// settings persistence to add. Purely a display flag: the bound <c>TextEditor.WordWrap</c> only wraps
    /// the RENDERED line, never the buffer — <see cref="Document"/>'s text (and therefore the saved bytes,
    /// FR-4) is never touched by this toggle, so the on-disk content stays byte-identical either way.
    /// </summary>
    [ObservableProperty]
    private bool _isWordWrapEnabled;

    public EditorViewModel(
        EditorFileService fileService,
        HoardPadRegistryOptions registryOptions,
        LanguageCatalog languageCatalog)
        : this(fileService, registryOptions, languageCatalog, new EditorDocument())
    {
    }

    public EditorViewModel(
        EditorFileService fileService,
        HoardPadRegistryOptions registryOptions,
        LanguageCatalog languageCatalog,
        EditorDocument document)
    {
        _fileService = fileService;
        _languageCatalog = languageCatalog;
        RegistryOptions = registryOptions;
        Document = document;
        SyncSegmentsFromDocument();
    }

    /// <summary>The "•" unsaved-edits marker (FR-5), shown after the document name in the status bar.</summary>
    public string DirtyMarker => IsDirty ? " •" : string.Empty;

    /// <summary>
    /// The TextMate scope prefix the Markdown grammar resolves to (verified: ".md"/".markdown" both map to
    /// "text.html.markdown" through <see cref="LanguageCatalog"/>). The FR-15 toggle is gated on this — it
    /// reuses the existing catalog/extension resolution rather than inventing a parallel "is Markdown" check.
    /// </summary>
    private const string MarkdownScopePrefix = "text.html.markdown";

    /// <summary>
    /// Whether the active document is Markdown (FR-15 Done-when — "only Markdown documents expose the
    /// toggle"). True iff the document's resolved grammar scope is the Markdown scope. A non-Markdown doc
    /// returns false, so the toggle command's <c>CanExecute</c> is false and the toolbar/menu affordance
    /// is disabled.
    /// </summary>
    public bool IsMarkdown =>
        Document.GrammarScope is { } scope &&
        scope.StartsWith(MarkdownScopePrefix, System.StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Toggle the FR-15 source↔preview view (swap-in-place; side-by-side split is deferred, ODQ-8). Enabled
    /// only for a Markdown document (<see cref="IsMarkdown"/>) — the editor view shows the read-only preview
    /// when <see cref="IsPreviewVisible"/> is true and the colorized source otherwise. The buffer is never
    /// touched by the preview, so a toggle-back yields a byte-identical buffer (SC-4).
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsMarkdown))]
    private void ToggleMarkdownPreview() => IsPreviewVisible = !IsPreviewVisible;

    /// <summary>
    /// FEAT-6: flip the View ▸ Word Wrap flag for this tab. Always enabled (no document-type gate, unlike
    /// <see cref="ToggleMarkdownPreview"/>) — word wrap is a display option meaningful for any text document.
    /// </summary>
    [RelayCommand]
    private void ToggleWordWrap() => IsWordWrapEnabled = !IsWordWrapEnabled;

    /// <summary>Open a file into this VM's document and refresh the status segments (FR-3).</summary>
    public async Task OpenAsync(string path, CancellationToken ct)
    {
        EditorDocument opened = await _fileService.OpenAsync(path, ct).ConfigureAwait(true);
        (string? scope, string label) = _languageCatalog.Resolve(opened.Path);
        opened.GrammarScope = scope;
        opened.LanguageLabel = label;
        CopyInto(Document, opened);
        IsDirty = false;
        SyncSegmentsFromDocument();
    }

    /// <summary>Save the document through the atomic store and clear the dirty flag (FR-4 / NFR-4).</summary>
    public async Task SaveAsync(CancellationToken ct)
    {
        await _fileService.SaveAsync(Document, ct).ConfigureAwait(true);
        IsDirty = false;
    }

    /// <summary>
    /// PANEL-4 (feature-ai-multidoc-panel FR-MD-7): the AI multi-doc auto-flush of this document before a
    /// turn. Saves the current buffer through the SAME durable path as <see cref="SaveAsync"/>, then seals an
    /// editor undo point so the pre-flush content is recoverable via Ctrl+Z — independently of the AI undo
    /// journal (FR-MD-7). The undo point is created by raising <see cref="FlushUndoPointRequested"/>, which the
    /// <c>EditorView</c> routes to the AvaloniaEdit undo stack (an empty undo group closes any in-progress
    /// typing group so the pre-flush state is a discrete undoable boundary). In the editor-only test path (no
    /// view attached) the event has no subscriber and the undo point is asserted via the raised event.
    /// </summary>
    public async Task FlushForAiTurnAsync(CancellationToken ct)
    {
        await SaveAsync(ct).ConfigureAwait(true);
        FlushUndoPointRequested?.Invoke();
    }

    /// <summary>
    /// PANEL-4 (FR-MD-7): raised after a pre-turn AI flush saves the buffer. The <c>EditorView</c> seals an
    /// undo boundary on the AvaloniaEdit undo stack so Ctrl+Z restores the pre-flush content after the AI
    /// reloads the file. No subscriber in the editor-only test path (a no-op there).
    /// </summary>
    internal event Action? FlushUndoPointRequested;

    /// <summary>
    /// QA3-9: refresh this document from a fresh on-disk read after the AI applied a write to its file
    /// OUTSIDE this editor (the M11 live-write / FR-39 replay path). Re-reads the current path under the
    /// SAME encoding (reuses <see cref="EditorFileService.ReinterpretAsync"/>'s decode, without changing
    /// the declared encoding) and, only if the decoded text actually differs from the buffer, applies it and
    /// raises <see cref="ExternalWriteApplied"/> — a DISTINCT signal from <see cref="BufferMutationRequested"/>
    /// because the view preserves the caret's character offset (clamped) for this signal instead of resetting
    /// it to 0 (QA3-9 "preserve cursor position where possible" — an AI's typically-small out-of-band edit
    /// warrants different caret handling than a find/replace whole-buffer swap). Marks the buffer NOT dirty
    /// afterward — it now matches disk exactly, so a subsequent Save can never silently clobber the AI's edit
    /// with stale bytes (the core QA3-9 safety concern); a Ctrl+Z (QA3-20) re-diverges it if the user chooses
    /// to revert. No-ops when there is no on-disk path (a scratch buffer is never an AI file-op target) or the
    /// decoded text is unchanged (nothing to refresh).
    /// </summary>
    public async Task RefreshFromDiskAsync(CancellationToken ct)
    {
        if (Document.Path is null)
            return;

        string priorText = Document.Text;
        await _fileService.ReinterpretAsync(Document, Document.EncodingName, ct).ConfigureAwait(true);
        if (string.Equals(Document.Text, priorText, StringComparison.Ordinal))
            return; // disk already matched the buffer — nothing to refresh.

        IsDirty = false;
        LineEndingLabel = LineEndings.DisplayLabel(Document.LineEnding);
        ExternalWriteApplied?.Invoke();
    }

    /// <summary>
    /// Raised by <see cref="RefreshFromDiskAsync"/> when the AI's on-disk edit differs from the buffer. The
    /// bound EditorView applies the new <see cref="Document"/> text via the SAME undo-preserving
    /// <c>TextDocument.Replace</c> mechanism <see cref="BufferMutationRequested"/> uses (Ctrl+Z reverts it,
    /// QA3-20) but PRESERVES the caret's character offset (clamped to the new length) instead of resetting it
    /// to 0, so the user's editing position survives a typically-small AI edit elsewhere in the document.
    /// </summary>
    public event Action? ExternalWriteApplied;

    /// <summary>
    /// Re-decode the on-disk bytes under a user-chosen encoding (FR-6 reinterpret, design §3.4) and
    /// refresh the segments. The view re-reads <see cref="EditorDocument.Text"/> after this completes.
    /// </summary>
    [RelayCommand]
    private async Task ReinterpretAsync(string encodingName)
    {
        await _fileService.ReinterpretAsync(Document, encodingName, CancellationToken.None).ConfigureAwait(true);
        SyncSegmentsFromDocument();
    }

    /// <summary>
    /// QA2-13: manually set the active document's language from the Language menu — a pure grammar-scope
    /// override, no file I/O and no buffer mutation (unlike <see cref="ReinterpretAsync"/>, which re-reads
    /// bytes off disk). <paramref name="label"/> is one of <see cref="LanguageCatalog.AvailableLanguages"/>
    /// ("Plain Text" clears the scope); the matching TextMate scope is looked up from that same canonical
    /// table so the menu and the resolved grammar never drift. Raises <see cref="GrammarChangeRequested"/> —
    /// NOT a text mutation, so the bound EditorView re-applies the grammar in place without ever touching
    /// the editor's text or undo stack (QA3-5 gap 1: a language-menu switch must never wipe undo history).
    /// </summary>
    [RelayCommand]
    private void SetLanguage(string label)
    {
        string? scope = null;
        foreach ((string entryLabel, string? entryScope) in LanguageCatalog.AvailableLanguages)
        {
            if (string.Equals(entryLabel, label, StringComparison.Ordinal))
            {
                scope = entryScope;
                break;
            }
        }

        Document.GrammarScope = scope;
        Document.LanguageLabel = label;
        SyncSegmentsFromDocument();
        GrammarChangeRequested?.Invoke();
    }

    /// <summary>
    /// Raised when the VM changes ONLY the grammar scope (QA2-13 language-menu override) — <see cref="EditorDocument.Text"/>
    /// is unchanged. The bound EditorView re-applies the TextMate grammar in place and never touches the
    /// editor's text or undo stack (a pure grammar swap is not a content mutation and must not be an
    /// undo-breaking event, QA3-5 gap 1).
    /// </summary>
    public event System.Action? GrammarChangeRequested;

    /// <summary>
    /// Raised when the VM has replaced <see cref="EditorDocument.Text"/> via an IN-SESSION content mutation
    /// that must remain undoable (a find/replace current-file replace, M6.7/QA3-5) — as opposed to a brand
    /// new document load (tab switch / open / reinterpret), which clears undo history because a Ctrl+Z must
    /// never bleed a previous document's content into the one just opened. The bound EditorView applies the
    /// new text via the AvaloniaEdit <c>TextDocument.Replace</c> overload (one undo entry, undo stack
    /// preserved) rather than the destructive whole-buffer <c>TextEditor.Text</c> setter that the
    /// new-document-load path correctly uses.
    /// </summary>
    public event System.Action? BufferMutationRequested;

    /// <summary>
    /// Replace the buffer text from a non-editor, in-session mutation (M6.7 find/replace current-file /
    /// replace-all) and push the change into the live editor control THROUGH THE UNDO STACK (QA3-5 —
    /// previously this raised the same signal a new-document-load uses, which wiped the AvaloniaEdit undo
    /// stack and made a replace unrecoverable). Marks the buffer dirty (it now diverges from disk) and
    /// refreshes the EOL segment. No-ops when the text is unchanged so a zero-replacement run leaves the
    /// buffer pristine.
    /// </summary>
    public void ReplaceBufferText(string text)
    {
        if (string.Equals(Document.Text, text, System.StringComparison.Ordinal))
            return;
        Document.Text = text;
        IsDirty = true;
        Document.LineEnding = LineEndings.Detect(Document.Text);
        LineEndingLabel = LineEndings.DisplayLabel(Document.LineEnding);
        BufferMutationRequested?.Invoke();
    }

    /// <summary>Update the cursor segment (FR-6) from the editor's 1-based caret position.</summary>
    public void UpdateCursor(int line, int column) => CursorLabel = $"Ln {line}, Col {column}";

    /// <summary>Refresh the language/encoding/EOL/name segments from the current document state.</summary>
    public void SyncSegmentsFromDocument()
    {
        DocumentName = Document.DisplayName;
        LanguageMode = Document.LanguageLabel;
        EncodingLabel = TextEncodings.DisplayLabel(Document.EncodingName);
        LineEndingLabel = LineEndings.DisplayLabel(Document.LineEnding);

        // The active document's language may have changed (open / reinterpret): re-broadcast IsMarkdown so
        // the FR-15 toggle enables/disables, and snap the preview off if the new document is not Markdown
        // (a non-Markdown doc must never be left showing a stale preview).
        if (!IsMarkdown)
            IsPreviewVisible = false;
        OnPropertyChanged(nameof(IsMarkdown));
        ToggleMarkdownPreviewCommand.NotifyCanExecuteChanged();
    }

    // ----- Edit menu operations (BUG-9): undo, redo, cut, copy, paste. -----
    //
    // Each raises an event that the EditorView subscribes to and routes to the TextEditor's built-in
    // implementation. When no EditorView is attached (editor-only test path), the events have zero
    // subscribers and are no-ops (handlers null).

    /// <summary>Raised when the Edit ▸ Undo command executes. The view calls <c>TextEditor.Undo()</c>.</summary>
    internal event Action? UndoRequested;

    /// <summary>Raised when the Edit ▸ Redo command executes. The view calls <c>TextEditor.Redo()</c>.</summary>
    internal event Action? RedoRequested;

    /// <summary>Raised when the Edit ▸ Cut command executes. The view calls <c>TextEditor.Cut()</c>.</summary>
    internal event Action? CutRequested;

    /// <summary>Raised when the Edit ▸ Copy command executes. The view calls <c>TextEditor.Copy()</c>.</summary>
    internal event Action? CopyRequested;

    /// <summary>Raised when the Edit ▸ Paste command executes. The view calls <c>TextEditor.Paste()</c>.</summary>
    internal event Action? PasteRequested;

    [RelayCommand]
    private void Undo() => UndoRequested?.Invoke();

    [RelayCommand]
    private void Redo() => RedoRequested?.Invoke();

    [RelayCommand]
    private void Cut() => CutRequested?.Invoke();

    [RelayCommand]
    private void Copy() => CopyRequested?.Invoke();

    [RelayCommand]
    private void Paste() => PasteRequested?.Invoke();

    private static void CopyInto(EditorDocument target, EditorDocument source)
    {
        target.Path = source.Path;
        target.Text = source.Text;
        target.EncodingName = source.EncodingName;
        target.HasBom = source.HasBom;
        target.LineEnding = source.LineEnding;
        target.GrammarScope = source.GrammarScope;
        target.LanguageLabel = source.LanguageLabel;
    }
}
