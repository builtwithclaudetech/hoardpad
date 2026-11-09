using System.Text.Json.Serialization;

namespace HoardPad.Core.Records;

/// <summary>
/// The optional §9.3 <c>tokens</c> object — input/output token counts populated for <c>claude -p</c>
/// from the agent's result message. Optional (the whole object is omitted when unknown).
/// </summary>
public sealed record TokenUsage(
    [property: JsonPropertyName("input")] long Input,
    [property: JsonPropertyName("output")] long Output);

/// <summary>
/// The §9.3 activity-log record — one NDJSON line per AI file op (FR-29). This is the DATA SHAPE only:
/// the Host <c>AtomicFileStore</c> (M2.1) frames + appends it (<c>[4B LE len][payload][4B CRC-32]</c>)
/// and the AI file-ops service (M4.3) emits it. No I/O, no clock — the producer supplies every value,
/// including <see cref="Ts"/> (§9.3 carries the timestamp as a producer-set field, not a clock call).
/// API keys and file contents are NEVER written here (NFR-7).
/// </summary>
/// <remarks>
/// Field order + names mirror the §9.3 contract exactly. <c>configuredMode</c>/<c>agencyMode</c>/
/// <c>downgradeReason</c> are the MA13 agency/downgrade fields that make the FR-10/FR-38 Done-when
/// provable. <c>error</c> is serialized even when <c>null</c> (the §9.3 example shows <c>"error": null</c>);
/// <c>tokens</c>/<c>costUsd</c> are optional and omitted when absent.
/// </remarks>
public sealed record ActivityLogRecord(
    [property: JsonPropertyName("ts")] DateTimeOffset Ts,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("backend")] AiBackend Backend,
    [property: JsonPropertyName("configuredMode")] AgencyMode ConfiguredMode,
    [property: JsonPropertyName("agencyMode")] AgencyMode AgencyMode,
    [property: JsonPropertyName("downgradeReason")] DowngradeReason DowngradeReason,
    [property: JsonPropertyName("op")] ActivityOp Op,
    [property: JsonPropertyName("paths")] OpPaths Paths,
    [property: JsonPropertyName("outcome")] ActivityOutcome Outcome,
    [property: JsonPropertyName("bytesBefore")] long BytesBefore,
    [property: JsonPropertyName("bytesAfter")] long BytesAfter,
    [property: JsonPropertyName("undoRef")] string? UndoRef,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("durationMs")] long DurationMs,
    [property: JsonPropertyName("tokens")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TokenUsage? Tokens = null,
    [property: JsonPropertyName("costUsd")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? CostUsd = null);
