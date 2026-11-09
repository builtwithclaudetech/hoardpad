// AiPanelUnionBeforeImageReplayTests.cs — OQ-MD-5 option ii (feature-ai-multidoc-panel §5.5) App-level gates.
//
// Proves ApplyPendingAsync's granted-out-of-folder replay path end-to-end with the REAL seams:
//   - a REAL UnionBeforeImageReader (temp-FS backed) reads the before-image of a granted out-of-folder file;
//   - a REAL AiTurnGrantComposer composes the per-op union-gated AiFileOpsService (PathResolverSandboxGate ∪
//     the one grant) over a CountingAtomicFileStore, so ReplayAsync actually admits + records the write.
//
// Gates:
//   (a) a parked out-of-folder proposal WITH its GrantedResolvedPath replays: before-image Found, the write
//       applies through the union-gated service, chip = Approved (AC-MD-2 at the replay level; undoable via
//       the FileOpRequest before-image path the shared service records).
//   (b) a parked out-of-folder proposal WITHOUT a grant → Blocked (the union reader is not consulted; the
//       root-only reader Refuses) — fail-closed.
//   (c) a parked out-of-folder proposal whose persisted grant does NOT equal the target (tampered) → Blocked
//       (the union reader self-enforces membership).
//   (d) an in-root proposal replays through the UNCHANGED root-only reader + root-gated service.
//
// Class name resolves the verify filter "UnionBeforeImage".

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HoardPad.App.Ai;
using HoardPad.App.ViewModels;
using HoardPad.Core.Ai.Caps;
using HoardPad.Core.Ai.FileOps;
using HoardPad.Core.Host.Abstractions;
using HoardPad.Core.Records;
using HoardPad.Host;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class AiPanelUnionBeforeImageReplayTests : IDisposable
{
    private const string ConversationId = "conv-union-replay";

    private readonly string _root;         // realpath'd sandbox root (temp FS)
    private readonly string _outsideDir;   // sibling dir, outside the root
    private readonly PathResolver _resolver = new();
    private readonly CountingAtomicFileStore _store = new();

    public AiPanelUnionBeforeImageReplayTests()
    {
        string raw = Path.Combine(Path.GetTempPath(), $"hoardpad-mdii-app-{Guid.NewGuid():N}");
        Directory.CreateDirectory(raw);
        _root = new PathResolver().ResolveCanonical(raw, raw).Value;
        if (!Directory.Exists(_root)) Directory.CreateDirectory(_root);

        string rawOut = Path.Combine(Path.GetTempPath(), $"hoardpad-mdii-app-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rawOut);
        _outsideDir = new PathResolver().ResolveCanonical(rawOut, rawOut).Value;
        if (!Directory.Exists(_outsideDir)) Directory.CreateDirectory(_outsideDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_outsideDir, recursive: true); } catch { }
    }

    private string Resolve(string rawPath) => _resolver.ResolveCanonical(rawPath, _root).Value;

    // Build a panel wired with the REAL union reader + a REAL grant composer (over the CountingAtomicFileStore),
    // its session bound to a REAL PathResolverSandboxGate rooted at _root. approveAnswers scripts the per-op confirm.
    private AiPanelViewModel BuildPanel(IEnumerable<bool>? approveAnswers = null)
    {
        var confirm = new ScriptedConfirmService(approveAnswers: approveAnswers ?? [true]);
        var paths = new FakeAppDataPaths();
        var capStore = new NullCapStateStore();

        var rootGate = new PathResolverSandboxGate(_resolver, _resolver.ResolveCanonical(_root, _root), _root);
        var sessionFileOps = new AiFileOpsService(rootGate, _store, capStore, paths, "s-union", CapConfig.Pinned);

        // The REAL composer — the same one the binder builds. ComposeReplayFileOps produces the per-op
        // union-gated service the granted replay writes through.
        var composer = new AiTurnGrantComposer(
            _resolver, _root, rootGate, _store, capStore, paths, "s-union", CapConfig.Pinned);

        var vm = new AiPanelViewModel(
            runner: new ScriptedTurnRunner([]),
            confirm: confirm,
            hookHost: new ScriptedHookHost(selfTestPasses: true),
            backend: new FakeAgentBackend(),
            time: TimeProvider.System,
            capConfig: null,
            noReplyTimeout: null,
            beforeImageReader: new BeforeImageReader(_resolver),      // frozen root-only reader (in-root path)
            replayStore: _store,
            replayPaths: paths,
            unionBeforeImageReader: new UnionBeforeImageReader(_resolver)); // OQ-MD-5 option ii union reader

        vm.BindSession(_root, sessionFileOps, ConversationId, composer);
        return vm;
    }

    private static PendingProposalViewModel Proposal(string targetPath, string? grantedResolvedPath, string content = "replayed content")
    {
        string inputJson = $"{{\"file_path\":\"{targetPath.Replace("\\", "\\\\")}\",\"content\":\"{content}\"}}";
        return new PendingProposalViewModel
        {
            ProposalId = "p-union-1",
            Op = ActivityOp.Edit,
            TargetPath = targetPath,
            Detail = "timed out",
            CapturedToolInputJson = inputJson,
            GrantedResolvedPath = grantedResolvedPath,
        };
    }

    // ---- (a) granted out-of-folder proposal replays: Found → union-gated write → Approved chip ----

    [Fact]
    public async Task GrantedOutOfFolderProposalReplaysAndAppliesViaUnionGatedService()
    {
        // A real out-of-folder file exists; the proposal carries its resolved grant.
        string outFile = Path.Combine(_outsideDir, "granted-doc.txt");
        await File.WriteAllBytesAsync(outFile, System.Text.Encoding.UTF8.GetBytes("original"));
        string grant = Resolve(outFile);

        AiPanelViewModel vm = BuildPanel(approveAnswers: [true]);
        var proposal = Proposal(outFile, grantedResolvedPath: grant, content: "new out-of-folder body");
        vm.ParseAndPark(proposal);

        await vm.ApplyPendingCommand.ExecuteAsync(proposal);

        Assert.Equal(0, vm.PendingCount);
        TranscriptLine chip = Assert.Single(vm.Transcript, t => t.Kind == TranscriptKind.FileOpChip);
        Assert.Equal(OpOutcomeBadge.Approved, chip.Badge); // union-gated write was ADMITTED and applied
        // The union-gated service recorded the out-of-folder mutation (proves the write path widened to the grant).
        Assert.NotNull(_store.BytesAt(grant));
        Assert.Equal("new out-of-folder body", System.Text.Encoding.UTF8.GetString(_store.BytesAt(grant)!));
    }

    // ---- (b) out-of-folder proposal WITHOUT a grant → Blocked (root-only reader Refuses) ----

    [Fact]
    public async Task OutOfFolderProposalWithoutGrantIsBlocked()
    {
        string outFile = Path.Combine(_outsideDir, "ungranted-doc.txt");
        await File.WriteAllBytesAsync(outFile, System.Text.Encoding.UTF8.GetBytes("original"));

        AiPanelViewModel vm = BuildPanel(approveAnswers: []); // no confirm should be reached
        var proposal = Proposal(outFile, grantedResolvedPath: null);
        vm.ParseAndPark(proposal);

        await vm.ApplyPendingCommand.ExecuteAsync(proposal);

        Assert.Equal(0, vm.PendingCount);
        TranscriptLine chip = Assert.Single(vm.Transcript, t => t.Kind == TranscriptKind.FileOpChip);
        Assert.Equal(OpOutcomeBadge.Blocked, chip.Badge);
        // No mutation to the out-of-folder file.
        Assert.Null(_store.BytesAt(Resolve(outFile)));
    }

    // ---- (c) tampered grant (persisted grant ≠ target) → union reader self-enforces → Blocked ----

    [Fact]
    public async Task GrantThatDoesNotEqualTargetIsBlocked()
    {
        // The proposal targets fileB but carries a grant for fileA. The union reader re-resolves fileB and
        // finds it is NOT the granted path → Refused. Self-enforcing containment (never trusts the caller hint).
        string fileA = Path.Combine(_outsideDir, "granted-A.txt");
        string fileB = Path.Combine(_outsideDir, "target-B.txt");
        await File.WriteAllBytesAsync(fileA, System.Text.Encoding.UTF8.GetBytes("A"));
        await File.WriteAllBytesAsync(fileB, System.Text.Encoding.UTF8.GetBytes("B"));
        string grantForA = Resolve(fileA);

        AiPanelViewModel vm = BuildPanel(approveAnswers: []);
        var proposal = Proposal(fileB, grantedResolvedPath: grantForA); // mismatched grant
        vm.ParseAndPark(proposal);

        await vm.ApplyPendingCommand.ExecuteAsync(proposal);

        TranscriptLine chip = Assert.Single(vm.Transcript, t => t.Kind == TranscriptKind.FileOpChip);
        Assert.Equal(OpOutcomeBadge.Blocked, chip.Badge);
        Assert.Null(_store.BytesAt(Resolve(fileB)));
    }

    // ---- (d) in-root proposal → unchanged root-only reader + root-gated service ----

    [Fact]
    public async Task InRootProposalReplaysThroughUnchangedRootOnlyPath()
    {
        string inFile = Path.Combine(_root, "in-root.txt");
        // NotFound → create: file absent on disk, replay creates it through the root-gated service.
        AiPanelViewModel vm = BuildPanel(approveAnswers: [true]);
        var proposal = Proposal(inFile, grantedResolvedPath: null, content: "in-root body");
        vm.ParseAndPark(proposal);

        await vm.ApplyPendingCommand.ExecuteAsync(proposal);

        TranscriptLine chip = Assert.Single(vm.Transcript, t => t.Kind == TranscriptKind.FileOpChip);
        Assert.Equal(OpOutcomeBadge.Approved, chip.Badge);
        Assert.NotNull(_store.BytesAt(Resolve(inFile)));
    }
}
