// IRecoveryFileSystem.cs — the App-side filesystem-probe seam for session/recovery (PRD §9.2).
//
// The §9.2 reconciliation is PURE Core domain logic; the directory enumeration + on-disk read are
// host concerns (architecture §5.2). This seam is the App's read side of those host concerns: it
// stats/hashes the on-disk file at a tab path (dimension 3). The production implementation reads the
// real filesystem off the UI thread; tests inject a fake so the reconciliation drive is deterministic
// without touching disk.
//
// It is DELIBERATELY NOT the recovery-snapshot directory scan — that lives in SessionStore over
// IAppDataPaths (the §5.5 layout owner). This seam is only the *target-file* probe.

using HoardPad.Core.Recovery;

namespace HoardPad.App.Session;

/// <summary>
/// The App's read side of the §9.2 host filesystem concerns: dimension-3 on-disk probes (existence +
/// content hash + mtime). The pure Core reconciliation consumes the facts this seam gathers; it never
/// owns the decision.
/// </summary>
public interface IRecoveryFileSystem
{
    /// <summary>
    /// Probe the current on-disk file at <paramref name="path"/> for the §9.2 dimension-3 classification.
    /// Returns existence + the content hash + the mtime. The hash is computed (hash is authoritative,
    /// §9.2 / FR-28(ii) — never skip it on mtime alone for the restore-conflict decision). For a
    /// <c>kind: hoard</c> tab the hash is over the CANONICAL-parsed content (couples to MA2), so a
    /// whitespace/escaping-only reserialization by another build does not flip the baseline.
    /// </summary>
    /// <param name="path">The absolute tab path.</param>
    /// <param name="kind">file vs hoard — selects raw-bytes vs canonical-parsed hashing.</param>
    Task<CurrentDiskFile> ProbeAsync(string path, TabKind kind, CancellationToken ct);
}
