// MainWindowViewModel.cs — the shell view-model (CommunityToolkit.Mvvm).
//
// Backs the main Window's six stacked regions (design §2.1): the window title (with dirty marker),
// the status-bar segments, and the View-menu toggle flags for the toolbar / folder-tree / AI-panel
// regions. M6.2 wires the live editor surface: the shell owns the EditorViewModel (the active
// document) and MIRRORS its status segments (language / encoding / EOL / cursor / dirty / name) into
// its own bound properties, so the M6.1 status-bar bindings + the FR-5 title stay intact while the
// values now track the real editor. M6.5 adds the Checklist menu seam (the _Checklist top menu +
// per-row context menu): the shell holds a nullable ChecklistViewModel reference and the focused-row
// id. These are populated by M6.3 (session/document model), which wires the content row to switch
// between EditorView and ChecklistView depending on document type. Until M6.3 the Checklist menu items
// are correctly disabled (no active checklist); the per-row context menu in ChecklistView is the
// working in-control affordance meanwhile. The remaining feature VMs (AI panel / settings) follow
// in later milestones.

using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HoardPad.App.Session;
using HoardPad.App.Settings;

namespace HoardPad.App.ViewModels;

/// <summary>
/// The shell view-model (architecture §1.4, design §2). Exposes the window title, the status-bar
/// segments, and the region-visibility toggles the View menu / toolbar drive. M6.1 scaffolding:
/// the bound values are placeholders for the default-window (no folder, one scratch buffer) state
/// (design §2.2); feature view-models replace the placeholder content in later milestones.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    /// <summary>
    /// The active document's editor view-model (M6.2). The editor region binds its DataContext to this; the
    /// shell mirrors its status segments into the bound properties below. M6.3 makes it settable so a tab
    /// switch repoints it to the newly-active text document (and re-subscribes its PropertyChanged). Internal
    /// — compiled XAML in this assembly binds it; external callers do not service-locate it (architecture §8.1).
    /// </summary>
    private EditorViewModel? _editor;

    internal EditorViewModel? Editor
    {
        get => _editor;
        private set
        {
            if (ReferenceEquals(_editor, value))
                return;

            // Carry-forward B: unsubscribe the OLD editor's PropertyChanged BEFORE the swap so a stale
            // editor cannot keep mirroring into the shell (no leak, no stale status segments).
            if (_editor is not null)
                _editor.PropertyChanged -= OnEditorPropertyChanged;

            _editor = value;

            if (_editor is not null)
                _editor.PropertyChanged += OnEditorPropertyChanged;

            OnPropertyChanged(nameof(Editor));
            OnPropertyChanged(nameof(UndoCommand));
            OnPropertyChanged(nameof(RedoCommand));
            OnPropertyChanged(nameof(CutCommand));
            OnPropertyChanged(nameof(CopyCommand));
            OnPropertyChanged(nameof(PasteCommand));
            OnPropertyChanged(nameof(ReinterpretCommand));
            OnPropertyChanged(nameof(SetLanguageCommand));
            OnPropertyChanged(nameof(ToggleMarkdownPreviewCommand));
            OnPropertyChanged(nameof(IsMarkdown));
            OnPropertyChanged(nameof(ToggleWordWrapCommand));
            OnPropertyChanged(nameof(IsWordWrapEnabled));
            MirrorEditorSegments();
        }
    }

    /// <summary>
    /// The open document tabs (FR-1). Each hosts its surface VM (EditorViewModel or ChecklistViewModel).
    /// M6.3 — the content row binds to the active tab's surface; the tab strip lists these. Internal
    /// because <see cref="DocumentTab"/> is internal (compiled XAML in this assembly binds it).
    /// </summary>
    internal System.Collections.ObjectModel.ObservableCollection<DocumentTab> Tabs { get; } = [];

    /// <summary>
    /// The folder-tree pane's root row (QA3-2, design §9.5). At most one element: the root node for the
    /// currently-open folder, labeled with its leaf name (QA4-6 — see
    /// <c>WorkspaceService.RootFolderDisplayName</c>), or empty when no folder is open. <see cref="Session.WorkspaceService"/>
    /// populates this off the UI thread (NFR-1) after <c>OpenFolderAsync</c> / <c>RestoreAsync</c>.
    /// Internal because <see cref="FolderTreeNode"/> is internal (compiled XAML in this assembly binds it).
    /// </summary>
    internal System.Collections.ObjectModel.ObservableCollection<FolderTreeNode> FolderTreeRoots { get; } = [];

    /// <summary>Replace the folder-tree root (QA3-2). <c>null</c> clears the tree (no folder open).</summary>
    internal void SetFolderTree(FolderTreeNode? root)
    {
        FolderTreeRoots.Clear();
        if (root is not null)
            FolderTreeRoots.Add(root);
    }

    /// <summary>
    /// The FR-22 AI panel view-model (M6.6). The content row's right column binds its DataContext to this;
    /// the View ▸ Toggle AI Panel command shows/hides the overlay. Null for the design-time / M6.1
    /// scaffolding default; the composition root supplies the live panel VM.
    /// </summary>
    public AiPanelViewModel? AiPanel { get; }

    /// <summary>
    /// The FR-17/FR-18 docked find/replace panel VM (M6.7). The editor area docks a FindReplaceView bound
    /// to this; Ctrl+F / Ctrl+H summon it (Esc closes). Null for the design-time / editor-only test path;
    /// the composition root supplies the live panel VM (over the shell tab set + the open-folder root).
    /// Internal because <see cref="FindReplaceViewModel"/> is internal (compiled XAML in this assembly binds it).
    /// </summary>
    internal FindReplaceViewModel? FindReplace { get; private set; }

    /// <summary>Parameterless ctor for the design-time / M6.1 scaffolding default (no live editor).</summary>
    public MainWindowViewModel()
    {
    }

    /// <summary>
    /// The composition-root ctor: takes the live editor + AI-panel VMs and mirrors editor status. The AI
    /// panel is optional (<c>null</c> in the editor-only test path); the composition root always supplies it.
    /// </summary>
    internal MainWindowViewModel(EditorViewModel editor, AiPanelViewModel? aiPanel = null)
    {
        AiPanel = aiPanel;
        Editor = editor; // the settable Editor subscribes PropertyChanged + mirrors segments.
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e) => MirrorEditorSegments();

    private void MirrorEditorSegments()
    {
        if (Editor is not { } editor)
            return;
        DocumentName = editor.DocumentName;
        IsDirty = editor.IsDirty;
        LanguageMode = editor.LanguageMode;
        EncodingLabel = editor.EncodingLabel;
        LineEndingLabel = editor.LineEndingLabel;
        CursorLabel = editor.CursorLabel;
    }

    // ----- BUG-9: Forwarding command properties from the active EditorViewModel. -----
    //
    // Compiled bindings under AvaloniaUseCompiledBindingsByDefault=true cannot traverse an internal-typed
    // intermediate property (Editor is internal EditorViewModel?). Exposing flat public forwarding properties
    // eliminates the two-segment Editor.XxxCommand paths and makes all compiled binding paths single-segment.
    // The Editor setter fires OnPropertyChanged for each so bindings stay live on tab switches.

    /// <summary>Edit ▸ Undo — forwarded from the active editor VM (BUG-9).</summary>
    public ICommand? UndoCommand => Editor?.UndoCommand;

    /// <summary>Edit ▸ Redo — forwarded from the active editor VM (BUG-9).</summary>
    public ICommand? RedoCommand => Editor?.RedoCommand;

    /// <summary>Edit ▸ Cut — forwarded from the active editor VM (BUG-9).</summary>
    public ICommand? CutCommand => Editor?.CutCommand;

    /// <summary>Edit ▸ Copy — forwarded from the active editor VM (BUG-9).</summary>
    public ICommand? CopyCommand => Editor?.CopyCommand;

    /// <summary>Edit ▸ Paste — forwarded from the active editor VM (BUG-9).</summary>
    public ICommand? PasteCommand => Editor?.PasteCommand;

    /// <summary>Status-bar Reinterpret encoding — forwarded from the active editor VM (BUG-9).</summary>
    public ICommand? ReinterpretCommand => Editor?.ReinterpretCommand;

    /// <summary>Language menu — set the active document's language (QA2-13), forwarded from the active editor VM.</summary>
    public ICommand? SetLanguageCommand => Editor?.SetLanguageCommand;

    /// <summary>View ▸ Toggle Markdown Preview / toolbar MD Preview button — forwarded from the active editor VM (BUG-9).</summary>
    public ICommand? ToggleMarkdownPreviewCommand => Editor?.ToggleMarkdownPreviewCommand;

    /// <summary>Whether the active document is Markdown — forwarded for the toolbar IsVisible binding (BUG-9).</summary>
    public bool IsMarkdown => Editor?.IsMarkdown ?? false;

    /// <summary>View ▸ Word Wrap — forwarded from the active editor VM (FEAT-6, per-tab scope).</summary>
    public ICommand? ToggleWordWrapCommand => Editor?.ToggleWordWrapCommand;

    /// <summary>Whether Word Wrap is on for the active tab — forwarded for the View-menu checkmark (FEAT-6).</summary>
    public bool IsWordWrapEnabled => Editor?.IsWordWrapEnabled ?? false;

    // ----- QA4-14 / W1: focus-scoped Cut/Copy/Paste for the WINDOW-LEVEL keyboard accelerators only. -----
    //
    // Ctrl+X/C/V (FEAT-3) forward unconditionally to CutCommand/CopyCommand/PasteCommand above, so they
    // always won the KeyBindings ancestor-walk (Avalonia.Input.KeyboardDevice.ProcessRawEvent) even when a
    // plain TextBox elsewhere in the shell had focus (the AI panel prompt, a Find/Replace box, a checklist
    // rename/add-item box, a Settings field, …) — QA4-14 fixed this locally for the AI panel's own PromptBox
    // (a local KeyBinding there wins the walk first); this generalizes it centrally so every OTHER such
    // TextBox is covered too, without a per-control retrofit. The Edit MENU still binds to the plain
    // CutCommand/CopyCommand/PasteCommand above (a menu click is a deliberate "act on the editor" action,
    // unaffected). Window.KeyBindings binds to THESE instead. IsNonEditorTextInputFocused is supplied by the
    // View (only it can query Avalonia's live FocusManager); CanExecute is evaluated FRESH at every keypress
    // by KeyBinding.TryHandle (it never subscribes to CanExecuteChanged), so one cached wrapper instance per
    // command is enough — no rebuild needed on tab switch or focus change.

    /// <summary>
    /// QA4-14/W1: supplied by the View (MainWindow.axaml.cs) — <c>true</c> when the currently focused
    /// control is a text-input control that is NOT the document editor. Null in the design-time/editor-only
    /// test path, where the window-level accelerators behave exactly as before (always forward to the editor).
    /// </summary>
    public Func<bool>? IsNonEditorTextInputFocused { get; set; }

    private ICommand? _windowLevelCutCommand;
    private ICommand? _windowLevelCopyCommand;
    private ICommand? _windowLevelPasteCommand;

    /// <summary>Ctrl+X window-level accelerator (QA4-14/W1) — a no-op while a non-editor text input has focus.</summary>
    public ICommand WindowLevelCutCommand => _windowLevelCutCommand ??= new RelayCommand(
        () => CutCommand?.Execute(null),
        () => CutCommand?.CanExecute(null) == true && IsNonEditorTextInputFocused?.Invoke() != true);

    /// <summary>Ctrl+C window-level accelerator (QA4-14/W1) — a no-op while a non-editor text input has focus.</summary>
    public ICommand WindowLevelCopyCommand => _windowLevelCopyCommand ??= new RelayCommand(
        () => CopyCommand?.Execute(null),
        () => CopyCommand?.CanExecute(null) == true && IsNonEditorTextInputFocused?.Invoke() != true);

    /// <summary>Ctrl+V window-level accelerator (QA4-14/W1) — a no-op while a non-editor text input has focus.</summary>
    public ICommand WindowLevelPasteCommand => _windowLevelPasteCommand ??= new RelayCommand(
        () => PasteCommand?.Execute(null),
        () => PasteCommand?.CanExecute(null) == true && IsNonEditorTextInputFocused?.Invoke() != true);

    /// <summary>
    /// Make <paramref name="tab"/> the active document (M6.3 — carry-forward A). Switches the content row's
    /// hosted surface: a text tab repoints <see cref="Editor"/> (re-subscribing its PropertyChanged, no
    /// leak — carry-forward B); a <c>.hoard</c> tab populates <see cref="ActiveChecklist"/> so the top
    /// _Checklist menu enables/routes. <see cref="IsChecklistActive"/> flips the content row's visibility.
    /// </summary>
    internal void ActivateTab(DocumentTab? tab)
    {
        // M8.1: keep IsActive consistent so the tab chip's background binding reflects the active accent.
        // Clear the previous active tab first (exactly one tab active at a time).
        if (_activeTab is not null)
            _activeTab.IsActive = false;

        ActiveTab = tab;
        SaveAsCommand.NotifyCanExecuteChanged(); // FEAT-2: CanExecute depends on the active tab's kind.

        // QA3-15: re-point (or clear) Editor for EVERY activation, not just the text-tab branch below. The
        // Editor setter no-ops on a same-reference assignment, so this is safe to call unconditionally; it
        // is what re-fires IsMarkdown when the newly-active tab is a checklist, the settings tab, or (on
        // closing the last tab) null — otherwise the MD-Preview toggle kept mirroring the PREVIOUS text
        // tab's IsMarkdown, staying visible after switching away from a Markdown document.
        Editor = tab?.Editor;

        if (tab is not null)
            tab.IsActive = true;

        if (tab is { IsSettings: true, Settings: { } settings })
        {
            // The settings tab (ODQ-12): show the settings surface, deactivate the document surfaces.
            ActiveChecklist = null;
            FocusedChecklistItemId = null;
            IsChecklistActive = false;
            ActiveSettings = settings;
            IsSettingsActive = true;
            DocumentName = settings.DisplayName;
            IsDirty = false; // the settings tab carries no document buffer.
            LanguageMode = string.Empty; // QA3-4 Fix 2: no editor active -> no Language menu item checked.
            return;
        }

        ActiveSettings = null;
        IsSettingsActive = false;

        if (tab is { IsChecklist: true, Checklist: { } checklist })
        {
            ActiveChecklist = checklist;
            IsChecklistActive = true;
            DocumentName = checklist.DisplayTitle;
            IsDirty = false; // .hoard saves are zero-debounce; the buffer is never "unsaved" in the FR-5 sense.
            LanguageMode = string.Empty; // QA3-4 Fix 2: a checklist has no editor language; no checkmark shows.
        }
        else
        {
            ActiveChecklist = null;
            FocusedChecklistItemId = null;
            IsChecklistActive = false;
            // Editor is already re-pointed above (QA3-15) — nothing further to do for a text-document tab.
        }

        // Re-point the live find/replace count at the newly-active buffer (M6.7 — the count is per
        // current-file scope, so a tab switch recomputes "N of M"). No-op when the panel is not attached.
        FindReplace?.RecomputeMatches();
    }

    /// <summary>
    /// Reorder a tab within the strip (M8.1 / FR-1, cut-tail #1). Moves <paramref name="dragged"/> to
    /// <paramref name="targetIndex"/> in <see cref="Tabs"/> via <see cref="System.Collections.ObjectModel.ObservableCollection{T}.Move"/>
    /// so the tab INSTANCE (and the active selection, which references that instance) survives the move —
    /// reorder preserves identity, not index. A drop onto the tab's own slot is a no-op (zero collection
    /// change), mirroring the checklist control's same-position-drop guard (<see cref="ChecklistViewModel.DropWithinGroup"/>).
    /// Keyboard tab-switch (M6.1) is unaffected — this only changes order, never which tab is active.
    /// </summary>
    internal void MoveTab(DocumentTab dragged, int targetIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(targetIndex);

        int from = Tabs.IndexOf(dragged);
        if (from < 0)
            return; // not in the strip — nothing to move.

        // Clamp the drop-at-end sentinel (`count`) down to the last valid index, then no-op a same-slot drop
        // (ZERO collection change, C-4 mirror). `ComputeDropIndex` returns `count` when the pointer lands past
        // the last tab; that is the "append at end" intent, which clamps to `Tabs.Count - 1`.
        int to = targetIndex >= Tabs.Count ? Tabs.Count - 1 : targetIndex;
        if (to == from)
            return;

        Tabs.Move(from, to); // identity-preserving — ActiveTab still references the same instance.
    }

    /// <summary>The active document's display name (design §2.2 — "untitled" for a scratch buffer).</summary>
    [ObservableProperty]
    private string _documentName = "untitled";

    /// <summary>Whether the active document has unsaved edits (FR-5 dirty marker).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(DirtyMarker))]
    [NotifyPropertyChangedFor(nameof(DirtyStatusLabel))]
    private bool _isDirty;

    /// <summary>Toolbar row visibility — View ▸ Toolbar (design §2.1, default shown).</summary>
    [ObservableProperty]
    private bool _isToolbarVisible = true;

    /// <summary>Folder-tree pane visibility — View ▸ Folder Tree (design §2.4, hidden until a folder opens).</summary>
    [ObservableProperty]
    private bool _isFolderTreeVisible;

    /// <summary>AI panel overlay visibility — the FR-22 toggle (design §2.4, default hidden).</summary>
    [ObservableProperty]
    private bool _isAiPanelVisible;

    /// <summary>
    /// Whether the active document is a <c>.hoard</c> checklist (M6.3 — carry-forward A). Drives the
    /// content row: the ChecklistView is shown and the EditorView hidden when true, and vice-versa.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditorActive))]
    private bool _isChecklistActive;

    /// <summary>
    /// Whether the active tab is the settings surface (M6.8, ODQ-12). Drives the content row: the
    /// SettingsView is shown and the editor/checklist hidden when true.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditorActive))]
    private bool _isSettingsActive;

    /// <summary>Whether the active surface is the text editor (neither checklist nor settings active).</summary>
    public bool IsEditorActive => !IsChecklistActive && !IsSettingsActive;

    /// <summary>The currently-active settings VM (the settings tab, ODQ-12), or null when no settings tab is active.</summary>
    private SettingsViewModel? _activeSettings;

    internal SettingsViewModel? ActiveSettings
    {
        get => _activeSettings;
        private set => SetProperty(ref _activeSettings, value);
    }

    /// <summary>The currently-active document tab (M6.3). Null in the design-time / no-document default.</summary>
    private DocumentTab? _activeTab;

    internal DocumentTab? ActiveTab
    {
        get => _activeTab;
        private set => SetProperty(ref _activeTab, value);
    }

    // ----- Status-bar segments (design §2.2 / §3.4). Placeholders for the scratch-buffer default. -----

    /// <summary>The language-mode segment (FR-8). Defaults to Plain Text for a new scratch buffer.</summary>
    [ObservableProperty]
    private string _languageMode = "Plain Text";

    /// <summary>The encoding segment (FR-6). UTF-8 is the default for new buffers (NFR-9).</summary>
    [ObservableProperty]
    private string _encodingLabel = "UTF-8";

    /// <summary>The line-ending segment (FR-6). LF default.</summary>
    [ObservableProperty]
    private string _lineEndingLabel = "LF";

    /// <summary>The cursor position segment (FR-6).</summary>
    [ObservableProperty]
    private string _cursorLabel = "Ln 1, Col 1";

    /// <summary>The window title: app name + active document + dirty marker (design §2.2).</summary>
    public string Title => $"HoardPad — {DocumentName}{DirtyMarker}";

    /// <summary>The "•" dirty marker shown after the document name when there are unsaved edits (FR-5).</summary>
    public string DirtyMarker => IsDirty ? " •" : string.Empty;

    /// <summary>The status-bar save/dirty segment label (FR-5, design §2.2) — surfaces dirty state outside the title.</summary>
    public string DirtyStatusLabel => IsDirty ? "● Unsaved" : "✓ Saved";

    [RelayCommand]
    private void ToggleToolbar() => IsToolbarVisible = !IsToolbarVisible;

    [RelayCommand]
    private void ToggleFolderTree() => IsFolderTreeVisible = !IsFolderTreeVisible;

    [RelayCommand]
    private void ToggleAiPanel() => IsAiPanelVisible = !IsAiPanelVisible;

    /// <summary>
    /// The status-bar theme segment (FR-37). Reflects the active theme ("☾ Dark" / "☀ Light"); set by the
    /// theme coordinator on startup + each toggle. Replaces the M6.1 hardcoded "☾ Dark" placeholder.
    /// </summary>
    [ObservableProperty]
    private string _themeStatusLabel = "☾ Dark";

    /// <summary>
    /// The View ▸ Toggle Theme handler (M7.1, FR-37). The theme coordinator (App-level) owns the live
    /// ThemeService + the settings persistence, so the shell VM stays decoupled from them: the composition
    /// root wires this to flip dark↔light, call <see cref="Theme.ThemeService.Switch(Settings.AppTheme)"/>,
    /// and persist the choice. Null in the design-time / editor-only test path (the command is then a no-op).
    /// </summary>
    internal Func<Task>? ToggleThemeHandler { get; set; }

    /// <summary>Toggle the FR-37 dark↔light theme (View ▸ Toggle Theme — the one lockstep switch).</summary>
    [RelayCommand]
    private async Task ToggleThemeAsync()
    {
        if (ToggleThemeHandler is { } handler)
            await handler().ConfigureAwait(true);
    }

    /// <summary>
    /// Wire the live find/replace panel VM (M6.7). Set by the composition root AFTER the shell is built (the
    /// panel's workspace seam needs the shell's tab set + the open-folder root, which exist only once the
    /// shell + workspace are constructed) — the same post-construction wiring as <see cref="OpenSettingsHandler"/>.
    /// </summary>
    internal void AttachFindReplace(FindReplaceViewModel findReplace)
    {
        FindReplace = findReplace;
        // Notify the DataContext="{Binding FindReplace}" binding on FindReplaceView in MainWindow.axaml
        // so it re-evaluates and picks up the non-null ViewModel. Without this, the DataContext stays null
        // and the internal IsVisible="{Binding IsVisible}" binding on FindReplaceView.axaml cannot resolve,
        // causing the panel to be visible at startup (default) instead of hidden (BUG-1).
        OnPropertyChanged(nameof(FindReplace));
    }

    /// <summary>Summon the find/replace panel focused on Find (Ctrl+F / Search ▸ Find — design §3.5).</summary>
    [RelayCommand]
    private void ShowFind()
    {
        if (FindReplace is { } fr)
        {
            fr.RefreshInFolderAvailability(); // a folder may have opened since the last summon.
            fr.IsVisible = true;
        }
    }

    /// <summary>Summon the find/replace panel for Replace (Ctrl+H / Search ▸ Replace — design §3.5).</summary>
    [RelayCommand]
    private void ShowReplace() => ShowFind(); // the same docked panel; both rows are always present.

    /// <summary>
    /// The Settings ▸ Preferences… handler (M6.8, ODQ-12). The WorkspaceService owns tab creation, so the
    /// shell VM stays decoupled from it: the composition root wires this to
    /// <see cref="Session.WorkspaceService.OpenSettingsAsync"/>. Null in the design-time / editor-only test
    /// path (the command is then a no-op).
    /// </summary>
    internal Func<Task>? OpenSettingsHandler { get; set; }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        if (OpenSettingsHandler is { } handler)
            await handler().ConfigureAwait(true);
    }

    /// <summary>
    /// The Help ▸ About HoardPad handler (QA2-14). A real modal dialog needs the owning Window, so
    /// (like <see cref="ConfirmCloseTab"/>) this is wired from MainWindow.axaml.cs's
    /// OnDataContextChanged rather than the composition root. Null in the design-time / test path
    /// (the command is then a no-op).
    /// </summary>
    internal Func<Task>? ShowHelpHandler { get; set; }

    [RelayCommand]
    private async Task ShowHelp()
    {
        if (ShowHelpHandler is { } handler)
            await handler().ConfigureAwait(true);
    }

    // ----- File operations (BUG-7: toolbar New/Open/Save) — wired by the composition root. -----

    /// <summary>
    /// The File ▸ New handler. The composition root wires this to
    /// <see cref="Session.WorkspaceService.NewDocumentAsync"/>.
    /// Null in the design-time / editor-only test path (the command is then a no-op).
    /// </summary>
    internal Func<Task>? NewDocumentHandler { get; set; }

    /// <summary>
    /// QA4-7: the FR-8 persisted default new-document language (a
    /// <see cref="HoardPad.App.Editor.LanguageCatalog.AvailableLanguages"/> canonical label, e.g. "Markdown")
    /// — closes the FR-35 <c>DefaultLanguage</c> inert-consumer follow-up logged against QA3-1. The
    /// composition root seeds this from the loaded settings at startup and keeps it live via
    /// <c>SettingsStore.SettingsChanged</c>, so an in-session Settings change takes effect on the very next
    /// File ▸ New without a relaunch. <see cref="NewDocument"/> applies it through the SAME
    /// <see cref="EditorViewModel.SetLanguageCommand"/> path QA2-13's manual Language-menu pick uses, so the
    /// grammar, the status-bar label, and the QA3-4 Language-menu checkmark all update together.
    /// <see cref="HoardPadSettings.DefaultLanguageLabel"/> (the default) is a harmless no-op — a fresh
    /// scratch buffer already starts Plain Text.
    /// </summary>
    internal string DefaultLanguage { get; set; } = HoardPadSettings.DefaultLanguageLabel;

    /// <summary>
    /// The File ▸ New Checklist handler (BUG-10). The composition root wires this to
    /// <see cref="Session.WorkspaceService.NewChecklist"/>.
    /// Null in the design-time / editor-only test path (the command is then a no-op).
    /// </summary>
    internal Func<Task>? NewChecklistHandler { get; set; }

    /// <summary>
    /// The File ▸ Open handler. The composition root wires this to
    /// <see cref="Session.WorkspaceService.OpenDocumentAsync"/>.
    /// Null in the design-time / editor-only test path (the command is then a no-op).
    /// </summary>
    internal Func<Task>? OpenDocumentHandler { get; set; }

    /// <summary>
    /// The File ▸ Save handler. The composition root wires this to
    /// <see cref="Session.WorkspaceService.SaveDocumentAsync"/>.
    /// Null in the design-time / editor-only test path (the command is then a no-op).
    /// </summary>
    internal Func<Task>? SaveDocumentHandler { get; set; }

    /// <summary>
    /// STF-5: the toolbar/menu File ▸ Open Folder… handler. The composition root wires this to
    /// <see cref="Session.WorkspaceService.OpenFolderPickerAsync"/>, which shows the OS folder picker and,
    /// on a confirmed pick, sets the AI sandbox root and binds/arms the panel. Null in the design-time /
    /// editor-only test path (the command is then a no-op).
    /// </summary>
    internal Func<Task>? OpenFolderHandler { get; set; }

    [RelayCommand]
    private async Task NewDocument()
    {
        if (NewDocumentHandler is { } handler)
        {
            await handler().ConfigureAwait(true);

            // QA4-7: pre-select the persisted default language on the freshly-created scratch tab. The
            // handler above synchronously creates + activates the new tab (WorkspaceService.NewDocument),
            // so Editor already points at it by the time we get here.
            Editor?.SetLanguageCommand?.Execute(DefaultLanguage);
        }
    }

    [RelayCommand]
    private async Task NewChecklist()
    {
        if (NewChecklistHandler is { } handler)
            await handler().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task OpenDocument()
    {
        if (OpenDocumentHandler is { } handler)
            await handler().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SaveDocument()
    {
        if (SaveDocumentHandler is { } handler)
            await handler().ConfigureAwait(true);
    }

    /// <summary>
    /// The File ▸ Save As… / _Checklist ▸ Save As… handler (QA3-7 Option B). The composition root wires
    /// this to <see cref="Session.WorkspaceService.SaveAsActiveChecklistAsync"/>. Only enabled while a
    /// checklist tab is active (<see cref="HasActiveChecklist"/>) — Save-As currently targets `.hoard`
    /// documents only. Null in the design-time / editor-only test path (the command is then a no-op).
    /// </summary>
    internal Func<Task>? SaveAsChecklistHandler { get; set; }

    [RelayCommand(CanExecute = nameof(HasActiveChecklist))]
    private async Task SaveAsChecklist()
    {
        if (SaveAsChecklistHandler is { } handler)
            await handler().ConfigureAwait(true);
    }

    /// <summary>
    /// FEAT-2: the File ▸ Save As… handler for the active document tab (any kind). The composition root
    /// wires this to <see cref="Session.WorkspaceService.SaveAsActiveDocumentAsync"/>, which routes a
    /// <c>.hoard</c> tab to the QA3-7 checklist relocate and everything else to the new editor relocate.
    /// Enabled while an editor or checklist tab is active (<see cref="HasActiveDocumentTab"/>) — disabled
    /// with no tabs open or while the settings tab is active. Null in the design-time / editor-only test
    /// path (the command is then a no-op).
    /// </summary>
    internal Func<Task>? SaveAsHandler { get; set; }

    private bool HasActiveDocumentTab() => ActiveTab?.Editor is not null || ActiveTab?.Checklist is not null;

    [RelayCommand(CanExecute = nameof(HasActiveDocumentTab))]
    private async Task SaveAs()
    {
        if (SaveAsHandler is { } handler)
            await handler().ConfigureAwait(true);
    }

    /// <summary>
    /// FEAT-7: the tab-strip context-menu Save item. <see cref="Session.WorkspaceService.SaveDocumentAsync"/>
    /// (wired via <see cref="SaveDocumentHandler"/>, BUG-7) operates on <see cref="ActiveTab"/> only, so a
    /// Save from a BACKGROUND tab's context menu activates it first (reusing <see cref="ActivateTab"/>) and
    /// then delegates to the same handler File ▸ Save / the toolbar Save button use. Uses <c>object?</c> for
    /// the same CS0053 accessibility reason as <see cref="CloseTab"/> (internal <see cref="DocumentTab"/>
    /// cannot appear in a public generic <see cref="System.Windows.Input.ICommand"/> parameter type).
    /// </summary>
    [RelayCommand]
    private async Task SaveTab(object? parameter)
    {
        if (parameter is not DocumentTab tab)
            return;
        if (!ReferenceEquals(tab, ActiveTab))
            ActivateTab(tab);
        if (SaveDocumentHandler is { } handler)
            await handler().ConfigureAwait(true);
    }

    /// <summary>
    /// FEAT-7: the tab-strip context-menu Save As… item — the same activate-first pattern as
    /// <see cref="SaveTab"/>, reusing FEAT-2's <see cref="SaveAsHandler"/> (which itself routes a
    /// <c>.hoard</c> tab to the QA3-7 checklist relocate and everything else to the editor relocate).
    /// </summary>
    [RelayCommand]
    private async Task SaveAsTab(object? parameter)
    {
        if (parameter is not DocumentTab tab)
            return;
        if (!ReferenceEquals(tab, ActiveTab))
            ActivateTab(tab);
        if (SaveAsHandler is { } handler)
            await handler().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task OpenFolder()
    {
        if (OpenFolderHandler is { } handler)
            await handler().ConfigureAwait(true);
    }

    /// <summary>
    /// The folder-tree click-to-open handler (QA3-2 — the recommended minimum-viable interaction: a
    /// listed file the user cannot act on would be flagged again next pass). The composition root wires
    /// this to <see cref="Session.WorkspaceService.OpenFolderTreeFileAsync"/>. Null in the design-time /
    /// editor-only test path (the command is then a no-op).
    /// </summary>
    internal Func<string, Task>? OpenFolderTreeFileHandler { get; set; }

    /// <summary>
    /// Double-click a FILE row in the folder tree to open it in a new tab (QA3-2). A directory row is a
    /// no-op here — expand/collapse is handled by the <see cref="FolderTreeNode.IsExpanded"/> binding,
    /// not this command. The "Loading…" placeholder row (<see cref="FolderTreeNode.IsPlaceholder"/>) is
    /// also excluded (QA3-2 REVISE) — it carries an empty <see cref="FolderTreeNode.FullPath"/>, which
    /// would otherwise resolve to the current directory and throw.
    /// </summary>
    [RelayCommand]
    private async Task OpenFolderTreeItem(object? parameter)
    {
        if (parameter is not FolderTreeNode { IsDirectory: false, IsPlaceholder: false } node)
            return;
        if (OpenFolderTreeFileHandler is { } handler)
            await handler(node.FullPath).ConfigureAwait(true);
    }

    /// <summary>
    /// The File ▸ Close handler (BUG-8). Removes the active tab from the Tabs collection and activates a
    /// neighboring tab (or null when the last tab is closed). BUG-3: now checks for unsaved changes and
    /// prompts via <see cref="ConfirmCloseTab"/> before closing.
    /// </summary>
    [RelayCommand]
    private async Task CloseActiveTab()
    {
        if (ActiveTab is null)
            return;
        await CloseTabCoreAsync(ActiveTab).ConfigureAwait(true);
    }

    /// <summary>
    /// Close a specific tab by instance (BUG-3 — the per-tab × button). Uses <c>object?</c> parameter type
    /// so the generated <see cref="IAsyncRelayCommand"/> avoids an accessibility mismatch (internal
    /// <see cref="DocumentTab"/> cannot appear in a public generic type — a CS0053 constraint).
    /// Checks for unsaved changes via <see cref="ConfirmCloseTab"/>; activates a neighbor when closing the
    /// active tab, or clears the editor when closing the last tab.
    /// </summary>
    [RelayCommand]
    private async Task CloseTab(object? parameter)
    {
        if (parameter is not DocumentTab tab)
            return;
        await CloseTabCoreAsync(tab).ConfigureAwait(true);
    }

    /// <summary>Core close logic shared by <see cref="CloseActiveTab"/> and <see cref="CloseTab"/>.</summary>
    private async Task CloseTabCoreAsync(DocumentTab tab)
    {
        if (!Tabs.Contains(tab))
            return;

        // Check unsaved changes — only for text documents (.hoard is auto-saved, see ActivateTab).
        bool isDirty = tab.Editor?.IsDirty ?? false;
        if (isDirty && ConfirmCloseTab is { } confirm)
        {
            bool proceed = await confirm(tab).ConfigureAwait(true);
            if (!proceed)
                return;
        }

        bool wasActive = ReferenceEquals(tab, ActiveTab);
        int idx = Tabs.IndexOf(tab);
        Tabs.Remove(tab);

        if (wasActive && Tabs.Count > 0)
        {
            int next = Math.Min(idx, Tabs.Count - 1);
            ActivateTab(Tabs[next]);
        }
        else if (wasActive)
        {
            ActivateTab(null);
        }
    }

    /// <summary>
    /// Optional confirmation prompt for unsaved-changes (BUG-3). When non-null, invoked before closing a
    /// dirty tab; return <c>true</c> to proceed with the close, <c>false</c> to cancel. The view supplies
    /// a real modal <c>ShowDialog(owner)</c>; null in headless/unit tests means dirty tabs close without
    /// prompting (callers set the delegate to test the confirmation branch).
    /// </summary>
    internal Func<DocumentTab, Task<bool>>? ConfirmCloseTab { get; set; }

    /// <summary>
    /// The File ▸ Exit handler (BUG-8). The composition root wires this to shut down the application.
    /// Null in the design-time / test path (the command is then a no-op).
    /// </summary>
    internal Func<Task>? ExitHandler { get; set; }

    [RelayCommand]
    private async Task ExitApplication()
    {
        if (ExitHandler is { } handler)
            await handler().ConfigureAwait(true);
    }

    partial void OnDocumentNameChanged(string value) => OnPropertyChanged(nameof(Title));

    // ----- Checklist menu seam (M6.5 / Fix 3: §5.1/§5.2 discoverability, C-2a). -----

    /// <summary>
    /// The active <c>.hoard</c> checklist VM. Null when the active document is not a checklist, which
    /// means the Checklist menu items are disabled (CanExecute = false). <b>Populated by M6.3</b> (the
    /// session/document-model milestone that switches the content row between EditorView and ChecklistView
    /// based on document type). Not yet wired — setting it here is intentionally deferred to M6.3.
    /// Manually managed (not [ObservableProperty]) because ChecklistViewModel is internal — the source
    /// generator would emit a public property, creating an accessibility conflict (CS0053).
    /// </summary>
    private ChecklistViewModel? _activeChecklist;

    internal ChecklistViewModel? ActiveChecklist
    {
        get => _activeChecklist;
        set
        {
            if (SetProperty(ref _activeChecklist, value))
            {
                ChecklistIndentCommand.NotifyCanExecuteChanged();
                ChecklistOutdentCommand.NotifyCanExecuteChanged();
                ChecklistMoveUpCommand.NotifyCanExecuteChanged();
                ChecklistMoveDownCommand.NotifyCanExecuteChanged();
                SaveAsChecklistCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// The id of the currently focused checklist row. <b>Populated by M6.3</b> alongside
    /// <see cref="ActiveChecklist"/>; null until then. The Checklist menu commands guard on both
    /// <c>ActiveChecklist != null</c> and <c>FocusedChecklistItemId != null</c> before routing.
    /// <b>QA4-3 fix 2:</b> the setter re-raises <see cref="CanUseChecklistNavigation"/> on the four
    /// Indent/Outdent/MoveUp/MoveDown commands so a checklist-tab reattach (which resets this to
    /// <c>null</c>, see <see cref="Views.ChecklistView"/>) greys the menu out HONESTLY instead of leaving it
    /// enabled-but-silently-no-op until a row is clicked.
    /// </summary>
    private string? _focusedChecklistItemId;

    internal string? FocusedChecklistItemId
    {
        get => _focusedChecklistItemId;
        set
        {
            if (_focusedChecklistItemId == value)
                return;
            _focusedChecklistItemId = value;
            ChecklistIndentCommand.NotifyCanExecuteChanged();
            ChecklistOutdentCommand.NotifyCanExecuteChanged();
            ChecklistMoveUpCommand.NotifyCanExecuteChanged();
            ChecklistMoveDownCommand.NotifyCanExecuteChanged();
        }
    }

    private bool HasActiveChecklist() => _activeChecklist is not null;

    /// <summary>
    /// CanExecute for Indent/Outdent/MoveUp/MoveDown (QA4-3 fix 2) — an active checklist AND a genuinely
    /// focused row. Deliberately separate from <see cref="HasActiveChecklist"/> (which still gates
    /// <c>SaveAsChecklistCommand</c> alone — Save-As needs no row focus).
    /// </summary>
    private bool CanUseChecklistNavigation() => _activeChecklist is not null && FocusedChecklistItemId is not null;

    [RelayCommand(CanExecute = nameof(CanUseChecklistNavigation))]
    private void ChecklistIndent()
    {
        if (ActiveChecklist is { } vm && FocusedChecklistItemId is { } id)
            vm.Indent(id);
    }

    [RelayCommand(CanExecute = nameof(CanUseChecklistNavigation))]
    private void ChecklistOutdent()
    {
        if (ActiveChecklist is { } vm && FocusedChecklistItemId is { } id)
            vm.Outdent(id);
    }

    [RelayCommand(CanExecute = nameof(CanUseChecklistNavigation))]
    private void ChecklistMoveUp()
    {
        if (ActiveChecklist is { } vm && FocusedChecklistItemId is { } id)
            vm.MoveWithinGroup(id, -1);
    }

    [RelayCommand(CanExecute = nameof(CanUseChecklistNavigation))]
    private void ChecklistMoveDown()
    {
        if (ActiveChecklist is { } vm && FocusedChecklistItemId is { } id)
            vm.MoveWithinGroup(id, +1);
    }

    // ----- Keyboard tab-switch (M8.1 scope-gap-fill): Ctrl+Tab / Ctrl+Shift+Tab cycle through open tabs. -----
    //
    // These are the cut-tail fallback the spec described as "already exists from M6.1" but was never built.
    // Next from the last tab wraps to the first; prev from the first wraps to the last; no-op when 0 or 1 tab.

    /// <summary>Activate the next tab (Ctrl+Tab), wrapping from the last to the first.</summary>
    [RelayCommand]
    private void ActivateNextTab()
    {
        if (Tabs.Count < 2)
            return;
        int next = (_activeTab is not null ? Tabs.IndexOf(_activeTab) : -1) + 1;
        ActivateTab(Tabs[next >= Tabs.Count ? 0 : next]);
    }

    /// <summary>Activate the previous tab (Ctrl+Shift+Tab), wrapping from the first to the last.</summary>
    [RelayCommand]
    private void ActivatePreviousTab()
    {
        if (Tabs.Count < 2)
            return;
        int prev = (_activeTab is not null ? Tabs.IndexOf(_activeTab) : Tabs.Count) - 1;
        ActivateTab(Tabs[prev < 0 ? Tabs.Count - 1 : prev]);
    }
}
