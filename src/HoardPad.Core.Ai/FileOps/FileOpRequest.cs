using HoardPad.Core.Ai.Caps;
using HoardPad.Core.Records;

namespace HoardPad.Core.Ai.FileOps;

/// <summary>
/// One file op handed to the shared file-ops service (M4.3) to apply (architecture §4.5). It carries the
/// op vocabulary + raw paths (<see cref="FileOperation"/>), the bytes to write (for a create / overwrite /
/// edit), the host-supplied pre-op facts the service needs to record an UNDOABLE entry (before-image
/// hash/mtime, pre-op size, and — for a clobbering move/copy — the clobbered destination's prior content
/// hash), and the cap-classification input.
/// </summary>
/// <remarks>
/// <para>
/// The service is platform-free: the host has ALREADY read the target's pre-op bytes/hash/mtime (the
/// before-image capture is a host I/O leg — it stages the blob; here we carry the metadata + the blob ref
/// the service writes into the journal). The service computes no hashes and reads no files; it
/// canonicalizes, sandbox-checks, orders the durable writes, applies via <see cref="IAtomicFileStore"/>,
/// and records the uniform log/journal.
/// </para>
/// <para>
/// <b>Raw vs resolved paths.</b> <see cref="FileOperation.Paths"/> carries the RAW target/source/dest the
/// agent proposed; the service resolves each through the <see cref="ISandboxGate"/> and records the
/// RESOLVED path in the log/journal (never the unresolved input — §9.3/§9.4 record host paths).
/// </para>
/// </remarks>
/// <param name="Operation">The op vocabulary + raw paths + requestId (M2's <see cref="FileOperation"/>).</param>
/// <param name="Content">
/// The bytes to write for a create / overwrite / edit (the post-op file content). Empty for move/delete
/// (which carry no new content). For a copy this is the source bytes the host read.
/// </param>
/// <param name="Classification">
/// The cap-classification input (tool, target-exists, pre/post byte sizes) the host built from its
/// <c>stat</c> — fed to <see cref="CapClassifier.Classify"/>. <c>null</c> for ops that are not
/// cap-counted (move/copy/delete are Archetype-B-only and governed by FR-10(b) per-op confirm, not the
/// per-turn caps); a create/overwrite/edit always carries it.
/// </param>
/// <param name="BufferJournalId">The durable, path-independent buffer journal id (MA8) for the undo entry.</param>
/// <param name="UndoRef">The stable undo-journal entry id (matches the activity-log <c>undoRef</c>, §9.3 join).</param>
/// <param name="PriorContent">
/// The before-image of the target the host staged (a <c>before/…bin</c> blob ref), or <c>null</c> for a
/// <c>create</c> (nothing existed before). Written into the undo journal BEFORE the mutation (M15).
/// </param>
/// <param name="PreOpContentHash">The target's pre-op content hash (FR-28(ii)), or <c>null</c> for a create.</param>
/// <param name="PreOpMtime">The target's pre-op mtime, or <c>null</c> for a create.</param>
/// <param name="DestPriorContent">
/// The before-image of a CLOBBERED destination for a move/copy whose <c>dest</c> already existed
/// (Decision 1 / M10), or <c>null</c> when the destination did not pre-exist. Its presence is what makes
/// the undo path pass <c>destCurrent</c> to <see cref="HoardPad.Core.Undo.UndoReverseDecision.Decide"/>.
/// </param>
/// <param name="DestPriorContentHash">The clobbered destination's pre-op content hash (FR-28(ii) dest axis), or <c>null</c>.</param>
/// <param name="BytesBefore">The target's pre-op size in bytes (for the §9.3 <c>bytesBefore</c> delta).</param>
/// <param name="BytesAfter">The target's post-op size in bytes (for the §9.3 <c>bytesAfter</c> delta).</param>
/// <param name="DurationMs">The op duration the host measured (the service does not read a clock).</param>
/// <param name="SnapshotSkipped">
/// <c>true</c> when the file exceeded the snapshot size cap (FR-28(iii)) → undo unavailable for this op;
/// the journal entry then carries no usable before-image and the undo decision reports it Unavailable.
/// </param>
public sealed record FileOpRequest(
    FileOperation Operation,
    ReadOnlyMemory<byte> Content,
    OpClassificationInput? Classification,
    string BufferJournalId,
    string UndoRef,
    BeforeImage? PriorContent,
    string? PreOpContentHash,
    DateTimeOffset? PreOpMtime,
    BeforeImage? DestPriorContent = null,
    string? DestPriorContentHash = null,
    long BytesBefore = 0,
    long BytesAfter = 0,
    long DurationMs = 0,
    bool SnapshotSkipped = false);
