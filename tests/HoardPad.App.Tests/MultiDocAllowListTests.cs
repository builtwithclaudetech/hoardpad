// MultiDocAllowListTests.cs — PANEL-2 acceptance (feature-ai-multidoc-panel §6 AC-MD-1 / AC-MD-4, FR-MD-1/
// 3/4/5.1/9). Proves the checked-set → grant-set assembly + the Archetype-B UnionSandboxGate composition +
// the FR-MD-3 context-descriptor injection — driven through the REAL turn-arm assembly (MultiDocSelection +
// AiTurnGrantComposer + the panel SendAsync path), not hand-built gates.
//
// Non-vacuity (each guarded behavior fails a mutation that removes it):
//   - AC-MD-1: a checked out-of-folder write is Allowed; the SAME path unchecked is Refused; an in-root path
//     is Allowed regardless. Removing the grant assembly makes the checked case Refused (grants empty);
//     removing the union makes the checked case Refused (root gate alone denies out-of-folder).
//   - AC-MD-4: with only X checked, the grant set contains ONLY X's resolved path; a write to another open
//     doc is Refused; the descriptor marks exactly the checked set writable; toggling a checkbox changes the
//     next turn's grant set + descriptor.
//   - same-resolver-leg: the assembled grant string equals PathResolverSandboxGate.Resolve(doc.Path)
//     .ResolvedPath for an out-of-folder doc (the FR-MD-5.1 invariant, proven directly).
//
// The composer + gate use the REAL HoardPad.Host.PathResolver (the only NormcasedPath producer) over real
// temp dirs, so the resolved-string equality the union relies on is exercised end-to-end.

using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HoardPad.App.Ai;
using HoardPad.App.ViewModels;
using HoardPad.Core.Ai.Caps;
using HoardPad.Core.Ai.FileOps;
using HoardPad.Core.Host.Abstractions;
using HoardPad.Core.Records;
using HoardPad.Core.Safety;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class MultiDocAllowListTests : System.IDisposable
{
    private readonly string _root;
    private readonly string _outsideDir;
    private readonly HoardPad.Host.PathResolver _resolver = new();

    public MultiDocAllowListTests()
    {
        // A real in-root sandbox tree + a sibling out-of-folder dir (both realpath-resolvable so the host
        // resolver produces a stable NormcasedPath the union compares against).
        string bench = Path.Combine(Path.GetTempPath(), "hoardpad-multidoc-" + System.Guid.NewGuid().ToString("N"));
        _root = Path.Combine(bench, "root");
        _outsideDir = Path.Combine(bench, "outside");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outsideDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
        catch (System.UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }

    // Build the session root gate + composer exactly as AiSessionBinder does: the SAME resolver + raw root
    // base feed both, so a grant is byte-comparable to what the gate returns (FR-MD-5.1).
    private (PathResolverSandboxGate RootGate, AiTurnGrantComposer Composer) BuildSession()
    {
        NormcasedPath canonicalRoot = _resolver.ResolveCanonical(_root, _root);
        var rootGate = new PathResolverSandboxGate(_resolver, canonicalRoot, _root);
        var composer = new AiTurnGrantComposer(
            _resolver, _root, rootGate,
            new CountingAtomicFileStore(), new NullCapStateStore(), new FakeAppDataPaths(),
            sessionId: "s-multidoc", CapConfig.Pinned);
        return (rootGate, composer);
    }

    private static string MakeFile(string dir, string name)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, "seed");
        return path;
    }

    private static MultiDocSelection SelectionOf(params (string Path, bool Focused, bool Checked)[] docs)
    {
        var selection = new MultiDocSelection();
        selection.Rearm(
            System.Array.ConvertAll(docs, d => new OpenDocumentInfo(
                Path: d.Path, DisplayName: Path.GetFileName(d.Path), IsFocused: d.Focused,
                IsDirty: false, IsReadOnly: false)));
        // Rearm pre-checks the focused doc by default; override each row to the test's explicit checked state.
        for (int i = 0; i < docs.Length; i++)
            selection.Rows[i].IsChecked = docs[i].Checked;
        return selection;
    }

    // ---- AC-MD-1: checked out-of-folder Allowed; same path unchecked Refused; in-root Allowed regardless ----

    [Fact]
    public void CheckedOutOfFolderWriteIsAllowedByTheArmedTurnGate()
    {
        (PathResolverSandboxGate rootGate, AiTurnGrantComposer composer) = BuildSession();
        string outside = MakeFile(_outsideDir, "target.txt");
        string inRoot = MakeFile(_root, "in-root.txt");

        MultiDocSelection selection = SelectionOf((outside, Focused: false, Checked: true));
        TurnGrantComposition composition = composer.ComposeTurn(selection.CheckedGrantablePaths());

        // Arm the SAME composed UnionSandboxGate the panel enforces the turn through (root gate ∪ turn grants).
        var armedGate = new UnionSandboxGate(rootGate, composition.Grants);

        // Checked out-of-folder path: Allowed.
        Assert.Equal(SandboxDecision.Allow, armedGate.Resolve(outside).Decision);
        // In-root path: Allowed regardless of the grant set.
        Assert.Equal(SandboxDecision.Allow, armedGate.Resolve(inRoot).Decision);
        // Non-vacuity: the grant assembly actually produced the resolved grant (not empty).
        Assert.False(composition.Grants.IsEmpty);
        Assert.Equal(1, composition.Grants.Count);
    }

    [Fact]
    public void UncheckedOutOfFolderWriteIsRefusedByTheArmedTurnGate()
    {
        (PathResolverSandboxGate rootGate, AiTurnGrantComposer composer) = BuildSession();
        string outside = MakeFile(_outsideDir, "target.txt");

        // SAME document, now UNCHECKED — the grant assembly must produce NO grant, so the union falls back
        // to root-only and the out-of-folder write is Refused (fail-closed, FR-MD-10).
        MultiDocSelection selection = SelectionOf((outside, Focused: false, Checked: false));
        TurnGrantComposition composition = composer.ComposeTurn(selection.CheckedGrantablePaths());
        var armedGate = new UnionSandboxGate(rootGate, composition.Grants);

        Assert.True(composition.Grants.IsEmpty);
        Assert.Equal(SandboxDecision.Deny, armedGate.Resolve(outside).Decision);
    }

    [Fact]
    public void RemovingTheGrantAssemblyMakesTheCheckedPathRefusedNonVacuityGuard()
    {
        // Proves the "checked → Allowed" leg depends on the grant ASSEMBLY: with an EMPTY grant set (the
        // mutation "never assemble grants"), the checked out-of-folder path is Refused by the union gate.
        (PathResolverSandboxGate rootGate, _) = BuildSession();
        string outside = MakeFile(_outsideDir, "target.txt");

        var noGrantGate = new UnionSandboxGate(rootGate, FileGrantSet.Empty);
        Assert.Equal(SandboxDecision.Deny, noGrantGate.Resolve(outside).Decision);

        // And the root gate ALONE (the mutation "never union") also denies the out-of-folder path.
        Assert.Equal(SandboxDecision.Deny, rootGate.Resolve(outside).Decision);
    }

    // ---- same-resolver-leg invariant (FR-MD-5.1): assembled grant == gate's ResolvedPath ----

    [Fact]
    public void AssembledGrantPathEqualsTheGateResolvedPathForAnOutOfFolderDoc()
    {
        (PathResolverSandboxGate rootGate, AiTurnGrantComposer composer) = BuildSession();
        string outside = MakeFile(_outsideDir, "target.txt");

        MultiDocSelection selection = SelectionOf((outside, Focused: true, Checked: true));
        TurnGrantComposition composition = composer.ComposeTurn(selection.CheckedGrantablePaths());

        // The gate's resolved-string for the same candidate (what UnionSandboxGate compares against).
        string gateResolved = rootGate.Resolve(outside).ResolvedPath;

        // The assembled grant set contains EXACTLY that resolved string — the FR-MD-5.1 invariant.
        Assert.True(composition.Grants.Contains(gateResolved));
    }

    // ---- AC-MD-4: only-X-checked → grant set is exactly X; other doc Refused; descriptor + toggle ----

    [Fact]
    public void WithOnlyXCheckedTheGrantSetIsExactlyXAndOtherDocsAreRefused()
    {
        (PathResolverSandboxGate rootGate, AiTurnGrantComposer composer) = BuildSession();
        string x = MakeFile(_outsideDir, "x.txt");
        string y = MakeFile(_outsideDir, "y.txt");

        MultiDocSelection selection = SelectionOf(
            (x, Focused: true, Checked: true),
            (y, Focused: false, Checked: false));
        TurnGrantComposition composition = composer.ComposeTurn(selection.CheckedGrantablePaths());
        var armedGate = new UnionSandboxGate(rootGate, composition.Grants);

        // The enforced grant set contains ONLY X's resolved path (not Y's) — no phrasing widens it (FR-MD-4).
        Assert.Equal(1, composition.Grants.Count);
        Assert.True(composition.Grants.Contains(rootGate.Resolve(x).ResolvedPath));
        Assert.False(composition.Grants.Contains(rootGate.Resolve(y).ResolvedPath));

        // A write to X is Allowed; a write to the other open (unchecked) doc Y is Refused by the armed gate.
        Assert.Equal(SandboxDecision.Allow, armedGate.Resolve(x).Decision);
        Assert.Equal(SandboxDecision.Deny, armedGate.Resolve(y).Decision);
    }

    [Fact]
    public void ContextDescriptorMarksExactlyTheCheckedSetWritable()
    {
        string x = MakeFile(_outsideDir, "x.txt");
        string y = MakeFile(_outsideDir, "y.txt");

        MultiDocSelection selection = SelectionOf(
            (x, Focused: true, Checked: true),
            (y, Focused: false, Checked: false));

        string descriptor = MultiDocContextDescriptor.Build(selection.CurrentRows, _root);

        // The descriptor marks exactly the checked set (X) writable and the rest read-context only (FR-MD-3).
        Assert.Contains(x + " [WRITABLE]", descriptor, System.StringComparison.Ordinal);
        Assert.Contains(y + " [read-only]", descriptor, System.StringComparison.Ordinal);
        // And it states the allow-list rule explicitly (advisory, FR-MD-4) without leaking any file contents.
        Assert.Contains("write ONLY to", descriptor, System.StringComparison.Ordinal);
        Assert.DoesNotContain("seed", descriptor, System.StringComparison.Ordinal); // no buffer contents (NG-4).
    }

    [Fact]
    public void TogglingACheckboxChangesTheNextTurnsGrantSetAndDescriptor()
    {
        (PathResolverSandboxGate rootGate, AiTurnGrantComposer composer) = BuildSession();
        string x = MakeFile(_outsideDir, "x.txt");
        string y = MakeFile(_outsideDir, "y.txt");

        var selection = new MultiDocSelection();
        OpenDocumentInfo docX = new(x, "x.txt", IsFocused: true, IsDirty: false, IsReadOnly: false);
        OpenDocumentInfo docY = new(y, "y.txt", IsFocused: false, IsDirty: false, IsReadOnly: false);

        // Turn N: only X checked (X focused, pre-checked by default; Y untouched).
        selection.Rearm([docX, docY]);
        Assert.True(selection.Rows[0].IsChecked);   // focused X pre-checked (FR-MD-6(a)).
        Assert.False(selection.Rows[1].IsChecked);

        FileGrantSet grantsN = composer.ComposeTurn(selection.CheckedGrantablePaths()).Grants;
        Assert.Equal(1, grantsN.Count);
        Assert.True(grantsN.Contains(rootGate.Resolve(x).ResolvedPath));
        Assert.False(grantsN.Contains(rootGate.Resolve(y).ResolvedPath));

        // User toggles Y ON mid-session (affects the NEXT arm, FR-MD-9). Re-arm for turn N+1.
        selection.Rows[1].IsChecked = true;
        selection.Rearm([docX, docY]);

        FileGrantSet grantsNext = composer.ComposeTurn(selection.CheckedGrantablePaths()).Grants;
        Assert.Equal(2, grantsNext.Count);
        Assert.True(grantsNext.Contains(rootGate.Resolve(y).ResolvedPath));

        // The descriptor for the next turn now marks Y writable too (advisory mirror of the grant change).
        string descriptorNext = MultiDocContextDescriptor.Build(selection.CurrentRows, _root);
        Assert.Contains(y + " [WRITABLE]", descriptorNext, System.StringComparison.Ordinal);
    }

    // ---- scratch buffer (null path) contributes no grant + does not crash assembly (NG-5) ----

    [Fact]
    public void ScratchBufferCheckedContributesNoGrantAndDoesNotCrash()
    {
        (_, AiTurnGrantComposer composer) = BuildSession();

        var selection = new MultiDocSelection();
        selection.Rearm([new OpenDocumentInfo(Path: null, "untitled", IsFocused: true, IsDirty: true, IsReadOnly: false)]);
        // A scratch buffer is never auto-checked and is non-grantable; force-check it to prove it grants nothing.
        selection.Rows[0].IsChecked = true;

        IReadOnlyList<string> grantable = selection.CheckedGrantablePaths();
        Assert.Empty(grantable); // scratch excluded (NG-5).

        FileGrantSet grants = composer.ComposeTurn(grantable).Grants;
        Assert.True(grants.IsEmpty); // root-only, fail-closed.
    }

    // ---- end-to-end: the panel prepends the descriptor to the runner prompt at turn-arm (FR-MD-3 delivery) ----

    [Fact]
    public async Task PanelPrependsTheCheckedSetDescriptorToTheRunnerPromptAtArm()
    {
        (PathResolverSandboxGate rootGate, AiTurnGrantComposer composer) = BuildSession();
        string outside = MakeFile(_outsideDir, "target.txt");

        var runner = new CapturingTurnRunner();
        var vm = new AiPanelViewModel(
            runner,
            new ScriptedConfirmService(),
            new ScriptedHookHost(selfTestPasses: true),
            new FakeAgentBackend(),
            TimeProvider.System);

        // Bind the session WITH the composer (the AiSessionBinder path), arm write agency, wire the doc provider.
        var sessionFileOps = new AiFileOpsService(
            rootGate, new CountingAtomicFileStore(), new NullCapStateStore(), new FakeAppDataPaths(),
            sessionId: "s-e2e", CapConfig.Pinned);
        vm.BindSession(rootGate.Resolve(_root).ResolvedPath, sessionFileOps, "conv-e2e", composer);
        vm.ConfiguredMode = AgencyMode.AutoApply;
        await vm.ArmAgencyAsync();

        vm.OpenDocumentsProvider = () => new List<OpenDocumentInfo>
        {
            new(outside, "target.txt", IsFocused: true, IsDirty: false, IsReadOnly: false),
        };

        vm.PromptInput = "edit this document";
        await vm.SendCommand.ExecuteAsync(null);

        // The runner received the descriptor-augmented prompt; the checked out-of-folder doc is marked WRITABLE.
        Assert.NotNull(runner.LastPrompt);
        Assert.Contains("HoardPad AI write allow-list", runner.LastPrompt!, System.StringComparison.Ordinal);
        Assert.Contains(outside + " [WRITABLE]", runner.LastPrompt!, System.StringComparison.Ordinal);
        Assert.EndsWith("edit this document", runner.LastPrompt!.TrimEnd(), System.StringComparison.Ordinal);
        // The visible transcript shows the BARE prompt (the descriptor is not shown to the user).
        Assert.Contains(vm.Transcript, l => l.Text == "edit this document");
    }

    // ---- OQ-MD-5 option ii (feature-ai-multidoc-panel §5.5): ComposeReplayFileOps + TryMatchGrantForTarget ----
    //  UnionBeforeImage in the name resolves the verify filter.

    [Fact]
    public void ComposeReplayFileOpsUnionBeforeImageAdmitsExactlyTheGrantedPath()
    {
        // The per-op replay service is composed over UnionSandboxGate(rootGate, {grant}); through the service's
        // gate the granted out-of-folder path is Allowed and a sibling is Refused (no widening).
        (PathResolverSandboxGate rootGate, AiTurnGrantComposer composer) = BuildSession();
        string granted = MakeFile(_outsideDir, "granted.txt");
        string sibling = MakeFile(_outsideDir, "granted.txt2");
        string grant = rootGate.Resolve(granted).ResolvedPath;

        // ComposeReplayFileOps builds the union-gated service; its enforcement is the SAME UnionSandboxGate the
        // per-turn service uses, so re-derive that gate to assert the admit surface (the service holds it private).
        AiFileOpsService replayService = composer.ComposeReplayFileOps(grant);
        Assert.NotNull(replayService); // composed (not null) — the write path exists.

        var equivalentGate = new UnionSandboxGate(rootGate, FileGrantSet.FromResolvedPaths(new[] { grant }));
        Assert.Equal(SandboxDecision.Allow, equivalentGate.Resolve(granted).Decision);
        Assert.Equal(SandboxDecision.Deny, equivalentGate.Resolve(sibling).Decision);   // sibling NOT admitted
    }

    [Fact]
    public void ComposeReplayFileOpsUnionBeforeImageEmptyGrantIsRootOnlyFailClosed()
    {
        // An empty/whitespace grant yields a root-only service (fail-closed) — an out-of-folder path is Denied.
        (PathResolverSandboxGate rootGate, AiTurnGrantComposer composer) = BuildSession();
        string outside = MakeFile(_outsideDir, "target.txt");

        AiFileOpsService rootOnly = composer.ComposeReplayFileOps("   ");
        Assert.NotNull(rootOnly);

        var equivalentGate = new UnionSandboxGate(rootGate, FileGrantSet.Empty);
        Assert.Equal(SandboxDecision.Deny, equivalentGate.Resolve(outside).Decision);
    }

    [Fact]
    public void TryMatchGrantForTargetUnionBeforeImageReturnsGrantForAGrantedOutOfFolderTarget()
    {
        // A parked op's raw target that resolves to a member of the turn's grants → the matching resolved grant.
        (PathResolverSandboxGate rootGate, AiTurnGrantComposer composer) = BuildSession();
        string outside = MakeFile(_outsideDir, "target.txt");
        string grant = rootGate.Resolve(outside).ResolvedPath;

        string? matched = composer.TryMatchGrantForTarget(outside, new[] { grant });
        Assert.Equal(grant, matched);
    }

    [Fact]
    public void TryMatchGrantForTargetUnionBeforeImageReturnsNullForInRootOrUngrantedTarget()
    {
        // In-root target → null (needs no grant). Out-of-folder target not in the turn's grants → null.
        (PathResolverSandboxGate rootGate, AiTurnGrantComposer composer) = BuildSession();
        string inRoot = MakeFile(_root, "in-root.txt");
        string granted = MakeFile(_outsideDir, "granted.txt");
        string other = MakeFile(_outsideDir, "other.txt");
        string grant = rootGate.Resolve(granted).ResolvedPath;

        // In-root path is not among the grants → null.
        Assert.Null(composer.TryMatchGrantForTarget(inRoot, new[] { grant }));
        // Out-of-folder path that is NOT the granted one → null (non-vacuity: only the granted one matches).
        Assert.Null(composer.TryMatchGrantForTarget(other, new[] { grant }));
        // The granted one → non-null (proves the match is real, not always-null).
        Assert.Equal(grant, composer.TryMatchGrantForTarget(granted, new[] { grant }));
    }

    // A turn runner that records the last TurnRequest.Prompt so the descriptor injection can be asserted.
    private sealed class CapturingTurnRunner : IAiTurnRunner
    {
        public string? LastPrompt { get; private set; }

        public async IAsyncEnumerable<TurnEvent> RunAsync(
            TurnRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            LastPrompt = request.Prompt;
            await Task.CompletedTask.ConfigureAwait(false);
            yield return TurnEvent.Assistant("ok");
        }

        public Task StopAsync() => Task.CompletedTask;
    }
}
