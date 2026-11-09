// ChecklistScrollPreserveTests.cs — QA4-19 regression: a long checklist's scroll offset must survive a
// tab deactivate -> reactivate round-trip, not reset to the top.
//
// Root cause (per build-context.md / nightly-schedule.md QA4-19): the single ChecklistView instance is
// reused across every open .hoard tab (MainWindow.axaml DataContext="{Binding ActiveChecklist}"). Switching
// tabs repoints DataContext (vm -> null -> vm on a round-trip to the SAME tab, or vm -> vm2 switching between
// two open checklists) which flows through OnDataContextChanged -> RebuildVisibleRows/ItemsSource clear+
// re-add -> the ScrollViewer's Offset resets to (0,0) as the row containers are torn down and rebuilt.
//
// These are interaction-layer tests (a real ChecklistView + ScrollViewer attached to a headless Window) for
// the same reason ChecklistFocusTests.cs gives: a VM-only test cannot see the ScrollViewer's Offset — only
// the real visual-tree control exposes it.

using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HoardPad.App.Checklist;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using HoardPad.Core.Checklist;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class ChecklistScrollPreserveTests
{
    private const string Path = "/tmp/hoardpad-test/checklist-scroll.hoard";
    private const string Path2 = "/tmp/hoardpad-test/checklist-scroll-2.hoard";
    private const string Path3 = "/tmp/hoardpad-test/checklist-scroll-3.hoard";

    private static HoardDocument LongDocument(int itemCount) =>
        new()
        {
            Title = "Long list",
            Settings = new HoardSettings { CheckedItemMode = "strikethrough-in-place" },
            Items = Enumerable.Range(0, itemCount)
                .Select(i => new HoardItem { Id = $"item-{i}", Text = $"Item {i}", Order = i })
                .ToArray(),
        };

    private static ChecklistViewModel NewVm(HoardDocument doc, string path = Path) =>
        ChecklistViewModel.ForDocument(
            new ChecklistFileService(new CountingAtomicFileStore()), new ManualTimeProvider(), path, doc);

    /// <summary>Attach a real ChecklistView (with its ScrollViewer) to a small headless Window and lay it out.</summary>
    private static (Window window, ChecklistView view, ScrollViewer scrollViewer) Attach(ChecklistViewModel vm)
    {
        var view = new ChecklistView { DataContext = vm };
        // Deliberately short so a 60-row list overflows the viewport and the ScrollViewer has real range.
        var window = new Window { Content = view, Width = 400, Height = 150 };
        window.Show();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.UpdateLayout();
        // Flush the initial-attach restore-Post (ChecklistView.OnDataContextChanged posts a Loaded-priority
        // scroll restore for EVERY DataContext change, including the null -> vm one this constructor just
        // made). Left pending, it would later fire mid-test and stomp a manually-set Offset back to the vm's
        // (currently 0) ScrollOffsetY — settle it now, before the test does its own scrolling.
        PumpUntilSettled(window);
        // Named lookup, not GetVisualDescendants().OfType<ScrollViewer>() — the ModeSelector ComboBox's own
        // template contains a ScrollViewer too (for its dropdown), so an unnamed type search is ambiguous.
        var scrollViewer = view.FindControl<ScrollViewer>("RowsScrollViewer")
            ?? throw new InvalidOperationException("RowsScrollViewer not found in the ChecklistView template.");
        return (window, view, scrollViewer);
    }

    /// <summary>Pump the dispatcher + force render/layout passes until the value stabilizes (mirrors
    /// ChecklistFocusTests.WaitForFocusedRow's "flush the posted Loaded-priority work" pattern).</summary>
    private static void PumpUntilSettled(Window window)
    {
        for (int i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.UpdateLayout();
        }
    }

    [AvaloniaFact]
    public async Task ScrollOffsetSurvivesDeactivateThenReactivateOfTheSameTab()
    {
        var vm = NewVm(LongDocument(60));
        var (window, view, scrollViewer) = Attach(vm);

        // Scroll down (simulates the user having scrolled a long checklist).
        scrollViewer.Offset = new Vector(0, 300);
        PumpUntilSettled(window);
        double scrolledY = scrollViewer.Offset.Y;
        Assert.True(scrolledY > 0, "precondition: the list must actually be scrollable and scrolled.");

        // Deactivate: MainWindowViewModel.ActivateTab sets ActiveChecklist = null for a non-.hoard tab.
        view.DataContext = null;
        PumpUntilSettled(window);

        // Reactivate the SAME tab: ActiveChecklist is repointed back to the SAME ChecklistViewModel instance.
        view.DataContext = vm;
        PumpUntilSettled(window);

        Assert.Equal(scrolledY, scrollViewer.Offset.Y, precision: 3);

        window.Close();
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task SwitchingDirectlyBetweenTwoChecklistTabsPreservesEachOnesOwnScrollOffset()
    {
        var vmA = NewVm(LongDocument(60), Path);
        var vmB = NewVm(LongDocument(60), Path2);
        var (window, view, scrollViewer) = Attach(vmA);

        // Scroll document A down, then switch straight to document B (no intervening null DataContext —
        // the "switch between two open checklist tabs" path, not just the "switch away to a text tab" path).
        scrollViewer.Offset = new Vector(0, 200);
        PumpUntilSettled(window);
        double scrolledAY = scrollViewer.Offset.Y;
        Assert.True(scrolledAY > 0);

        view.DataContext = vmB;
        PumpUntilSettled(window);

        // B has never been scrolled — it must open at the top, not inherit A's offset.
        Assert.Equal(0, scrollViewer.Offset.Y, precision: 3);

        // Scroll B, then switch back to A — A's own saved offset must come back, not B's.
        scrollViewer.Offset = new Vector(0, 100);
        PumpUntilSettled(window);
        double scrolledBY = scrollViewer.Offset.Y;
        Assert.True(scrolledBY > 0);

        view.DataContext = vmA;
        PumpUntilSettled(window);
        Assert.Equal(scrolledAY, scrollViewer.Offset.Y, precision: 3);

        view.DataContext = vmB;
        PumpUntilSettled(window);
        Assert.Equal(scrolledBY, scrollViewer.Offset.Y, precision: 3);

        window.Close();
        await vmA.DisposeAsync();
        await vmB.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task ANewlyOpenedChecklistStartsScrolledToTheTop()
    {
        var vm = NewVm(LongDocument(60));
        var (window, _, scrollViewer) = Attach(vm);
        PumpUntilSettled(window);

        Assert.Equal(0, scrollViewer.Offset.Y, precision: 3);

        window.Close();
        await vm.DisposeAsync();
    }

    // ---- Hostile-review CRITICAL fix: a rapid A -> B -> C -> B chain (no intervening layout pass, e.g.
    // Ctrl+Tab-repeat / fast clicks) must not leave a STALE deferred restore-Post from an earlier hop
    // clobbering the tab the user actually lands on. Two failure modes, both closed by the fix:
    //   (1) the restore-Post itself must re-check identity at execution time (ViewModel == the vm it was
    //       queued for) and no-op on mismatch — a Post queued for C must never touch Offset once B is active
    //       again.
    //   (2) the OUTGOING-offset CAPTURE (OnPropertyChanged) must not stamp a vm with a stale Offset reading
    //       that never actually belonged to it — a hop away from a vm whose OWN restore never got a chance
    //       to run (no layout pass caught up) sees a leftover Offset from an EARLIER vm, not a real reading
    //       for the vm being deactivated; capturing it anyway corrupts that vm for whenever it reactivates.
    [AvaloniaFact]
    public async Task RapidTabSwitchingDoesNotLeaveAStaleDeferredRestoreCorruptingTheCurrentTab()
    {
        var vmA = NewVm(LongDocument(60), Path);
        var vmB = NewVm(LongDocument(60), Path2);
        var vmC = NewVm(LongDocument(60), Path3);
        var (window, view, scrollViewer) = Attach(vmA); // settles A (restore-Post has run, _settledVm == A).

        // A real user scroll on A — this is the value a bug would leak into B.
        scrollViewer.Offset = new Vector(0, 500);
        PumpUntilSettled(window);
        Assert.Equal(500, scrollViewer.Offset.Y, precision: 3); // precondition.

        // Rapid A -> B -> C -> B with NO PumpUntilSettled in between — every restore-Post from every hop
        // stays queued; none of them (nor any layout pass) has run yet by the time we land back on B.
        view.DataContext = vmB;
        view.DataContext = vmC;
        view.DataContext = vmB;

        // Now let every stacked Post drain.
        PumpUntilSettled(window);

        // B was NEVER scrolled by the user — it must show its own (0) offset, not A's stale 500 (the
        // CRITICAL bug: stacked posts draining FIFO left A's leftover offset stamped in from the corrupted
        // outgoing-capture chain) and not whatever transient value a mismatched vm=C Post might have applied.
        Assert.NotEqual(500, scrollViewer.Offset.Y, precision: 3);
        Assert.Equal(0, scrollViewer.Offset.Y, precision: 3);

        window.Close();
        await vmA.DisposeAsync();
        await vmB.DisposeAsync();
        await vmC.DisposeAsync();
    }
}
