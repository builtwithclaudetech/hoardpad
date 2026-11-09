// Fr10cLiveWriteVerifyTests.cs — FR-10(c) accept-loop fix, DECISIVE LIVE end-to-end proof.
//
// Context (docs/phase0-fr10c-findings.md): the production
// host observe endpoint (HookObservationEndpoints.cs) used to accept exactly ONE socket connection
// per turn and serve it to EOF with no re-accept loop. The real `claude` CLI runs a FRESH
// `deny_by_default.py` process per PreToolUse op, and each process opens its OWN connection, sends
// exactly one report, and closes it — so every op after the first arrived on a connection the host
// never accepted, its write was never MA14-matched, and `ArchetypeAOrchestration.ResolveLiveness`
// degraded fail-closed at turn end EVEN THOUGH the write had already applied on disk. The fix makes
// `UnixObservationEndpoint` accept-loop for the turn's life, merging every connection's reports into
// one sequence (a bounded post-cancel accept-drain covers a connection queued in the backlog right at
// the turn's end). This project's history is "green unit tests but broken live app" (STF, QA2-3/4,
// the self-test real-CLI stand-in defect) — so this file drives a REAL `claude -p` turn through the
// REAL production wiring: the REAL CompositionRoot-built MainWindowViewModel + AiPanelViewModel +
// WorkspaceService, using a spy IOpenFolderPrompt to inject a real temp sandbox root over the
// production seam (the same technique Run12AiClusterVerifyTests uses for QA2-18/QA2-3), so the turn
// runs through the REAL AiSessionBinder -> REAL AiTurnRunner -> REAL HoardPad.Host.HookObservationChannel
// -> the fixed UnixObservationEndpoint -> the real bundled hooks/deny_by_default.py -> the real
// claude -p launcher. Every OTHER seam is production; only the OS folder-picker dialog is stood in for
// (a real GTK/portal dialog cannot be driven from a headless test — the root path it would have
// returned is supplied directly instead).
//
// This goes further than Run12's plumbing-only live gate: it asserts on the OBSERVED transcript
// sequence the runner's TurnEvents project (no Degrade chip) and on the on-disk write, not just "the
// turn ended without throwing". The panel's Transcript IS the observed projection of the runner's raw
// TurnEvent sequence — every SafetyEvent/FileOp/AssistantText event HandleTurnEventAsync receives is
// rendered 1:1 into a TranscriptLine (kind + text/op/badge), with NO renaming/filtering that would
// hide a Degrade. Reading it back is therefore equivalent to observing the raw TurnEvent stream for
// this proof's purposes, without adding a decorator around the production runner.
//
// GATED: the live drive (Fr10cLiveTwoRealTurnsProduceNoFalseDegradeAndTheWritesArePersisted) is
// skipped unless HOARDPAD_LIVE_FR10C=1 is set (same convention as the StfLive manual-run tests) —
// each run spends real `claude -p` API budget (~$0.30-0.60 for the two turns below). CI / the normal
// `dotnet test` run stays green because the test returns immediately when the env var is unset. The
// companion Fr10cLiveEvidenceTests class (NOT gated) asserts the artifacts this driver produces are
// present and well-formed, mirroring StfLiveEvidenceTests — that is the durable, CI-checkable
// evidence gate.
//
// Class/method names contain "Fr10cLive" so a verify filter (`--filter FullyQualifiedName~Fr10cLive`)
// resolves both this file and Fr10cLiveEvidenceTests.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using HoardPad.App.Ai;
using HoardPad.App.Hosting;
using HoardPad.App.Session;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using HoardPad.Core.Records;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class Fr10cLiveWriteVerifyTests
{
    private static readonly string ArtifactDir = ResolveArtifactDir();

    private static string ResolveArtifactDir()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HoardPad.sln")))
            dir = dir.Parent;
        string root = dir?.FullName ?? AppContext.BaseDirectory;
        string path = Path.Combine(root, "artifacts", "fr10c-live");
        Directory.CreateDirectory(path);
        return path;
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

    /// <summary>A throwaway real directory (the binder requires Directory.Exists(root)) — the in-root open folder.</summary>
    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "hoardpad-fr10c-live-" + Guid.NewGuid().ToString("N"));

        public TempFolder() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { /* best-effort */ } }
    }

    /// <summary>A spy folder-picker seam that returns a scripted root — records every call (mirrors Run12's SpyOpenFolderPrompt).</summary>
    private sealed class SpyOpenFolderPrompt : IOpenFolderPrompt
    {
        private readonly string? _root;
        public SpyOpenFolderPrompt(string? root) => _root = root;

        public Task<string?> RePromptForRootAsync(string? priorRoot, CancellationToken ct) =>
            Task.FromResult(_root);
    }

    /// <summary>
    /// Build the REAL production service provider (CompositionRoot.ConfigureServices — the real
    /// AiSessionBinder, AgentBackend, ToolPermissionHookHost, HookObservationChannel, WorkspaceService)
    /// but override ONLY the IOpenFolderPrompt seam with a spy that hands back <paramref name="root"/>
    /// (the OS dialog itself cannot be driven headlessly) and IAppDataPaths with a fresh throwaway temp
    /// profile (so no prior session.json biases the reconciler). Every AI seam — backend, hook host,
    /// observation channel, cap store — is the real production singleton.
    /// </summary>
    private static ServiceProvider BuildRealProviderWithSpyPicker(string root, TempAppDataPaths paths)
    {
        var services = new ServiceCollection();
        CompositionRoot.ConfigureServices(services);
        services.AddSingleton<IOpenFolderPrompt>(new SpyOpenFolderPrompt(root));
        services.AddSingleton<HoardPad.Core.Host.Abstractions.IAppDataPaths>(paths);
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    /// <summary>
    /// Bind the panel to <paramref name="root"/> through the REAL lazy first-write-folder path (the exact
    /// production sequence a live user triggers: type a prompt in a write-capable mode with no folder
    /// bound yet -> SendAsync fires RequestWriteFolderAsync -> the spy picker "picks" root -> the REAL
    /// AiSessionBinder.BindLiveFolderAsync arms the session against the REAL claude -p launcher + the
    /// REAL fixed HookObservationChannel). Returns the armed panel + workspace for the caller to drive
    /// further turns against (a folder is now bound, so subsequent sends skip the picker).
    /// </summary>
    private static async Task<(AiPanelViewModel Panel, MainWindowViewModel Vm)> ArmedPanelAsync(
        string root, TempAppDataPaths paths, ServiceProvider provider)
    {
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        var workspace = provider.GetRequiredService<WorkspaceService>();
        var panel = provider.GetRequiredService<AiPanelViewModel>();

        var window = new MainWindow { DataContext = vm };
        window.Show();

        // Wire the lazy handler exactly as App.axaml.cs does in production (the one line the App class
        // owns outside the composition root — Run12's QA2-18 verifier reproduces the same line).
        panel.RequestWriteFolderAsync = () => workspace.RequestWriteFolderAsync(CancellationToken.None);

        await workspace.RestoreAsync(CancellationToken.None);
        panel.ConfiguredMode = AgencyMode.AutoApply; // write-capable, REPORT-mode observe channel (not approve).

        // FS-1 (Decision A, FINAL): RestoreAsync ALWAYS derives a root (here, a clean profile → the My
        // Documents fallback), so the OLD "first write-mode send fires the lazy picker" path no longer
        // reaches this test's intended `root`. Open it explicitly (the production File ▸ Open Folder path)
        // to bind + arm the SAME real `root` this test's later assertions depend on.
        await workspace.OpenFolderAsync(root, CancellationToken.None);
        Assert.Equal(SandboxState.Armed, panel.Sandbox);
        Assert.Contains(root, panel.SandboxRoot);

        // A throwaway prompt establishes the session; the two real assertions run against later turns.
        await DriveTurnAsync(panel, "Reply with exactly the word READY and nothing else.", TimeSpan.FromSeconds(90));

        Assert.Equal(SandboxState.Armed, panel.Sandbox); // the self-test passed — write agency is genuinely armed.
        Assert.Contains(root, panel.SandboxRoot);
        return (panel, vm);
    }

    /// <summary>Drive one real turn to completion through the panel's real SendCommand (the production path).</summary>
    private static async Task DriveTurnAsync(AiPanelViewModel panel, string prompt, TimeSpan timeout)
    {
        panel.PromptInput = prompt;
        using var cts = new CancellationTokenSource(timeout);
        Task send = panel.SendCommand.ExecuteAsync(null);
        while (!send.IsCompleted && !cts.IsCancellationRequested)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }
        await send; // propagate any fault; a timeout here means the turn hung (itself a finding to report).
    }

    // ─────────────────────────────────────────────────────────────────────────
    // The decisive proof: two real turns, asserting NO Degrade chip + the write is present on disk.
    // ─────────────────────────────────────────────────────────────────────────
    [AvaloniaFact]
    public async Task Fr10cLiveTwoRealTurnsProduceNoFalseDegradeAndTheWritesArePersisted()
    {
        if (Environment.GetEnvironmentVariable("HOARDPAD_LIVE_FR10C") != "1")
            return; // manually-gated live drive — set HOARDPAD_LIVE_FR10C=1 to run (spends real API budget).

        Assert.True(ResolveClaudeOnPath() is not null, "HOARDPAD_LIVE_FR10C=1 requires the claude CLI on PATH.");

        // ---- Turn (a): Read-then-Write, in a fresh armed sandbox ----
        using var tempA = new TempFolder();
        File.WriteAllText(Path.Combine(tempA.Path, "seed.txt"), "seed content for FR-10(c) live proof");
        using var pathsA = new TempAppDataPaths();
        using ServiceProvider providerA = BuildRealProviderWithSpyPicker(tempA.Path, pathsA);
        (AiPanelViewModel panelA, _) = await ArmedPanelAsync(tempA.Path, pathsA, providerA);

        string outA = Path.Combine(tempA.Path, "out_a.txt");
        await DriveTurnAsync(
            panelA,
            "Read the file seed.txt in this folder first. Then create a NEW file named out_a.txt "
                + "in this same folder containing exactly the text DONE (no other content). "
                + "Do the Read before the Write. Do not do anything else.",
            TimeSpan.FromSeconds(120));

        WriteTranscriptJson("transcript-readwrite.json", panelA.Transcript);

        bool degradeA = panelA.Transcript.Any(l =>
            l.Kind == TranscriptKind.SafetyEventChip && l.Text.Contains("degrade", StringComparison.OrdinalIgnoreCase));
        Assert.False(degradeA,
            "FR-10(c) REGRESSION (Read-then-Write turn): a Degrade chip rendered for a legitimate in-root write.\n"
                + "Full transcript:\n" + DumpTranscript(panelA.Transcript));
        Assert.True(File.Exists(outA), $"Turn (a) did not persist the expected file to disk: {outA}");
        Assert.True(
            panelA.Transcript.Any(l => l.Kind == TranscriptKind.FileOpChip && l.Badge == OpOutcomeBadge.Applied),
            "Turn (a): no FileOpChip with Badge=Applied was rendered (the fixed endpoint should have "
                + "accepted the write op's connection and reported it).\nFull transcript:\n" + DumpTranscript(panelA.Transcript));

        // ---- Turn (b): Write-last (the write is the LAST action of the turn), in a second fresh sandbox ----
        using var tempB = new TempFolder();
        using var pathsB = new TempAppDataPaths();
        using ServiceProvider providerB = BuildRealProviderWithSpyPicker(tempB.Path, pathsB);
        (AiPanelViewModel panelB, MainWindowViewModel vmB) = await ArmedPanelAsync(tempB.Path, pathsB, providerB);

        string outB = Path.Combine(tempB.Path, "out_b.txt");
        await DriveTurnAsync(
            panelB,
            "Create a NEW file named out_b.txt in this folder containing exactly the text DONE "
                + "(no other content). Make this the LAST action you take, then end the turn immediately "
                + "with no further commentary or tool calls.",
            TimeSpan.FromSeconds(120));

        WriteTranscriptJson("transcript-writelast.json", panelB.Transcript);

        bool degradeB = panelB.Transcript.Any(l =>
            l.Kind == TranscriptKind.SafetyEventChip && l.Text.Contains("degrade", StringComparison.OrdinalIgnoreCase));
        Assert.False(degradeB,
            "FR-10(c) REGRESSION (Write-last turn): a Degrade chip rendered for a legitimate in-root write.\n"
                + "Full transcript:\n" + DumpTranscript(panelB.Transcript));
        Assert.True(File.Exists(outB), $"Turn (b) did not persist the expected file to disk: {outB}");
        Assert.True(
            panelB.Transcript.Any(l => l.Kind == TranscriptKind.FileOpChip && l.Badge == OpOutcomeBadge.Applied),
            "Turn (b): no FileOpChip with Badge=Applied was rendered.\nFull transcript:\n" + DumpTranscript(panelB.Transcript));

        // ---- Render evidence: the REAL MainWindow (from providerB) hosting the armed, post-live-turn
        // panel, showing an applied chip and NO degrade chip. ----
        var window = new MainWindow { DataContext = vmB };
        window.Show();
        vmB.ToggleAiPanelCommand.Execute(null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var aiPanelView = window.FindControl<AiPanelView>("AiPanel");
        Assert.NotNull(aiPanelView);

        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(ArtifactDir, "fr10c-armed-no-degrade.png"));
    }

    private static string DumpTranscript(IEnumerable<HoardPad.App.Ai.TranscriptLine> lines) =>
        string.Join("\n", lines.Select(l =>
            $"  [{l.Kind}] Text={l.Text} Op={l.Op} Target={l.TargetPath} Badge={l.Badge}"));

    private static readonly JsonSerializerOptions TranscriptJsonOptions = new() { WriteIndented = true };

    private static void WriteTranscriptJson(string fileName, IEnumerable<HoardPad.App.Ai.TranscriptLine> lines)
    {
        var payload = lines.Select(l => new
        {
            kind = l.Kind.ToString(),
            text = l.Text,
            op = l.Op.ToString(),
            targetPath = l.TargetPath,
            badge = l.Badge.ToString(),
            isDanger = l.IsDanger,
        }).ToList();
        string json = JsonSerializer.Serialize(payload, TranscriptJsonOptions);
        File.WriteAllText(Path.Combine(ArtifactDir, fileName), json);
    }
}
