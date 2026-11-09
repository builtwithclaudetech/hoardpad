// Sc5FileGrantsGateTests.cs — SC-5 acceptance gates for the PANEL-A1 Path A per-file grant leg.
//
// HoardPad's AI-safety sandbox was widened (OQ-MD-4 Path A): the frozen hooks/deny_by_default.py
// gained an additive, env-gated (HOARDPAD_FILE_GRANTS) per-file grant leg that can turn a Step-3
// root-deny into an ALLOW for a target whose resolved path EXACTLY equals a resolved granted path.
// Because this WIDENS the enforcement floor, these gates must prove the widened enforcement
// NON-VACUOUSLY against the REAL shipped hook (feature-ai-multidoc-panel §6 — the Archetype-A legs of
// AC-MD-1/2/5/6/7/8).
//
// MECHANISM (identical to Sc5HookGateTests / SC-5(i)): every gate drives the REAL production
// deny_by_default.py as a python3 subprocess via HookDriver — it locates the shipped script by
// walking up to the repo root, wires HOARDPAD_SANDBOX_ROOT / HOARDPAD_CAP_STATE_FILE / caps (+ now
// HOARDPAD_FILE_GRANTS) as env, feeds a crafted PreToolUse `tool_input` JSON on stdin, and parses the
// hook's nested-JSON permissionDecision. No mock of the hook: the shipped artifact IS the thing gated.
//
// NON-VACUITY: each ALLOW gate is paired with the env-absent (or unlisted / faulted) DENY of the same
// target, so the allow is PROVABLY the grant leg's doing — reverting the Step-3 grant branch makes the
// paired ALLOW gates fail. The grant fixtures are REAL out-of-root dirs/files (created on disk so the
// hook's realpath-based _resolve_normcased resolves them), mirroring the PANEL-A1 self-test.
//
// linux-x64 only (C-6): drives the real python3 hook + libc realpath/fsync legs (same as SC-5(i)).

using Xunit;

namespace HoardPad.AcceptanceHarness;

public sealed class Sc5FileGrantsGateTests
{
    // ---- AC-MD-1 / AC-MD-2 (Archetype-A union): a GRANTED out-of-root target is ALLOWED,
    //      and the SAME target with the grant env ABSENT is DENIED (non-vacuous). ----

    [Fact]
    public async Task GrantedOutOfRootTargetIsAllowedButDeniedWhenGrantEnvAbsent()
    {
        using var stack = new RealStack();
        string outDir = stack.OutsideDir("checked-doc-dir");
        string target = Path.Combine(outDir, "checked.txt");   // out-of-root, IS granted.

        string ev = HookDriver.BuildEvent("Write", new Dictionary<string, string>
        {
            ["file_path"] = target,
            ["content"] = "checked out-of-folder write",
        });

        // (1) grant env ABSENT => the out-of-root write is DENIED (the frozen floor, AC-MD-1 refuse).
        HookOutcome absent = await HookDriver.RunAsync(
            ev, stack.HookEnvWithGrants(grantsFile: null), CancellationToken.None);
        Assert.Equal(HookDecision.Deny, absent.Decision);

        // (2) grant env present + target listed => the SAME write is ALLOWED (AC-MD-2 writable).
        //     Because (1) denied it, this ALLOW is PROVABLY the grant leg's doing (non-vacuous).
        string grants = stack.WriteGrantsFile(target);
        HookOutcome granted = await HookDriver.RunAsync(
            ev, stack.HookEnvWithGrants(grants), CancellationToken.None);
        Assert.Equal(HookDecision.Allow, granted.Decision);
    }

    [Fact]
    public async Task UncheckedOutOfRootTargetIsDeniedEvenWithAGrantsFileForADifferentPath()
    {
        // AC-MD-1: an out-of-root target NOT listed (the "unchecked open doc") is Refused even when a
        // grants file IS present for a DIFFERENT path — the leg widens for the exact granted path only.
        using var stack = new RealStack();
        string outDir = stack.OutsideDir("mixed-dir");
        string checkedTarget = Path.Combine(outDir, "checked.txt");     // granted.
        string uncheckedTarget = Path.Combine(outDir, "unchecked.txt"); // NOT granted.

        string grants = stack.WriteGrantsFile(checkedTarget);

        string ev = HookDriver.BuildEvent("Write", new Dictionary<string, string>
        {
            ["file_path"] = uncheckedTarget,
            ["content"] = "unchecked open-doc write",
        });
        HookOutcome outcome = await HookDriver.RunAsync(
            ev, stack.HookEnvWithGrants(grants), CancellationToken.None);
        Assert.Equal(HookDecision.Deny, outcome.Decision);

        // Non-vacuity within the same grants file: the LISTED sibling IS allowed, proving the deny
        // above is per-file granularity, not a deny-everything grants file.
        string evChecked = HookDriver.BuildEvent("Write", new Dictionary<string, string>
        {
            ["file_path"] = checkedTarget,
            ["content"] = "checked write",
        });
        HookOutcome allowed = await HookDriver.RunAsync(
            evChecked, stack.HookEnvWithGrants(grants), CancellationToken.None);
        Assert.Equal(HookDecision.Allow, allowed.Decision);
    }

    // ---- AC-MD-5 (per-file granularity): grant c.txt; ALLOW c.txt, DENY siblings, prefix traps,
    //      and the directory itself. ----

    [Fact]
    public async Task PerFileGrantAllowsTheGrantedFileAndDeniesSiblingsPrefixTrapsAndTheDirectory()
    {
        using var stack = new RealStack();
        string dir = stack.OutsideDir("per-file-dir");
        string granted = Path.Combine(dir, "c.txt");                 // the ONLY granted path.
        string grants = stack.WriteGrantsFile(granted);

        // Non-vacuity: the granted path IS allowed (not a deny-everything file).
        HookOutcome allow = await Decide(stack, grants, granted, "granted");
        Assert.Equal(HookDecision.Allow, allow.Decision);

        // A sibling in the same dir is DENIED (no per-dir widening).
        HookOutcome sibling = await Decide(stack, grants, Path.Combine(dir, "other.txt"), "sibling");
        Assert.Equal(HookDecision.Deny, sibling.Decision);

        // Prefix traps: c.txt.bak and c.txt2 must NOT be admitted by a c.txt grant (exact-equality,
        // no string-prefix widening).
        HookOutcome bak = await Decide(stack, grants, Path.Combine(dir, "c.txt.bak"), "bak");
        Assert.Equal(HookDecision.Deny, bak.Decision);

        HookOutcome two = await Decide(stack, grants, Path.Combine(dir, "c.txt2"), "two");
        Assert.Equal(HookDecision.Deny, two.Decision);

        // The granted file's own DIRECTORY as a create target is DENIED (a file grant has no children).
        HookOutcome asDir = await Decide(stack, grants, dir + Path.DirectorySeparatorChar, "dir-create");
        Assert.Equal(HookDecision.Deny, asDir.Decision);
    }

    // ---- AC-MD-6 (fail-closed grant channel): a malformed / nonexistent grants file => DENY
    //      (root-only fallback), each paired with the well-formed ALLOW so the deny is fault-handling. ----

    [Fact]
    public async Task NonexistentGrantsFileDeniesTheWouldBeGrantedWriteButWellFormedAllows()
    {
        using var stack = new RealStack();
        string dir = stack.OutsideDir("fc-missing-dir");
        string target = Path.Combine(dir, "c.txt");

        // Pair: a well-formed grants file listing the target ALLOWS (proves the target is grantable).
        string ok = stack.WriteGrantsFile(target);
        HookOutcome allowed = await Decide(stack, ok, target, "well-formed");
        Assert.Equal(HookDecision.Allow, allowed.Decision);

        // Fault: HOARDPAD_FILE_GRANTS points at a nonexistent file => empty grant set => DENY.
        string missing = Path.Combine(stack.Base, "no-such-grants.json");
        HookOutcome outcome = await Decide(stack, missing, target, "missing");
        Assert.Equal(HookDecision.Deny, outcome.Decision);
    }

    [Theory]
    [InlineData("not json at all {{{")]                 // not JSON.
    [InlineData("{\"grants\":[]}")]                     // valid JSON but NOT an array (an object).
    [InlineData("\"just-a-string\"")]                   // valid JSON but a bare string, not an array.
    [InlineData("[123, 456]")]                          // an array whose elements are NOT strings.
    public async Task MalformedGrantsFileDeniesTheWouldBeGrantedWrite(string malformedBody)
    {
        using var stack = new RealStack();
        string dir = stack.OutsideDir("fc-malformed-dir");
        string target = Path.Combine(dir, "c.txt");

        // Pair: a well-formed grants file for the SAME target ALLOWS, so the deny below is the
        // malformed-file fault-handling (FR-MD-10), not a broken/deny-everything target path.
        string ok = stack.WriteGrantsFile(target);
        HookOutcome allowed = await Decide(stack, ok, target, "well-formed");
        Assert.Equal(HookDecision.Allow, allowed.Decision);

        // Fault: a malformed grants-file body => empty grant set (root-only) => DENY.
        string bad = stack.WriteRawGrantsFile(malformedBody);
        HookOutcome outcome = await Decide(stack, bad, target, "malformed");
        Assert.Equal(HookDecision.Deny, outcome.Decision);
    }

    [Fact]
    public async Task MalformedGrantsFileWithANonStringElementDeniesTheGrantedTarget()
    {
        // A grants array that CONTAINS the granted string plus a non-string element must fail closed to
        // NO grants (the whole file is suspect) — a partial-admit would be a widening bug.
        using var stack = new RealStack();
        string dir = stack.OutsideDir("fc-nonstring-dir");
        string target = Path.Combine(dir, "c.txt");

        // Well-formed pair ALLOWS.
        string ok = stack.WriteGrantsFile(target);
        Assert.Equal(HookDecision.Allow, (await Decide(stack, ok, target, "well-formed")).Decision);

        // The granted path is present in the array but so is a non-string element => empty set => DENY.
        string bad = stack.WriteRawGrantsFile($"[{System.Text.Json.JsonSerializer.Serialize(target)}, 42]");
        HookOutcome outcome = await Decide(stack, bad, target, "non-string-element");
        Assert.Equal(HookDecision.Deny, outcome.Decision);
    }

    // ---- AC-MD-8 (caps still apply to a granted write): a granted out-of-root create over the FR-11
    //      cap is DENIED; an under-cap granted create is ALLOWED. ----

    [Fact]
    public async Task GrantedCreateOverTheFr11CapIsDeniedButUnderCapGrantedCreateIsAllowed()
    {
        using var stack = new RealStack();
        string dir = stack.OutsideDir("cap-dir");
        string target = Path.Combine(dir, "c.txt");
        string grants = stack.WriteGrantsFile(target);

        string ev = HookDriver.BuildEvent("Write", new Dictionary<string, string>
        {
            ["file_path"] = target,
            ["content"] = "granted create",
        });

        // (1) Under-cap: the fresh (missing) cap-state file => create 1/25 => ALLOW. This proves the
        //     grant admits the write into the cap accounting (non-vacuous for the deny below).
        HookOutcome underCap = await HookDriver.RunAsync(
            ev, stack.HookEnvWithGrants(grants), CancellationToken.None);
        Assert.Equal(HookDecision.Allow, underCap.Decision);

        // (2) At the FR-11 create cap: seed the cap-state file the hook reads (HOARDPAD_CAP_STATE_FILE)
        //     with CreateCount == cap (and the flag tripped). A GRANTED create must STILL be DENIED —
        //     the grant widens containment (Step 3) but does NOT bypass the Step-4 caps (FR-MD-5).
        int cap = HoardPad.Core.Ai.Caps.CapConfig.Pinned.CreateCap;
        await WriteCapStateAtCreateCap(stack.CapStateFile, cap);
        HookOutcome overCap = await HookDriver.RunAsync(
            ev, stack.HookEnvWithGrants(grants), CancellationToken.None);
        Assert.Equal(HookDecision.Deny, overCap.Decision);
    }

    // ---- AC-MD-7 (frozen floor / regression guard): with HOARDPAD_FILE_GRANTS ABSENT, an out-of-root
    //      write is DENIED (root-only) and an in-root under-cap write is ALLOWED — the leg is inert
    //      when unarmed. (A focused restatement; the 39 existing gates already exercise the floor.) ----

    [Fact]
    public async Task WithGrantsAbsentOutOfRootIsDeniedAndInRootUnderCapIsAllowed()
    {
        using var stack = new RealStack();

        // Out-of-root, grant env absent => DENY (frozen floor).
        string outTarget = stack.OutsideRoot("floor-escape.txt");
        string outEv = HookDriver.BuildEvent("Write", new Dictionary<string, string>
        {
            ["file_path"] = outTarget,
            ["content"] = "escape",
        });
        HookOutcome outOfRoot = await HookDriver.RunAsync(
            outEv, stack.HookEnvWithGrants(grantsFile: null), CancellationToken.None);
        Assert.Equal(HookDecision.Deny, outOfRoot.Decision);

        // In-root, under cap, grant env absent => ALLOW (unchanged floor behavior).
        string inTarget = stack.InRoot("floor-note.txt");
        string inEv = HookDriver.BuildEvent("Write", new Dictionary<string, string>
        {
            ["file_path"] = inTarget,
            ["content"] = "in-root note",
        });
        HookOutcome inRoot = await HookDriver.RunAsync(
            inEv, stack.HookEnvWithGrants(grantsFile: null), CancellationToken.None);
        Assert.Equal(HookDecision.Allow, inRoot.Decision);
    }

    // ---- helpers ----

    /// <summary>Drive the real hook for a Write to <paramref name="target"/> under a grants file.</summary>
    private static Task<HookOutcome> Decide(RealStack stack, string grantsFile, string target, string content)
    {
        string ev = HookDriver.BuildEvent("Write", new Dictionary<string, string>
        {
            ["file_path"] = target,
            ["content"] = content,
        });
        return HookDriver.RunAsync(ev, stack.HookEnvWithGrants(grantsFile), CancellationToken.None);
    }

    /// <summary>
    /// Seed the host-owned cap-state JSON (the C# CapState shape, PascalCase keys, the hook reads it
    /// read-only) at the FR-11 create cap so the next create trips it — mirrors the self-test's
    /// Regression-A cap-state seeding.
    /// </summary>
    private static async Task WriteCapStateAtCreateCap(string capStateFile, int createCap)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new
        {
            CreateCount = createCap,
            DestructiveCount = 0,
            FilesTouched = Array.Empty<string>(),
            CreateCapTripped = true,
            DestructiveCapTripped = false,
            FilesTouchedCapTripped = false,
        });
        await File.WriteAllTextAsync(capStateFile, json);
    }
}
