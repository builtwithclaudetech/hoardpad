namespace HoardPad.Core.Recovery;

/// <summary>
/// Dimension 3 of the §9.2 reconciliation table: the on-disk file state at <c>tab.path</c> for a
/// <c>kind: file</c>/<c>hoard</c> tab, compared against the snapshot's recorded baseline hash+mtime.
/// </summary>
/// <remarks>
/// The comparison is by content <b>hash</b> (authoritative); <c>mtime</c> is only a cheap re-hash-skip
/// optimization, <b>never</b> the sole basis (§9.2, FR-28(ii)/FR-19) — clock skew or a sync-daemon
/// mtime touch must not re-enter as a false-unchanged verdict. For <c>kind: scratch</c> there is no
/// on-disk file; that case is <see cref="NotApplicable"/>.
/// </remarks>
public enum OnDiskFileState
{
    /// <summary>The on-disk file is present and its content hash matches the snapshot baseline.</summary>
    PresentUnchanged,

    /// <summary>The on-disk file is present but its content hash differs from the snapshot baseline (external mod).</summary>
    PresentChangedExternally,

    /// <summary>The on-disk file at <c>tab.path</c> is gone.</summary>
    Deleted,

    /// <summary>No on-disk file applies (a <c>kind: scratch</c> tab).</summary>
    NotApplicable,
}
