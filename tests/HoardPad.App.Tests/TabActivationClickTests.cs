// TabActivationClickTests.cs — QA3-14 / QA3-3 regression guard.
//
// Symptom: clicking a NON-ACTIVE tab only registered in a tiny central region of the chip — everywhere
// else the click silently did nothing. QA3-3 ("Language menu wrongly applies to all tabs") turned out to
// be the SAME bug filed under a different symptom: the maintainer's "wrong tab" clicks were clicks that never
// activated the tab he thought he'd switched to.
//
// FIRST root cause (fixed, still correct): OnTabPointerPressed (MainWindow.axaml.cs) called
// DragDrop.DoDragDrop UNCONDITIONALLY on every left-button press on the tab chip, gated only by
// ShouldStartDrag (the × button exclusion), with no movement threshold. DoDragDrop captures the pointer for
// the OS drag loop immediately, so a click's matching PointerReleased never arrived undisturbed. Fix: defer
// the drag start — OnTabPointerPressed now only records a drag/activation CANDIDATE; OnWindowPointerMoved
// promotes it to a real DragDrop.DoDragDrop only once the pointer travels past a small threshold
// (ExceedsDragThreshold). A stationary press+release therefore NEVER starts a drag.
//
// SECOND root cause — QA3-14 REOPENED 2026-07-06, fixed 2026-07-09 (this is why the first fix alone passed
// every test here but the maintainer still saw center-only activation on his real desktop): activation used to be
// wired through Avalonia's built-in Tapped gesture (Border Tapped="OnTabTapped"). Decompiling Avalonia.Base
// 11.3.6's Avalonia.Input.Gestures shows Tapped only fires when PointerPressed's and PointerReleased's raw
// hit-test SOURCE is the *identical* Interactive instance. The chip nests a TextBlock (hit-testable only
// where it actually painted glyph ink — RenderDataGlyphRunNode.HitTest checks the shaped run's own ink
// bounds, not the TextBlock's layout Bounds) and a StackPanel wrapper that paints nothing (so it is never a
// hit target at all — a miss there falls through to the Border underneath). A press and a release a couple
// of pixels apart — ordinary real-mouse settle, not a drag — can resolve to genuinely different raw
// hit-test elements (e.g. the TextBlock's glyph ink vs. the Border's own background), silently dropping
// Tapped. Fix: OnWindowPointerReleased now activates the pending candidate directly, gated only on the
// physical IPointer matching (never on which descendant visual either event hit-tested to) — see
// MainWindow.axaml.cs's tab-strip file-header comment for the full writeup.
//
// IMPORTANT — this second root cause is NOT reproducible under Avalonia.Headless, and this file does not
// claim otherwise. Headless's headless-drawing test platform never paints real glyph geometry, so a chip's
// TextBlock never produces its own hit-testable draw node there. An explicit probe was tried before writing
// this file's tests — a press in the StackPanel's paint-nothing inter-child gap (raw-hit-tests to the
// Border) followed by a release a few pixels away, inside the × close Button's own painted
// Background="Transparent" rect (a genuinely different raw hit-test element than the Border) — expecting
// this to force the OLD Tapped-based code to drop the tap. It did not: the OLD code still activated
// correctly under headless in that scenario, confirming headless's simulated pointer gesture does not
// reproduce the real-compositor source-identity split this fix addresses. That probe was therefore removed
// rather than kept as a misleadingly-named "regression test" it cannot actually be — this is exactly the
// same headless/real-WM gap the deferred-drag OS loop already has (see below), and it's why this task's own
// Done-when criteria mark QA3-14 `[~]` rather than `[x]` pending the maintainer's real-desktop confirmation.
//
// The Avalonia OS drag loop (DragDrop.DoDragDrop) itself cannot be exercised headless — see the note atop
// TabReorderTests.cs, and confirmed empirically here: a direct probe of DragDrop.DoDragDrop under the
// headless test platform neither completed nor threw within several seconds, i.e. it hangs — so this file
// proves the LOGIC that makes a plain click work, using Avalonia.Headless's MouseMove (already used in
// SplitterTests.cs) in addition to the zero-movement MouseDown/MouseUp used above:
//   - a stationary press+release near the chip's LEFT edge activates the tab.
//   - a stationary press+release near the chip's RIGHT edge activates the tab.
//   - a stationary press+release on the × close button still closes (not activates) the tab.
//   - a real-world JITTER click (press → sub-threshold MouseMove → release) still activates the tab — the
//     concrete case the first fix exists for.
//   - an over-threshold MouseMove consumes the drag candidate (observed via the read-only
//     MainWindow.HasPendingDragCandidate seam) BEFORE the hung DragDrop.DoDragDrop is ever awaited — this is
//     as far as the promotion path is verifiable without blocking on the unsupported OS drag loop; whether a
//     real OS pointer-capture then suppresses activation on an actual drag is NOT decidable headless and
//     needs the maintainer's real-desktop confirmation (see the closing report).
//   - ExceedsDragThreshold, the pure predicate gating the deferred drag start, directly.
//
// Class names contain "Tab" so --filter "FullyQualifiedName~Tab" resolves them.

using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HoardPad.App.Editor;
using HoardPad.App.Session;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using HoardPad.Core.Recovery;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class TabActivationClickTests
{
    [AvaloniaFact]
    public void StationaryClickNearTheChipsRightEdgeActivatesTheTab()
    {
        var shell = new MainWindowViewModel();
        DocumentTab a = MakeTab("a"), b = MakeTab("b");
        shell.Tabs.Add(a);
        shell.Tabs.Add(b);
        shell.ActivateTab(a); // a starts active; the click below targets b, the non-active tab.

        var window = new MainWindow { DataContext = shell };
        window.Show();
        window.UpdateLayout();

        Border chipB = FindChip(window, b);
        Point nearRightEdge = WindowOffsetOf(chipB, window)
            + new Point(chipB.Bounds.Width - 2, chipB.Bounds.Height / 2);

        window.MouseDown(nearRightEdge, MouseButton.Left);
        window.MouseUp(nearRightEdge, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(b, shell.ActiveTab);
    }

    [AvaloniaFact]
    public void StationaryClickNearTheChipsLeftEdgeActivatesTheTab()
    {
        var shell = new MainWindowViewModel();
        DocumentTab a = MakeTab("a"), b = MakeTab("b");
        shell.Tabs.Add(a);
        shell.Tabs.Add(b);
        shell.ActivateTab(a);

        var window = new MainWindow { DataContext = shell };
        window.Show();
        window.UpdateLayout();

        Border chipB = FindChip(window, b);
        Point nearLeftEdge = WindowOffsetOf(chipB, window) + new Point(2, chipB.Bounds.Height / 2);

        window.MouseDown(nearLeftEdge, MouseButton.Left);
        window.MouseUp(nearLeftEdge, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(b, shell.ActiveTab);
    }

    [AvaloniaFact]
    public void ClickingTheCloseButtonClosesTheTabInsteadOfActivatingIt()
    {
        var shell = new MainWindowViewModel();
        DocumentTab a = MakeTab("a"), b = MakeTab("b");
        shell.Tabs.Add(a);
        shell.Tabs.Add(b);
        shell.ActivateTab(a); // a active; press the × on the non-active tab b.

        var window = new MainWindow { DataContext = shell };
        window.Show();
        window.UpdateLayout();

        Border chipB = FindChip(window, b);
        Button close = chipB.GetVisualDescendants().OfType<Button>().Single();
        Point closePoint = WindowOffsetOf(close, window)
            + new Point(close.Bounds.Width / 2, close.Bounds.Height / 2);

        window.MouseDown(closePoint, MouseButton.Left);
        window.MouseUp(closePoint, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(a, shell.ActiveTab); // b was never activated by the press.
        Assert.DoesNotContain(b, shell.Tabs); // the × closed it instead.
    }

    [AvaloniaFact]
    public void SubThresholdJitterBetweenPressAndReleaseStillActivatesTheTab()
    {
        // The real-world case the whole fix exists for: a human hand is never perfectly stationary between
        // press and release. A couple of pixels of jitter must NOT be promoted to a drag.
        var shell = new MainWindowViewModel();
        DocumentTab a = MakeTab("a"), b = MakeTab("b");
        shell.Tabs.Add(a);
        shell.Tabs.Add(b);
        shell.ActivateTab(a);

        var window = new MainWindow { DataContext = shell };
        window.Show();
        window.UpdateLayout();

        Border chipB = FindChip(window, b);
        Point pressPoint = WindowOffsetOf(chipB, window)
            + new Point(chipB.Bounds.Width / 2, chipB.Bounds.Height / 2);
        Point jitterPoint = pressPoint + new Point(2, 0); // well under DragStartThresholdPixels (5).

        window.MouseDown(pressPoint, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.MouseMove(jitterPoint, RawInputModifiers.LeftMouseButton);
        Dispatcher.UIThread.RunJobs();
        window.MouseUp(jitterPoint, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(b, shell.ActiveTab);
        Assert.False(window.HasPendingDragCandidate); // released cleanly, nothing left pending.
    }

    [AvaloniaFact]
    public void OverThresholdMoveConsumesTheDragCandidateBeforeAnyReleaseCanActivate()
    {
        // DragDrop.DoDragDrop cannot be driven to completion headless (verified empirically — see the file
        // header), so this test can only prove the promotion DECISION: OnWindowPointerMoved clears the
        // pending candidate SYNCHRONOUSLY, before it ever awaits DoDragDrop. It does NOT (and headless
        // cannot) prove what a real OS drag loop's pointer capture then does to the sibling Tapped gesture —
        // that half needs a real desktop.
        var shell = new MainWindowViewModel();
        DocumentTab a = MakeTab("a"), b = MakeTab("b");
        shell.Tabs.Add(a);
        shell.Tabs.Add(b);
        shell.ActivateTab(a);

        var window = new MainWindow { DataContext = shell };
        window.Show();
        window.UpdateLayout();

        Border chipB = FindChip(window, b);
        Point pressPoint = WindowOffsetOf(chipB, window)
            + new Point(chipB.Bounds.Width / 2, chipB.Bounds.Height / 2);

        window.MouseDown(pressPoint, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.HasPendingDragCandidate, "a press on a tab chip must record a drag candidate");

        // 20px comfortably clears DragStartThresholdPixels (5).
        Point overThreshold = pressPoint + new Point(20, 0);
        window.MouseMove(overThreshold, RawInputModifiers.LeftMouseButton);
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.HasPendingDragCandidate, "a threshold-crossing move must consume the candidate");

        window.MouseUp(overThreshold, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.HasPendingDragCandidate); // still consumed after release — no re-arm.
    }

    /// <summary>Finds the realized chip Border for <paramref name="tab"/> (Tag = the bound DocumentTab).</summary>
    private static Border FindChip(Window window, DocumentTab tab) =>
        window.GetVisualDescendants().OfType<Border>().First(b => ReferenceEquals(b.Tag, tab));

    /// <summary>Sum a node's local Bounds top-left up to <paramref name="root"/> (mirrors the identical
    /// helper in Run12RenderVerifyTests.cs / LanguageMenuCheckmarkTests.cs).</summary>
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

    private static DocumentTab MakeTab(string id) => TabFactory.New(id);
}

// ─────────────────────────────────────────────────────────────────────────────
// ExceedsDragThreshold — pure predicate gating the deferred drag start (QA3-14 / QA3-3)
// ─────────────────────────────────────────────────────────────────────────────

public sealed class TabDragThresholdTests
{
    [Fact]
    public void NoMovementDoesNotExceedTheThreshold()
    {
        Assert.False(MainWindow.ExceedsDragThreshold(new Point(10, 10), new Point(10, 10), thresholdPixels: 5));
    }

    [Fact]
    public void MovementJustUnderTheThresholdDoesNotExceedIt()
    {
        Assert.False(MainWindow.ExceedsDragThreshold(new Point(0, 0), new Point(4, 0), thresholdPixels: 5));
    }

    [Fact]
    public void MovementPastTheThresholdExceedsIt()
    {
        Assert.True(MainWindow.ExceedsDragThreshold(new Point(0, 0), new Point(10, 0), thresholdPixels: 5));
    }

    [Fact]
    public void DiagonalMovementIsMeasuredByEuclideanDistanceNotAxisAlignedDelta()
    {
        // dx=4, dy=4 -> distance ~5.66 > threshold 5, even though neither axis alone crosses it.
        Assert.True(MainWindow.ExceedsDragThreshold(new Point(0, 0), new Point(4, 4), thresholdPixels: 5));
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Shared factory (file-scoped, mirrors TabFactory in TabReorderTests.cs / TabCloseTests.cs)
// ─────────────────────────────────────────────────────────────────────────────

file static class TabFactory
{
    public static DocumentTab New(string id) =>
        DocumentTab.ForEditor(id, TabKind.Scratch, MakeEditorVm());

    private static EditorViewModel MakeEditorVm()
    {
        var registry = new HoardPadRegistryOptions(TextMateSharp.Grammars.ThemeName.OneDark);
        var fileService = new EditorFileService(
            new CountingAtomicFileStore(),
            new HoardPad.Host.PathResolver(),
            new HoardPad.App.Hosting.UtfDefaultEncodingDetector());
        return new EditorViewModel(fileService, registry, new LanguageCatalog(registry));
    }
}
