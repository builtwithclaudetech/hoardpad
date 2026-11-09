// abstraction — lives in HoardPad.Core, NOT HoardPad.Host
namespace HoardPad.Core.Host.Abstractions;

/// <summary>
/// The conversation-keyed cap-state store seam (architecture §4.4). The cap-state SHAPE and the
/// increment/no-re-arm/trip logic are pure <c>HoardPad.Core.Ai</c> (M4.1 — <c>CapState</c> /
/// <c>CapAccounting</c>); this seam owns ONLY the durable persistence under a conversation id. The
/// concrete <c>O_EXCL</c>-locked file I/O (SPIKE-4 <c>O_CREAT|O_EXCL</c> / Win32 <c>CREATE_NEW</c>
/// SpinLock; never <c>fcntl</c>; 5 ms spin, 10 s hard timeout, unlinked in <c>finally</c>) is supplied
/// by <c>HoardPad.Host</c> in M5.2 — the file-ops service (M4.3) calls only this abstraction.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a typed payload, not <c>CapState</c> directly.</b> <c>CapState</c> lives in
/// <c>HoardPad.Core.Ai</c>; <c>HoardPad.Core</c> must not depend on <c>HoardPad.Core.Ai</c> (the arrow
/// is one-way, architecture §2.1). So this seam reads/writes the cap-state as its serialized PascalCase
/// JSON (the persisted shape M5.1's Python hook also reads) — a plain string the host stores under the
/// conversation id. The Core.Ai service serializes its <c>CapState</c> to/from this string.
/// </para>
/// <para>
/// <b>Compare-and-write (the no-lost-update guarantee).</b> A continuation turn and a fresh invocation
/// can both read-modify-write the same conversation's cap-state; the write is a locked
/// compare-and-swap against the <see cref="CapStateSnapshot.Version"/> the read returned, so a
/// concurrent writer cannot silently clobber an increment (which would re-arm a tripped cap — the
/// exact R5-BL1 failure the conversation-keyed lifetime exists to prevent). A version mismatch surfaces
/// as <see cref="CapStateWriteResult.Conflict"/>; the caller re-reads and re-applies.
/// </para>
/// </remarks>
public interface ICapStateStore
{
    /// <summary>
    /// Read the persisted cap-state for <paramref name="conversationId"/>, or
    /// <see cref="CapStateSnapshot.Empty"/> (a null payload, version 0) when no state has been
    /// persisted yet (a new user-initiated conversation). The returned <see cref="CapStateSnapshot.Version"/>
    /// is the compare token <see cref="WriteAsync"/> must be handed back.
    /// </summary>
    Task<CapStateSnapshot> ReadAsync(string conversationId, CancellationToken ct);

    /// <summary>
    /// Persist <paramref name="payloadJson"/> under <paramref name="conversationId"/> under the host's
    /// exclusive lock, but ONLY if the store's current version still equals
    /// <paramref name="expectedVersion"/> (the version the matching <see cref="ReadAsync"/> returned).
    /// On success the new version is returned; on a version mismatch the write does not happen and
    /// <see cref="CapStateWriteResult.Conflict"/> is returned so the caller re-reads.
    /// </summary>
    Task<CapStateWriteResult> WriteAsync(
        string conversationId, string payloadJson, long expectedVersion, CancellationToken ct);

    /// <summary>
    /// Resolve the EXACT cap-state payload file path this store reads/writes for
    /// <paramref name="conversationId"/> — a pure, read-only projection of the store's own naming
    /// (it computes the same leaf <see cref="ReadAsync"/> / <see cref="WriteAsync"/> use; it opens
    /// nothing and mutates nothing). The M11 launch-env seam injects this path as the hook's
    /// <c>HOARDPAD_CAP_STATE_FILE</c> so the hook reads — and locks (its <c>&lt;path&gt;.lock</c>
    /// sibling) — the SAME file the spine writes (invariant 4: no double-count, no drift). Exposing
    /// the already-computed path here is the ONE authority; callers must NOT re-derive the file name.
    /// </summary>
    string ResolveCapStateFilePath(string conversationId);
}

/// <summary>
/// A read of the conversation's cap-state: the serialized PascalCase JSON payload (<c>null</c> when no
/// state has been persisted yet) and the optimistic-concurrency <see cref="Version"/> token.
/// </summary>
/// <param name="PayloadJson">The serialized <c>CapState</c> JSON, or <c>null</c> for a never-persisted conversation.</param>
/// <param name="Version">The store's current version for this conversation (0 when never persisted).</param>
public readonly record struct CapStateSnapshot(string? PayloadJson, long Version)
{
    /// <summary>The empty snapshot for a never-persisted conversation (the M4.1 <c>CapState.Empty</c> case).</summary>
    public static CapStateSnapshot Empty => new(PayloadJson: null, Version: 0);
}

/// <summary>The outcome of a compare-and-write: the new version on success, or a conflict requiring a re-read.</summary>
/// <param name="Committed"><c>true</c> when the write took effect; <c>false</c> on a version conflict.</param>
/// <param name="NewVersion">The new store version when <see cref="Committed"/>; the unchanged current version on conflict.</param>
public readonly record struct CapStateWriteResult(bool Committed, long NewVersion)
{
    /// <summary>A successful commit advancing to <paramref name="newVersion"/>.</summary>
    public static CapStateWriteResult Ok(long newVersion) => new(Committed: true, NewVersion: newVersion);

    /// <summary>A version conflict — the caller must re-read and re-apply against <paramref name="currentVersion"/>.</summary>
    public static CapStateWriteResult Conflict(long currentVersion) => new(Committed: false, NewVersion: currentVersion);
}
