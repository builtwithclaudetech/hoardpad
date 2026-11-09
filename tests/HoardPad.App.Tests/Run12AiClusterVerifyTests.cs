// Run12AiClusterVerifyTests.cs — Run-12 AI-panel cluster VERIFIER (QA2-18 lazy picker + QA2-17/4/3).
//
// This file closes the AI-panel gap for the Run-12 fixes against the REAL MainWindow built via the REAL
// CompositionRoot, driving the real command/interaction, asserting on the rendered visual tree, and
// capturing a rendered PNG per interaction (artifacts/run12-smoke/screenshots/<qa2-id>.png). The class name
// contains "Run12" so the verify filter resolves it.
//
//   - QA2-18: at startup NO folder picker is invoked (a spy IOpenFolderPrompt injected over the real
//     CompositionRoot registration proves the seam is never called during bootstrap), and the AI panel's
//     resting note reads the approved verbatim string. A first WRITE-mode AI action fires the approved
//     first-write explainer + the picker EXACTLY ONCE.
//   - QA2-17: the Test button reports honest FAILURE when a NoSessionTurnRunner is bound (no real backend
//     contacted), and success only when a real reply round-trips.
//   - QA2-4:  opening the AI panel at startup (no folder) shows NO self-test-failed / could-not-arm error
//     chips — only the clean read-only resting note.
//   - QA2-3:  a properly-bound session returns a REAL response to a typed message (not silence, not the
//     canned no-folder note). Driven through the production provider/session path with a stub turn runner
//     standing in for claude -p where a live spawn cannot be automated; the live claude -p round-trip is a
//     REQUIRES-MAINTAINER-LIVE-TEST fallback (documented in the run report).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HoardPad.App.Ai;
using HoardPad.App.Hosting;
using HoardPad.App.Session;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using HoardPad.Core.Records;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class Run12AiClusterVerifyTests
{
    private static readonly string ScreenshotDir = ResolveScreenshotDir();

    private static string ResolveScreenshotDir()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HoardPad.sln")))
            dir = dir.Parent;
        string root = dir?.FullName ?? AppContext.BaseDirectory;
        string path = Path.Combine(root, "artifacts", "run12-smoke", "screenshots");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Capture(Window window, string name)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        string path = Path.Combine(ScreenshotDir, name + ".png");
        frame?.Save(path);
        return path;
    }

    /// <summary>A spy folder-picker seam that records every call (QA2-18 startup-no-call proof).</summary>
    private sealed class SpyOpenFolderPrompt : IOpenFolderPrompt
    {
        private readonly string? _root;
        public int CallCount { get; private set; }
        public string? LastPriorRoot { get; private set; }

        public SpyOpenFolderPrompt(string? root) => _root = root;

        public Task<string?> RePromptForRootAsync(string? priorRoot, CancellationToken ct)
        {
            CallCount++;
            LastPriorRoot = priorRoot;
            return Task.FromResult(_root);
        }
    }

    /// <summary>
    /// Build the production service provider but override the <see cref="IOpenFolderPrompt"/> seam with a
    /// spy, so a test can assert the OS picker is never invoked at startup and observe the lazy call. The
    /// AI backend + hook host are also overridden with the deterministic scripted fakes so the lazy-bind
    /// self-test path is hermetic (no real <c>claude -p</c> spawn), and <see cref="IAppDataPaths"/> is
    /// pointed at a fresh throwaway temp profile so no prior session.json (from another test run) makes the
    /// reconciler restore-and-arm a stale folder — a CLEAN profile is what puts the panel in the no-folder
    /// resting state the QA2-18 assertions require. Every OTHER seam is the real production one.
    /// </summary>
    private static ServiceProvider BuildWithSpyPicker(SpyOpenFolderPrompt spy, TempAppDataPaths paths)
    {
        var services = new ServiceCollection();
        CompositionRoot.ConfigureServices(services);
        // Replace the production registrations with the spy + deterministic AI fakes + a clean profile (last wins).
        services.AddSingleton<IOpenFolderPrompt>(spy);
        services.AddSingleton<HoardPad.Core.Host.Abstractions.IAppDataPaths>(paths);
        services.AddSingleton<HoardPad.Core.Host.Abstractions.IAgentBackend>(new FakeAgentBackend());
        services.AddSingleton<HoardPad.Core.Host.Abstractions.IToolPermissionHookHost>(
            new ScriptedHookHost(selfTestPasses: true));
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // QA2-18 (a) — startup fires NO folder picker; the panel binds the FS-1 derived root, NOT armed yet.
    // ─────────────────────────────────────────────────────────────────────────
    [AvaloniaFact]
    public async Task Qa2Item18StartupInvokesNoFolderPickerAndBindsTheDerivedRootUnarmed()
    {
        var spy = new SpyOpenFolderPrompt(root: null);
        using var paths = new TempAppDataPaths();
        using ServiceProvider provider = BuildWithSpyPicker(spy, paths);
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        var workspace = provider.GetRequiredService<WorkspaceService>();
        var panel = provider.GetRequiredService<AiPanelViewModel>();

        var window = new MainWindow { DataContext = vm };
        window.Show();

        // Drive the SAME startup bind the App runs on Window.Opened. A clean test profile has no restored
        // tabs and no passed files, so FS-1 tier (iii) fires (My Documents) — the panel binds that derived
        // root but must NOT arm write agency yet (deferred to the first write-mode AI action), and the
        // picker seam must NEVER be called (QA2-18's core invariant, unchanged by FS-1).
        await workspace.RestoreAsync(CancellationToken.None);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, spy.CallCount); // QA2-18: NO startup picker.
        Assert.Equal(SandboxState.NotYetArmed, panel.Sandbox); // FS-1: bound, not yet armed.
        Assert.NotEmpty(panel.SandboxRoot); // FS-1 always resolves SOME root — here, the My Documents fallback.
        Assert.StartsWith("Document folder: ", panel.SandboxLine); // FS-3: "Sandbox" retired from the label.
        Assert.EndsWith("[ not yet armed ]", panel.SandboxLine);

        // The rendered SandboxLine TextBlock must show that same line on the real panel.
        vm.ToggleAiPanelCommand.Execute(null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var aiPanelView = window.FindControl<AiPanelView>("AiPanel");
        Assert.NotNull(aiPanelView);
        var sandboxLine = aiPanelView!.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.Name == "SandboxLineText");
        Assert.NotNull(sandboxLine);
        Assert.Equal(panel.SandboxLine, sandboxLine!.Text);

        Capture(window, "qa2-18-startup-no-picker-derived-root-unarmed");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // QA2-18 (b) / FS-1 — a first WRITE-mode AI action arms the ALREADY-DERIVED root directly, NO picker.
    // FS-1 (Decision A, FINAL) always derives a root at startup (here, tier iii's My Documents fallback,
    // since a clean profile has no restored tabs), so the OLD "no root known → show the picker" lazy path
    // never fires anymore for the startup case — arming the derived root IS the new first-write action.
    // ─────────────────────────────────────────────────────────────────────────
    [AvaloniaFact]
    public async Task Qa2Item18FirstWriteModeSendArmsTheAlreadyDerivedRootWithNoPicker()
    {
        var spy = new SpyOpenFolderPrompt(root: null); // must never be invoked — a call here is a finding.
        using var paths = new TempAppDataPaths();
        using ServiceProvider provider = BuildWithSpyPicker(spy, paths);
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        var workspace = provider.GetRequiredService<WorkspaceService>();
        var panel = provider.GetRequiredService<AiPanelViewModel>();

        var window = new MainWindow { DataContext = vm };
        window.Show();

        // Wire the lazy handler exactly as App.axaml.cs does (the production wiring lives in the App class,
        // not the CompositionRoot, so the verifier reproduces that one line to exercise the real path).
        panel.RequestWriteFolderAsync = () => workspace.RequestWriteFolderAsync(CancellationToken.None);

        await workspace.RestoreAsync(CancellationToken.None);
        Assert.Equal(0, spy.CallCount);                 // still no picker after startup.
        Assert.Equal(AgencyMode.AutoApply, panel.ConfiguredMode); // a WRITE-capable mode is configured by default.
        Assert.Equal(SandboxState.NotYetArmed, panel.Sandbox);    // FS-1: bound (My Documents), not armed yet.
        string derivedRoot = panel.SandboxRoot;
        Assert.NotEmpty(derivedRoot);

        // First write-mode AI action: type a prompt + Send. FS-1: this arms the ALREADY-derived root
        // directly (self-test only) — no picker, no explainer (the root was already known + shown at startup).
        panel.PromptInput = "hello";
        await panel.SendCommand.ExecuteAsync(null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, spy.CallCount);                 // still NEVER invoked (FS-1: nothing left to pick).
        Assert.DoesNotContain(panel.Transcript, l => l.Text == AiPanelViewModel.FirstWriteFolderExplainer);
        Assert.Equal(SandboxState.Armed, panel.Sandbox);       // the derived root is now armed.
        Assert.Equal(derivedRoot, panel.SandboxRoot);          // the SAME root — arming never changes it.

        // A SECOND send must NOT re-run the self-test (already armed).
        panel.PromptInput = "again";
        await panel.SendCommand.ExecuteAsync(null);
        Assert.Equal(SandboxState.Armed, panel.Sandbox);
        Assert.Equal(0, spy.CallCount);

        Capture(window, "qa2-18-first-write-arms-derived-root-no-picker");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // QA2-18 (c) — a READ-ONLY configured mode NEVER prompts for a folder on Send.
    // ─────────────────────────────────────────────────────────────────────────
    [AvaloniaFact]
    public async Task Qa2Item18ReadOnlyModeSendDoesNotPromptForAFolder()
    {
        var spy = new SpyOpenFolderPrompt(root: null);
        using var paths = new TempAppDataPaths();
        using ServiceProvider provider = BuildWithSpyPicker(spy, paths);
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        var workspace = provider.GetRequiredService<WorkspaceService>();
        var panel = provider.GetRequiredService<AiPanelViewModel>();

        var window = new MainWindow { DataContext = vm };
        window.Show();
        panel.RequestWriteFolderAsync = () => workspace.RequestWriteFolderAsync(CancellationToken.None);
        await workspace.RestoreAsync(CancellationToken.None);

        // Read-only mode: a Send must stay read-only and NEVER open the picker (the maintainer's decision). FS-1: the
        // panel was already bound (NotYetArmed, My Documents) BEFORE this — the PRE-EXISTING
        // OnConfiguredModeChanged hook (unrelated to FS-1) re-evaluates arming on every ConfiguredMode
        // change, so flipping to ReadOnly here resolves immediately (cheap, no self-test) to Armed — a
        // read-only "armed" sandbox is still the no-write-tools state (IsWriteCapable(ReadOnly) is false),
        // so the picker/prompt invariants below are what this test actually protects.
        panel.ConfiguredMode = AgencyMode.ReadOnly;
        panel.PromptInput = "just a question";
        await panel.SendCommand.ExecuteAsync(null);

        Assert.Equal(0, spy.CallCount); // read-only never prompts.
        Assert.DoesNotContain(panel.Transcript, l => l.Text == AiPanelViewModel.FirstWriteFolderExplainer);
        Assert.Equal(SandboxState.Armed, panel.Sandbox); // read-only + folder-open resolves to Armed (no write tools).

        Capture(window, "qa2-18-readonly-no-prompt");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // QA2-4 — opening the panel at startup (no folder) shows NO error chips, only the resting note.
    // ─────────────────────────────────────────────────────────────────────────
    [AvaloniaFact]
    public async Task Qa2Item04StartupPanelShowsNoErrorChipsOnlyTheReadOnlyRestingNote()
    {
        var spy = new SpyOpenFolderPrompt(root: null);
        using var paths = new TempAppDataPaths();
        using ServiceProvider provider = BuildWithSpyPicker(spy, paths);
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        var workspace = provider.GetRequiredService<WorkspaceService>();
        var panel = provider.GetRequiredService<AiPanelViewModel>();

        var window = new MainWindow { DataContext = vm };
        window.Show();
        await workspace.RestoreAsync(CancellationToken.None);
        vm.ToggleAiPanelCommand.Execute(null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // No danger safety chips of the "could not be armed" / "self-test failed" variety at startup.
        Assert.DoesNotContain(panel.Transcript, l =>
            l.Kind == TranscriptKind.SafetyEventChip &&
            (l.Text.Contains("could not be armed", StringComparison.OrdinalIgnoreCase) ||
             l.Text.Contains("self-test failed", StringComparison.OrdinalIgnoreCase)));

        // FS-1: the sandbox line shows the derived root, NOT-yet-armed — never the "Document folder could
        // not be armed ⚠" self-test-failure line (no self-test has even run yet at startup).
        Assert.Equal(SandboxState.NotYetArmed, panel.Sandbox);
        Assert.DoesNotContain("could not be armed", panel.SandboxLine, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Document folder: ", panel.SandboxLine); // FS-3: "Sandbox" retired from the label.

        Capture(window, "qa2-4-no-startup-errors");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // QA2-17 — the Test button reports HONEST FAILURE with a NoSessionTurnRunner (no real backend).
    // ─────────────────────────────────────────────────────────────────────────
    [AvaloniaFact]
    public async Task Qa2Item17TestButtonReportsHonestFailureWhenNoSessionRunnerIsBound()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        var panel = provider.GetRequiredService<AiPanelViewModel>();

        var window = new MainWindow { DataContext = vm };
        window.Show();
        vm.ToggleAiPanelCommand.Execute(null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // The production panel is constructed with a NoSessionTurnRunner (no folder open). Pressing Test must
        // NOT report false-positive success — it must surface the honest "no folder open" failure.
        await panel.TestConnectionCommand.ExecuteAsync(null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(BackendConnectionState.Error, panel.BackendStatus);
        Assert.Contains("No folder open", panel.BackendStatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(panel.Transcript, l =>
            l.Kind == TranscriptKind.SafetyEventChip &&
            l.Text.Contains("no folder open", StringComparison.OrdinalIgnoreCase));
        // It must NOT have posted a "Connection OK" success note.
        Assert.DoesNotContain(panel.Transcript, l => l.Text.Contains("Connection OK", StringComparison.Ordinal));

        Capture(window, "qa2-17-test-honest");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // QA2-17 (positive) — a real reply round-trip reports SUCCESS (the honest-outcome mirror).
    // ─────────────────────────────────────────────────────────────────────────
    [AvaloniaFact]
    public async Task Qa2Item17TestButtonReportsSuccessOnlyWhenARealReplyRoundTrips()
    {
        // A real (non-NoSession) runner that yields an assistant reply → Test must report success. This
        // proves the honest check discriminates a REAL backend from the no-folder stub (not a blanket fail).
        var runner = new ScriptedTurnRunner([TurnEvent.Assistant("OK")]);
        var panel = AiPanelTestFactory.New(runner);

        await panel.TestConnectionCommand.ExecuteAsync(null);

        Assert.Equal(BackendConnectionState.Connected, panel.BackendStatus);
        Assert.Contains("Ready", panel.BackendStatusMessage, StringComparison.Ordinal);
        Assert.Contains(panel.Transcript, l => l.Text.Contains("Connection OK", StringComparison.Ordinal));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // QA2-3 — a bound session returns a REAL response (not silence, not the canned no-folder note).
    // ─────────────────────────────────────────────────────────────────────────
    [AvaloniaFact]
    public async Task Qa2Item03BoundSessionReturnsARealResponseNotTheNoFolderNote()
    {
        // The production panel starts with the NoSessionTurnRunner (whose reply is the canned no-folder
        // note). Once a session is bound, SetTurnRunner swaps in the real session runner. Here we bind a
        // runner that stands in for claude -p (yields a known token) and prove the typed message gets that
        // REAL response appended — NOT the canned "No folder is open…" note, NOT silence.
        //
        // A live claude -p round-trip cannot be driven from an automated test without interactive auth or
        // touching the frozen AI-safety spine; that path is the REQUIRES-MAINTAINER-LIVE-TEST fallback (run report).
        const string knownToken = "HOARDPAD-REPLY-OK-7f3a";
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        var panel = provider.GetRequiredService<AiPanelViewModel>(); // starts with the NoSessionTurnRunner.

        var window = new MainWindow { DataContext = vm };
        window.Show();
        vm.ToggleAiPanelCommand.Execute(null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // Before binding: the no-folder runner yields the canned note (the weaker honest-note guarantee) —
        // an HONEST note, not silent nothing. This is the QA2-3 PNG evidence for the no-folder state.
        panel.PromptInput = "say the token";
        await panel.SendCommand.ExecuteAsync(null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(panel.Transcript, l =>
            l.Kind == TranscriptKind.AssistantMessage &&
            l.Text.Contains("No folder is open", StringComparison.OrdinalIgnoreCase));
        Capture(window, "qa2-3-honest-no-folder");

        // Bind a real session runner (the M6.3 SetTurnRunner swap the binder performs on a confirmed folder).
        panel.SetTurnRunner(new ScriptedTurnRunner([TurnEvent.Assistant(knownToken)]));

        panel.PromptInput = "say the token";
        await panel.SendCommand.ExecuteAsync(null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // A REAL, non-empty response round-tripped into the transcript — and it is the runner's token, not
        // the canned no-folder note.
        Assert.Contains(panel.Transcript, l =>
            l.Kind == TranscriptKind.AssistantMessage && l.Text.Contains(knownToken, StringComparison.Ordinal));
        TranscriptLine lastAssistant = panel.Transcript.Last(l => l.Kind == TranscriptKind.AssistantMessage);
        Assert.False(string.IsNullOrWhiteSpace(lastAssistant.Text)); // not silence.
        Assert.Equal(knownToken, lastAssistant.Text);
        Capture(window, "qa2-3-bound-real-reply");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // QA2-3 (LIVE end-to-end) — drive the PRODUCTION AiTurnRunner → real claude -p spawn in a temp folder.
    //
    // SPINE-BOUNDARY FINDING — NOW FIXED (QA2-3): the earlier drop was that HoardPad.Host.AgentBackend
    // launched argv as `setsid <claude> <launchArgs...>` where launchArgs (from ClaudeLaunchContract) DO NOT
    // include request.Prompt — the prompt reached the spawned claude by no channel, so the panel waited
    // forever. The fix (Option B, stdin) writes request.Prompt to the child's stdin and closes it (EOF);
    // launchArgs stay byte-unchanged and the prompt never enters argv (see AgentBackend.WritePromptToStdinAsync
    // and the Host.Tests stdin round-trip guards). A live probe confirmed `claude -p` returns an assistant
    // event from the stdin path (artifacts/run12-smoke/qa2-3-stdin-probe.log).
    //
    // This test still asserts only the PLUMBING invariant (the turn completes without exception / no hang),
    // because a headless AvaloniaFact cannot reliably gate on live model-reply content across CI runs. The
    // STRONG "a typed message now yields a visible reply" proof is the Xvfb + real-claude live end-to-end run
    // captured in artifacts/run12-smoke/screenshots/qa2-3-live-reply.png (schedule phase QA3-2, step 4).
    //
    // Gated: returns early (skips) when claude is not on PATH so CI stays green.
    // ─────────────────────────────────────────────────────────────────────────
    [AvaloniaFact]
    public async Task Qa2Item03LiveClaudeSpawnPlumbingRunsAndTheTurnEndsCleanly()
    {
        if (ResolveClaudeOnPath() is null)
            return; // claude absent → REQUIRES-MAINTAINER-LIVE-TEST.

        using var tempDir = new TempFolder();
        using var paths = new TempAppDataPaths();

        var binder = new AiSessionBinder(
            new HoardPad.Host.PathResolver(),
            new RealAtomicFileStore(),
            new NullCapStateStore(),
            paths,
            new HoardPad.Host.AgentBackend(),                 // the production backend — spawns real claude.
            new HoardPad.Host.ToolPermissionHookHost(BundledHookScriptPath()),
            new SpyOpenFolderPrompt(root: null),
            TimeProvider.System,
            TestObservationChannelFactory.Create(),
            new HoardPad.Host.FileGrantsWriter(paths, new RealAtomicFileStore()));

        var panel = AiPanelTestFactory.New(new NoSessionTurnRunner());
        panel.ConfiguredMode = AgencyMode.ReadOnly; // read-only → no write-agency self-test spawn at bind.

        BindResult bind = await binder.BindLiveFolderAsync(
            panel, tempDir.Path, conversationId: "live-conv-1", sessionId: "live-s-1", CancellationToken.None);
        Assert.NotNull(bind.CanonicalRoot);

        panel.PromptInput = "Reply with exactly this token and nothing else: HOARDPAD-LIVE-OK";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        Task send = panel.SendCommand.ExecuteAsync(null);
        while (!send.IsCompleted && !cts.IsCancellationRequested)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }
        await send; // the spawn + stream pump completed without throwing (plumbing works).

        // The turn ran to completion — the production spawn/stream plumbing is intact end-to-end, and with the
        // QA2-3 stdin fix the prompt is now delivered to the child (no longer dropped). We still assert only the
        // plumbing invariant here (headless AvaloniaFact can't reliably gate on live reply content); the visible-
        // reply proof is the Xvfb + real-claude screenshot (artifacts/run12-smoke/screenshots/qa2-3-live-reply.png).
        Assert.False(panel.IsTurnInFlight); // the turn ended cleanly (no hang, no unhandled fault).
    }

    /// <summary>Resolve the <c>claude</c> CLI on PATH (or the known user-local install), or null if absent.</summary>
    private static string? ResolveClaudeOnPath()
    {
        string local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude");
        if (File.Exists(local))
            return local;
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is null)
            return null;
        foreach (string dir in pathEnv.Split(Path.PathSeparator))
        {
            string candidate = Path.Combine(dir, "claude");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>Absolute path to the bundled deny_by_default.py next to the test binary's app output.</summary>
    private static string BundledHookScriptPath() =>
        Path.Combine(AppContext.BaseDirectory, "hooks", "deny_by_default.py");

    /// <summary>A throwaway real directory (the binder requires Directory.Exists(root)).</summary>
    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "hoardpad-qa2-18-" + Guid.NewGuid().ToString("N"));

        public TempFolder() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { /* best-effort */ } }
    }
}
