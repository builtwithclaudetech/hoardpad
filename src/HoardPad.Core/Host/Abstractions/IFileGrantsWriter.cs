// abstraction — lives in HoardPad.Core, NOT HoardPad.Host. Kept free of HoardPad.Core.Ai (it takes
// only resolved path strings; the FileGrantSet stays in the Ai domain).
//
// PANEL-A2 (feature-ai-multidoc-panel §5.3 Path A): the host-side grants-file writer for Archetype A
// (claude -p). The App composes the turn's per-file grant set (PANEL-2's UnionSandboxGate) from the
// SAME resolved paths it hands here; this seam serializes those resolved paths to a host-written JSON
// file whose path the launch env's HOARDPAD_FILE_GRANTS points at, so the PANEL-A1 hook grant leg can
// admit exactly those out-of-folder paths. Read-only to the hook; the host is the sole writer.
namespace HoardPad.Core.Host.Abstractions;

/// <summary>
/// HOST [host]. Writes the per-turn AI file-grant list to an app-controlled JSON file the
/// <c>deny_by_default.py</c> hook reads via <c>HOARDPAD_FILE_GRANTS</c> (feature-ai-multidoc-panel §5.3).
/// The file is a JSON array of resolved, normcased absolute path strings — exactly the grant strings the
/// App built the turn's <see cref="HoardPad.Core.Ai.FileOps.UnionSandboxGate"/> from, so Archetype A
/// (this file → hook) and Archetype B (the gate) enforce the identical set.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fail-closed by construction (FR-MD-10 / AC-MD-6):</b> an empty grant collection writes NO file and
/// returns <c>null</c> — the runner then never sets <c>HOARDPAD_FILE_GRANTS</c>, so the hook's grant leg
/// stays inert (root-only). An empty-array file is deliberately never written: a stale/empty file must
/// never be mistaken for "grants armed". A write fault is the caller's to handle (root-only fallback).
/// </para>
/// <para>
/// <b>Atomic + app-controlled:</b> the implementation writes via the <see cref="IAtomicFileStore"/> temp
/// + rename discipline (no partial file ever visible to the hook) to a per-conversation deterministic
/// path under the <see cref="IAppDataPaths"/> root — the same app-owned location the cap-state file uses.
/// The file is immutable for the turn (written before launch, deleted after) and is NOT lock-protected
/// (unlike the cap-state CAS file): it is never mutated during the turn, only read by the hook.
/// </para>
/// </remarks>
public interface IFileGrantsWriter
{
    /// <summary>
    /// Write the turn's resolved grant paths to an app-controlled JSON array file for
    /// <paramref name="conversationKey"/> and return its absolute path, or <c>null</c> when
    /// <paramref name="resolvedGrantPaths"/> is empty (no file written — root-only, FR-MD-10). The file
    /// is written atomically (temp + rename) and overwrites any prior per-conversation grants file.
    /// </summary>
    /// <param name="conversationKey">The conversation id keying the per-conversation grants file leaf.</param>
    /// <param name="resolvedGrantPaths">
    /// The resolved, normcased absolute grant path strings for this turn (the SAME strings the turn's
    /// <see cref="HoardPad.Core.Ai.FileOps.FileGrantSet"/> holds). Empty ⇒ no file, returns <c>null</c>.
    /// </param>
    /// <param name="ct">Cancellation for the atomic write.</param>
    /// <returns>The absolute path of the written grants file, or <c>null</c> for an empty grant set.</returns>
    System.Threading.Tasks.Task<string?> WriteAsync(
        string conversationKey,
        System.Collections.Generic.IReadOnlyCollection<string> resolvedGrantPaths,
        System.Threading.CancellationToken ct);

    /// <summary>
    /// Best-effort delete the grants file written by <see cref="WriteAsync"/> at turn end (the grant is
    /// per-turn, FR-MD-9). A missing file is a no-op; a delete failure is swallowed (the file is
    /// overwritten on the next turn and never read outside an armed turn). <c>null</c> is a no-op.
    /// </summary>
    /// <param name="filePath">The path returned by <see cref="WriteAsync"/>, or <c>null</c> (no-op).</param>
    void Delete(string? filePath);
}
