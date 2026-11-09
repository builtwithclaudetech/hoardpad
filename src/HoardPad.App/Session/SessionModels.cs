// SessionModels.cs — the on-disk session/recovery shapes (PRD §9.2 field contracts).
//
// Two persisted JSON shapes, both UTF-8 via System.Text.Json:
//   - SessionState / SessionTab: the session.json index (the rebuildable hint, §9.2). Carries
//     openFolder + activeTabId + tabs[]; each tab carries its last-persist `seq` so PathAuthority
//     can follow the more-recently-persisted of {snapshot, session-entry} (the Save-As stale-path
//     rule, R3-MA1).
//   - RecoverySnapshot: the SELF-DESCRIBING recovery snapshot (§9.2). It embeds its own tabId, path,
//     kind, cursor, savedAt, the monotonic per-machine `seq`, the baseline content hash + mtime
//     (for file/hoard), and the unsaved buffer bytes. The recovery-directory scan is authoritative;
//     the snapshot filename is opaque and is NEVER the join key — tabId is (§9.2).
//
// These are pure data records; the I/O is SessionStore, the reconcile is SessionReconciler.

using System.Text.Json.Serialization;
using HoardPad.Core.Recovery;

namespace HoardPad.App.Session;

/// <summary>A caret position (1-based line/column), or null for a kind that has none (a <c>.hoard</c> tab).</summary>
/// <param name="Line">1-based line.</param>
/// <param name="Column">1-based column.</param>
public sealed record CursorPosition(int Line, int Column);

/// <summary>
/// One <c>session.json</c> tab entry (PRD §9.2 field contract). A rebuildable hint — the recovery
/// directory scan is authoritative; this index is reconciled against it.
/// </summary>
/// <param name="Id">The durable tab id (== the snapshot's embedded <c>tabId</c>, the §9.2 join key).</param>
/// <param name="Kind">file / scratch / hoard.</param>
/// <param name="Path">Absolute file path; null for a scratch buffer.</param>
/// <param name="Language">The active language id (for file/scratch).</param>
/// <param name="IsDirty">Whether the buffer had unsaved edits at last persist.</param>
/// <param name="Cursor">The last caret position, or null.</param>
/// <param name="RecoveryRef">Hint to the recovery snapshot (not authoritative); null when nothing needs recovery.</param>
/// <param name="Seq">The per-machine <c>seq</c> at this entry's last persist (§9.2 — drives PathAuthority).</param>
public sealed record SessionTab(
    string Id,
    TabKind Kind,
    string? Path,
    string Language,
    bool IsDirty,
    CursorPosition? Cursor,
    string? RecoveryRef,
    long? Seq);

/// <summary>
/// The <c>session.json</c> index (PRD §9.2). A rebuildable hint reconciled against the authoritative
/// recovery-directory scan. Carries the monotonic per-machine <c>seq</c> counter so a fresh snapshot
/// write can mint the next value (the counter lives with the index, persisted atomically alongside it).
/// </summary>
/// <param name="SchemaVersion">Versioned for migration (PRD §9.2 sample = 1).</param>
/// <param name="OpenFolder">The raw open-folder path as it was supplied by the user (the AI sandbox root, FR-26), or null.</param>
/// <param name="CanonicalOpenFolder">
/// The canonical (realpath-normalised) form of <see cref="OpenFolder"/> at last persist time. Always null
/// since FS-1 (docs/tasklist.md) retired the persisted open-folder baseline — the open folder is derived
/// fresh every launch instead. Retained on the schema for backward compatibility with older session files.
/// </param>
/// <param name="ActiveTabId">Id of the focused tab on restore.</param>
/// <param name="Tabs">The open-tab entries.</param>
/// <param name="NextSeq">The next monotonic per-machine sequence value to mint for a snapshot write.</param>
public sealed record SessionState(
    int SchemaVersion,
    string? OpenFolder,
    string? CanonicalOpenFolder,
    string? ActiveTabId,
    IReadOnlyList<SessionTab> Tabs,
    long NextSeq)
{
    /// <summary>The current session-index schema version (PRD §9.2 sample = 1).</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>An empty session (no folder, no tabs) with the seq counter at 1.</summary>
    public static SessionState Empty { get; } =
        new(CurrentSchemaVersion, OpenFolder: null, CanonicalOpenFolder: null,
            ActiveTabId: null, Tabs: [], NextSeq: 1);
}

/// <summary>
/// A self-describing recovery snapshot (PRD §9.2). It embeds everything reconciliation needs so the
/// recovery directory alone is the source of truth: the durable <see cref="TabId"/> join key, the
/// buffer's last-persisted reality (<see cref="Kind"/> / <see cref="Path"/> / <see cref="Cursor"/>),
/// the per-machine <see cref="Seq"/> tie-break key, and — for a file/hoard buffer — the on-disk file's
/// <see cref="BaselineContentHash"/> + <see cref="BaselineMtime"/> captured at snapshot time (B7).
/// The unsaved buffer bytes ride in <see cref="ContentBase64"/>.
/// </summary>
/// <param name="TabId">The durable correlation key (== the session tab id). The §9.2 join key.</param>
/// <param name="Kind">The buffer's last-persisted kind (authoritative over a stale session entry, §9.2).</param>
/// <param name="Path">The buffer's last-persisted path; null for a scratch buffer.</param>
/// <param name="Language">The active language id at snapshot time.</param>
/// <param name="Cursor">The caret position at snapshot time, or null.</param>
/// <param name="SavedAt">The wall-clock save instant (secondary tie-break only) — from the injected TimeProvider.</param>
/// <param name="Seq">The monotonic per-machine sequence counter at this write (the primary tie-break, §9.2).</param>
/// <param name="BaselineContentHash">The on-disk file's content hash at snapshot time (B7), or null for scratch.</param>
/// <param name="BaselineMtime">The on-disk file's mtime at snapshot time (the re-hash-skip hint only), or null.</param>
/// <param name="ContentBase64">The unsaved buffer's decoded text bytes, base64-encoded.</param>
public sealed record RecoverySnapshot(
    string TabId,
    TabKind Kind,
    string? Path,
    string Language,
    CursorPosition? Cursor,
    DateTimeOffset SavedAt,
    long Seq,
    string? BaselineContentHash,
    DateTimeOffset? BaselineMtime,
    string ContentBase64)
{
    /// <summary>The snapshot's <see cref="SnapshotOrder"/> view for the §9.2 same-machine tie-break.</summary>
    [JsonIgnore]
    public SnapshotOrder Order => new(Seq, SavedAt);
}
