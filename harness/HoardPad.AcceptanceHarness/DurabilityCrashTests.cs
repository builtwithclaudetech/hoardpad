// DurabilityCrashTests.cs — the durability crash fixtures (M9.3, A-6 append-log torn-tail case).
//
// SC-5(c) requires every op to appear in the activity log; the §9.3 append-log durability model
// (per-record fsync + on-open truncate-repair) is what makes that log survive a crash. This fixture
// proves the production AtomicFileStore.OpenAndRepairAsync repairs a TORN TRAILING record — a crash
// mid-append leaves a partial final frame — by recovering every COMPLETE prior record and truncating
// the torn tail to the last validated offset. It exercises the real append-log the AiFileOpsService
// writes (the activity log), so the crash fixture rides the same artifact SC-5 depends on.

using System.Text;
using HoardPad.Core.Ai.Caps;
using HoardPad.Core.Ai.FileOps;
using HoardPad.Core.Host.Abstractions;
using Xunit;

namespace HoardPad.AcceptanceHarness;

public sealed class DurabilityCrashTests
{
    [Fact]
    public async Task TornTrailingRecordInTheActivityLogIsTruncateRepairedAndPriorRecordsSurvive()
    {
        using var stack = new RealStack();

        // Apply three ops via the real service so the activity log has three complete framed records.
        await stack.Service.ApplyAsync("conv-d", OpFactory.Create(stack.InRoot("d1.txt"), "1"), OpFactory.Context(), CancellationToken.None);
        await stack.Service.ApplyAsync("conv-d", OpFactory.Create(stack.InRoot("d2.txt"), "2"), OpFactory.Context(), CancellationToken.None);
        await stack.Service.ApplyAsync("conv-d", OpFactory.Create(stack.InRoot("d3.txt"), "3"), OpFactory.Context(), CancellationToken.None);

        string logPath = stack.Paths.ActivityLogPath(stack.SessionId);

        // Sanity: three complete records read back cleanly before the corruption.
        AppendLogRepairResult before = await stack.Store.OpenAndRepairAsync(
            logPath, AppendReadMode.StopAtFirstBad, CancellationToken.None);
        Assert.Equal(3, before.Records.Count);
        Assert.False(before.WasTruncated);
        long validEndBefore = before.ValidEndOffset;

        // Simulate a crash mid-append: append a TORN trailing frame — a length prefix claiming a long
        // payload, followed by only a few payload bytes (the record runs past EOF). This is exactly the
        // "torn tail" a power-cut mid-write leaves.
        await using (var fs = new FileStream(logPath, FileMode.Append, FileAccess.Write))
        {
            // 4-byte LE length prefix claiming 9999 bytes, then only 5 bytes of payload (no CRC).
            byte[] lenPrefix = { 0x0F, 0x27, 0x00, 0x00 };   // 9999 LE
            await fs.WriteAsync(lenPrefix);
            await fs.WriteAsync(Encoding.UTF8.GetBytes("torn!"));
            await fs.FlushAsync();
        }

        // On open, the repair recovers exactly the three COMPLETE prior records and truncates the torn tail.
        AppendLogRepairResult after = await stack.Store.OpenAndRepairAsync(
            logPath, AppendReadMode.StopAtFirstBad, CancellationToken.None);

        Assert.Equal(3, after.Records.Count);                         // every complete prior record survives.
        Assert.True(after.WasTruncated, "the torn trailing record must be detected and truncate-repaired.");
        Assert.Equal(validEndBefore, after.ValidEndOffset);           // repaired back to the last valid offset.

        // The physical file is now exactly the last valid offset (the torn tail is gone) — a subsequent
        // open is clean, with no torn tail re-detected.
        Assert.Equal(validEndBefore, new FileInfo(logPath).Length);
        AppendLogRepairResult reopened = await stack.Store.OpenAndRepairAsync(
            logPath, AppendReadMode.StopAtFirstBad, CancellationToken.None);
        Assert.Equal(3, reopened.Records.Count);
        Assert.False(reopened.WasTruncated);

        // The recovered records are the real activity-log lines (each a §9.3 JSON object with op+outcome).
        string firstRecord = Encoding.UTF8.GetString(after.Records[0].Span);
        Assert.Contains("\"op\":\"create\"", firstRecord);
        Assert.Contains("\"outcome\":\"applied\"", firstRecord);
    }

    [Fact]
    public async Task TornTrailingRecordInTheUndoJournalIsTruncateRepaired()
    {
        // The same per-record-fsync + truncate-repair model backs the undo journal (§9.4). A destructive
        // op's undo entry is appended via the SAME AppendRecordAsync; assert a torn tail on an undo-journal
        // entry file repairs identically (so a crash can never strand a half-written undo entry).
        using var stack = new RealStack();
        string target = stack.InRoot("u.txt");
        await File.WriteAllTextAsync(target, new string('a', 50));

        FileOpResult result = await stack.Service.ApplyAsync(
            "conv-u", OpFactory.Edit(target, new string('b', 5), priorBytes: 50, tool: WriteTool.Edit),
            OpFactory.Context(), CancellationToken.None);
        Assert.NotNull(result.JournalEntry);

        // Locate the undo-journal entry file the service wrote (undo/<undoRef>.entry).
        string undoRef = result.JournalEntry!.UndoRef;
        string entryPath = Path.Combine(stack.Paths.UndoJournalRoot, undoRef + ".entry");
        Assert.True(File.Exists(entryPath), "the destructive op must have written an undo-journal entry.");

        AppendLogRepairResult before = await stack.Store.OpenAndRepairAsync(
            entryPath, AppendReadMode.StopAtFirstBad, CancellationToken.None);
        Assert.Single(before.Records);
        long validEnd = before.ValidEndOffset;

        // Corrupt with a torn tail.
        await using (var fs = new FileStream(entryPath, FileMode.Append, FileAccess.Write))
        {
            await fs.WriteAsync(new byte[] { 0xFF, 0xFF, 0x00, 0x00 });   // claims 65535 bytes; none follow.
            await fs.FlushAsync();
        }

        AppendLogRepairResult after = await stack.Store.OpenAndRepairAsync(
            entryPath, AppendReadMode.StopAtFirstBad, CancellationToken.None);
        Assert.Single(after.Records);                 // the complete undo entry survives.
        Assert.True(after.WasTruncated);
        Assert.Equal(validEnd, after.ValidEndOffset);
    }
}
