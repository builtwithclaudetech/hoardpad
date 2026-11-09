// CrossLanguageBoundaryTests.cs — the cross-language FR-38/FR-40 agreement gate (M9.3, carried
// forward from the M5.1 adversarial gate).
//
// SC-5 exists, at bottom, to assert ONE thing: the shipped C# classifier/accounting
// (CapClassifier / CapAccounting) and the shipped python deny-by-default hook (deny_by_default.py)
// make the SAME decision on the boundary inputs — because they are two independent implementations
// of the same predicates, and a drift between them is a sandbox hole. This drives BOTH languages on
// the two boundary inputs the M5.1 gate named and asserts they agree:
//
//   (a) FR-38 magnitude — a shrink-to-15% Edit of a 100-byte file. delta=85, 85*100 >= 80*100
//       (8500 >= 8000) => DestructiveOverwrite. C# CapClassifier.Classify must return
//       DestructiveOverwrite; the python hook must treat it as destructive (denied when the
//       destructive cap is tripped, while a matched SUB-threshold edit under the same tripped
//       destructive cap is ALLOWED — isolating the magnitude predicate, not the cap).
//
//   (b) FR-40 off-by-one — the at-exactly-cap op. With cap-1 distinct files already touched, the
//       cap-th distinct file (files_after_count == cap) trips in BOTH: C# CapAccounting sets
//       FilesTouchedCapTripped (>= cap) and RequiresConfirm; the python hook denies it (>= cap).
//
// Each boundary drives the REAL python hook (a subprocess) against a hand-written cap-state file in
// the exact PascalCase shape both languages share, and compares against the C# logic over the same
// inputs. No reimplementation of either side — the shipped artifacts are the things under test.

using System.Text.Json;
using HoardPad.Core.Ai.Caps;
using Xunit;

namespace HoardPad.AcceptanceHarness;

public sealed class CrossLanguageBoundaryTests
{
    // ---- (a) FR-38 magnitude: shrink-to-15% Edit of a 100-byte file ----

    [Fact]
    public void CSharpClassifiesShrinkTo15PctAsDestructive()
    {
        // The C# side: a 100B -> 15B Edit meets the >= 80% magnitude predicate => DestructiveOverwrite.
        var op = new OpClassificationInput(WriteTool.Edit, TargetExists: true, TargetPreOpBytes: 100, ProposedBytes: 15);
        OpKind kind = CapClassifier.Classify(op, CapConfig.PinnedDestructiveMagnitudePercent);
        Assert.Equal(OpKind.DestructiveOverwrite, kind);

        // And a 100B -> 90B Edit (delta=10, 10% < 80%) is SUB-threshold — the negative side of the predicate.
        var subOp = new OpClassificationInput(WriteTool.Edit, TargetExists: true, TargetPreOpBytes: 100, ProposedBytes: 90);
        Assert.Equal(OpKind.SubThresholdEdit, CapClassifier.Classify(subOp, CapConfig.PinnedDestructiveMagnitudePercent));
    }

    [Fact]
    public async Task PythonHookAgreesShrinkTo15PctIsDestructiveButSubThresholdIsNot()
    {
        using var stack = new RealStack();

        // An existing 100-byte file in-sandbox.
        string target = stack.InRoot("large.txt");
        await File.WriteAllTextAsync(target, new string('x', 100));

        // Cap-state with the DESTRUCTIVE cap already tripped (so a DestructiveOverwrite is denied, but a
        // SubThresholdEdit — which doesn't touch the destructive cap — is allowed). This isolates the
        // magnitude predicate: the hook's decision differs ONLY by how it CLASSIFIES the op.
        string capFile = WriteCapState(stack, new CapStatePayload(
            CreateCount: 0, DestructiveCount: 10, FilesTouched: Array.Empty<string>(),
            CreateCapTripped: false, DestructiveCapTripped: true, FilesTouchedCapTripped: false));

        Dictionary<string, string?> env = stack.HookEnv();
        env["HOARDPAD_CAP_STATE_FILE"] = capFile;

        // The shrink-to-15B Edit: the hook must classify it DESTRUCTIVE => denied (destructive cap tripped).
        string destructiveEv = HookDriver.BuildEvent("Edit", new Dictionary<string, string>
        {
            ["file_path"] = target,
            ["old_string"] = new string('x', 100),
            ["new_string"] = new string('y', 15),
        });
        HookOutcome destructive = await HookDriver.RunAsync(destructiveEv, env, CancellationToken.None);
        Assert.Equal(HookDecision.Deny, destructive.Decision);
        Assert.Contains("FR-38", destructive.Reason, StringComparison.OrdinalIgnoreCase);

        // A 100B -> 90B Edit (10% < 80%): the hook must classify it SUB-THRESHOLD => allowed despite the
        // tripped destructive cap (a sub-threshold edit touches no per-op cap, only FR-40 which is untripped).
        string subEv = HookDriver.BuildEvent("Edit", new Dictionary<string, string>
        {
            ["file_path"] = target,
            ["old_string"] = new string('x', 10),
            ["new_string"] = new string('z', 90),
        });
        HookOutcome sub = await HookDriver.RunAsync(subEv, env, CancellationToken.None);
        Assert.Equal(HookDecision.Allow, sub.Decision);
    }

    // ---- (b) FR-40 off-by-one: the at-exactly-cap op ----

    [Fact]
    public void CSharpTripsFr40AtExactlyCap()
    {
        // Build a state with cap-1 distinct files already touched (cap = 20).
        const int cap = CapConfig.PinnedFilesTouchedCap;
        CapState state = CapState.Empty;
        for (int i = 0; i < cap - 1; i++)
            state = CapAccounting.Account(state, OpKind.Create, $"/root/f{i}.txt", CapConfig.Pinned).State;
        Assert.Equal(cap - 1, state.FilesTouchedCount);

        // The cap-th distinct file (files_after_count == cap) trips FR-40 and requires a confirm.
        CapDecision atCap = CapAccounting.Account(state, OpKind.Create, "/root/at-cap.txt", CapConfig.Pinned);
        Assert.Equal(cap, atCap.State.FilesTouchedCount);
        Assert.True(atCap.State.FilesTouchedCapTripped);
        Assert.True(atCap.RequiresConfirm);
        Assert.Equal(CapTrigger.FilesTouchedCap, atCap.Trigger);
    }

    [Fact]
    public async Task PythonHookDeniesTheCapthDistinctFile()
    {
        using var stack = new RealStack();
        const int cap = CapConfig.PinnedFilesTouchedCap;

        // Cap-state with cap-1 distinct canonical paths already in FilesTouched (none tripped yet). The
        // canonical form must match what the hook computes (os.path.normcase of resolve()), so we use
        // in-sandbox absolute paths the hook will canonicalize identically.
        string[] existing = Enumerable.Range(0, cap - 1)
            .Select(i => CanonicalInRoot(stack, $"f{i}.txt")).ToArray();
        string capFile = WriteCapState(stack, new CapStatePayload(
            CreateCount: 0, DestructiveCount: 0, FilesTouched: existing,
            CreateCapTripped: false, DestructiveCapTripped: false, FilesTouchedCapTripped: false));

        Dictionary<string, string?> env = stack.HookEnv();
        env["HOARDPAD_CAP_STATE_FILE"] = capFile;

        // The cap-th distinct file (a new create) => files_after_count == cap == FR-40 cap => DENY.
        string ev = HookDriver.BuildEvent("Write", new Dictionary<string, string>
        {
            ["file_path"] = stack.InRoot("at-cap.txt"),
            ["content"] = "the 20th distinct file",
        });
        HookOutcome outcome = await HookDriver.RunAsync(ev, env, CancellationToken.None);
        Assert.Equal(HookDecision.Deny, outcome.Decision);
        Assert.Contains("FR-40", outcome.Reason, StringComparison.OrdinalIgnoreCase);

        // And the (cap-1)th would have been allowed — prove the boundary is exactly at the cap, not before.
        string[] existingMinus2 = Enumerable.Range(0, cap - 2)
            .Select(i => CanonicalInRoot(stack, $"g{i}.txt")).ToArray();
        string underCapFile = WriteCapState(stack, new CapStatePayload(
            CreateCount: 0, DestructiveCount: 0, FilesTouched: existingMinus2,
            CreateCapTripped: false, DestructiveCapTripped: false, FilesTouchedCapTripped: false), name: "undercap");
        env["HOARDPAD_CAP_STATE_FILE"] = underCapFile;
        string underEv = HookDriver.BuildEvent("Write", new Dictionary<string, string>
        {
            ["file_path"] = stack.InRoot("g-under.txt"),
            ["content"] = "the 19th distinct file (under cap)",
        });
        HookOutcome under = await HookDriver.RunAsync(underEv, env, CancellationToken.None);
        Assert.Equal(HookDecision.Allow, under.Decision);
    }

    // ---- helpers: write the shared PascalCase cap-state shape both languages read ----

    private readonly record struct CapStatePayload(
        int CreateCount, int DestructiveCount, IReadOnlyList<string> FilesTouched,
        bool CreateCapTripped, bool DestructiveCapTripped, bool FilesTouchedCapTripped);

    private static string WriteCapState(RealStack stack, CapStatePayload payload, string name = "boundary")
    {
        // The exact PascalCase JSON both the C# CapStateSerialization and the python hook read.
        string json = JsonSerializer.Serialize(new
        {
            payload.CreateCount,
            payload.DestructiveCount,
            payload.FilesTouched,
            payload.CreateCapTripped,
            payload.DestructiveCapTripped,
            payload.FilesTouchedCapTripped,
        });
        string path = Path.Combine(stack.CapStateDirectory, $"{name}.capstate");
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>An in-root path canonicalized the way the hook does (resolve + normcase) — identity on Linux.</summary>
    private static string CanonicalInRoot(RealStack stack, string relative)
    {
        string abs = stack.InRoot(relative);
        // The hook computes os.path.normcase(str(Path(abs).resolve())). On Linux normcase is identity and
        // resolve() returns the absolute, symlink-free form; for a non-existent in-root leaf under an
        // already-real directory, Path.GetFullPath matches realpath's result.
        return Path.GetFullPath(abs);
    }
}
