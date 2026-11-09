// ChannelKillSelfTestTests.cs — the M11 extension to the self-test gate (spec §5 invariant 5).
//
// Filter: ~Channel. RunChannelKillSelfTestAsync must PASS only when a turn whose approve-each
// observation channel is KILLED mid-ask DENIES and leaves NO file. The stand-in models the hook's
// ask leg (stdlib socket): connect to HOARDPAD_OBSERVE_CHANNEL, send an `ask`, write a `.connected`
// marker (the host's cue to kill the channel), then BLOCK reading the `decision`. On a closed
// channel (EOF / reset) it DENIES per protocol §4: writes nothing at the target + records the denial.
//
// Non-vacuity: a SECOND stand-in models a hook that IGNORES the closed channel and writes the file
// anyway (a disarmed approve-each leg) — the self-test MUST detect it (a file appeared) and FAIL.
// Together these prove the gate is non-vacuous: it passes the fail-closed hook and fails the disarmed one.

using HoardPad.Core.Host.Abstractions;
using HoardPad.Host;
using Xunit;

namespace HoardPad.Host.Tests;

public sealed class ChannelKillSelfTestTests : IDisposable
{
    private readonly string _dir;

    public ChannelKillSelfTestTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"hoardpad-m11-chankill-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public async Task ChannelKillSelfTestPassesWhenTheHookDeniesOnTheKilledChannel()
    {
        // A faithful hook stand-in: connects, sends the ask, marks connected, blocks reading the
        // decision, and on the killed channel (EOF) DENIES — writes nothing + records the denial.
        string standIn = WriteStandIn("faithful-hook.py", FaithfulHookOnKilledChannel);
        var host = new ToolPermissionHookHost(ResolveProductionHookPath());
        var launcher = new LaunchDescriptor(standIn, IsCmd: false, FromPath: false, ResolvedVia: "test");

        // Generous deadline: 120 s absorbs scheduler starvation under a full parallel test run.
        // The self-test itself is a real python3 socket-spawn sequence bounded by WaitForMarkerAsync
        // (5 s) + process exit; 30 s was insufficient under 8-core CPU contention.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        SelfTestResult result = await host.RunChannelKillSelfTestAsync(launcher, cts.Token);

        Assert.True(result.Passed,
            $"channel-kill self-test should PASS when the hook denies on the killed channel; detail: {result.Detail}");
    }

    [Fact]
    public async Task ChannelKillSelfTestFailsClosedWhenTheHookIgnoresTheClosedChannelAndWritesAnyway()
    {
        // THE NON-VACUITY GUARD: a disarmed hook stand-in ignores the closed channel and writes the
        // file anyway. The self-test MUST detect the appeared file and FAIL — proving the gate is not
        // trivially-always-pass (if the deny detection were replaced by "always pass", THIS fails).
        string standIn = WriteStandIn("disarmed-hook.py", DisarmedHookWritesAnyway);
        var host = new ToolPermissionHookHost(ResolveProductionHookPath());
        var launcher = new LaunchDescriptor(standIn, IsCmd: false, FromPath: false, ResolvedVia: "test");

        // Generous deadline: 120 s absorbs scheduler starvation under a full parallel test run.
        // 30 s was insufficient under 8-core CPU contention when the full solution runs in parallel.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        SelfTestResult result = await host.RunChannelKillSelfTestAsync(launcher, cts.Token);

        Assert.False(result.Passed,
            "channel-kill self-test MUST fail closed when a disarmed hook writes the file despite the killed "
            + "channel — it is the guard that a channel fault cannot silently arm a widened sandbox.");
        Assert.NotNull(result.Detail);
    }

    // ---- STF-4: the REAL compiled HoardPad.SelfTestStandIn binary, not a Python mirror ----
    //
    // Mirrors the two tests above, but against the actual STF-3 artifact
    // (src/HoardPad.SelfTestStandIn/Program.cs's RunChannelKill mode), driven exactly as
    // AiPanelViewModel.ArmAgencyAsync drives it in production for AgencyMode.ApproveEach.

    [Fact]
    public async Task ChannelKillSelfTestPassesWhenTheCompiledStandInBinaryDeniesOnTheKilledChannel()
    {
        var host = new ToolPermissionHookHost(ResolveProductionHookPath());
        var launcher = new LaunchDescriptor(
            ResolveSelfTestStandInBinaryPath(), IsCmd: false, FromPath: false, ResolvedVia: "test");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        SelfTestResult result = await host.RunChannelKillSelfTestAsync(launcher, cts.Token);

        Assert.True(result.Passed,
            "the compiled HoardPad.SelfTestStandIn binary must PASS the channel-kill gate (production "
            + $"wiring) — it must deny on a closed channel; detail: {result.Detail}");
    }

    // ---- helpers ----

    private string WriteStandIn(string name, string body)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, body);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return path;
    }

    private static string ResolveProductionHookPath()
    {
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

    // STF-4: resolve the REAL compiled HoardPad.SelfTestStandIn binary (mirrors HookHostTests.cs's
    // helper of the same purpose — kept local rather than shared to avoid a cross-file test-infra
    // dependency for a 2-line resolver).
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

    // Faithful hook stand-in: connect over AF_UNIX, send the `ask`, write the `.connected` marker
    // (the host kills the channel after seeing it), block reading the `decision`. On a closed channel
    // (EOF / reset / any error) DENY: write NOTHING at the target, record the denial (protocol §4).
    private const string FaithfulHookOnKilledChannel = """
#!/usr/bin/env python3
import json, os, socket, sys

descriptor_path = sys.argv[1]
with open(descriptor_path, "r", encoding="utf-8") as f:
    d = json.load(f)

channel = os.environ["HOARDPAD_OBSERVE_CHANNEL"]
tool_use_id = d["toolUseId"]

def deny():
    # Fail-closed: write nothing at the target, record the denial.
    with open(d["denialRecordPath"], "w", encoding="utf-8") as rf:
        json.dump({"permission_denials": [{"tool": "Write", "path": d["writeTarget"], "reason": "channel-closed"}]}, rf)
    sys.exit(2)

try:
    s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    s.connect(channel)
except OSError:
    # Unreachable channel => DENY (protocol §4 unreachable).
    deny()

try:
    ask = {"type": "ask", "tool_use_id": tool_use_id, "tool_name": "Write",
           "target_path": d["writeTarget"], "op_class": "create"}
    s.sendall((json.dumps(ask) + "\n").encode("utf-8"))
except OSError:
    deny()

# Signal the host that the ask is in flight; the host now kills the channel.
with open(descriptor_path + ".connected", "w", encoding="utf-8") as mf:
    mf.write("connected")

# BLOCK reading the decision line. On the killed channel this returns EOF (b"") or raises => DENY.
buf = b""
try:
    while b"\n" not in buf:
        chunk = s.recv(4096)
        if not chunk:
            deny()   # EOF: the host closed the channel before a decision (protocol §4 closed).
        buf += chunk
except OSError:
    deny()   # reset / error reading => DENY.

# If we somehow got a full line, only allow on an explicit matching allow:true (we never do here).
try:
    decision = json.loads(buf.split(b"\n", 1)[0].decode("utf-8"))
    if decision.get("type") == "decision" and decision.get("tool_use_id") == tool_use_id and decision.get("allow") is True:
        with open(d["writeTarget"], "w", encoding="utf-8") as wf:
            wf.write(d["content"])
        sys.exit(0)
except Exception:
    pass
deny()
""";

    // Disarmed hook stand-in: connect + ask + mark connected, but on the closed channel IGNORE the
    // fail-closed rule and write the file anyway (a disarmed approve-each leg). The self-test must
    // detect the appeared file and FAIL — the non-vacuity guard.
    private const string DisarmedHookWritesAnyway = """
#!/usr/bin/env python3
import json, os, socket, sys

descriptor_path = sys.argv[1]
with open(descriptor_path, "r", encoding="utf-8") as f:
    d = json.load(f)

channel = os.environ["HOARDPAD_OBSERVE_CHANNEL"]
tool_use_id = d["toolUseId"]

try:
    s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    s.connect(channel)
    ask = {"type": "ask", "tool_use_id": tool_use_id, "tool_name": "Write",
           "target_path": d["writeTarget"], "op_class": "create"}
    s.sendall((json.dumps(ask) + "\n").encode("utf-8"))
except OSError:
    pass

with open(descriptor_path + ".connected", "w", encoding="utf-8") as mf:
    mf.write("connected")

# Block briefly then WRITE THE FILE ANYWAY (disarmed — ignores the closed channel).
try:
    s.recv(4096)
except OSError:
    pass
with open(d["writeTarget"], "w", encoding="utf-8") as wf:
    wf.write(d["content"])
sys.exit(0)
""";
}
