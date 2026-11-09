// Qa320LiveUndoVerifyTests.cs — QA3-20, DECISIVE LIVE end-to-end proof.
//
// Question: after a real AI turn edits a document that is ALREADY OPEN in an editor tab, does that edit
// land in the tab's undo stack so a real Ctrl+Z key press reverts it?
//
// This is a verification-first task (docs/tasklist.md QA3-20) — QA3-9 (RefreshFromDiskAsync ->
// ExternalWriteApplied) and QA3-22 (per-tab TextDocument/UndoStack) were both already fixed with this
// exact requirement in mind (see EditorViewModel.cs / EditorView.axaml.cs doc comments referencing
// "QA3-20" directly, and ExternalWriteRefreshTests.RefreshIsUndoneByCtrlZRestoringThePreRefreshBuffer,
// which proves the mechanism by calling RefreshFromDiskAsync directly). What that unit-level coverage
// does NOT prove is the WIRING from a real AI turn through to that mechanism:
// AiPanelViewModel.HandleTurnEventAsync (FileOp, Outcome=Applied) -> RefreshOpenDocumentAsync ->
// WorkspaceService.RefreshOpenDocumentByPathAsync -> the matching open tab's EditorViewModel ->
// EditorViewModel.RefreshFromDiskAsync -> ExternalWriteApplied -> EditorView.OnExternalWriteApplied's
// undo-preserving Document.Replace. This file drives that FULL chain with a REAL `claude -p` turn through
// the REAL production wiring (mirrors Fr10cLiveWriteVerifyTests' technique: the REAL CompositionRoot-built
// MainWindowViewModel + AiPanelViewModel + WorkspaceService, using a spy IOpenFolderPrompt to inject a
// real temp sandbox root over the production seam — the OS folder-picker dialog is the only stood-in
// seam), then proves the decisive QA3-20 assertion with a REAL Ctrl+Z KEY PRESS (not a direct command
// call) against the real, XAML-loaded MainWindow's Window-level KeyBinding (mirrors
// FindReplaceUndoTests.WindowLevelCtrlZUndoesAReplaceWhenFocusIsOnANonEditorControl's technique for the
// QA3-5 acceptance scenario).
//
// GATED: the live drive (Qa320LiveAiEditOfAnAlreadyOpenTabIsUndoneByCtrlZ) is skipped unless
// HOARDPAD_LIVE_QA320=1 is set (same convention as StfLive / Fr10cLive) — each run spends real
// `claude -p` API budget (~$0.10-0.30 for the two turns below: one throwaway arm turn + one edit turn).
// CI / the normal `dotnet test` run stays green because the test returns immediately when the env var is
// unset.
//
// Class name contains "Qa320Live" so a verify filter (`--filter FullyQualifiedName~Qa320Live`) resolves
// this file alone.

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
using Avalonia.Input;
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

public sealed class Qa320LiveUndoVerifyTests
{
    private const string Marker = "QA320-LIVE-MARKER-9f31";

    private static readonly string ArtifactDir = ResolveArtifactDir();

    private static string ResolveArtifactDir()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HoardPad.sln")))
            dir = dir.Parent;
        string root = dir?.FullName ?? AppContext.BaseDirectory;
        string path = Path.Combine(root, "artifacts", "qa3-20-live");
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
            System.IO.Path.GetTempPath(), "hoardpad-qa320-live-" + Guid.NewGuid().ToString("N"));

        public TempFolder() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { /* best-effort */ } }
    }

    /// <summary>A spy folder-picker seam that returns a scripted root (the OS dialog cannot be driven headlessly).</summary>
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
    /// but override ONLY the IOpenFolderPrompt seam with a spy that hands back <paramref name="root"/> and
    /// IAppDataPaths with a fresh throwaway temp profile (no prior session.json bias). Every AI seam is the
    /// real production singleton.
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

    /// <summary>
    /// QA3-9's refresh is fired-and-forgotten from HandleTurnEventAsync (`_ = RefreshOpenDocumentAsync?.Invoke(...)`)
    /// so it can still be in flight when DriveTurnAsync's SendCommand awaitable completes. Poll for the
    /// editor buffer to actually contain the marker (bounded — a timeout here is itself a finding).
    /// </summary>
    private static async Task<string> WaitForEditorTextToContainAsync(
        AvaloniaEdit.TextEditor editor, string needle, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            Dispatcher.UIThread.RunJobs();
            if (editor.Text.Contains(needle, StringComparison.Ordinal))
                return editor.Text;
            await Task.Delay(100);
        }
        return editor.Text; // caller asserts — the returned (non-matching) text is useful failure context.
    }

    [AvaloniaFact]
    public async Task Qa320LiveAiEditOfAnAlreadyOpenTabIsUndoneByCtrlZ()
    {
        if (Environment.GetEnvironmentVariable("HOARDPAD_LIVE_QA320") != "1")
            return; // manually-gated live drive — set HOARDPAD_LIVE_QA320=1 to run (spends real API budget).

        Assert.True(ResolveClaudeOnPath() is not null, "HOARDPAD_LIVE_QA320=1 requires the claude CLI on PATH.");

        using var temp = new TempFolder();
        const string originalContent = "line one\nline two\nline three\n";
        string targetPath = Path.Combine(temp.Path, "target.txt");
        await File.WriteAllTextAsync(targetPath, originalContent);

        using var paths = new TempAppDataPaths();
        using ServiceProvider provider = BuildRealProviderWithSpyPicker(temp.Path, paths);

        var vm = provider.GetRequiredService<MainWindowViewModel>();
        var workspace = provider.GetRequiredService<WorkspaceService>();
        var panel = provider.GetRequiredService<AiPanelViewModel>();

        var window = new MainWindow { DataContext = vm };
        window.Show();

        // Wire the two seams App.axaml.cs owns OUTSIDE the composition root, exactly as production does:
        // the lazy write-folder picker, and (QA3-9, the mechanism THIS test verifies) the post-write
        // refresh seam that lets an applied AI write reach a matching OPEN editor tab's buffer.
        panel.RequestWriteFolderAsync = () => workspace.RequestWriteFolderAsync(CancellationToken.None);
        panel.RefreshOpenDocumentAsync = path => workspace.RefreshOpenDocumentByPathAsync(path, CancellationToken.None);

        await workspace.RestoreAsync(CancellationToken.None);
        panel.ConfiguredMode = AgencyMode.AutoApply; // write-capable, REPORT-mode observe channel.

        // FS-1 (Decision A, FINAL): RestoreAsync ALWAYS derives a root (here, a clean profile → the My
        // Documents fallback), so the OLD "first write-mode send fires the lazy picker" path no longer
        // reaches this test's intended `temp.Path` root. Open it explicitly (the production File ▸ Open
        // Folder path) so the AI is scoped to `temp.Path` — where target.txt actually lives — as before.
        await workspace.OpenFolderAsync(temp.Path, CancellationToken.None);

        // Open target.txt into a REAL tab BEFORE any AI turn runs — the QA3-20 scenario requires the
        // document to already be open (in the editor's undo stack) when the AI edits it.
        await workspace.OpenFolderTreeFileAsync(targetPath, CancellationToken.None);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var editorView = window.FindControl<EditorView>("EditorSurface");
        Assert.NotNull(editorView);
        AvaloniaEdit.TextEditor textEditor = editorView!.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().Single();
        Assert.Equal(originalContent, textEditor.Text);

        // The first write-mode send triggers the real lazy bind (picker -> BindLiveFolderAsync -> arm). A
        // throwaway prompt establishes the session; the decisive assertions run against the second turn.
        await DriveTurnAsync(panel, "Reply with exactly the word READY and nothing else.", TimeSpan.FromSeconds(90));
        Assert.Equal(SandboxState.Armed, panel.Sandbox); // the self-test passed — write agency is genuinely armed.
        Assert.Contains(temp.Path, panel.SandboxRoot);

        // The decisive turn: instruct the AI to edit the file that is ALREADY OPEN in the tab above.
        await DriveTurnAsync(
            panel,
            $"Append exactly one new line containing the text {Marker} to the end of the existing file "
                + "target.txt in this folder. Do not remove or change any of the existing lines. Make "
                + "this edit your only tool call, then end the turn immediately with no further "
                + "commentary or tool calls.",
            TimeSpan.FromSeconds(120));

        WriteTranscriptJson("transcript-edit-turn.json", panel.Transcript);

        bool degrade = panel.Transcript.Any(l =>
            l.Kind == TranscriptKind.SafetyEventChip && l.Text.Contains("degrade", StringComparison.OrdinalIgnoreCase));
        Assert.False(degrade,
            "QA3-20 live drive: a Degrade chip rendered for a legitimate in-root write.\n"
                + "Full transcript:\n" + DumpTranscript(panel.Transcript));
        Assert.True(
            panel.Transcript.Any(l => l.Kind == TranscriptKind.FileOpChip && l.Badge == OpOutcomeBadge.Applied),
            "QA3-20 live drive: no FileOpChip with Badge=Applied was rendered.\n"
                + "Full transcript:\n" + DumpTranscript(panel.Transcript));

        // Poll for the QA3-9 refresh (fire-and-forget from HandleTurnEventAsync) to land in the OPEN tab.
        string editedText = await WaitForEditorTextToContainAsync(textEditor, Marker, TimeSpan.FromSeconds(15));
        Assert.Contains(Marker, editedText); // the AI's edit reached the already-open tab's buffer.
        Assert.NotEqual(originalContent, editedText);

        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        WriteableBitmap? beforeUndoFrame = window.CaptureRenderedFrame();
        beforeUndoFrame?.Save(Path.Combine(ArtifactDir, "qa3-20-before-undo.png"));

        // THE DECISIVE QA3-20 ASSERTION: a real Ctrl+Z KEY PRESS (not a direct command call) against the
        // real MainWindow's Window-level KeyBinding (MainWindow.axaml: <KeyBinding Gesture="Ctrl+Z"
        // Command="{Binding UndoCommand}" />) reverts the AI's edit on the already-open tab.
        window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(originalContent, textEditor.Text); // one Ctrl+Z fully reverted the AI's live edit.

        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        WriteableBitmap? afterUndoFrame = window.CaptureRenderedFrame();
        afterUndoFrame?.Save(Path.Combine(ArtifactDir, "qa3-20-after-undo.png"));

        File.WriteAllText(
            Path.Combine(ArtifactDir, "qa3-20-evidence.txt"),
            "QA3-20 live evidence\n"
                + $"target file: {targetPath}\n"
                + $"original content (captured pre-turn): {originalContent.Replace("\n", "\\n")}\n"
                + $"editor text after the AI's live edit: {editedText.Replace("\n", "\\n")}\n"
                + $"editor text after one real Ctrl+Z key press: {textEditor.Text.Replace("\n", "\\n")}\n"
                + "result: PASS — the AI-applied edit was undone by one Ctrl+Z.\n");
    }

    private static string DumpTranscript(IEnumerable<TranscriptLine> lines) =>
        string.Join("\n", lines.Select(l =>
            $"  [{l.Kind}] Text={l.Text} Op={l.Op} Target={l.TargetPath} Badge={l.Badge}"));

    private static readonly JsonSerializerOptions TranscriptJsonOptions = new() { WriteIndented = true };

    private static void WriteTranscriptJson(string fileName, IEnumerable<TranscriptLine> lines)
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
