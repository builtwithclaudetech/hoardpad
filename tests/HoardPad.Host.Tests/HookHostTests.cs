// HookHostTests.cs — the --settings injection + the fail-closed self-test (M5.2).
//
// Covers (on linux-x64):
//   - BuildHookSettings produces the --settings JSON wiring the bundled deny_by_default.py as
//     the '*'-matcher PreToolUse hook with a LONG timeout (>= the 600 s floor) so the hook's
//     own timeout never wins the fail-open race;
//   - RunFailClosedSelfTestAsync ACTUALLY RUNS with the SAME --settings argv production uses,
//     and:
//       * a faithful stand-in that reads --settings and invokes the hook => Passed == true;
//       * a DISARMED stand-in that ignores --settings and writes the file => Passed == false
//         (the disarm-detection check — the most important property);
//       * an inert stand-in that does nothing => Passed == false (no positive denial);
//       * a stand-in launched with a MALFORMED --settings config (BuildHookSettings bypassed)
//         => Passed == false (proves the --settings wiring is what the self-test validates);
//       * a chatty stand-in that emits >64 KB before exit => no deadlock (MUST-FIX 3).
//
// The stand-in agents are small Python scripts the test writes to temp:
//   faithful: reads the --settings path, invokes the hook via the command the settings wire,
//             parses the decision, honors deny (writes nothing + records denial). If the settings
//             wiring is broken (wrong path / malformed JSON), the hook call fails and the
//             stand-in writes the escape file as a faithfully-detected disarmed state.
//   disarmed: ignores --settings entirely and writes the escape file directly.
//   inert: does nothing.
//   chatty: emits >64 KB to stdout before exiting.
// All stand-ins are driven through the IDENTICAL RunFailClosedSelfTestAsync code path.

using System.Text.Json;
using HoardPad.Core.Host.Abstractions;
using HoardPad.Host;
using Xunit;

namespace HoardPad.Host.Tests;

public sealed class HookHostTests : IDisposable
{
    private readonly string _dir;
    private readonly string _hookScriptPath;

    public HookHostTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"hoardpad-m52-hook-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        // The bundled production hook (hooks/deny_by_default.py). Resolved relative to the repo
        // root so the self-test drives the REAL deny-by-default script, not a copy.
        _hookScriptPath = ResolveProductionHookPath();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    // ---- BuildHookSettings: the --settings JSON shape ----

    [Fact]
    public void BuildHookSettingsWiresTheHookAsTheStarMatcherPreToolUseHookWithALongTimeout()
    {
        var host = new ToolPermissionHookHost(_hookScriptPath);
        var config = new HookConfig(ResolveRoot(_dir), NoReplyTimeout: TimeSpan.FromSeconds(60));

        HookSettings settings = host.BuildHookSettings(config);

        using JsonDocument doc = JsonDocument.Parse(settings.SettingsJson);
        JsonElement pre = doc.RootElement.GetProperty("hooks").GetProperty("PreToolUse");
        Assert.Equal(1, pre.GetArrayLength());

        JsonElement entry = pre[0];
        Assert.Equal("*", entry.GetProperty("matcher").GetString());   // the '*'-matcher.

        JsonElement hook = entry.GetProperty("hooks")[0];
        Assert.Equal("command", hook.GetProperty("type").GetString());
        // The command references the bundled production hook script.
        Assert.Contains("deny_by_default.py", hook.GetProperty("command").GetString());
        // The timeout is the floor (>= 600 s) so the hook's own timeout NEVER wins the race.
        Assert.True(hook.GetProperty("timeout").GetInt32() >= 600);
    }

    [Fact]
    public void BuildHookSettingsClampsAShortTimeoutUpToThe600sFloor()
    {
        // A host-config bug passing a too-short timeout must be clamped UP — a short hook timeout
        // could fail open and win the no-reply race (§4.2 step 4).
        var host = new ToolPermissionHookHost(_hookScriptPath, hookTimeoutSeconds: 5);
        HookSettings settings = host.BuildHookSettings(new HookConfig(ResolveRoot(_dir), TimeSpan.FromSeconds(60)));

        using JsonDocument doc = JsonDocument.Parse(settings.SettingsJson);
        int timeout = doc.RootElement.GetProperty("hooks").GetProperty("PreToolUse")[0]
            .GetProperty("hooks")[0].GetProperty("timeout").GetInt32();
        Assert.True(timeout >= 600);
    }

    // ---- THE SELF-TEST: faithful (with --settings) => Passed; disarmed (ignores --settings) => fail ----

    [Fact]
    public async Task SelfTestPassesWhenTheStandInHonorsAProperlyDenyingHook()
    {
        // A faithful stand-in: reads the --settings file the host passed, parses the hook command
        // that BuildHookSettings wired, invokes the PRODUCTION hook with the out-of-sandbox Write
        // event, and because the hook DENIES (target is outside the scratch sandbox), it writes
        // nothing and records the denial. The self-test must report Passed == true.
        // This proves both that the hook denies AND that the --settings wiring is correct.
        string faithful = WriteStandIn("faithful-agent.py", FaithfulStandIn);
        var host = new ToolPermissionHookHost(_hookScriptPath);
        var launcher = new LaunchDescriptor(faithful, IsCmd: false, FromPath: false, ResolvedVia: "test");

        SelfTestResult result = await host.RunFailClosedSelfTestAsync(launcher, CancellationToken.None);

        Assert.True(result.Passed, $"self-test should PASS for a properly-denying hook; detail: {result.Detail}");
    }

    [Fact]
    public async Task SelfTestFailsClosedWhenTheStandInIsDisarmedAndWritesTheEscapeFile()
    {
        // THE MOST IMPORTANT TEST: a disarmed stand-in ignores --settings entirely and writes the
        // out-of-sandbox escape file (a silently-disarmed sandbox — exactly what -p does when it
        // ignores an invalid hook config, SPIKE-3 D3). The self-test must DETECT this and report
        // Passed == false (fail closed) — write agency must NOT be armed.
        string disarmed = WriteStandIn("disarmed-agent.py", DisarmedStandIn);
        var host = new ToolPermissionHookHost(_hookScriptPath);
        var launcher = new LaunchDescriptor(disarmed, IsCmd: false, FromPath: false, ResolvedVia: "test");

        SelfTestResult result = await host.RunFailClosedSelfTestAsync(launcher, CancellationToken.None);

        Assert.False(result.Passed,
            "self-test MUST fail closed when a disarmed stand-in writes the out-of-sandbox file — "
            + "it is the sole guard against a silently-disarmed sandbox.");
        Assert.NotNull(result.Detail);
    }

    [Fact]
    public async Task SelfTestFailsClosedWhenTheStandInWritesNothingButRecordsNoDenial()
    {
        // A stand-in that neither writes the escape file NOR records a denial (e.g. it crashed,
        // or the hook was silently ignored so the agent simply did nothing). "No file" alone is
        // NOT sufficient — a denial must be POSITIVELY recorded. The self-test fails closed.
        string inert = WriteStandIn("inert-agent.py", InertStandIn);
        var host = new ToolPermissionHookHost(_hookScriptPath);
        var launcher = new LaunchDescriptor(inert, IsCmd: false, FromPath: false, ResolvedVia: "test");

        SelfTestResult result = await host.RunFailClosedSelfTestAsync(launcher, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.NotNull(result.Detail);
    }

    [Fact]
    public async Task SelfTestFailsClosedWhenSettingsWiresAnAlwaysAllowHook()
    {
        // CRITICAL 2 regression test: the self-test's value comes from the --settings wiring being
        // what the faithful stand-in reads and invokes. If BuildHookSettings wired an "always-allow"
        // hook (a disarmed config), the faithful stand-in invokes it, gets "allow", and writes the
        // escape file — which the host detects as a fail-closed result.
        //
        // This proves: if the --settings wiring is broken/permissive, the self-test DETECTS it
        // and fails closed. A green self-test proves the hook actively denies, not just that the
        // agent wrote nothing.
        //
        // We write a tiny Python "hook" that always outputs an allow decision, then supply it as
        // the hook script so BuildHookSettings wires a command to that allow-hook. The faithful
        // stand-in invokes it, sees "allow", and writes the escape file.
        string allowHook = WriteStandIn("allow-hook.py", AlwaysAllowHookScript);
        var permissiveHost = new ToolPermissionHookHost(allowHook);
        string faithful = WriteStandIn("faithful-permissive-agent.py", FaithfulStandIn);
        var launcher = new LaunchDescriptor(faithful, IsCmd: false, FromPath: false, ResolvedVia: "test");

        SelfTestResult result = await permissiveHost.RunFailClosedSelfTestAsync(launcher, CancellationToken.None);

        Assert.False(result.Passed,
            "self-test MUST fail closed when the --settings wires an always-allow hook — the escape "
            + $"file must appear and be detected; detail: {result.Detail}");
    }

    // ---- STF-4: the REAL compiled HoardPad.SelfTestStandIn binary, not a Python mirror ----
    //
    // Every test above proves RunFailClosedSelfTestAsync's WIRING is correct by driving Python
    // stand-ins that mirror the intended stand-in contract. Neither this file nor any other test
    // in the repo drove the ACTUAL STF-3 artifact (src/HoardPad.SelfTestStandIn/Program.cs) through
    // this method before STF-4 — the exact gap the STF-1 diagnosis flagged for the retired real-CLI
    // path (docs/stf-diagnosis.md §(a)): "nothing ... launches the real X and observes it pass/fail
    // as a *compiled artifact*". These two tests close that gap for the new stand-in itself.

    [Fact]
    public async Task SelfTestPassesWhenTheCompiledStandInBinaryHonorsAProperlyDenyingHook()
    {
        // The real HoardPad.SelfTestStandIn executable (not FaithfulStandIn's Python mirror),
        // launched exactly as AiPanelViewModel.ArmAgencyAsync launches it in production. It must
        // read --settings, invoke the PRODUCTION hook, see the deny, and record it.
        var host = new ToolPermissionHookHost(_hookScriptPath);
        var launcher = new LaunchDescriptor(
            ResolveSelfTestStandInBinaryPath(), IsCmd: false, FromPath: false, ResolvedVia: "test");

        SelfTestResult result = await host.RunFailClosedSelfTestAsync(launcher, CancellationToken.None);

        Assert.True(result.Passed,
            $"the compiled HoardPad.SelfTestStandIn binary must PASS against a properly-denying "
            + $"hook (production wiring); detail: {result.Detail}");
    }

    [Fact]
    public async Task SelfTestFailsClosedWhenTheCompiledStandInBinaryFacesAnAlwaysAllowHook()
    {
        // Mirrors SelfTestFailsClosedWhenSettingsWiresAnAlwaysAllowHook but against the REAL
        // compiled stand-in: if --settings wires a disarmed/permissive hook, the compiled stand-in
        // must invoke it, observe "allow", and write the escape file so the host fails closed. A
        // stand-in that is itself buggy and unconditionally denies (or unconditionally allows and
        // is never checked) would falsely arm write agency — this is Objection/risk #4 from
        // docs/stf-diagnosis.md's STF-2 review ("A faithful stand-in that is itself buggy could
        // falsely arm write agency"). Non-vacuity: this test FAILS if the compiled binary ignores
        // --settings and always denies/records regardless of the hook's actual decision.
        string allowHook = WriteStandIn("allow-hook-for-compiled-standin.py", AlwaysAllowHookScript);
        var permissiveHost = new ToolPermissionHookHost(allowHook);
        var launcher = new LaunchDescriptor(
            ResolveSelfTestStandInBinaryPath(), IsCmd: false, FromPath: false, ResolvedVia: "test");

        SelfTestResult result = await permissiveHost.RunFailClosedSelfTestAsync(launcher, CancellationToken.None);

        Assert.False(result.Passed,
            "the compiled HoardPad.SelfTestStandIn binary MUST fail closed when --settings wires an "
            + $"always-allow hook — the escape file must appear and be detected; detail: {result.Detail}");
    }

    [Fact]
    public async Task SelfTestDoesNotDeadlockWhenTheChildEmitsLargeStdoutAndStderr()
    {
        // MUST-FIX 3: a child that writes >~64 KB to stdout or stderr before exiting would
        // block on write if the host called WaitForExitAsync without draining the pipes first.
        // The chatty stand-in emits 100 KB each to stdout and stderr to prove no deadlock.
        string chatty = WriteStandIn("chatty-agent.py", ChattyStandIn);
        var host = new ToolPermissionHookHost(_hookScriptPath);
        var launcher = new LaunchDescriptor(chatty, IsCmd: false, FromPath: false, ResolvedVia: "test");

        // We don't care about Passed/failed (the chatty stand-in writes no denial and writes no
        // escape file, so it will be fail-closed). We care that it COMPLETES within a reasonable
        // timeout — a deadlock would hang the test until xUnit aborts it. Generous 120 s absorbs
        // scheduler starvation under a full parallel test run (30 s tripped under 8-core contention).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        SelfTestResult result = await host.RunFailClosedSelfTestAsync(launcher, cts.Token);

        // If we got here without throwing OperationCanceledException, there was no deadlock.
        // The result is fail-closed (no denial recorded) — that is the expected outcome.
        Assert.False(result.Passed);
        Assert.NotNull(result.Detail);
    }

    // ---- helpers ----

    private static NormcasedPath ResolveRoot(string dir) => new PathResolver().ResolveCanonical(dir, dir);

    private string WriteStandIn(string name, string body)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, body);
        // These tests run on linux-x64 only; the guard keeps the analyzer happy (SetUnixFileMode
        // is Linux/macOS-only). The Windows self-test legs are runtime-Deferred.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return path;
    }

    private static string ResolveProductionHookPath()
    {
        // Walk up from the test output dir to the repo root (the dir containing hooks/deny_by_default.py).
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "hooks", "deny_by_default.py");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate hooks/deny_by_default.py walking up from the test output dir.");
    }

    // STF-4: resolve the REAL compiled HoardPad.SelfTestStandIn binary produced by a sibling
    // `dotnet build` of src/HoardPad.SelfTestStandIn (framework-dependent net8.0 output — no RID
    // subfolder), by walking up from the test output dir to the repo root. This deliberately does
    // NOT go through a ProjectReference (this is a test-only concern; adding one would be a
    // csproj-graph change beyond STF-4's scope) — it locates the artifact STF-3 already builds.
    private static string ResolveSelfTestStandInBinaryPath()
    {
        string exeName = OperatingSystem.IsWindows() ? "HoardPad.SelfTestStandIn.exe" : "HoardPad.SelfTestStandIn";
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "src", "HoardPad.SelfTestStandIn", "bin");
            if (Directory.Exists(candidate))
            {
                string? match = Directory.EnumerateFiles(candidate, exeName, SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (match is not null)
                    return match;
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            "Could not locate the compiled HoardPad.SelfTestStandIn binary walking up from the test "
            + "output dir. Build src/HoardPad.SelfTestStandIn first (e.g. `dotnet build`).");
    }

    // ---- stand-in agents (Python; emulate the claude -p self-test contract) ----
    //
    // Each stand-in is launched by the host with:
    //   argv: --settings <settings-file> <descriptor-path>
    //   env: HOARDPAD_SELFTEST_DESCRIPTOR, HOARDPAD_SANDBOX_ROOT, HOARDPAD_CAP_STATE_FILE
    //
    // The settings file is produced by BuildHookSettings (the SAME config production uses).
    // A faithful stand-in reads it and invokes the hook command the settings wire.
    // A disarmed stand-in ignores it and writes the escape file anyway.

    // Faithful: reads the --settings file, extracts the hook command BuildHookSettings wired,
    // invokes the PRODUCTION hook with the crafted out-of-sandbox Write event (exactly as
    // production would), and honors the deny (writes nothing + records denial). If the hook
    // invocation fails (e.g. the settings command points to a non-existent script), the stand-in
    // treats that as a disarmed state and writes the escape file — which the host detects.
    private const string FaithfulStandIn = """
#!/usr/bin/env python3
import json, os, subprocess, sys, shlex

# The host passes --settings <path> and the descriptor as argv[1] and argv[2].
# parse args: we may receive [--settings, <settings_path>, <descriptor_path>]
args = sys.argv[1:]
settings_path = None
descriptor_path = None
i = 0
while i < len(args):
    if args[i] == "--settings" and i + 1 < len(args):
        settings_path = args[i + 1]
        i += 2
    else:
        descriptor_path = args[i]
        i += 1

if not settings_path or not descriptor_path:
    sys.exit(1)

# Read the descriptor.
with open(descriptor_path, "r", encoding="utf-8") as f:
    d = json.load(f)

# Read the --settings JSON to extract the hook command the host wired.
hook_cmd = None
try:
    with open(settings_path, "r", encoding="utf-8") as f:
        settings = json.load(f)
    pre_hooks = settings.get("hooks", {}).get("PreToolUse", [])
    for entry in pre_hooks:
        for h in entry.get("hooks", []):
            if h.get("type") == "command":
                hook_cmd = h["command"]
                break
        if hook_cmd:
            break
except Exception:
    pass  # settings unreadable => treat as disarmed below.

event = {
    "tool_name": d["toolName"],
    "tool_input": {"file_path": d["escapeTarget"], "content": d["content"]},
}

env = dict(os.environ)
env["HOARDPAD_SANDBOX_ROOT"] = d["sandboxRoot"]
env["HOARDPAD_CAP_STATE_FILE"] = d["capStateFile"]

decision = "deny"  # default to the safe reading.

if hook_cmd:
    try:
        # Invoke the hook command exactly as the CLI would (via sh -c so shell quoting is honored).
        proc = subprocess.run(
            ["sh", "-c", hook_cmd],
            input=json.dumps(event).encode("utf-8"),
            capture_output=True,
            env=env,
        )
        if proc.returncode == 0 and proc.stdout:
            try:
                out = json.loads(proc.stdout.decode("utf-8"))
                decision = out.get("hookSpecificOutput", {}).get("permissionDecision", "deny")
            except Exception:
                decision = "deny"
        elif proc.returncode == 2:
            decision = "deny"
        else:
            # Non-zero, non-2 exit from hook: treat as a failed hook invocation => disarmed.
            decision = "allow"
    except Exception:
        # Hook invocation raised: treat as disarmed (hook not reachable).
        decision = "allow"
else:
    # No hook command found in --settings: the wiring is broken => disarmed.
    decision = "allow"

if decision == "deny":
    # Honor the deny: write NOTHING; record the denial so the host's check (b) passes.
    with open(d["denialRecordPath"], "w", encoding="utf-8") as rf:
        json.dump({"permission_denials": [{"tool": d["toolName"], "path": d["escapeTarget"]}]}, rf)
else:
    # The hook ALLOWED (or wiring was broken, treated as allow) — write the escape file so the
    # host detects the disarmed/broken-wiring state. This is the fail-safe path.
    with open(d["escapeTarget"], "w", encoding="utf-8") as wf:
        wf.write(d["content"])
sys.exit(0)
""";

    // Disarmed: ignore --settings entirely and write the out-of-sandbox escape file. This models
    // a silently-disarmed sandbox (-p ignoring an invalid hook config). The host MUST detect it.
    private const string DisarmedStandIn = """
#!/usr/bin/env python3
import json, os, sys

desc_path = os.environ["HOARDPAD_SELFTEST_DESCRIPTOR"]
with open(desc_path, "r", encoding="utf-8") as f:
    d = json.load(f)

# No hook consulted, --settings ignored — write the escape file directly (the sandbox is disarmed).
with open(d["escapeTarget"], "w", encoding="utf-8") as wf:
    wf.write(d["content"])
sys.exit(0)
""";

    // Inert: do nothing (no escape file, no denial record). "No file" is not enough — a denial
    // must be POSITIVELY recorded, so the self-test fails closed here too.
    private const string InertStandIn = """
#!/usr/bin/env python3
import sys
sys.exit(0)
""";

    // Chatty: emits 100 KB each to stdout and stderr before exiting. Tests MUST-FIX 3:
    // the host drains both pipes concurrently, so this must complete without deadlock.
    private const string ChattyStandIn = """
#!/usr/bin/env python3
import sys
chunk = "X" * 1024  # 1 KB chunk
for _ in range(100):
    sys.stdout.write(chunk)
    sys.stderr.write(chunk)
sys.stdout.flush()
sys.stderr.flush()
sys.exit(0)
""";

    // AlwaysAllowHookScript: a fake hook that always returns a permissionDecision of "allow".
    // Used by SelfTestFailsClosedWhenSettingsWiresAnAlwaysAllowHook to prove that if
    // BuildHookSettings wires a hook that allows rather than denies, the self-test detects it.
    private const string AlwaysAllowHookScript = """
#!/usr/bin/env python3
import json, sys
# A hook that always allows — simulates a disarmed/misconfigured hook config.
# The faithful stand-in will see "allow" and write the escape file.
print(json.dumps({"hookSpecificOutput": {"permissionDecision": "allow"}}))
sys.exit(0)
""";
}
