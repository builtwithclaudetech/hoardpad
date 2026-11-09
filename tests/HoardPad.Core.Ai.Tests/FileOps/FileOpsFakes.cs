using System.Collections.Concurrent;
using HoardPad.Core.Ai.FileOps;
using HoardPad.Core.Host.Abstractions;
using HoardPad.Core.Safety;

namespace HoardPad.Core.Ai.Tests.FileOps;

/// <summary>
/// A call-order-recording fake <see cref="IAtomicFileStore"/>. It records every mutating/append call in
/// arrival order so a test can assert the M15 ordering (the journal append precedes the apply mutation)
/// and that the deny path issues NO mutation at all. No real I/O — append "writes" go to an in-memory log.
/// </summary>
public sealed class RecordingAtomicFileStore : IAtomicFileStore
{
    /// <summary>The ordered call trace: e.g. "append:/data/undo/u1.entry", "replace:/root/a.md", "create:/root/b.md".</summary>
    public List<string> Calls { get; } = new();

    /// <summary>The payloads appended per log path (so a test can read back the NDJSON record bytes).</summary>
    public Dictionary<string, List<ReadOnlyMemory<byte>>> Appended { get; } = new(StringComparer.Ordinal);

    private long _offset;

    public Task CreateWhenAbsentAsync(string destPath, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        Calls.Add($"create:{destPath}");
        return Task.CompletedTask;
    }

    public Task ReplaceWhenPresentAsync(string destPath, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        Calls.Add($"replace:{destPath}");
        return Task.CompletedTask;
    }

    public Task<long> AppendRecordAsync(string logPath, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        Calls.Add($"append:{logPath}");
        if (!Appended.TryGetValue(logPath, out List<ReadOnlyMemory<byte>>? list))
        {
            list = new List<ReadOnlyMemory<byte>>();
            Appended[logPath] = list;
        }
        list.Add(payload);
        _offset += payload.Length + 8;
        return Task.FromResult(_offset);
    }

    public Task<AppendLogRepairResult> OpenAndRepairAsync(string logPath, AppendReadMode mode, CancellationToken ct) =>
        Task.FromResult(new AppendLogRepairResult(Array.Empty<ReadOnlyMemory<byte>>(), 0, false));

    public Task<AtomicWriteCapability> ProbeAtomicCapabilityAsync(string destPath, CancellationToken ct) =>
        Task.FromResult(AtomicWriteCapability.Atomic);

    /// <summary>The mutating calls only (create/replace), in order — the asserts for "what touched the FS".</summary>
    public IReadOnlyList<string> Mutations => Calls.Where(c => c.StartsWith("create:", StringComparison.Ordinal)
                                                            || c.StartsWith("replace:", StringComparison.Ordinal)).ToList();

    /// <summary>The append calls only (activity log + undo journal), in order.</summary>
    public IReadOnlyList<string> AppendCalls => Calls.Where(c => c.StartsWith("append:", StringComparison.Ordinal)).ToList();
}

/// <summary>
/// A fake <see cref="ISandboxGate"/> driven by a raw-path rule, so a test never has to mint a
/// <see cref="NormcasedPath"/> (its factory is internal to HoardPad.Core, pinned away from Core.Ai.Tests).
/// By default everything at-or-under <see cref="Root"/> is Allowed and anything else is Denied; the
/// "resolved" path is just the raw path (the test supplies already-clean paths).
/// </summary>
public sealed class FakeSandboxGate : ISandboxGate
{
    public string Root { get; init; } = "/root";

    /// <summary>An optional override: raw path → decision, for the few cases that need an explicit verdict.</summary>
    public Dictionary<string, SandboxDecision> Overrides { get; } = new(StringComparer.Ordinal);

    public SandboxResolution Resolve(string rawCandidatePath)
    {
        if (Overrides.TryGetValue(rawCandidatePath, out SandboxDecision forced))
            return new SandboxResolution(forced, rawCandidatePath);

        bool inside = string.Equals(rawCandidatePath, Root, StringComparison.Ordinal)
            || rawCandidatePath.StartsWith(Root + "/", StringComparison.Ordinal);
        return new SandboxResolution(inside ? SandboxDecision.Allow : SandboxDecision.Deny, rawCandidatePath);
    }
}

/// <summary>
/// An in-memory <see cref="ICapStateStore"/> with real compare-and-write semantics, keyed by conversation.
/// Two conversation ids accrue independently (matching the host's O_EXCL-locked per-conversation file).
/// </summary>
public sealed class InMemoryCapStateStore : ICapStateStore
{
    private readonly ConcurrentDictionary<string, (string? Payload, long Version)> _byConversation = new(StringComparer.Ordinal);

    /// <summary>Read count per conversation — lets a test prove a replay never read the cap-state.</summary>
    public ConcurrentDictionary<string, int> ReadCount { get; } = new(StringComparer.Ordinal);

    /// <summary>Write count per conversation — lets a test prove a replay never wrote the cap-state.</summary>
    public ConcurrentDictionary<string, int> WriteCount { get; } = new(StringComparer.Ordinal);

    public Task<CapStateSnapshot> ReadAsync(string conversationId, CancellationToken ct)
    {
        ReadCount.AddOrUpdate(conversationId, 1, (_, n) => n + 1);
        if (_byConversation.TryGetValue(conversationId, out (string? Payload, long Version) entry))
            return Task.FromResult(new CapStateSnapshot(entry.Payload, entry.Version));
        return Task.FromResult(CapStateSnapshot.Empty);
    }

    public Task<CapStateWriteResult> WriteAsync(
        string conversationId, string payloadJson, long expectedVersion, CancellationToken ct)
    {
        WriteCount.AddOrUpdate(conversationId, 1, (_, n) => n + 1);
        long current = _byConversation.TryGetValue(conversationId, out (string? Payload, long Version) entry) ? entry.Version : 0;
        if (current != expectedVersion)
            return Task.FromResult(CapStateWriteResult.Conflict(current));

        long next = current + 1;
        _byConversation[conversationId] = (payloadJson, next);
        return Task.FromResult(CapStateWriteResult.Ok(next));
    }

    public string ResolveCapStateFilePath(string conversationId) =>
        $"/data/capstate/{conversationId}.capstate";
}

/// <summary>A minimal fake <see cref="IAppDataPaths"/> — only the activity-log + undo-journal roots are used by M4.3.</summary>
public sealed class FakeAppDataPaths : IAppDataPaths
{
    public string AppDataRoot => "/data";
    public string RecoveryDirectory => "/data/recovery";
    public string SettingsFilePath => "/data/settings.json";
    public string ActivityLogPath(string sessionId) => $"/data/activity/{sessionId}.log";
    public string UndoJournalRoot => "/data/undo";
    public void RestrictToCurrentUser(string path) { /* no-op in tests */ }
}

/// <summary>
/// A store that throws <see cref="AtomicWriteUnavailable"/> on the first mutation call (create/replace),
/// then delegates to the underlying recording store. Used to verify MUST-FIX 2a: a mutation failure must
/// log a blocked result and NOT propagate raw.
/// </summary>
public sealed class ThrowOnMutationStore : IAtomicFileStore
{
    private readonly RecordingAtomicFileStore _inner = new();
    private int _mutationCallCount;

    public IReadOnlyList<string> Calls => _inner.Calls;
    public IReadOnlyList<string> AppendCalls => _inner.AppendCalls;
    public Dictionary<string, List<ReadOnlyMemory<byte>>> Appended => _inner.Appended;

    public Task CreateWhenAbsentAsync(string destPath, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        if (++_mutationCallCount == 1)
            throw new AtomicWriteUnavailable();
        return _inner.CreateWhenAbsentAsync(destPath, content, ct);
    }

    public Task ReplaceWhenPresentAsync(string destPath, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        if (++_mutationCallCount == 1)
            throw new AtomicWriteUnavailable();
        return _inner.ReplaceWhenPresentAsync(destPath, content, ct);
    }

    public Task<long> AppendRecordAsync(string logPath, ReadOnlyMemory<byte> payload, CancellationToken ct) =>
        _inner.AppendRecordAsync(logPath, payload, ct);

    public Task<AppendLogRepairResult> OpenAndRepairAsync(string logPath, AppendReadMode mode, CancellationToken ct) =>
        _inner.OpenAndRepairAsync(logPath, mode, ct);

    public Task<AtomicWriteCapability> ProbeAtomicCapabilityAsync(string destPath, CancellationToken ct) =>
        _inner.ProbeAtomicCapabilityAsync(destPath, ct);
}

/// <summary>
/// A store that succeeds on journal and mutation but throws on the FIRST activity-log append after
/// the mutation, then succeeds on the second attempt (the service's one-retry path for MUST-FIX 2b).
/// The test exercises the path where BOTH attempts fail by setting <see cref="ActivityLogThrowCount"/>
/// to a value &gt;= 2.
/// </summary>
public sealed class ThrowOnActivityLogStore : IAtomicFileStore
{
    private readonly RecordingAtomicFileStore _inner = new();
    private int _activityAppendsSeen;

    /// <summary>How many consecutive activity-log appends to throw on. 1 → first fails, retry succeeds. 2+ → both fail → ActivityLogWriteFailed.</summary>
    public int ActivityLogThrowCount { get; init; } = 1;

    public IReadOnlyList<string> Calls => _inner.Calls;
    public IReadOnlyList<string> AppendCalls => _inner.AppendCalls;
    public Dictionary<string, List<ReadOnlyMemory<byte>>> Appended => _inner.Appended;

    public Task CreateWhenAbsentAsync(string destPath, ReadOnlyMemory<byte> content, CancellationToken ct) =>
        _inner.CreateWhenAbsentAsync(destPath, content, ct);

    public Task ReplaceWhenPresentAsync(string destPath, ReadOnlyMemory<byte> content, CancellationToken ct) =>
        _inner.ReplaceWhenPresentAsync(destPath, content, ct);

    public Task<long> AppendRecordAsync(string logPath, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        // Only intercept appends to the activity log (paths contain "/activity/"). Journal appends pass through.
        if (logPath.Contains("/activity/", StringComparison.Ordinal))
        {
            _activityAppendsSeen++;
            if (_activityAppendsSeen <= ActivityLogThrowCount)
                throw new IOException("simulated activity-log I/O failure");
        }
        return _inner.AppendRecordAsync(logPath, payload, ct);
    }

    public Task<AppendLogRepairResult> OpenAndRepairAsync(string logPath, AppendReadMode mode, CancellationToken ct) =>
        _inner.OpenAndRepairAsync(logPath, mode, ct);

    public Task<AtomicWriteCapability> ProbeAtomicCapabilityAsync(string destPath, CancellationToken ct) =>
        _inner.ProbeAtomicCapabilityAsync(destPath, ct);
}

/// <summary>
/// An <see cref="ICapStateStore"/> that returns <see cref="CapStateWriteResult.Conflict"/> a configurable
/// number of times before committing, exercising the <c>AccountAsync</c> compare-and-write retry loop
/// (MUST-FIX 3). On each injected conflict the payload is persisted and the version is advanced (so the
/// retry re-reads a new base) but the caller sees a Conflict result and must retry. This means every
/// increment is applied to the running state exactly once across the retries — no lost update, no re-arm.
/// </summary>
public sealed class ConflictingCapStateStore : ICapStateStore
{
    // Internal storage: one slot per conversation — (payload, version).
    private readonly Dictionary<string, (string? Payload, long Version)> _store = new(StringComparer.Ordinal);
    private int _conflictsInjected;

    /// <summary>How many times to inject a Conflict before finally allowing the write.</summary>
    public int ConflictsBeforeCommit { get; init; } = 2;

    public int TotalReads { get; private set; }
    public int TotalWrites { get; private set; }

    public Task<CapStateSnapshot> ReadAsync(string conversationId, CancellationToken ct)
    {
        TotalReads++;
        if (_store.TryGetValue(conversationId, out (string? Payload, long Version) entry))
            return Task.FromResult(new CapStateSnapshot(entry.Payload, entry.Version));
        return Task.FromResult(CapStateSnapshot.Empty);
    }

    public Task<CapStateWriteResult> WriteAsync(
        string conversationId, string payloadJson, long expectedVersion, CancellationToken ct)
    {
        TotalWrites++;
        long current = _store.TryGetValue(conversationId, out (string? Payload, long Version) entry) ? entry.Version : 0;

        if (current != expectedVersion)
            return Task.FromResult(CapStateWriteResult.Conflict(current));

        if (_conflictsInjected < ConflictsBeforeCommit)
        {
            // Simulate a concurrent writer that advanced the version but left the PAYLOAD unchanged.
            // This means the retry's re-read returns the same payload state (same cap counts), so
            // AccountAsync re-applies one increment against the same base — producing the correct
            // final value. The version bump forces the retry to re-read before writing.
            _conflictsInjected++;
            long bumpedVersion = current + 1;
            // Preserve the CURRENT payload (the concurrent writer's commit didn't change our counts).
            string? currentPayload = entry.Payload;
            _store[conversationId] = (currentPayload, bumpedVersion);
            return Task.FromResult(CapStateWriteResult.Conflict(bumpedVersion));
        }

        // No more injected conflicts — commit normally.
        long committed = current + 1;
        _store[conversationId] = (payloadJson, committed);
        return Task.FromResult(CapStateWriteResult.Ok(committed));
    }

    public string ResolveCapStateFilePath(string conversationId) =>
        $"/data/capstate/{conversationId}.capstate";
}
