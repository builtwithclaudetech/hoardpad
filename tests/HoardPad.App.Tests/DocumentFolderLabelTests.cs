// DocumentFolderLabelTests.cs — FS-3 relabel gates (design §6.7, tasklist FS-3 Decision B, FINAL) and
// FS-4 (tasklist "Remove the AI-menu sandbox item — only File ▸ Open Folder changes the folder").
//
// FS-3 removes user-facing "Sandbox" wording in favor of the canonical "Document folder: <root>" label,
// while the safety boundary itself (FR-26) stays fully enforced — these tests assert WORDING only, never
// enforcement. Covers the AI-panel scope line (every SandboxState), the out-of-folder note, the
// approve-each confirm's document-folder line, the blocked-write transcript-chip fallback text, the
// replay-refused chip, and the no-folder-open assistant note.
//
// FS-4 removes the entire AI-menu "Change Sandbox Folder…" item + its ChangeSandboxFolderCommand
// plumbing — File ▸ Open Folder is the ONLY change-folder affordance. Test (6) below is the FS-4
// regression guard: builds a REAL MainWindow through the composition root, opens the live "_AI" menu,
// and asserts no child MenuItem header survives referencing "Sandbox" or a folder-change action — this
// closes the FS-3 cs-review Critical (an AI-menu item still said "Sandbox" after the relabel) permanently
// by removing the item rather than merely renaming it.
//
// The Core.Ai activity-log outcome-reason literal (the single deliberate PROTECTED-spine touch) has its
// own regression test in tests/HoardPad.Core.Ai.Tests/FileOps/AiFileOpsServiceTests.cs
// (SandboxDenyReasonReadsOutsideDocumentFolderNotSandbox) — not duplicated here.
//
// Class name "DocumentFolderLabelTests" resolves the verify filter "DocumentFolderLabel".

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HoardPad.App.Ai;
using HoardPad.App.Hosting;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using HoardPad.Core.Ai.Caps;
using HoardPad.Core.Ai.FileOps;
using HoardPad.Core.Records;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class DocumentFolderLabelTests
{
    private static AiFileOpsService FakeFileOps(string sessionId) =>
        new(
            new ChecklistTestSupportSandboxGate(),
            new CountingAtomicFileStore(),
            new NullCapStateStore(),
            new FakeAppDataPaths(),
            sessionId,
            CapConfig.Pinned);

    // ---------------------------------------------------------------------------------------------------
    //  (1) The AI-panel scope line (AiPanelViewModel.SandboxLine) — every reachable state.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void RestingNoFolderStateHasNoSandboxWording()
    {
        var vm = AiPanelTestFactory.New(new ScriptedTurnRunner([]));

        Assert.Equal(SandboxState.NoFolder, vm.Sandbox);
        Assert.DoesNotContain("Sandbox", vm.SandboxLine, StringComparison.Ordinal);
    }

    [Fact]
    public void NotYetArmedStateShowsDocumentFolderLabel()
    {
        // FS-1: a folder is resolved but write agency has not armed yet — BindSession alone reaches this
        // state (ArmAgencyAsync is what would move it to Armed/SelfTestFailed).
        var vm = AiPanelTestFactory.New(new ScriptedTurnRunner([]));

        vm.BindSession("/proj", FakeFileOps("sess-not-armed"), conversationId: "conv-not-armed");

        Assert.Equal(SandboxState.NotYetArmed, vm.Sandbox);
        Assert.Equal("Document folder: /proj  [ not yet armed ]", vm.SandboxLine);
        Assert.DoesNotContain("Sandbox", vm.SandboxLine, StringComparison.Ordinal);
        // The header-bar downgrade badge (design §6.2/§6.3) rendered alongside the scope line — caught live
        // on :214 during the FS-3 verification drive ("auto-apply (read-only: sandbox not yet armed)").
        Assert.Equal("document folder not yet armed", vm.DowngradeBadge);
        Assert.DoesNotContain("sandbox", vm.DowngradeBadge, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ArmedStateShowsDocumentFolderLabel()
    {
        var vm = AiPanelTestFactory.New(new ScriptedTurnRunner([]), selfTestPasses: true);
        vm.ConfiguredMode = AgencyMode.AutoApply;
        vm.BindSession("/proj", FakeFileOps("sess-armed"), conversationId: "conv-armed");

        await vm.ArmAgencyAsync();

        Assert.Equal(SandboxState.Armed, vm.Sandbox);
        Assert.Equal("Document folder: /proj  [ scoped ✓ ]", vm.SandboxLine);
        Assert.DoesNotContain("Sandbox", vm.SandboxLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelfTestFailedStateShowsDocumentFolderLabelNotSandbox()
    {
        var vm = AiPanelTestFactory.New(new ScriptedTurnRunner([]), selfTestPasses: false);
        vm.ConfiguredMode = AgencyMode.AutoApply;
        vm.BindSession("/proj", FakeFileOps("sess-selftest-failed"), conversationId: "conv-selftest-failed");

        await vm.ArmAgencyAsync();

        Assert.Equal(SandboxState.SelfTestFailed, vm.Sandbox);
        Assert.Equal("Document folder could not be armed ⚠", vm.SandboxLine);
        Assert.DoesNotContain("Sandbox", vm.SandboxLine, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------
    //  (2) The out-of-folder note (PANEL-3 / FR-MD-6(c)) — relabeled by FS-3.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void OutOfFolderNoteReadsOutsideTheDocumentFolder()
    {
        Assert.Contains("outside the document folder", AiPanelViewModel.OutOfFolderNote, StringComparison.Ordinal);
        Assert.DoesNotContain("Sandbox", AiPanelViewModel.OutOfFolderNote, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------------------------------
    //  (3) The no-folder-open assistant note (NoSessionTurnRunner, design §6.8).
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task NoSessionTurnRunnerNoteHasNoSandboxWording()
    {
        var runner = new NoSessionTurnRunner();
        var events = new List<TurnEvent>();

        await foreach (TurnEvent ev in runner.RunAsync(
            new TurnRequest("hi", AgencyMode.AutoApply, WriteAgencyArmed: false), CancellationToken.None))
        {
            events.Add(ev);
        }

        string note = Assert.Single(events).Text!;
        Assert.DoesNotContain("sandbox", note, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------------------------------
    //  (4) The approve-each confirm's document-folder line (AiConfirmView, design §6.6.1).
    // ---------------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public void ApproveEachConfirmLineReadsDocumentFolderNotSandbox()
    {
        var window = new Window { Width = 600, Height = 400 };
        var host = new Panel();
        window.Content = host;
        window.Show();

        var view = new AiConfirmView();
        host.Children.Add(view);

        var request = new ApproveEachRequest("edit", "/proj/app.py", "- old\n+ new", "/proj");
        view.ShowApproveEach(request);

        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        TextBlock? sandboxLine = view.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.Name == "SandboxText");
        Assert.NotNull(sandboxLine);
        Assert.Equal("in the document folder /proj · undoable", sandboxLine!.Text);
        Assert.DoesNotContain("sandbox", sandboxLine.Text!, StringComparison.OrdinalIgnoreCase);

        window.Close();
    }

    // ---------------------------------------------------------------------------------------------------
    //  (5) The blocked-write transcript-chip fallback text (SandboxBlocked safety event, design §6.7).
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SandboxBlockedFallbackChipReadsWriteOutsideTheDocumentFolder()
    {
        // Text: null exercises the FALLBACK wording (ev.Text ?? "..."), the production case where no
        // caller currently supplies an explicit safety-event message (see AiPanelViewModel.HandleSafetyEventAsync).
        var runner = new ScriptedTurnRunner(
            [new TurnEvent(TurnEventKind.SafetyEvent, Text: null, SafetyKind: SafetyEventKind.SandboxBlocked)]);
        var vm = AiPanelTestFactory.New(runner);

        await AiPanelTestFactory.RunTurnAsync(vm, "attempt an escape write");

        TranscriptLine chip = Assert.Single(vm.Transcript, t => t.Kind == TranscriptKind.SafetyEventChip);
        Assert.Equal("blocked — write outside the document folder", chip.Text);
        Assert.DoesNotContain("sandbox", chip.Text, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------------------------------
    //  (6) FS-4: the "_AI" menu no longer offers any change-folder / "Sandbox" item — File ▸ Open Folder
    //      is the sole change-folder affordance. Builds a REAL MainWindow via the composition root
    //      (mirrors EditMenuOpensAndExecutesTests) so this fails if the item, or any re-add under a new
    //      name, ever reappears.
    // ---------------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public void AiMenuNoLongerOffersAChangeFolderOrSandboxItem()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();

        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.UpdateLayout();

        MenuItem? aiMenuItem = window
            .GetVisualDescendants()
            .OfType<MenuItem>()
            .FirstOrDefault(m => (m.Header as string) == "_AI");

        Assert.NotNull(aiMenuItem);

        Point clickPoint = WindowOffsetOf(aiMenuItem!, window)
            + new Point(aiMenuItem!.Bounds.Width / 2, aiMenuItem.Bounds.Height / 2);
        window.MouseDown(clickPoint, MouseButton.Left);
        window.MouseUp(clickPoint, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(aiMenuItem.IsSubMenuOpen);

        List<string> aiMenuHeaders = aiMenuItem
            .GetLogicalDescendants()
            .OfType<MenuItem>()
            .Select(m => m.Header as string ?? string.Empty)
            .ToList();

        Assert.NotEmpty(aiMenuHeaders); // sanity: the submenu actually has items (Toggle/Clear Conversation).
        Assert.DoesNotContain(aiMenuHeaders, h => h.Contains("Sandbox", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(aiMenuHeaders, h => h.Contains("Change", StringComparison.OrdinalIgnoreCase));

        window.Close();
    }

    private static Point WindowOffsetOf(Visual node, Visual root)
    {
        var offset = new Point(0, 0);
        Visual? current = node;
        while (current is not null && !ReferenceEquals(current, root))
        {
            if (current is Control control)
                offset += control.Bounds.Position;
            current = current.GetVisualParent();
        }
        return offset;
    }
}
