// ScrubFindingsLog.cs — DS-6 (docs/decisions.md DR-DS-3): the activity-log half of "clean + badge + log".
//
// HoardPad.Core.Ai/FileOps/AiFileOpsService.cs (frozen spine) is the ONLY producer of the §9.3
// ActivityLogRecord shape, and that record mirrors the §9.3 file-op contract exactly (Op/Paths/Outcome/
// BytesBefore/BytesAfter — fields a scrub finding has no value for). DS-6's task note explicitly allows
// falling back to "the established logging seam" when the file-op activity log does not fit a non-file-op
// entry, which is the case here — repurposing ActivityLogRecord would either violate its documented §9.3
// shape or require touching AiFileOpsService (frozen). Instead this reuses the SAME durable-append
// primitive every other App-layer persisted surface already uses — IAtomicFileStore.AppendRecordAsync
// rooted under IAppDataPaths (see SettingsStore/SessionStore, and AiPanelViewModel's own undo-blob staging
// via the same _replayStore/_replayPaths handles) — for a NEW, dedicated NDJSON file. No Host/Core.Ai
// file changes; App-layer only.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DragonScrub;
using HoardPad.Core.Host.Abstractions;

namespace HoardPad.App.Ai;

/// <summary>
/// One persisted DragonScrub finding (mirrors <see cref="Finding"/>'s category/rule id/confidence only —
/// never <see cref="Finding.MatchedTextNormalized"/>, so no scrubbed/adversarial content round-trips to
/// disk via this log).
/// </summary>
public sealed record ScrubFindingLogItem(
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("ruleId")] string RuleId,
    [property: JsonPropertyName("confidence")] string Confidence);

/// <summary>One DS-6 NDJSON line: a scrub event that redacted at least one span.</summary>
public sealed record ScrubFindingLogEntry(
    [property: JsonPropertyName("ts")] DateTimeOffset Ts,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("spanCount")] int SpanCount,
    [property: JsonPropertyName("findings")] IReadOnlyList<ScrubFindingLogItem> Findings);

/// <summary>
/// Appends a <see cref="ScrubFindingLogEntry"/> to the DS-6 scrub-findings NDJSON log
/// (<c>scrub-findings.ndjson</c> under <see cref="IAppDataPaths.AppDataRoot"/>) via the same
/// <see cref="IAtomicFileStore.AppendRecordAsync"/> durability primitive (fsync-before-return, §3.3)
/// the rest of the App layer already uses for its own append logs.
/// </summary>
public static class ScrubFindingsLogWriter
{
    private const string FileName = "scrub-findings.ndjson";

    public static Task AppendAsync(
        IAtomicFileStore store,
        IAppDataPaths paths,
        DateTimeOffset ts,
        string source,
        int spanCount,
        IReadOnlyList<Finding> findings,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(paths);

        var items = new List<ScrubFindingLogItem>(findings.Count);
        foreach (Finding f in findings)
            items.Add(new ScrubFindingLogItem(f.Category, f.RuleId, f.Confidence));

        var entry = new ScrubFindingLogEntry(ts, source, spanCount, items);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(entry);
        string logPath = Path.Combine(paths.AppDataRoot, FileName);
        return store.AppendRecordAsync(logPath, payload, ct);
    }
}
