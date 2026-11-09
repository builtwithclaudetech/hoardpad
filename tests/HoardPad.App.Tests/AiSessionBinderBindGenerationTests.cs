// AiSessionBinderBindGenerationTests.cs — F-FS1d: the bind-generation epoch guard (confirmed
// non-escape, cosmetic arm-state hygiene — CLAUDE.md carry-forward from FS-1's own BindSession
// docstring). Class name contains "AiSessionBinder" so the verify filter "FullyQualifiedName~AiSessionBinder"
// resolves it.
//
// The race this closes: a bind's ArmAgencyAsync runs its self-test (and, for an auto-apply session with
// the home/large-root confirm ON, the large-root probe) off the UI thread. If a NEWER BindSession call
// supersedes the panel (the user opens a different folder before the OLDER bind's arm work resolves), the
// older bind's eventual ApplyResolution must NOT stomp the newer session's presented arm-state. The guard
// lives in AiPanelViewModel (BindSession/ClearSession bump a generation counter; ArmAgencyAsync captures it
// at entry and skips ApplyResolution if a newer bind ran while it was in flight) — not in AiSessionBinder
// itself, because ApplyResolution is called INSIDE AiPanelViewModel.ArmAgencyAsync; AiSessionBinder, which
// only awaits that call from the outside, has no vantage point to intercept the mutation before it happens.
// This is exactly the "future defense-in-depth hardening" FS-1's BindSession docstring already named.
//
// Non-escape: the sandbox/cap/hook enforcement is scoped to _fileOps/the resolved root, never to the
// Sandbox/EffectiveMode/DowngradeBadge cosmetic state this guard protects — a write still can't leave
// either session's own root regardless of which generation's arm-state is showing.

using System.Threading;
using System.Threading.Tasks;
using HoardPad.App.Ai;
using HoardPad.App.ViewModels;
using HoardPad.Core.Ai.FileOps;
using HoardPad.Core.Ai.Provider;
using HoardPad.Core.Records;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class AiSessionBinderBindGenerationTests
{
    private static AiFileOpsService FakeFileOps(string sessionId) =>
        new(
            new ChecklistTestSupportSandboxGate(),
            new CountingAtomicFileStore(),
            new NullCapStateStore(),
            new FakeAppDataPaths(),
            sessionId,
            HoardPad.Core.Ai.Caps.CapConfig.Pinned);

    /// <summary>
    /// The F-FS1d scenario: gen1 binds and starts arming; while its self-test/large-root probe is still
    /// in flight, gen2 supersedes it with a fresh BindSession. Releasing gen1's blocked probe lets its
    /// ArmAgencyAsync resolve AFTER gen2's bind — its resolution must be ignored, not applied onto gen2's
    /// panel state. This is non-vacuous: removing the generation guard makes this test fail (gen1's stale
    /// resolution flips Sandbox to Armed / EffectiveMode to AutoApply / DowngradeBadge to empty, clobbering
    /// gen2's NotYetArmed / ReadOnly / "document folder not yet armed" resting state).
    /// </summary>
    [Fact]
    public async Task AStaleGenerationArmResultIsIgnoredWhenANewerBindSupersedesIt()
    {
        var gen1ProbeStarted = new TaskCompletionSource();
        var releaseGen1Probe = new TaskCompletionSource();

        DowngradeReason BlockUntilReleased(string root, int threshold, CancellationToken ct)
        {
            // Runs on a background thread (inside ArmAgencyAsync's Task.Run) — blocking here is exactly
            // what simulates "gen1's arm work is still in flight" while the test drives gen2's bind on
            // the calling (UI) thread below.
            gen1ProbeStarted.TrySetResult();
            releaseGen1Probe.Task.Wait(ct);
            return DowngradeReason.None;
        }

        var vm = new AiPanelViewModel(
            new ScriptedTurnRunner([]),
            new ScriptedConfirmService(),
            new ScriptedHookHost(selfTestPasses: true),
            new FakeAgentBackend(),
            TimeProvider.System,
            largeHomeRootDetector: BlockUntilReleased)
        {
            ConfiguredMode = AgencyMode.AutoApply,
            ConfirmLargeHomeFolder = true, // required so ArmAgencyAsync reaches the (blocked) probe.
        };

        vm.BindSession("/gen1-root", FakeFileOps("s-gen1"), conversationId: "conv-gen1");
        Task<AgencyResolution> gen1ArmTask = vm.ArmAgencyAsync();

        await gen1ProbeStarted.Task; // gen1's self-test passed; it is now blocked in the large-root probe.

        // The newer bind: the user opened a different folder before gen1's arm resolved.
        vm.BindSession("/gen2-root", FakeFileOps("s-gen2"), conversationId: "conv-gen2");

        // Capture gen2's freshly-bound resting state BEFORE letting gen1 resolve, so the assertions below
        // prove gen1's arrival changed nothing.
        SandboxState gen2SandboxBeforeGen1Resolves = vm.Sandbox;
        AgencyMode gen2EffectiveModeBeforeGen1Resolves = vm.EffectiveMode;
        string gen2BadgeBeforeGen1Resolves = vm.DowngradeBadge;

        releaseGen1Probe.SetResult(); // let gen1's blocked probe return.
        AgencyResolution gen1Resolution = await gen1ArmTask;

        // gen1 itself still resolved to an armed, no-downgrade posture — proving the guard's skip is about
        // NOT APPLYING the result, not about failing the self-test/probe.
        Assert.True(gen1Resolution.WriteAgencyArmed);

        // gen2's panel state is exactly what its own BindSession left it in — gen1's stale ApplyResolution
        // never ran.
        Assert.Equal("/gen2-root", vm.SandboxRoot);
        Assert.Equal(gen2SandboxBeforeGen1Resolves, vm.Sandbox);
        Assert.Equal(gen2EffectiveModeBeforeGen1Resolves, vm.EffectiveMode);
        Assert.Equal(gen2BadgeBeforeGen1Resolves, vm.DowngradeBadge);
        Assert.Equal(SandboxState.NotYetArmed, vm.Sandbox);
        Assert.Equal(AgencyMode.ReadOnly, vm.EffectiveMode);
        Assert.Equal("document folder not yet armed", vm.DowngradeBadge);
    }

    /// <summary>
    /// The guard must not change a normal, non-superseded single bind: one BindSession + one ArmAgencyAsync
    /// with nothing else in between still applies its own resolution exactly as before F-FS1d.
    /// </summary>
    [Fact]
    public async Task ALoneBindStillAppliesItsOwnArmStateNormally()
    {
        var vm = new AiPanelViewModel(
            new ScriptedTurnRunner([]),
            new ScriptedConfirmService(),
            new ScriptedHookHost(selfTestPasses: true),
            new FakeAgentBackend(),
            TimeProvider.System,
            largeHomeRootDetector: (_, _, _) => DowngradeReason.None)
        {
            ConfiguredMode = AgencyMode.AutoApply,
            ConfirmLargeHomeFolder = true,
        };

        vm.BindSession("/lone-root", FakeFileOps("s-lone"), conversationId: "conv-lone");
        AgencyResolution resolution = await vm.ArmAgencyAsync();

        Assert.True(resolution.WriteAgencyArmed);
        Assert.Equal(SandboxState.Armed, vm.Sandbox);
        Assert.Equal(AgencyMode.AutoApply, vm.EffectiveMode);
        Assert.Equal(string.Empty, vm.DowngradeBadge);
    }
}
