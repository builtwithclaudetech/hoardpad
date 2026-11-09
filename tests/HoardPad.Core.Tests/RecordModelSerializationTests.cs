using System.Globalization;
using System.Text.Json;
using HoardPad.Core.Records;
using Xunit;

namespace HoardPad.Core.Tests;

/// <summary>
/// M1.3 tests pinning the §9.3 (activity log) + §9.4 (undo journal) record-model field contracts.
/// Each model is round-tripped through System.Text.Json and the on-the-wire JSON is asserted to carry
/// the EXACT field names + enum tokens the persisted contract names — a divergence here breaks the
/// Host AtomicFileStore (M2.1), the AI file-ops service (M4.3), and the reverse decision (M1.4).
/// No serializer options are supplied: the contract is pinned by the types' own attributes.
/// </summary>
public sealed class RecordModelSerializationTests
{
    private static DateTimeOffset Iso(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture);

    private static JsonElement Wire<T>(T value)
    {
        var json = JsonSerializer.Serialize(value);
        return JsonDocument.Parse(json).RootElement;
    }

    private static void AssertHasString(JsonElement el, string name, string expected)
    {
        Assert.True(el.TryGetProperty(name, out var prop), $"missing field: {name}");
        Assert.Equal(expected, prop.GetString());
    }

    // ── §9.3 activity-log record ──────────────────────────────────────────────

    private static ActivityLogRecord SampleActivity() => new(
        Ts: Iso("2026-06-13T18:04:22.117Z"),
        RequestId: "turn-7f3a",
        Backend: AiBackend.ClaudeCli,
        ConfiguredMode: AgencyMode.AutoApply,
        AgencyMode: AgencyMode.ApproveEach,
        DowngradeReason: DowngradeReason.Clobber,
        Op: ActivityOp.Move,
        Paths: new OpPaths("/home/you/notes/b.md", "/home/you/notes/a.md", "/home/you/notes/b.md"),
        Outcome: ActivityOutcome.Applied,
        BytesBefore: 1840,
        BytesAfter: 1912,
        UndoRef: "undo/turn-7f3a-03",
        Error: null,
        DurationMs: 41,
        Tokens: new TokenUsage(5120, 380),
        CostUsd: 0.012m);

    [Fact]
    public void ActivityLogRecordFieldNamesAndEnumTokensMatchSection93()
    {
        var el = Wire(SampleActivity());

        AssertHasString(el, "ts", "2026-06-13T18:04:22.117+00:00");
        AssertHasString(el, "requestId", "turn-7f3a");
        AssertHasString(el, "backend", "claude-cli");
        AssertHasString(el, "configuredMode", "auto-apply");
        AssertHasString(el, "agencyMode", "approve-each");
        AssertHasString(el, "downgradeReason", "clobber");
        AssertHasString(el, "op", "move");
        AssertHasString(el, "outcome", "applied");
        AssertHasString(el, "undoRef", "undo/turn-7f3a-03");

        Assert.Equal(1840, el.GetProperty("bytesBefore").GetInt64());
        Assert.Equal(1912, el.GetProperty("bytesAfter").GetInt64());
        Assert.Equal(41, el.GetProperty("durationMs").GetInt64());

        // error appears explicitly as null (per the §9.3 example line).
        Assert.True(el.TryGetProperty("error", out var error));
        Assert.Equal(JsonValueKind.Null, error.ValueKind);

        // paths is an OBJECT (MA13), target/source/dest.
        var paths = el.GetProperty("paths");
        AssertHasString(paths, "target", "/home/you/notes/b.md");
        AssertHasString(paths, "source", "/home/you/notes/a.md");
        AssertHasString(paths, "dest", "/home/you/notes/b.md");

        // tokens object: input/output.
        var tokens = el.GetProperty("tokens");
        Assert.Equal(5120, tokens.GetProperty("input").GetInt64());
        Assert.Equal(380, tokens.GetProperty("output").GetInt64());

        Assert.Equal(0.012m, el.GetProperty("costUsd").GetDecimal());
    }

    [Fact]
    public void ActivityLogRecordRoundTrips()
    {
        var original = SampleActivity();
        var json = JsonSerializer.Serialize(original);
        var back = JsonSerializer.Deserialize<ActivityLogRecord>(json);

        Assert.Equal(original, back);
    }

    [Fact]
    public void ActivityLogRecordOptionalTokensAndCostOmittedWhenNull()
    {
        var record = SampleActivity() with { Tokens = null, CostUsd = null };
        var el = Wire(record);

        Assert.False(el.TryGetProperty("tokens", out _));
        Assert.False(el.TryGetProperty("costUsd", out _));
    }

    [Fact]
    public void OpPathsTargetOnlyOmitsSourceAndDest()
    {
        var el = Wire(new OpPaths("/home/you/notes/todo.md"));

        AssertHasString(el, "target", "/home/you/notes/todo.md");
        Assert.False(el.TryGetProperty("source", out _));
        Assert.False(el.TryGetProperty("dest", out _));
    }

    [Theory]
    [InlineData(AiBackend.ClaudeCli, "claude-cli")]
    [InlineData(AiBackend.AnthropicApi, "anthropic-api")]
    [InlineData(AiBackend.Local, "local")]
    public void AiBackendKebabLowerTokens(AiBackend backend, string token)
    {
        var el = Wire(SampleActivity() with { Backend = backend });
        AssertHasString(el, "backend", token);
    }

    [Theory]
    [InlineData(DowngradeReason.None, "none")]
    [InlineData(DowngradeReason.HomeRoot, "home-root")]
    [InlineData(DowngradeReason.LargeRoot, "large-root")]
    [InlineData(DowngradeReason.OverCap, "over-cap")]
    [InlineData(DowngradeReason.CreateCap, "create-cap")]
    [InlineData(DowngradeReason.DestructiveCap, "destructive-cap")]
    [InlineData(DowngradeReason.FilesTouchedCap, "files-touched-cap")]
    public void DowngradeReasonKebabLowerTokens(DowngradeReason reason, string token)
    {
        var el = Wire(SampleActivity() with { DowngradeReason = reason });
        AssertHasString(el, "downgradeReason", token);
    }

    // ── §9.4 undo-journal entry ───────────────────────────────────────────────

    private static UndoJournalEntry SampleUndo() => new(
        UndoRef: "undo/turn-7f3a-03",
        BufferJournalId: "buf-9c2e",
        RequestId: "turn-7f3a",
        Op: FileOp.Edit,
        Paths: new OpPaths("/home/you/notes/todo.md"),
        PriorContent: new BeforeImage("blob", "before/turn-7f3a-03.bin"),
        DestPriorContent: null,
        DestPriorContentHash: null,
        PreOpContentHash: "sha256:9b1c",
        PreOpMtime: Iso("2026-06-13T18:04:22.061Z"),
        SnapshotSkipped: false);

    [Fact]
    public void UndoJournalEntryFieldNamesAndShapeMatchSection94()
    {
        var el = Wire(SampleUndo());

        AssertHasString(el, "undoRef", "undo/turn-7f3a-03");
        AssertHasString(el, "bufferJournalId", "buf-9c2e");
        AssertHasString(el, "requestId", "turn-7f3a");
        AssertHasString(el, "op", "edit");
        AssertHasString(el, "preOpContentHash", "sha256:9b1c");
        AssertHasString(el, "preOpMtime", "2026-06-13T18:04:22.061+00:00");
        Assert.False(el.GetProperty("snapshotSkipped").GetBoolean());

        // priorContent before-image object: kind/ref.
        var prior = el.GetProperty("priorContent");
        AssertHasString(prior, "kind", "blob");
        AssertHasString(prior, "ref", "before/turn-7f3a-03.bin");

        // Nullable clobber/dest fields present as null (stable shape for the reverse decision).
        Assert.Equal(JsonValueKind.Null, el.GetProperty("destPriorContent").ValueKind);
        Assert.Equal(JsonValueKind.Null, el.GetProperty("destPriorContentHash").ValueKind);

        // paths object: target.
        AssertHasString(el.GetProperty("paths"), "target", "/home/you/notes/todo.md");
    }

    [Fact]
    public void UndoJournalEntryRoundTrips()
    {
        var original = SampleUndo();
        var json = JsonSerializer.Serialize(original);
        var back = JsonSerializer.Deserialize<UndoJournalEntry>(json);

        Assert.Equal(original, back);
    }

    [Fact]
    public void UndoJournalEntryCreateOpHasNullPriorContent()
    {
        var create = SampleUndo() with
        {
            Op = FileOp.Create,
            PriorContent = null,
            PreOpContentHash = null,
            PreOpMtime = null,
        };
        var el = Wire(create);

        AssertHasString(el, "op", "create");
        Assert.Equal(JsonValueKind.Null, el.GetProperty("priorContent").ValueKind);
        Assert.Equal(JsonValueKind.Null, el.GetProperty("preOpContentHash").ValueKind);
        Assert.Equal(JsonValueKind.Null, el.GetProperty("preOpMtime").ValueKind);
    }

    [Theory]
    [InlineData(FileOp.Create, "create")]
    [InlineData(FileOp.Copy, "copy")]
    [InlineData(FileOp.Move, "move")]
    [InlineData(FileOp.Edit, "edit")]
    [InlineData(FileOp.Delete, "delete")]
    public void FileOpVocabularyTokens(FileOp op, string token)
    {
        var el = Wire(SampleUndo() with { Op = op });
        AssertHasString(el, "op", token);
    }

    // ── §4.5 file-operation record ────────────────────────────────────────────

    [Fact]
    public void FileOperationFieldNamesAndRoundTrip()
    {
        var original = new FileOperation(
            RequestId: "turn-7f3a",
            Op: FileOp.Move,
            Paths: new OpPaths("/home/you/notes/b.md", "/home/you/notes/a.md", "/home/you/notes/b.md"));

        var el = Wire(original);
        AssertHasString(el, "requestId", "turn-7f3a");
        AssertHasString(el, "op", "move");
        AssertHasString(el.GetProperty("paths"), "source", "/home/you/notes/a.md");

        var back = JsonSerializer.Deserialize<FileOperation>(JsonSerializer.Serialize(original));
        Assert.Equal(original, back);
    }
}
