using System.Text.Json.Serialization;

namespace HoardPad.Core.Records;

/// <summary>
/// A single file operation — the DATA SHAPE the shared file-ops service (§4.5) operates over, not the
/// apply logic. It pairs the §9.3/§9.4 op vocabulary (<see cref="FileOp"/>: create/copy/move/edit/delete)
/// with its <see cref="OpPaths"/> (<c>target</c>; <c>source</c>+<c>dest</c> for move/copy) and the
/// <c>requestId</c> that correlates all ops from one AI turn (§9.3). Carrying the op + paths together is
/// what the service canonicalizes, sandbox-checks, applies, then records as an
/// <see cref="ActivityLogRecord"/> + <see cref="UndoJournalEntry"/>.
/// </summary>
public sealed record FileOperation(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("op")] FileOp Op,
    [property: JsonPropertyName("paths")] OpPaths Paths);
