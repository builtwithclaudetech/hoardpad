// ConfirmLargeFolderSettingTests.cs — FS-5 / FR-10(a) (docs/decisions.md DR-1): the "Confirm each AI
// edit in large/home folders" setting. Class names contain "ConfirmLargeFolderSetting" so the verify
// filter "FullyQualifiedName~ConfirmLargeFolderSetting" resolves every case.
//
// Covers:
//   - LargeHomeRootDetector: the pure/bounded-IO home-root + large-root predicates in isolation.
//   - AiPanelViewModel wiring: ON forces a one-time approve-each downgrade on a home/large root under
//     auto-apply; OFF auto-applies the SAME root; a non-home/small root never downgrades either way;
//     the trigger applies ONLY to auto-apply (approve-each/read-only are untouched).
//   - Settings round-trip: default ON on a missing settings file; an explicit OFF persists and reloads.
//   - Invariant preservation (H8): in BOTH toggle states, write agency still arms, the cap meters are
//     untouched, and the toggle is never consulted for a write-capable-but-not-auto-apply configured
//     mode — proving the toggle changes ONLY the auto-apply confirm-vs-silent decision on an in-bounds
//     root, never FR-26/caps/arming (the spine paths for those are untouched by this build — see
//     `git diff --stat`; HoardPad.Core.Ai/HoardPad.Host/hooks/deny_by_default.py carry zero changes).

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HoardPad.App.Ai;
using HoardPad.App.Settings;
using HoardPad.App.ViewModels;
using HoardPad.Core.Ai.FileOps;
using HoardPad.Core.Records;
using Xunit;

namespace HoardPad.App.Tests;

// ---------------------------------------------------------------------------------------------------
//  LargeHomeRootDetector — the pure/bounded-IO predicates in isolation.
// ---------------------------------------------------------------------------------------------------

public sealed class ConfirmLargeFolderSettingDetectorTests
{
    [Fact]
    public void IsHomeRootMatchesTheSamePathIgnoringATrailingSeparator()
    {
        Assert.True(LargeHomeRootDetector.IsHomeRoot("/home/you", "/home/you"));
        Assert.True(LargeHomeRootDetector.IsHomeRoot("/home/you/", "/home/you"));
    }

    [Fact]
    public void IsHomeRootDoesNotMatchADifferentOrEmptyPath()
    {
        Assert.False(LargeHomeRootDetector.IsHomeRoot("/home/you/proj", "/home/you"));
        Assert.False(LargeHomeRootDetector.IsHomeRoot(string.Empty, "/home/you"));
        Assert.False(LargeHomeRootDetector.IsHomeRoot("/home/you", string.Empty));
    }

    [Fact]
    public void ExceedsThresholdIsFalseForANonExistentRoot()
    {
        Assert.False(LargeHomeRootDetector.ExceedsThreshold(
            "/does/not/exist/hoardpad-fs5-probe", threshold: 1, CancellationToken.None));
    }

    [Fact]
    public void ExceedsThresholdIsFalseWhenTheWritableSetIsUnderTheThreshold()
    {
        string dir = CreateTempDirWithFiles(fileCount: 3);
        try
        {
            Assert.False(LargeHomeRootDetector.ExceedsThreshold(dir, threshold: 5, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ExceedsThresholdIsTrueWhenTheWritableSetIsOverTheThresholdIncludingNestedFiles()
    {
        string dir = CreateTempDirWithFiles(fileCount: 2);
        string nested = Path.Combine(dir, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "n1.txt"), "x");
        File.WriteAllText(Path.Combine(nested, "n2.txt"), "x");
        try
        {
            // 4 files total (2 top-level + 2 nested), threshold 3 -> over.
            Assert.True(LargeHomeRootDetector.ExceedsThreshold(dir, threshold: 3, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DetectPrefersHomeRootOverLargeRootWhenBothWouldFire()
    {
        string dir = CreateTempDirWithFiles(fileCount: 5);
        try
        {
            // dir IS "home" (by construction of the test) AND exceeds a threshold of 1 -> HomeRoot wins.
            DowngradeReason reason = LargeHomeRootDetector.Detect(dir, home: dir, threshold: 1, CancellationToken.None);
            Assert.Equal(DowngradeReason.HomeRoot, reason);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DetectReturnsNoneForASmallNonHomeRoot()
    {
        string dir = CreateTempDirWithFiles(fileCount: 2);
        try
        {
            DowngradeReason reason = LargeHomeRootDetector.Detect(
                dir, home: "/definitely/not/this/dir", threshold: 100, CancellationToken.None);
            Assert.Equal(DowngradeReason.None, reason);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string CreateTempDirWithFiles(int fileCount)
    {
        string dir = Path.Combine(Path.GetTempPath(), "hoardpad-fs5-detector-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        for (int i = 0; i < fileCount; i++)
            File.WriteAllText(Path.Combine(dir, $"f{i}.txt"), "x");
        return dir;
    }
}

// ---------------------------------------------------------------------------------------------------
//  AiPanelViewModel wiring — ON/OFF changes the confirm-vs-silent decision on a home/large root.
// ---------------------------------------------------------------------------------------------------

public sealed class ConfirmLargeFolderSettingViewModelTests
{
    private static AiFileOpsService FakeFileOps() =>
        new(
            new ChecklistTestSupportSandboxGate(),
            new CountingAtomicFileStore(),
            new NullCapStateStore(),
            new FakeAppDataPaths(),
            sessionId: "s1",
            HoardPad.Core.Ai.Caps.CapConfig.Pinned);

    private static AiPanelViewModel NewArmed(
        DowngradeReason detectorReturns,
        bool confirmLargeHomeFolder,
        AgencyMode configuredMode = AgencyMode.AutoApply)
    {
        var vm = new AiPanelViewModel(
            new ScriptedTurnRunner([]),
            new ScriptedConfirmService(),
            new ScriptedHookHost(selfTestPasses: true),
            new FakeAgentBackend(),
            TimeProvider.System,
            largeHomeRootDetector: (_, _, _) => detectorReturns)
        {
            ConfiguredMode = configuredMode,
            ConfirmLargeHomeFolder = confirmLargeHomeFolder,
        };
        vm.BindSession("/proj-fs5", FakeFileOps(), conversationId: "conv-fs5");
        return vm;
    }

    [Fact]
    public async Task OnForcesApproveEachOnALargeRoot()
    {
        AiPanelViewModel vm = NewArmed(DowngradeReason.LargeRoot, confirmLargeHomeFolder: true);

        await vm.ArmAgencyAsync();

        Assert.Equal(AgencyMode.ApproveEach, vm.EffectiveMode);
        Assert.Equal("large root", vm.DowngradeBadge);
        Assert.True(vm.HasDowngrade);
    }

    [Fact]
    public async Task OnForcesApproveEachOnAHomeRoot()
    {
        AiPanelViewModel vm = NewArmed(DowngradeReason.HomeRoot, confirmLargeHomeFolder: true);

        await vm.ArmAgencyAsync();

        Assert.Equal(AgencyMode.ApproveEach, vm.EffectiveMode);
        Assert.Equal("home root", vm.DowngradeBadge);
    }

    [Fact]
    public async Task OffAutoAppliesTheSameHomeOrLargeRootThatOnWouldConfirm()
    {
        AiPanelViewModel vm = NewArmed(DowngradeReason.LargeRoot, confirmLargeHomeFolder: false);

        await vm.ArmAgencyAsync();

        // The SAME detector verdict (LargeRoot) — only the toggle differs from the ON test above — yet
        // the effective mode stays auto-apply: OFF genuinely changes the confirm-vs-silent decision.
        Assert.Equal(AgencyMode.AutoApply, vm.EffectiveMode);
        Assert.False(vm.HasDowngrade);
        Assert.Equal(string.Empty, vm.DowngradeBadge);
    }

    [Fact]
    public async Task OnNeverDowngradesASmallNonHomeRoot()
    {
        AiPanelViewModel vm = NewArmed(DowngradeReason.None, confirmLargeHomeFolder: true);

        await vm.ArmAgencyAsync();

        Assert.Equal(AgencyMode.AutoApply, vm.EffectiveMode);
        Assert.False(vm.HasDowngrade);
    }

    [Fact]
    public async Task AnUnexpectedDetectorExceptionFailsTowardConfirmNotSilentAutoApply()
    {
        // Cross-platform-review nit fix: LargeHomeRootDetector's walk does not catch every possible
        // exception (e.g. File.GetAttributes throwing ArgumentException on a malformed path) — an
        // unexpected fault must fail TOWARD CONFIRM, the safe direction for a confirm-vs-silent trigger.
        var vm = new AiPanelViewModel(
            new ScriptedTurnRunner([]),
            new ScriptedConfirmService(),
            new ScriptedHookHost(selfTestPasses: true),
            new FakeAgentBackend(),
            TimeProvider.System,
            largeHomeRootDetector: (_, _, _) => throw new ArgumentException("malformed path (scripted fault)"))
        {
            ConfiguredMode = AgencyMode.AutoApply,
            ConfirmLargeHomeFolder = true,
        };
        vm.BindSession("/proj-fs5-exfault", FakeFileOps(), conversationId: "conv-fs5-exfault");

        HoardPad.Core.Ai.Provider.AgencyResolution resolution = await vm.ArmAgencyAsync();

        Assert.True(resolution.WriteAgencyArmed);   // still arms — the fault does not disarm write agency.
        Assert.Equal(AgencyMode.ApproveEach, vm.EffectiveMode); // fails TOWARD confirm, not silent auto-apply.
        Assert.Equal("large root", vm.DowngradeBadge);
    }

    [Fact]
    public async Task ADetectorCancellationStillPropagatesRatherThanBeingTreatedAsAFault()
    {
        // Cancellation is first-class (house style) — never swallowed/reinterpreted as a downgrade signal.
        var vm = new AiPanelViewModel(
            new ScriptedTurnRunner([]),
            new ScriptedConfirmService(),
            new ScriptedHookHost(selfTestPasses: true),
            new FakeAgentBackend(),
            TimeProvider.System,
            largeHomeRootDetector: (_, _, ct) => throw new OperationCanceledException(ct))
        {
            ConfiguredMode = AgencyMode.AutoApply,
            ConfirmLargeHomeFolder = true,
        };
        vm.BindSession("/proj-fs5-cancel", FakeFileOps(), conversationId: "conv-fs5-cancel");

        await Assert.ThrowsAsync<OperationCanceledException>(() => vm.ArmAgencyAsync());
    }

    [Fact]
    public async Task TheDetectorIsNeverConsultedWhenTheConfiguredModeIsAlreadyApproveEach()
    {
        // FR-10(a) only downgrades AUTO-APPLY; an already-approve-each session confirms every op anyway,
        // so the home/large-root walk is pure unnecessary I/O and must not run.
        int calls = 0;
        DowngradeReason Detector(string root, int threshold, CancellationToken ct)
        {
            calls++;
            return DowngradeReason.LargeRoot;
        }

        var vm = new AiPanelViewModel(
            new ScriptedTurnRunner([]),
            new ScriptedConfirmService(),
            new ScriptedHookHost(selfTestPasses: true),
            new FakeAgentBackend(),
            TimeProvider.System,
            largeHomeRootDetector: Detector)
        {
            ConfiguredMode = AgencyMode.ApproveEach,
            ConfirmLargeHomeFolder = true,
        };
        vm.BindSession("/proj-fs5", FakeFileOps(), conversationId: "conv-fs5-ae");

        await vm.ArmAgencyAsync();

        Assert.Equal(0, calls);
        Assert.Equal(AgencyMode.ApproveEach, vm.EffectiveMode); // unchanged — no downgrade needed or applied.
        Assert.False(vm.HasDowngrade);
    }

    [Fact]
    public async Task TheDetectorIsNeverConsultedWhenTheConfiguredModeIsReadOnly()
    {
        int calls = 0;
        DowngradeReason Detector(string root, int threshold, CancellationToken ct)
        {
            calls++;
            return DowngradeReason.LargeRoot;
        }

        var vm = new AiPanelViewModel(
            new ScriptedTurnRunner([]),
            new ScriptedConfirmService(),
            new ScriptedHookHost(selfTestPasses: true),
            new FakeAgentBackend(),
            TimeProvider.System,
            largeHomeRootDetector: Detector)
        {
            ConfiguredMode = AgencyMode.ReadOnly,
            ConfirmLargeHomeFolder = true,
        };
        vm.BindSession("/proj-fs5", FakeFileOps(), conversationId: "conv-fs5-ro");

        await vm.ArmAgencyAsync();

        Assert.Equal(0, calls);
        Assert.Equal(AgencyMode.ReadOnly, vm.EffectiveMode);
    }

    [Fact]
    public async Task AnAutoApplyArmWithTheToggleOnRunsTheChannelKillSelfTestAndStillArmsCorrectly()
    {
        // Cross-platform-review REVISE fix: EffectiveMode can become ApproveEach for a user-configured
        // AutoApply session (the FR-10(a) downgrade), and AiTurnRunner.MergeObserveEnv keys the LIVE
        // approve channel off EffectiveMode — so an arm that COULD downgrade must run the SAME M11
        // invariant-5 channel-kill self-test a user-configured approve-each arm runs, not a weaker proof.
        var hookHost = new ScriptedHookHost(selfTestPasses: true, channelKillPasses: true);
        var vm = new AiPanelViewModel(
            new ScriptedTurnRunner([]),
            new ScriptedConfirmService(),
            hookHost,
            new FakeAgentBackend(),
            TimeProvider.System,
            largeHomeRootDetector: (_, _, _) => DowngradeReason.LargeRoot)
        {
            ConfiguredMode = AgencyMode.AutoApply,
            ConfirmLargeHomeFolder = true,
        };
        vm.BindSession("/proj-fs5-ck", FakeFileOps(), conversationId: "conv-fs5-ck");

        HoardPad.Core.Ai.Provider.AgencyResolution resolution = await vm.ArmAgencyAsync();

        Assert.Equal(1, hookHost.ChannelKillCalls); // the gate ran, not skipped.
        Assert.True(resolution.WriteAgencyArmed);   // and it still arms correctly (channel-kill passed).
        Assert.Equal(AgencyMode.ApproveEach, vm.EffectiveMode);
        Assert.Equal("large root", vm.DowngradeBadge);
    }

    [Fact]
    public async Task AnAutoApplyArmWithTheToggleOnRunsTheChannelKillGateEvenWhenTheRootTurnsOutSmall()
    {
        // The gate condition is a SUPERSET (runs whenever an arm COULD downgrade, decided BEFORE the
        // walk) — harmless over-caution on a small root, never skipped just because the trigger didn't
        // end up firing.
        var hookHost = new ScriptedHookHost(selfTestPasses: true, channelKillPasses: true);
        var vm = new AiPanelViewModel(
            new ScriptedTurnRunner([]),
            new ScriptedConfirmService(),
            hookHost,
            new FakeAgentBackend(),
            TimeProvider.System,
            largeHomeRootDetector: (_, _, _) => DowngradeReason.None)
        {
            ConfiguredMode = AgencyMode.AutoApply,
            ConfirmLargeHomeFolder = true,
        };
        vm.BindSession("/proj-fs5-ck2", FakeFileOps(), conversationId: "conv-fs5-ck2");

        await vm.ArmAgencyAsync();

        Assert.Equal(1, hookHost.ChannelKillCalls);
        Assert.Equal(AgencyMode.AutoApply, vm.EffectiveMode); // no downgrade — the root was small.
    }

    [Fact]
    public async Task TheChannelKillGateIsSkippedWhenAutoApplyAndTheToggleIsOff()
    {
        // OFF means the arm can NEVER downgrade to approve-each (FS-5 contract) — no live approve
        // channel is possible, so there is nothing for the channel-kill self-test to prove here.
        var hookHost = new ScriptedHookHost(selfTestPasses: true, channelKillPasses: true);
        var vm = new AiPanelViewModel(
            new ScriptedTurnRunner([]),
            new ScriptedConfirmService(),
            hookHost,
            new FakeAgentBackend(),
            TimeProvider.System,
            largeHomeRootDetector: (_, _, _) => DowngradeReason.LargeRoot)
        {
            ConfiguredMode = AgencyMode.AutoApply,
            ConfirmLargeHomeFolder = false,
        };
        vm.BindSession("/proj-fs5-ck3", FakeFileOps(), conversationId: "conv-fs5-ck3");

        await vm.ArmAgencyAsync();

        Assert.Equal(0, hookHost.ChannelKillCalls);
        Assert.Equal(AgencyMode.AutoApply, vm.EffectiveMode);
    }
}

// ---------------------------------------------------------------------------------------------------
//  Invariant preservation (H8): the toggle must NEVER affect arming, caps, or the FR-26 boundary in
//  either state — only the confirm-vs-silent decision on an already-in-bounds write.
// ---------------------------------------------------------------------------------------------------

public sealed class ConfirmLargeFolderSettingInvariantTests
{
    private static AiFileOpsService FakeFileOps() =>
        new(
            new ChecklistTestSupportSandboxGate(),
            new CountingAtomicFileStore(),
            new NullCapStateStore(),
            new FakeAppDataPaths(),
            sessionId: "s1",
            HoardPad.Core.Ai.Caps.CapConfig.Pinned);

    private static AiPanelViewModel NewWithDetector(DowngradeReason detectorReturns, bool confirmLargeHomeFolder) =>
        new(
            new ScriptedTurnRunner([]),
            new ScriptedConfirmService(),
            new ScriptedHookHost(selfTestPasses: true),
            new FakeAgentBackend(),
            TimeProvider.System,
            largeHomeRootDetector: (_, _, _) => detectorReturns)
        {
            ConfiguredMode = AgencyMode.AutoApply,
            ConfirmLargeHomeFolder = confirmLargeHomeFolder,
        };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WriteAgencyStillArmsOnAHomeOrLargeRootRegardlessOfToggleState(bool confirmLargeHomeFolder)
    {
        // The toggle only changes EffectiveMode/DowngradeBadge; it must never flip WriteAgencyArmed —
        // arming is decided solely by AgencyMapping.Resolve (the self-test gate), untouched by this build.
        var vm = NewWithDetector(DowngradeReason.LargeRoot, confirmLargeHomeFolder);
        vm.BindSession("/proj-fs5-inv", FakeFileOps(), conversationId: "conv-inv-1");

        HoardPad.Core.Ai.Provider.AgencyResolution resolution = await vm.ArmAgencyAsync();

        Assert.True(resolution.WriteAgencyArmed);
        Assert.Equal(SandboxState.Armed, vm.Sandbox);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CapMetersAreUnaffectedByTheToggleOrTheDowngrade(bool confirmLargeHomeFolder)
    {
        // FR-11/FR-38/FR-40 caps come from CapConfig, a completely separate structure the toggle never
        // touches — the meter caps must be identical in every toggle/downgrade combination.
        var vm = NewWithDetector(DowngradeReason.LargeRoot, confirmLargeHomeFolder);
        vm.BindSession("/proj-fs5-inv", FakeFileOps(), conversationId: "conv-inv-2");

        await vm.ArmAgencyAsync();

        Assert.Equal(25, vm.CreatedMeter.Cap);
        Assert.Equal(10, vm.DestructiveMeter.Cap);
        Assert.Equal(20, vm.TouchedMeter.Cap);
    }

    [Fact]
    public async Task TheDowngradedEffectiveModeStillFlowsIntoAWriteAgencyArmedTurnRequest()
    {
        // A downgraded (ApproveEach) turn is still WRITE-CAPABLE and still WriteAgencyArmed — the
        // confirm precedes the write, it does not disable write agency (DR-1's exact boundary: the
        // op still applies, journaled/undoable/logged, just after a confirm).
        var vm = NewWithDetector(DowngradeReason.LargeRoot, confirmLargeHomeFolder: true);
        vm.BindSession("/proj-fs5-inv", FakeFileOps(), conversationId: "conv-inv-3");

        HoardPad.Core.Ai.Provider.AgencyResolution resolution = await vm.ArmAgencyAsync();

        Assert.True(resolution.WriteAgencyArmed);
        Assert.Equal(AgencyMode.ApproveEach, vm.EffectiveMode);
        Assert.True(HoardPad.Core.Ai.Provider.AgencyMapping.IsWriteCapable(vm.EffectiveMode));
    }
}

// ---------------------------------------------------------------------------------------------------
//  Settings round-trip: default ON on a missing file; explicit OFF persists and reloads.
// ---------------------------------------------------------------------------------------------------

public sealed class ConfirmLargeFolderSettingRoundTripTests
{
    [Fact]
    public async Task MissingSettingsFileDefaultsConfirmLargeHomeFolderToOn()
    {
        var paths = new TempAppDataPaths();
        using var _paths = paths;
        var store = new SettingsStore(new RealAtomicFileStore(), paths);

        HoardPadSettings loaded = await store.LoadAsync(CancellationToken.None);

        Assert.True(loaded.ConfirmLargeHomeFolder);
    }

    [Fact]
    public async Task ExplicitOffPersistsAndReloadsOff()
    {
        var paths = new TempAppDataPaths();
        using var _paths = paths;
        var store = new SettingsStore(new RealAtomicFileStore(), paths);

        var saved = HoardPadSettings.Default with { ConfirmLargeHomeFolder = false };
        await store.SaveAsync(saved, CancellationToken.None);

        HoardPadSettings reloaded = await store.LoadAsync(CancellationToken.None);

        Assert.False(reloaded.ConfirmLargeHomeFolder);
    }

    [Fact]
    public async Task SettingsViewModelRoundTripsTheToggleThroughToSettingsAndApply()
    {
        var paths = new TempAppDataPaths();
        using var _paths = paths;
        var store = new SettingsStore(new RealAtomicFileStore(), paths);

        SettingsViewModel vm = await SettingsViewModel.LoadAsync(store, CancellationToken.None);
        Assert.True(vm.ConfirmLargeHomeFolder); // default ON on a fresh (missing-file) load.

        vm.ConfirmLargeHomeFolder = false;
        await vm.SaveCommand.ExecuteAsync(null);

        SettingsViewModel reloaded = await SettingsViewModel.LoadAsync(store, CancellationToken.None);
        Assert.False(reloaded.ConfirmLargeHomeFolder);
    }
}
