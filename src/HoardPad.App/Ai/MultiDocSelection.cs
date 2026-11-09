// MultiDocSelection.cs — the AI multi-doc panel's per-open-document "checked for AI write" STATE model
// (PANEL-2, feature-ai-multidoc-panel FR-MD-6). This is the data model the PANEL-3 checklist checkboxes
// will bind to; PANEL-2 builds the STATE + the arm-time assembly + the Archetype-B enforcement, PANEL-3
// builds the visual checklist over these rows.
//
// Contract (FR-MD-6 / FR-MD-9):
//   - the FOCUSED document is PRE-CHECKED by default when a turn is armed (FR-MD-6(a) default-on-arm);
//   - the checked-set may PERSIST across turns while a session is open (a convenience, OQ-MD-6) — the
//     enforced grant set is recomputed per turn from the then-current checked set at arm time (FR-MD-9);
//   - a checkbox toggled mid-turn affects only the NEXT turn (FR-MD-6(e)) — the arm-time snapshot is what
//     the running turn enforces, and the running turn's grant set is immutable (FileGrantSet, FR-MD-9);
//   - a SCRATCH buffer (null path) may be checked but resolves to NO grant (NG-5); PANEL-3 owns the
//     disabled affordance + the Save-As-forcing UX — here it simply contributes nothing to the grant set.
//
// Keying: the checked-state persists across arms keyed by the document's on-disk path (ordinal). Scratch
// buffers (null path) cannot persist a stable key across arms, so their checked state is not remembered
// between arms in v1 — consistent with NG-5 (they grant nothing regardless).

using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HoardPad.App.Ai;

/// <summary>
/// One row in the AI multi-doc selection panel (feature-ai-multidoc-panel FR-MD-6) — the bindable
/// checked-state for one open document. PANEL-3 binds a checklist to <see cref="MultiDocSelection.Rows"/>;
/// PANEL-2 reads <see cref="IsChecked"/> at turn-arm to assemble the grant set. Immutable identity fields
/// (path / name / focus / scratch); the only mutable, user-toggled state is <see cref="IsChecked"/>.
/// </summary>
public sealed partial class MultiDocRowViewModel : ObservableObject
{
    /// <summary>The document's on-disk path, or <c>null</c> for a scratch buffer (non-grantable, NG-5).</summary>
    public string? Path { get; init; }

    /// <summary>
    /// PANEL-3 (FR-MD-6(d), OQ-MD-3): the durable tab id, so the Save-As-on-check flow can target the exact
    /// scratch buffer the user checked. Null in tests / rows built without a snapshot tab id.
    /// </summary>
    public string? TabId { get; init; }

    /// <summary>The tab display name (PANEL-3 row label).</summary>
    public string DisplayName { get; init; } = "untitled";

    /// <summary><c>true</c> for the focused document (PANEL-3 marks it; pre-checked by default on arm, FR-MD-6(a)).</summary>
    public bool IsFocused { get; init; }

    /// <summary><c>true</c> when the buffer has unsaved edits (PANEL-3 affordance; FR-MD-7 flush is PANEL-4).</summary>
    public bool IsDirty { get; init; }

    /// <summary><c>true</c> when the on-disk file is read-only (PANEL-3 read-only-vs-writable affordance, FR-MD-6(b)).</summary>
    public bool IsReadOnly { get; init; }

    /// <summary>
    /// <c>true</c> when this row is a never-saved scratch buffer (null path). PANEL-3 shows it disabled for
    /// write-targeting with a note (FR-MD-6(d)); PANEL-2 drops it from the grant set (NG-5).
    /// </summary>
    public bool IsScratch { get; init; }

    /// <summary>
    /// PANEL-3 (FR-MD-6(c)): <c>true</c> when this document's path lies OUTSIDE the open-folder root — a
    /// display flag only (an ordinal path-prefix heuristic against the root, computed at
    /// <see cref="MultiDocSelection.Rearm"/>). It drives the inline out-of-folder note (OQ-MD-2) and NOTHING
    /// else — it is not the enforcement boundary (the union gate / grant set is, FR-MD-4). A scratch buffer is
    /// never out-of-folder (it has no path yet).
    /// </summary>
    public bool IsOutOfFolder { get; init; }

    /// <summary>
    /// Whether the user has ticked this document as an AI write target for the NEXT turn (the FR-MD-2
    /// allow-list bit). PANEL-3 binds a checkbox to this. Toggling mid-turn affects only the next turn
    /// (FR-MD-6(e)) — the running turn enforces the immutable arm-time snapshot.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEffectivelyWritable))]
    [NotifyPropertyChangedFor(nameof(ShowOutOfFolderNote))]
    private bool _isChecked;

    /// <summary>
    /// PANEL-3 (FR-MD-6(b)): the row's EFFECTIVE write posture for the writable-vs-read-only affordance —
    /// <c>true</c> only when the row is checked AND is not a scratch buffer AND is not read-only. A checked
    /// read-only (or scratch) row is therefore NEVER shown as writable (the affordance stays truthful). This
    /// is a UI reflection of the effective posture, not the enforcement boundary (the gate is, FR-MD-4).
    /// Recomputes on <see cref="IsChecked"/> change.
    /// </summary>
    public bool IsEffectivelyWritable => IsChecked && !IsScratch && !IsReadOnly;

    /// <summary>
    /// PANEL-3 (FR-MD-6(c)): whether to show the inline out-of-folder note (OQ-MD-2) on this row —
    /// <c>true</c> when the row is CHECKED and OUT-OF-FOLDER and not scratch. Recomputes on
    /// <see cref="IsChecked"/> change.
    /// </summary>
    public bool ShowOutOfFolderNote => IsChecked && IsOutOfFolder && !IsScratch;
}

/// <summary>
/// The AI multi-doc panel's checked-set state (feature-ai-multidoc-panel FR-MD-6/FR-MD-9). Holds the
/// per-document checked flags across turns for one open session and rebuilds the bindable
/// <see cref="Rows"/> from a fresh <see cref="OpenDocumentInfo"/> snapshot at each turn-arm — pre-checking
/// the focused document by default (FR-MD-6(a)) and preserving a prior explicit check keyed by path
/// (FR-MD-9 persistence). This is the clean state surface PANEL-3 binds its visual checklist to.
/// </summary>
public sealed class MultiDocSelection
{
    // The persisted checked-state across arms, keyed by the document's on-disk path (ordinal). A path
    // present here with value true is a user-ticked write target; absent means "never explicitly ticked".
    // Scratch buffers (null path) are not persisted (no stable key; they grant nothing regardless, NG-5).
    private readonly Dictionary<string, bool> _checkedByPath = new(System.StringComparer.Ordinal);

    // Set once the FIRST arm has pre-checked the focused document, so a later un-check of the focused
    // document is NOT re-forced back on by a subsequent arm (FR-MD-6(a) is a DEFAULT, not a lock).
    private bool _focusDefaultApplied;

    /// <summary>
    /// The bindable rows for PANEL-3's checklist (one per open document, in the snapshot's order). Rebuilt
    /// by <see cref="Rearm"/> at each turn-arm. Empty until the first arm.
    /// </summary>
    public ObservableCollection<MultiDocRowViewModel> Rows { get; } = [];

    /// <summary>
    /// Rebuild <see cref="Rows"/> from a fresh open-document snapshot at turn-arm (FR-MD-9 recompute).
    /// The focused document is pre-checked by default the FIRST time it is armed (FR-MD-6(a)); a prior
    /// explicit user check/uncheck (keyed by path) is preserved across arms (FR-MD-9 persistence). Scratch
    /// buffers are shown as rows but are non-grantable (NG-5); a document no longer open drops out of the
    /// bound rows (its stale persisted flag is harmless — it can never match a grant, FR-MD-9 revocation).
    /// </summary>
    /// <param name="documents">The fresh open-document snapshot (the panel's provider yields this at arm).</param>
    /// <param name="sandboxRoot">
    /// PANEL-3 (FR-MD-6(c)): the open-folder root, used ONLY to compute each row's display-only
    /// <see cref="MultiDocRowViewModel.IsOutOfFolder"/> flag (an ordinal path-prefix heuristic for the inline
    /// OQ-MD-2 note). It is not the enforcement boundary (the union gate / grant set is, FR-MD-4). Null/empty
    /// (no folder bound, or a test that does not exercise the out-of-folder note) ⇒ no row is out-of-folder.
    /// </param>
    public void Rearm(IReadOnlyList<OpenDocumentInfo> documents, string? sandboxRoot = null)
    {
        System.ArgumentNullException.ThrowIfNull(documents);

        Rows.Clear();
        foreach (OpenDocumentInfo doc in documents)
        {
            bool isScratch = doc.IsScratch;

            // Resolve the checked default: a persisted explicit user choice wins; otherwise the focused
            // document is pre-checked ONCE by default (FR-MD-6(a)). A scratch buffer is never auto-checked
            // (it grants nothing; PANEL-3 disables it, FR-MD-6(d)).
            bool isChecked;
            if (!isScratch && doc.Path is { } path && _checkedByPath.TryGetValue(path, out bool persisted))
            {
                isChecked = persisted;
            }
            else if (!isScratch && doc.IsFocused && !_focusDefaultApplied)
            {
                isChecked = true;
            }
            else
            {
                isChecked = false;
            }

            var row = new MultiDocRowViewModel
            {
                Path = doc.Path,
                TabId = doc.TabId,
                DisplayName = doc.DisplayName,
                IsFocused = doc.IsFocused,
                IsDirty = doc.IsDirty,
                IsReadOnly = doc.IsReadOnly,
                IsScratch = isScratch,
                IsOutOfFolder = !isScratch && IsOutsideRoot(doc.Path, sandboxRoot),
                IsChecked = isChecked,
            };

            // Persist the resolved state so the NEXT arm remembers it (FR-MD-9), and mirror later user
            // toggles back into the persistence map so a mid-session check/uncheck survives the next arm.
            if (!isScratch && doc.Path is { } p)
            {
                _checkedByPath[p] = isChecked;
                MultiDocRowViewModel captured = row;
                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MultiDocRowViewModel.IsChecked) && captured.Path is { } cp)
                        _checkedByPath[cp] = captured.IsChecked;
                };
            }

            Rows.Add(row);
        }

        // The focus default has now been applied for this session's arming lifecycle (FR-MD-6(a) is a
        // one-time default, so a subsequent arm does not re-force a focused doc the user has since unchecked).
        _focusDefaultApplied = true;
    }

    /// <summary>
    /// PANEL-3 display heuristic (FR-MD-6(c)): whether <paramref name="path"/> lies outside
    /// <paramref name="sandboxRoot"/> for the inline OQ-MD-2 note. An ordinal directory-prefix test on the
    /// raw strings — deliberately NOT the enforcement resolution (that is the composer's <c>ResolveCanonical</c>
    /// leg, FR-MD-5.1); this only decides whether to show an advisory note. Returns false when either string
    /// is empty (no folder bound ⇒ nothing is flagged out-of-folder). A path equal to the root, or under it
    /// (root + separator prefix), is in-folder; anything else is out-of-folder.
    /// </summary>
    private static bool IsOutsideRoot(string? path, string? sandboxRoot)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(sandboxRoot))
            return false;

        string root = sandboxRoot.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        if (string.Equals(path, root, System.StringComparison.Ordinal))
            return false;

        string rootWithSep = root + System.IO.Path.DirectorySeparatorChar;
        return !path.StartsWith(rootWithSep, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The resolved-grant INPUT for the current arm: the on-disk paths of the CHECKED, non-scratch rows
    /// (FR-MD-2). These raw paths are handed to the session grant composer, which resolves each through the
    /// EXACT <c>IPathResolver.ResolveCanonical</c> + root-base leg the sandbox gate uses (FR-MD-5.1) and
    /// builds the immutable per-turn <see cref="HoardPad.Core.Ai.FileOps.FileGrantSet"/>. Scratch buffers
    /// are excluded here (NG-5).
    /// </summary>
    public IReadOnlyList<string> CheckedGrantablePaths()
    {
        var paths = new List<string>();
        foreach (MultiDocRowViewModel row in Rows)
            if (row.IsChecked && !row.IsScratch && row.Path is { Length: > 0 } path)
                paths.Add(path);
        return paths;
    }

    /// <summary>A read-only view of the current rows for the FR-MD-3 context descriptor + assertions.</summary>
    public IReadOnlyList<MultiDocRowViewModel> CurrentRows => Rows;

    /// <summary>Clear all checked-state (folder close / session clear, FR-MD-9 — a new root re-arms from scratch).</summary>
    public void Reset()
    {
        _checkedByPath.Clear();
        _focusDefaultApplied = false;
        Rows.Clear();
    }
}
