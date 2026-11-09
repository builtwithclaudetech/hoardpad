using System.Text.Json.Serialization;

namespace HoardPad.Core.Records;

/// <summary>
/// The §9.4 undo-journal entry — FR-28's durable backing structure for reversing a file op. DATA SHAPE
/// only (no I/O, no before-image bytes here — the bytes live in the <c>before/…bin</c> blob the
/// <see cref="PriorContent"/>/<see cref="DestPriorContent"/> <see cref="BeforeImage"/> points at).
/// The reverse decision (M1.4) and the file-ops service (M4.3) consume this; the host supplies the
/// current hash/mtime read at reversal time.
/// </summary>
/// <remarks>
/// Field names + order mirror the §9.4 contract exactly. The <see cref="Op"/> enum is the
/// create/copy/move/edit/delete subset (no <c>read</c>/<c>blocked</c>; no <c>rename</c> — §9.4 MA11).
/// Nullable members are <c>null</c> for <c>create</c> (nothing existed before) or for a non-clobbering
/// move/copy (no pre-existing <c>dest</c>); they are emitted as <c>null</c> (the §9.4 example/contract
/// lists them as nullable fields, not omitted keys) so the persisted shape is stable for the reverse
/// decision to read.
/// </remarks>
public sealed record UndoJournalEntry(
    [property: JsonPropertyName("undoRef")] string UndoRef,
    [property: JsonPropertyName("bufferJournalId")] string BufferJournalId,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("op")] FileOp Op,
    [property: JsonPropertyName("paths")] OpPaths Paths,
    [property: JsonPropertyName("priorContent")] BeforeImage? PriorContent,
    [property: JsonPropertyName("destPriorContent")] BeforeImage? DestPriorContent,
    [property: JsonPropertyName("destPriorContentHash")] string? DestPriorContentHash,
    [property: JsonPropertyName("preOpContentHash")] string? PreOpContentHash,
    [property: JsonPropertyName("preOpMtime")] DateTimeOffset? PreOpMtime,
    [property: JsonPropertyName("snapshotSkipped")] bool SnapshotSkipped);
