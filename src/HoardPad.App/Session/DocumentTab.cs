// DocumentTab.cs — the document/tab model (carry-forward A: closes the M6.5 + M6.1 deferral).
//
// One open document hosting the RIGHT surface per type:
//   - a TEXT document (file/scratch) → an EditorViewModel (the editor surface binds it);
//   - a `.hoard` document            → a ChecklistViewModel (the ChecklistView binds it);
//   - the SETTINGS surface (M6.8)    → a SettingsViewModel (settings-as-tab, ODQ-12). This is a
//     NON-PERSISTED tab — it is not a document, has no §9.2 snapshot identity, and is excluded from
//     the session index (only editor/checklist tabs are session-restorable). It rides TabKind.Scratch
//     (path-less, like a scratch buffer) but is distinguished by IsSettings so the workspace never
//     persists or snapshots it.
// The shell's content row binds to the ACTIVE tab's surface VM; when a `.hoard` tab is active the shell
// populates ActiveChecklist so the top _Checklist menu commands enable/route (the command logic is
// already built + unit-tested in M6.5 — this is the activation it was waiting on).
//
// The durable tab identity is TabId (the §9.2 join key). Kind is the authoritative TabKind. The tab also
// carries the §9.2 cursor + the recovery-conflict / on-disk-deleted markers the reconcile attached, so
// the workspace can show the restore-conflict dialog and treat a deleted-on-disk file correctly.

using System.ComponentModel;
using HoardPad.App.ViewModels;
using HoardPad.Core.Recovery;

namespace HoardPad.App.Session;

/// <summary>
/// An open document tab (FR-1). Hosts exactly one surface view-model per <see cref="Kind"/>: an
/// <see cref="EditorViewModel"/> for a text buffer, a <see cref="ChecklistViewModel"/> for a
/// <c>.hoard</c>. The active tab's surface is what the shell content row shows.
/// </summary>
internal sealed class DocumentTab : INotifyPropertyChanged
{
    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;
    private DocumentTab(
        string tabId,
        TabKind kind,
        EditorViewModel? editor,
        ChecklistViewModel? checklist,
        SettingsViewModel? settings)
    {
        TabId = tabId;
        Kind = kind;
        Editor = editor;
        Checklist = checklist;
        Settings = settings;

        // QA3-12/QA3-13: DisplayName below is a computed projection over the hosted surface VM's own
        // name property. Without forwarding the underlying change here, PropertyChanged(DisplayName) never
        // fires when that name changes post-construction (Save-As assigning a path, or a checklist rename)
        // — the tab-strip TextBlock's `{Binding DisplayName}` (MainWindow.axaml) never re-evaluates and the
        // chip is left showing the old/placeholder title. Editor and Checklist are exclusively owned by
        // this tab for its whole lifetime (never shared across tabs), so no unsubscribe is needed — both
        // become collectible together once the tab closes and nothing else references either.
        //
        // FEAT-13 reuses the same forwarding mechanism for EditorViewModel.IsDirty: DisplayName also folds
        // in the " *" unsaved-changes suffix (FR-5), so a dirty-flag flip (buffer edit / Save) must re-raise
        // PropertyChanged(DisplayName) exactly like a name change does.
        if (editor is not null)
            editor.PropertyChanged += OnHostedSurfacePropertyChanged;
        if (checklist is not null)
            checklist.PropertyChanged += OnHostedSurfacePropertyChanged;
    }

    private void OnHostedSurfacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorViewModel.DocumentName) ||
            e.PropertyName == nameof(EditorViewModel.IsDirty) ||
            e.PropertyName == nameof(ChecklistViewModel.DisplayTitle))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
        }
    }

    /// <summary>The durable tab id (== the §9.2 snapshot <c>tabId</c> join key).</summary>
    public string TabId { get; }

    /// <summary>The authoritative kind (file / scratch / hoard).</summary>
    public TabKind Kind { get; }

    /// <summary>The text-surface VM (file/scratch tabs), or null for a <c>.hoard</c> tab.</summary>
    public EditorViewModel? Editor { get; }

    /// <summary>The checklist-surface VM (<c>.hoard</c> tabs), or null for a text tab.</summary>
    public ChecklistViewModel? Checklist { get; }

    /// <summary>The settings-surface VM (the M6.8 settings tab, ODQ-12), or null for a document tab.</summary>
    public SettingsViewModel? Settings { get; }

    /// <summary>Whether this tab hosts a <c>.hoard</c> checklist (drives the _Checklist menu activation).</summary>
    public bool IsChecklist => Kind == TabKind.Hoard;

    /// <summary>
    /// Whether this tab is the settings surface (ODQ-12). A settings tab is NON-PERSISTED: the workspace
    /// excludes it from the session index and never snapshots it (it carries no document buffer).
    /// </summary>
    public bool IsSettings => Settings is not null;

    /// <summary>
    /// <c>true</c> when the reconcile flagged a restore-conflict (disk changed externally vs the recovered
    /// buffer, FR-19). The workspace must show the restore-conflict dialog before arming a clobbering Save.
    /// Set once at construction (init-only — a conflict flag must not be toggled post-construction, REVISE-8).
    /// </summary>
    public bool HasRestoreConflict { get; init; }

    /// <summary>
    /// <c>true</c> when the on-disk file is gone (§9.2 deleted modifier) — the next Save is a prompted
    /// re-create, not a silent resurrection of a deliberately-deleted file.
    /// Set once at construction (init-only — a deleted flag must not be toggled post-construction, REVISE-8).
    /// </summary>
    public bool OnDiskFileDeleted { get; init; }

    /// <summary>
    /// The display name (file name, "Settings" for the settings tab, "untitled" for a scratch buffer). For
    /// an editor tab with unsaved changes, a trailing " *" is appended (FEAT-13, FR-5) — a `.hoard`
    /// checklist is never suffixed because it auto-saves on every mutation (M6.3, never "unsaved" in the
    /// FR-5 sense).
    /// </summary>
    public string DisplayName
    {
        get
        {
            if (Settings is not null)
                return Settings.DisplayName;
            if (Editor is not null)
                return Editor.IsDirty ? Editor.Document.DisplayName + " *" : Editor.Document.DisplayName;
            return Checklist?.DisplayTitle ?? "untitled";
        }
    }

    /// <summary>
    /// The on-disk path of this tab's document (FEAT-7 — tab-strip context-menu Copy Filename / Copy File
    /// Path / Copy Folder Path). Null for a never-saved scratch buffer, a never-saved checklist, or the
    /// settings tab (none of which have a resolvable path yet).
    /// </summary>
    public string? FilePath => Editor?.Document.Path ?? Checklist?.FilePath;

    /// <summary>
    /// Whether this tab is the currently-active tab in the strip (M8.1). The shell sets this via
    /// <see cref="HoardPad.App.ViewModels.MainWindowViewModel.ActivateTab"/> so the XAML chip binding can
    /// switch the active-tab accent without a converter. The previous active tab is cleared first (exactly one
    /// tab is active at a time, or none when all tabs are closed).
    /// </summary>
    private bool _isActive;

    public bool IsActive
    {
        get => _isActive;
        internal set
        {
            if (_isActive == value)
                return;
            _isActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
        }
    }

    /// <summary>Wrap a text-surface VM as a tab (REVISE-8: flags are init-only, set at construction).</summary>
    /// <param name="hasRestoreConflict">True when the reconcile surfaced a disk-vs-recovered conflict (FR-19).</param>
    /// <param name="onDiskFileDeleted">True when the on-disk file is gone at restore (§9.2 deleted modifier).</param>
    public static DocumentTab ForEditor(
        string tabId,
        TabKind kind,
        EditorViewModel editor,
        bool hasRestoreConflict = false,
        bool onDiskFileDeleted = false)
    {
        ArgumentNullException.ThrowIfNull(editor);
        return new DocumentTab(tabId, kind, editor, checklist: null, settings: null)
        {
            HasRestoreConflict = hasRestoreConflict,
            OnDiskFileDeleted = onDiskFileDeleted,
        };
    }

    /// <summary>Wrap a checklist-surface VM as a <c>.hoard</c> tab (REVISE-8: flags are init-only, set at construction).</summary>
    /// <param name="onDiskFileDeleted">True when the on-disk file is gone at restore (§9.2 deleted modifier).</param>
    public static DocumentTab ForChecklist(
        string tabId,
        ChecklistViewModel checklist,
        bool onDiskFileDeleted = false)
    {
        ArgumentNullException.ThrowIfNull(checklist);
        return new DocumentTab(tabId, TabKind.Hoard, editor: null, checklist, settings: null)
        {
            OnDiskFileDeleted = onDiskFileDeleted,
        };
    }

    /// <summary>
    /// Wrap the settings-surface VM as a NON-PERSISTED settings tab (ODQ-12). It rides
    /// <see cref="TabKind.Scratch"/> (path-less) but is flagged <see cref="IsSettings"/> so the workspace
    /// excludes it from the session index and never snapshots it.
    /// </summary>
    public static DocumentTab ForSettings(string tabId, SettingsViewModel settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new DocumentTab(tabId, TabKind.Scratch, editor: null, checklist: null, settings);
    }
}
