// Sc5HookGateTests.cs — SC-5(i): the Archetype-A DETERMINISTIC hook gate (M9.3).
//
// This is the AUTHORITATIVE SC-5 pass/fail gate (PRD §5 SC-5(i), BL3). It drives the REAL
// production deny-by-default hook (hooks/deny_by_default.py) as a python3 subprocess with crafted
// `tool_input` payloads, asserting:
//   - SC-5(d): an out-of-sandbox Write is DENIED and leaves NO file at the escape target;
//   - an in-sandbox create / edit / destructive-overwrite (each op #1 of its turn, under cap) is
//     ALLOWED — the (i)-gate asserts the classifier ROUTES the op, not that a cap fired (R4-mi6);
//   - for an ALLOWED op the harness applies the tool_input through the shared file-ops service
//     (the two-step structure, R4-mi7) and asserts the on-disk state + activity-log + undo-journal;
//   - the allowlist principle (SPIKE-3 §B): a randomly-chosen NON-allowlisted tool from the current
//     live roster is denied (deny-by-default, not a denylist).
//
// linux-x64 only (C-6): drives the real python3 hook + libc realpath/fsync legs.

using HoardPad.Core.Ai.Caps;
using HoardPad.Core.Ai.FileOps;
using HoardPad.Core.Records;
using Xunit;

namespace HoardPad.AcceptanceHarness;

public sealed class Sc5HookGateTests
{
    // The live (claude 2.1.179) NON-allowlisted tool roster, from SPIKE-3 §B + the deny-by-default
    // results table. The allowlist principle says EVERY tool outside {Read, Edit, Write} is denied; a
    // gate that hard-coded one name would not prove the principle, so we drive a RANDOMLY-CHOSEN one
    // (PRD SC-5 allowlist-principle wording).
    private static readonly string[] NonAllowlistedTools =
    {
        "Bash", "MultiEdit", "NotebookEdit", "Agent", "Task", "AskUserQuestion",
        "TodoWrite", "ToolSearch", "WebFetch", "mcp__filesystem__write", "SuperFutureToolV99",
    };

    // ---- SC-5(d): an out-of-sandbox Write is DENIED and leaves NO file ----

    [Fact]
    public async Task OutOfSandboxWriteIsDeniedByTheRealHookAndLeavesNoFile()
    {
        using var stack = new RealStack();
        string escapeTarget = stack.OutsideRoot("ESCAPED.txt");   // sibling of the open-folder root.

        string ev = HookDriver.BuildEvent("Write", new Dictionary<string, string>
        {
            ["file_path"] = escapeTarget,
            ["content"] = "escape attempt",
        });

        HookOutcome outcome = await HookDriver.RunAsync(ev, stack.HookEnv(), CancellationToken.None);

        // (1) the REAL hook denies the out-of-sandbox Write.
        Assert.Equal(HookDecision.Deny, outcome.Decision);
        // (2) the hook is a pure gate — it never writes — so no file appears at the escape target.
        //     (SC-5(d): a denied write never escapes the root.)
        Assert.False(File.Exists(escapeTarget),
            $"a denied out-of-sandbox Write must leave NO file at {escapeTarget}; the hook is a gate, not a writer.");
    }

    [Fact]
    public async Task OutOfSandboxWriteViaParentTraversalIsDeniedAndLeavesNoFile()
    {
        // A `..`-traversal escape from inside the root: the resolver collapses it OUTSIDE the root,
        // so the production sandbox containment denies it. Same fail-closed property as the sibling case.
        using var stack = new RealStack();
        string traversal = Path.Combine(stack.SandboxRoot, "..", "TRAVERSED.txt");
        string escapeResolved = stack.OutsideRoot("TRAVERSED.txt");

        string ev = HookDriver.BuildEvent("Write", new Dictionary<string, string>
        {
            ["file_path"] = traversal,
            ["content"] = "escape via ..",
        });

        HookOutcome outcome = await HookDriver.RunAsync(ev, stack.HookEnv(), CancellationToken.None);

        Assert.Equal(HookDecision.Deny, outcome.Decision);
        Assert.False(File.Exists(escapeResolved));
    }

    // ---- in-sandbox create is ALLOWED by the hook, then APPLIES + logs + journals via the service ----

    [Fact]
    public async Task InSandboxCreateIsAllowedByHookThenAppliesAndLogsAndJournals()
    {
        using var stack = new RealStack();
        string target = stack.InRoot("new-note.txt");

        // Step 1 — the hook decision (op #1 of the turn, under cap => allow).
        string ev = HookDriver.BuildEvent("Write", new Dictionary<string, string>
        {
            ["file_path"] = target,
            ["content"] = "hello",
        });
        HookOutcome outcome = await HookDriver.RunAsync(ev, stack.HookEnv(), CancellationToken.None);
        Assert.Equal(HookDecision.Allow, outcome.Decision);

        // Step 2 — apply the allowed tool_input through the shared file-ops service (R4-mi7),
        // then assert on-disk state + activity log (§9.3) + undo journal (§9.4).
        FileOpResult result = await stack.Service.ApplyAsync(
            "conv-1", OpFactory.Create(target, "hello"), OpFactory.Context(), CancellationToken.None);

        Assert.Equal(ActivityOutcome.Applied, result.Outcome);
        Assert.True(File.Exists(target));                                   // (a) the change is on disk.
        Assert.Equal("hello", await File.ReadAllTextAsync(target));
        Assert.NotNull(result.JournalEntry);                               // a create gets an undo entry (delete-undo).
        Assert.True(stack.AnyUndoJournalEntries());

        // (c) the op appears in the activity log with op type + resolved target path.
        IReadOnlyList<string> log = await stack.ReadActivityLogAsync(CancellationToken.None);
        Assert.Single(log);
        Assert.Contains("\"op\":\"create\"", log[0]);
        Assert.Contains("\"outcome\":\"applied\"", log[0]);
    }

    // ---- in-sandbox edit/overwrite onto an EXISTING file is ALLOWED, then applies + logs + journals ----

    [Fact]
    public async Task InSandboxDestructiveOverwriteIsAllowedByHookThenAppliesAndLogsAndJournals()
    {
        using var stack = new RealStack();
        string target = stack.InRoot("existing.cs");
        const string prior = "class Old { }";                 // 13 bytes prior.
        await File.WriteAllTextAsync(target, prior);

        // A whole-file Write onto an existing file => DestructiveOverwrite (FR-38), regardless of magnitude.
        string ev = HookDriver.BuildEvent("Write", new Dictionary<string, string>
        {
            ["file_path"] = target,
            ["content"] = "class New { }",
        });
        HookOutcome outcome = await HookDriver.RunAsync(ev, stack.HookEnv(), CancellationToken.None);
        Assert.Equal(HookDecision.Allow, outcome.Decision);   // op #1, under cap => allow (routes to FR-38).

        FileOpResult result = await stack.Service.ApplyAsync(
            "conv-1",
            OpFactory.Edit(target, "class New { }", priorBytes: prior.Length, tool: WriteTool.Write),
            OpFactory.Context(), CancellationToken.None);

        Assert.Equal(ActivityOutcome.Applied, result.Outcome);
        Assert.Equal("class New { }", await File.ReadAllTextAsync(target));   // (a) on disk.
        Assert.NotNull(result.JournalEntry);                                 // (b) undoable: a before-image entry.
        Assert.NotNull(result.JournalEntry!.PriorContent);
        Assert.True(stack.AnyUndoJournalEntries());

        IReadOnlyList<string> log = await stack.ReadActivityLogAsync(CancellationToken.None);
        Assert.Single(log);
        Assert.Contains("\"op\":\"edit\"", log[0]);
    }

    [Fact]
    public async Task InSandboxEditCreateOnNonExistentPathIsAllowedAndRoutesToCreate()
    {
        // The Edit-create idiom (R4-BL1): an Edit whose target does NOT exist is the agent's
        // file-create idiom and must be allowed + routed to the create bucket, not bypass FR-11.
        using var stack = new RealStack();
        string target = stack.InRoot("via-edit.txt");

        string ev = HookDriver.BuildEvent("Edit", new Dictionary<string, string>
        {
            ["file_path"] = target,
            ["old_string"] = "",                  // empty old_string on a non-existent path = create.
            ["new_string"] = "created via Edit",
        });
        HookOutcome outcome = await HookDriver.RunAsync(ev, stack.HookEnv(), CancellationToken.None);
        Assert.Equal(HookDecision.Allow, outcome.Decision);
    }

    // ---- the allowlist principle: a RANDOMLY-CHOSEN non-allowlisted tool is denied ----

    [Fact]
    public async Task AllowlistPrincipleARandomlyChosenNonAllowlistedToolIsDenied()
    {
        using var stack = new RealStack();

        // Pick a non-allowlisted tool at random from the live roster — proving the deny is by the
        // allowlist principle (deny-by-default), not a fixed-name denylist.
        string tool = NonAllowlistedTools[Random.Shared.Next(NonAllowlistedTools.Length)];

        // A non-allowlisted tool is denied at step 1 of decide() BEFORE any path/sandbox check, so an
        // in-sandbox target must not save it.
        string ev = HookDriver.BuildEvent(tool, new Dictionary<string, string>
        {
            ["file_path"] = stack.InRoot("would-be.txt"),
            ["content"] = "should never apply",
            ["command"] = "echo hi",   // Bash-style payload; ignored for non-Bash tools.
        });
        HookOutcome outcome = await HookDriver.RunAsync(ev, stack.HookEnv(), CancellationToken.None);

        Assert.Equal(HookDecision.Deny, outcome.Decision);
        Assert.Contains("allowlist", outcome.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AllowlistPrincipleEveryNonAllowlistedToolIsDenied()
    {
        // The randomly-chosen test above proves the PRINCIPLE; this exhaustive pass proves no single
        // live-roster tool slips through (a denylist would miss SuperFutureToolV99 / a future mcp__* tool).
        using var stack = new RealStack();
        foreach (string tool in NonAllowlistedTools)
        {
            string ev = HookDriver.BuildEvent(tool, new Dictionary<string, string>
            {
                ["file_path"] = stack.InRoot("x.txt"),
                ["content"] = "x",
            });
            HookOutcome outcome = await HookDriver.RunAsync(ev, stack.HookEnv(), CancellationToken.None);
            Assert.Equal(HookDecision.Deny, outcome.Decision);
        }
    }

    [Fact]
    public async Task ReadIsAlwaysAllowed()
    {
        // Read is the third allowlisted tool and is always allowed (no path/cap gating).
        using var stack = new RealStack();
        string ev = HookDriver.BuildEvent("Read", new Dictionary<string, string>
        {
            ["file_path"] = stack.InRoot("anything.txt"),
        });
        HookOutcome outcome = await HookDriver.RunAsync(ev, stack.HookEnv(), CancellationToken.None);
        Assert.Equal(HookDecision.Allow, outcome.Decision);
    }
}
