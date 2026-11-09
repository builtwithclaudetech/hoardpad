// FlushBeforeImageOrderingTests.cs — PANEL-4 acceptance (feature-ai-multidoc-panel §6 AC-MD-3, FR-MD-7/8).
// Proves the strict pre-turn ordering the panel enforces at turn-arm, driven through the REAL SendAsync path
// (MultiDocSelection + AiTurnGrantComposer + AiPanelViewModel.SendAsync + the real Host BeforeImageReader),
// not hand-built stubs:
//
//   1. FLUSH  — every CHECKED, DIRTY doc is auto-saved to its on-disk path (each sealing an editor undo point),
//   2. CAPTURE — the before-image is read from the FLUSHED on-disk content (its hash reflects flushed bytes),
//   3. RUN     — only then is the first backend call (IAiTurnRunner.RunAsync) made.
//
// Non-vacuity (AC-MD-3):
//   - the recording flush seam WRITES the flushed bytes to disk; the real BeforeImageReader then reads them,
//     so the captured hash equals the hash of the FLUSHED content. If the capture ran BEFORE the flush (order
//     swapped), the reader would hash the PRE-FLUSH bytes and the hash-equality assertion would FAIL. The
//     `SwappingTheOrderWouldReadPreFlushBytes` test constructs that pre-flush hash and asserts it DIFFERS from
//     the flushed hash — the exact discriminator the ordering test relies on (removing the flush-before-capture
//     guarantee makes the two collide only when content is unchanged, which the dirty-flush case rules out).
//   - the runner records the pre-turn log length AT RunAsync-start; asserting it equals the full pre-turn log
//     proves flush + before-image both completed strictly before the first backend call.
//   - an UNCHECKED dirty doc is NOT flushed (its on-disk file is unchanged, no flush event recorded).
//   - a flush FAILURE aborts the turn (the runner is never called; a safety chip is shown).
//   - the flush raises the editor undo-point event (EditorViewModel.FlushForAiTurnAsync).

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using HoardPad.App.Ai;
using HoardPad.App.Editor;
using HoardPad.App.ViewModels;
using HoardPad.Core.Ai.Caps;
using HoardPad.Core.Ai.FileOps;
using HoardPad.Core.Ai.Provider;
using HoardPad.Core.Host.Abstractions;
using HoardPad.Core.Records;
using HoardPad.Core.Safety;
using TextMateSharp.Grammars;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class FlushBeforeImageOrderingTests : IDisposable
{
    private readonly string _root;
    private readonly string _outsideDir;
    private readonly HoardPad.Host.PathResolver _resolver = new();
    private readonly HoardPad.Host.BeforeImageReader _beforeImage;

    public FlushBeforeImageOrderingTests()
    {
        string bench = Path.Combine(Path.GetTempPath(), "hoardpad-flushorder-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(bench, "root");
        _outsideDir = Path.Combine(bench, "outside");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outsideDir);
        _beforeImage = new HoardPad.Host.BeforeImageReader(_resolver);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Sha256Hex(string content) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));

    private (PathResolverSandboxGate RootGate, AiTurnGrantComposer Composer) BuildSession()
    {
        NormcasedPath canonicalRoot = _resolver.ResolveCanonical(_root, _root);
        var rootGate = new PathResolverSandboxGate(_resolver, canonicalRoot, _root);
        var composer = new AiTurnGrantComposer(
            _resolver, _root, rootGate,
            new CountingAtomicFileStore(), new NullCapStateStore(), new FakeAppDataPaths(),
            sessionId: "s-flushorder", CapConfig.Pinned);
        return (rootGate, composer);
    }

    // Build an armed, folder-bound panel with the multi-doc composer + the REAL before-image reader wired,
    // over the given open-document snapshot. The recording runner captures the pre-turn ordering at RunAsync.
    private async Task<(AiPanelViewModel Vm, RecordingFlushSeam Flush, RecordingRunner Runner)> ArmedPanelAsync(
        IReadOnlyList<OpenDocumentInfo> docs)
    {
        (PathResolverSandboxGate rootGate, AiTurnGrantComposer composer) = BuildSession();
        var runner = new RecordingRunner();
        var vm = new AiPanelViewModel(
            runner,
            new ScriptedConfirmService(),
            new ScriptedHookHost(selfTestPasses: true),
            new FakeAgentBackend(),
            TimeProvider.System,
            beforeImageReader: _beforeImage);

        var sessionFileOps = new AiFileOpsService(
            rootGate, new CountingAtomicFileStore(), new NullCapStateStore(), new FakeAppDataPaths(),
            sessionId: "s-flushorder-e2e", CapConfig.Pinned);
        vm.BindSession(rootGate.Resolve(_root).ResolvedPath, sessionFileOps, "conv-flushorder", composer);
        vm.ConfiguredMode = AgencyMode.AutoApply;
        await vm.ArmAgencyAsync();

        vm.OpenDocumentsProvider = () => docs;
        var flush = new RecordingFlushSeam(vm);
        vm.FlushDocumentAsync = flush.FlushAsync;
        runner.Bind(vm); // let the runner read PreTurnOrderLog at RunAsync-start (proves flush ran first).
        return (vm, flush, runner);
    }

    // ---- AC-MD-3: flush strictly before before-image; both strictly before the first backend call ----

    [Fact]
    public async Task DirtyCheckedDocIsFlushedToDiskThenBeforeImageCapturedThenBackendCalled()
    {
        string target = Path.Combine(_root, "doc.txt");
        File.WriteAllText(target, "PRE-FLUSH stale on-disk content"); // the stale bytes on disk before flush.
        const string flushedContent = "FLUSHED current buffer content";

        (AiPanelViewModel vm, RecordingFlushSeam flush, RecordingRunner runner) = await ArmedPanelAsync(
            new List<OpenDocumentInfo> { new(target, "doc.txt", IsFocused: true, IsDirty: true, IsReadOnly: false) });
        flush.WriteOnFlush(flushedContent); // the seam writes the flushed content to disk, mirroring a real save.

        vm.PromptInput = "edit doc";
        await vm.SendCommand.ExecuteAsync(null);

        // 1) The pre-turn log records FLUSH strictly before BEFORE-IMAGE for the target.
        IReadOnlyList<PreTurnEvent> log = vm.PreTurnOrderLog;
        int flushIdx = IndexOf(log, PreTurnStep.Flush, target);
        int biIdx = IndexOf(log, PreTurnStep.BeforeImage, target);
        Assert.True(flushIdx >= 0, "a flush event was recorded");
        Assert.True(biIdx >= 0, "a before-image event was recorded");
        Assert.True(flushIdx < biIdx, "flush must precede the before-image capture");

        // 2) The backend (RunAsync) was called, and at that instant the ENTIRE pre-turn log was already
        //    populated — proving flush + before-image both completed strictly before the first backend call.
        Assert.True(runner.WasCalled);
        Assert.Equal(log.Count, runner.PreTurnLogLengthAtStart);
        Assert.Equal(2, runner.PreTurnLogLengthAtStart); // exactly [Flush, BeforeImage] for the one dirty doc.

        // 3) The captured before-image hash equals the hash of the FLUSHED content (NOT the pre-flush bytes).
        //    This is the non-vacuity discriminator: had capture run before flush, the hash would be of the
        //    pre-flush bytes and this assertion would fail.
        Assert.Equal(Sha256Hex(flushedContent), vm.LastBeforeImageHash(target));
        Assert.NotEqual(Sha256Hex("PRE-FLUSH stale on-disk content"), vm.LastBeforeImageHash(target));
    }

    [Fact]
    public void SwappingTheOrderWouldReadPreFlushBytes()
    {
        // Non-vacuity proof (comment-as-code): capturing the before-image BEFORE the flush hashes the
        // pre-flush bytes; capturing AFTER hashes the flushed bytes. For a dirty doc the two contents differ,
        // so the hashes differ — this is exactly what makes the ordering assertion above non-vacuous. If the
        // production code swapped step 1 and step 2, LastBeforeImageHash would equal the pre-flush hash and
        // the DirtyChecked... test's final Assert.Equal(flushedHash, ...) would fail.
        string preFlush = Sha256Hex("PRE-FLUSH stale on-disk content");
        string flushed = Sha256Hex("FLUSHED current buffer content");
        Assert.NotEqual(preFlush, flushed);
    }

    // ---- an UNCHECKED dirty doc is NOT flushed (its on-disk file is unchanged, retains dirty state) ----

    [Fact]
    public async Task UncheckedDirtyDocIsNotFlushed()
    {
        string checkedDoc = Path.Combine(_root, "checked.txt");
        string uncheckedDoc = Path.Combine(_root, "unchecked.txt");
        File.WriteAllText(checkedDoc, "checked-pre");
        File.WriteAllText(uncheckedDoc, "unchecked-on-disk"); // must remain byte-identical after the turn.

        (AiPanelViewModel vm, RecordingFlushSeam flush, RecordingRunner runner) = await ArmedPanelAsync(
            new List<OpenDocumentInfo>
            {
                new(checkedDoc, "checked.txt", IsFocused: true, IsDirty: true, IsReadOnly: false),
                new(uncheckedDoc, "unchecked.txt", IsFocused: false, IsDirty: true, IsReadOnly: false),
            });
        flush.WriteOnFlush("checked-flushed");

        // The focused doc (checked.txt) is pre-checked by default on arm (FR-MD-6(a)); the second row
        // (unchecked.txt) is NOT focused and stays unchecked by default — the exact unchecked-dirty case.

        vm.PromptInput = "edit";
        await vm.SendCommand.ExecuteAsync(null);

        // The unchecked doc was never handed to the flush seam and its on-disk file is unchanged.
        Assert.DoesNotContain(uncheckedDoc, flush.FlushedPaths);
        Assert.Equal("unchecked-on-disk", File.ReadAllText(uncheckedDoc));
        Assert.Null(vm.LastBeforeImageHash(uncheckedDoc));

        // The checked doc WAS flushed and captured.
        Assert.Contains(checkedDoc, flush.FlushedPaths);
        Assert.Equal(Sha256Hex("checked-flushed"), vm.LastBeforeImageHash(checkedDoc));
    }

    // ---- a NON-dirty checked doc skips the flush (nothing to write) but does not break the turn ----

    [Fact]
    public async Task NonDirtyCheckedDocSkipsFlush()
    {
        string clean = Path.Combine(_root, "clean.txt");
        File.WriteAllText(clean, "clean-on-disk");

        (AiPanelViewModel vm, RecordingFlushSeam flush, RecordingRunner runner) = await ArmedPanelAsync(
            new List<OpenDocumentInfo> { new(clean, "clean.txt", IsFocused: true, IsDirty: false, IsReadOnly: false) });

        vm.PromptInput = "edit clean";
        await vm.SendCommand.ExecuteAsync(null);

        Assert.DoesNotContain(clean, flush.FlushedPaths);  // not dirty ⇒ no flush.
        Assert.True(runner.WasCalled);                     // the turn still runs.
        // No flush event was recorded for it (nothing to write); the before-image still reflects on-disk bytes.
        Assert.DoesNotContain(vm.PreTurnOrderLog, e => e.Step == PreTurnStep.Flush && e.Path == clean);
    }

    // ---- flush FAILURE aborts the turn (never launch the agent against stale bytes) ----

    [Fact]
    public async Task FlushFailureAbortsTheTurn()
    {
        string target = Path.Combine(_root, "fails.txt");
        File.WriteAllText(target, "stale");

        (AiPanelViewModel vm, RecordingFlushSeam flush, RecordingRunner runner) = await ArmedPanelAsync(
            new List<OpenDocumentInfo> { new(target, "fails.txt", IsFocused: true, IsDirty: true, IsReadOnly: false) });
        flush.FailNextFlush(); // the flush seam returns false (save failed / no matching tab).

        vm.PromptInput = "edit";
        await vm.SendCommand.ExecuteAsync(null);

        // The backend was NEVER called — the turn aborted before the first backend call.
        Assert.False(runner.WasCalled);
        // A safety chip explains the abort; the transcript did NOT proceed as if the turn ran.
        Assert.Contains(vm.Transcript, l => l.IsDanger && l.Text.Contains("could not be saved", StringComparison.Ordinal));
    }

    // ---- the flush raises the editor undo-point event (FR-MD-7 undo point) ----

    [Fact]
    public async Task FlushForAiTurnRaisesTheEditorUndoPointEvent()
    {
        // The EditorViewModel-level contract: FlushForAiTurnAsync saves then raises FlushUndoPointRequested,
        // which the EditorView routes to the AvaloniaEdit undo stack (the discrete pre-flush undo boundary).
        string target = Path.Combine(_root, "undo.txt");
        File.WriteAllText(target, "seed");

        var options = new HoardPadRegistryOptions(ThemeName.OneDark);
        var fileService = new HoardPad.App.Editor.EditorFileService(
            new HoardPad.Host.AtomicFileStore(), _resolver, new HoardPad.App.Hosting.UtfDefaultEncodingDetector());
        var editor = new EditorViewModel(fileService, options, new LanguageCatalog(options));
        await editor.OpenAsync(target, CancellationToken.None);
        editor.Document.Text = "edited buffer";
        editor.IsDirty = true;

        bool undoPointRaised = false;
        editor.FlushUndoPointRequested += () => undoPointRaised = true;

        await editor.FlushForAiTurnAsync(CancellationToken.None);

        Assert.True(undoPointRaised, "the flush must raise the editor undo-point event (FR-MD-7)");
        Assert.False(editor.IsDirty); // the durable save cleared the dirty flag.
        Assert.Equal("edited buffer", File.ReadAllText(editor.Document.Path!)); // the flushed content is on disk.
    }

    private static int IndexOf(IReadOnlyList<PreTurnEvent> log, PreTurnStep step, string path)
    {
        for (int i = 0; i < log.Count; i++)
            if (log[i].Step == step && log[i].Path == path)
                return i;
        return -1;
    }

    // A flush seam that records the paths it was asked to flush, optionally writes the flushed content to disk
    // (so the real BeforeImageReader reads the flushed bytes), and can be told to fail (return false) once.
    private sealed class RecordingFlushSeam
    {
        private readonly AiPanelViewModel _vm;
        private string? _writeContent;
        private bool _failNext;

        public RecordingFlushSeam(AiPanelViewModel vm) => _vm = vm;

        public List<string> FlushedPaths { get; } = [];

        public void WriteOnFlush(string content) => _writeContent = content;

        public void FailNextFlush() => _failNext = true;

        public Task<bool> FlushAsync(string path)
        {
            if (_failNext)
            {
                _failNext = false;
                return Task.FromResult(false); // a save failure / no matching tab — the panel must abort.
            }

            FlushedPaths.Add(path);
            if (_writeContent is { } content)
                File.WriteAllText(path, content); // mirror a durable save: the on-disk bytes are now flushed.
            return Task.FromResult(true);
        }
    }

    // A runner that records whether it was called and the pre-turn log length AT the instant RunAsync started —
    // so the test can prove the full flush + before-image sequence completed before the first backend call.
    private sealed class RecordingRunner : IAiTurnRunner
    {
        private AiPanelViewModel? _vm;

        public bool WasCalled { get; private set; }
        public int PreTurnLogLengthAtStart { get; private set; }

        public void Bind(AiPanelViewModel vm) => _vm = vm;

        public async IAsyncEnumerable<TurnEvent> RunAsync(
            TurnRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            WasCalled = true;
            PreTurnLogLengthAtStart = _vm?.PreTurnOrderLog.Count ?? -1;
            await Task.CompletedTask;
            yield break;
        }

        public Task StopAsync() => Task.CompletedTask;
    }
}
