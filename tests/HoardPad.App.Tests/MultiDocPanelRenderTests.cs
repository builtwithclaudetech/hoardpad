// MultiDocPanelRenderTests.cs — PANEL-3 acceptance (feature-ai-multidoc-panel FR-MD-6(a)–(e)). The AI
// multi-doc checklist UX: the render-tree verifier + the Save-As-on-check interaction + the truthful
// IsReadOnly population. The render test follows the Run-12 pattern — it builds the REAL MainWindow via the
// REAL CompositionRoot, drives the visual tree, and captures a PNG to
// artifacts/run12-smoke/screenshots/panel-multidoc.png so the pixels are inspectable ground truth. The class
// name contains "MultiDocPanelRender" so the --filter "FullyQualifiedName~MultiDocPanelRender" resolves it.
//
// These tests are PANEL-3 UX only: they consume the PANEL-2 state surface (MultiDoc.Rows) + drive the display
// refresh / Save-As seam. They never touch the grant/enforcement logic (PANEL-2/A2/ii own it).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HoardPad.App;
using HoardPad.App.Ai;
using HoardPad.App.Hosting;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class MultiDocPanelRenderTests
{
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
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        string path = Path.Combine(ResolveScreenshotDir(), name + ".png");
        frame?.Save(path);
        return path;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FR-MD-6(a)/(b)/(c)/(d): the real rendered checklist — one row per open doc, the focused row marked +
    // pre-checked, a checked out-of-folder row shows the OQ-MD-2 note, a read-only row shows the read-only
    // (not-writable) affordance, and a scratch row is selectable. Captures panel-multidoc.png.
    // ─────────────────────────────────────────────────────────────────────────
    [AvaloniaFact]
    public void MultiDocPanelRendersRowsFocusReadOnlyOutOfFolderAndScratch()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        Assert.NotNull(vm.AiPanel);
        AiPanelViewModel panel = vm.AiPanel!;

        var window = new MainWindow { DataContext = vm };
        window.Show();

        vm.ToggleAiPanelCommand.Execute(null); // reveal the AI panel column.
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // A representative open-doc set: an in-root FOCUSED doc, an in-root READ-ONLY doc, an OUT-OF-FOLDER
        // doc, and a never-saved SCRATCH buffer. The root is the enclosing folder of the in-root docs.
        string root = OperatingSystem.IsWindows() ? @"C:\proj" : "/proj";
        string focusedPath = Path.Combine(root, "main.cs");
        string readOnlyPath = Path.Combine(root, "locked.txt");
        string outOfFolderPath = OperatingSystem.IsWindows() ? @"C:\other\notes.md" : "/other/notes.md";

        panel.MultiDoc.Rearm(
            new List<OpenDocumentInfo>
            {
                new(focusedPath, "main.cs", IsFocused: true, IsDirty: false, IsReadOnly: false, TabId: "t-focused"),
                new(readOnlyPath, "locked.txt", IsFocused: false, IsDirty: false, IsReadOnly: true, TabId: "t-ro"),
                new(outOfFolderPath, "notes.md", IsFocused: false, IsDirty: false, IsReadOnly: false, TabId: "t-oof"),
                new(Path: null, "untitled", IsFocused: false, IsDirty: false, IsReadOnly: false, TabId: "t-scratch"),
            },
            root);

        // The focused row is pre-checked (FR-MD-6(a)); check the out-of-folder row so its OQ-MD-2 note shows.
        MultiDocRowViewModel focusedRow = panel.MultiDoc.Rows.Single(r => r.IsFocused);
        MultiDocRowViewModel readOnlyRow = panel.MultiDoc.Rows.Single(r => r.IsReadOnly);
        MultiDocRowViewModel outOfFolderRow = panel.MultiDoc.Rows.Single(r => r.IsOutOfFolder);
        MultiDocRowViewModel scratchRow = panel.MultiDoc.Rows.Single(r => r.IsScratch);

        Assert.True(focusedRow.IsChecked); // FR-MD-6(a): focused doc pre-checked on first arm.
        outOfFolderRow.IsChecked = true;   // FR-MD-6(c): a checked out-of-folder row shows the note.

        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        AiPanelView? panelView = window.FindControl<AiPanelView>("AiPanel");
        Assert.NotNull(panelView);
        var list = panelView!.GetVisualDescendants().OfType<ItemsControl>()
            .FirstOrDefault(c => c.Name == "MultiDocList");
        Assert.NotNull(list);

        // (a) one rendered checkbox per open document (all four rows).
        var checkboxes = panelView.GetVisualDescendants().OfType<CheckBox>().ToList();
        Assert.Equal(4, checkboxes.Count);

        // (a) the focused row is pre-checked AND marked (its focus dot renders, non-zero size).
        CheckBox focusedBox = checkboxes[panel.MultiDoc.Rows.IndexOf(focusedRow)];
        Assert.True(focusedBox.IsChecked == true);
        var focusDots = panelView.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => (t.Text == "●") && t.IsEffectivelyVisible).ToList();
        Assert.Single(focusDots); // exactly one focused row → exactly one visible focus dot.
        Assert.True(focusDots[0].Bounds.Width > 0);

        // (b) the read-only row shows the read-only (not-writable) affordance, NOT the writable one.
        var readOnlyBadges = panelView.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => (t.Text == "🔒 read-only") && t.IsEffectivelyVisible).ToList();
        Assert.Single(readOnlyBadges);
        // and even if the read-only row were checked, it must never present as writable (effective posture).
        readOnlyRow.IsChecked = true;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.False(readOnlyRow.IsEffectivelyWritable);

        // (c) the checked out-of-folder row shows the approved OQ-MD-2 note VERBATIM.
        var noteBlocks = panelView.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Text == AiPanelViewModel.OutOfFolderNote && t.IsEffectivelyVisible).ToList();
        Assert.Single(noteBlocks);

        // (d) a scratch row is present + selectable (its checkbox is enabled, i.e. selectable per OQ-MD-3).
        CheckBox scratchBox = checkboxes[panel.MultiDoc.Rows.IndexOf(scratchRow)];
        Assert.True(scratchBox.IsEnabled, "the scratch row must be selectable (OQ-MD-3 overrides non-selectable).");

        string png = Capture(window, "panel-multidoc");
        Assert.True(File.Exists(png), $"expected the multi-doc panel screenshot at {png}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FEAT-11: with more than 4 open docs, the checklist caps its visible height to ~4 rows and scrolls for
    // the rest — the checkbox allow-list itself (all rows still present + toggleable) is unaffected. Captures
    // panel-multidoc-scroll.png for vision review.
    // ─────────────────────────────────────────────────────────────────────────
    [AvaloniaFact]
    public void MultiDocChecklistCapsVisibleHeightAndScrollsPastFourRows()
    {
        using ServiceProvider provider = CompositionRoot.Build();
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        AiPanelViewModel panel = vm.AiPanel!;

        var window = new MainWindow { DataContext = vm };
        window.Show();

        vm.ToggleAiPanelCommand.Execute(null);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        // 8 in-root docs — well past the 4-row cap.
        string root = OperatingSystem.IsWindows() ? @"C:\proj" : "/proj";
        var docs = new List<OpenDocumentInfo>();
        for (int i = 0; i < 8; i++)
        {
            string path = OperatingSystem.IsWindows() ? $@"{root}\file{i}.cs" : $"{root}/file{i}.cs";
            docs.Add(new(path, $"file{i}.cs", IsFocused: i == 0, IsDirty: false, IsReadOnly: false, TabId: $"t{i}"));
        }
        panel.MultiDoc.Rearm(docs, root);

        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        AiPanelView? panelView = window.FindControl<AiPanelView>("AiPanel");
        Assert.NotNull(panelView);

        // All 8 rows still exist and are toggleable — the checkbox allow-list is unchanged by the cap.
        var checkboxes = panelView!.GetVisualDescendants().OfType<CheckBox>().ToList();
        Assert.Equal(8, checkboxes.Count);
        Assert.All(checkboxes, cb => Assert.True(cb.IsEnabled)); // rows past row 4 stay selectable, just scrolled.
        MultiDocRowViewModel lastRow = panel.MultiDoc.Rows[7];
        Assert.False(lastRow.IsChecked); // still individually toggleable, unaffected by scroll clipping.
        lastRow.IsChecked = true;
        Assert.True(lastRow.IsChecked);

        // The checklist's ScrollViewer is height-capped (~4 rows) and its content overflows it (scrollbar active).
        ScrollViewer? scroller = panelView.GetVisualDescendants().OfType<ScrollViewer>()
            .FirstOrDefault(s => s.Content is ItemsControl { Name: "MultiDocList" });
        Assert.NotNull(scroller);
        Assert.True(scroller!.Viewport.Height < scroller.Extent.Height,
            "8 rows must overflow the capped viewport so the scrollbar activates.");
        Assert.True(scroller.Viewport.Height <= 140,
            $"the checklist viewport must stay capped to ~4 rows, was {scroller.Viewport.Height}.");
        // Roughly 4 rows' worth of content should be visible (measured ~34px/row incl. item margin).
        Assert.InRange(scroller.Viewport.Height / 34.0, 3.0, 4.5);

        string png = Capture(window, "panel-multidoc-scroll");
        Assert.True(File.Exists(png), $"expected the multi-doc scroll screenshot at {png}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FR-MD-6(d) / OQ-MD-3: checking a SCRATCH buffer triggers Save-As; cancel reverts the check; success
    // gives the row a path (the buffer participates as a normal grant thereafter).
    // ─────────────────────────────────────────────────────────────────────────
    [AvaloniaFact]
    public async Task ScratchCheckCancelledSaveAsRevertsTheCheck()
    {
        AiPanelViewModel panel = BuildPanel();

        var docs = new List<OpenDocumentInfo>
        {
            new(Path: null, "untitled", IsFocused: true, IsDirty: false, IsReadOnly: false, TabId: "t-scratch"),
        };
        panel.OpenDocumentsProvider = () => docs;
        bool saveAsCalled = false;
        panel.RequestSaveAsForScratchAsync = _ => { saveAsCalled = true; return Task.FromResult<string?>(null); }; // user cancels.

        panel.RefreshMultiDocRows();
        MultiDocRowViewModel scratch = panel.MultiDoc.Rows.Single();
        Assert.False(scratch.IsChecked); // scratch is never auto-checked.

        scratch.IsChecked = true;              // the user checks the scratch row …
        await panel.OnScratchRowCheckedAsync(scratch).ConfigureAwait(true); // … driving the Save-As flow.

        Assert.True(saveAsCalled, "checking a scratch buffer must trigger Save-As (OQ-MD-3).");
        Assert.False(scratch.IsChecked, "a cancelled Save-As must REVERT the scratch check (a path-less buffer cannot be granted).");
    }

    [AvaloniaFact]
    public async Task ScratchCheckSuccessfulSaveAsGivesTheRowAPath()
    {
        AiPanelViewModel panel = BuildPanel();

        string root = OperatingSystem.IsWindows() ? @"C:\proj" : "/proj";
        string savedPath = Path.Combine(root, "was-scratch.txt");

        // The provider first yields the scratch buffer; after a successful Save-As it yields the SAME tab now
        // bearing the saved path (exactly what the real WorkspaceService Save-As does to the tab).
        bool saved = false;
        panel.OpenDocumentsProvider = () => saved
            ? new List<OpenDocumentInfo> { new(savedPath, "was-scratch.txt", IsFocused: true, IsDirty: false, IsReadOnly: false, TabId: "t-scratch") }
            : new List<OpenDocumentInfo> { new(Path: null, "untitled", IsFocused: true, IsDirty: false, IsReadOnly: false, TabId: "t-scratch") };
        panel.RequestSaveAsForScratchAsync = _ => { saved = true; return Task.FromResult<string?>(savedPath); };

        panel.RefreshMultiDocRows();
        MultiDocRowViewModel scratch = panel.MultiDoc.Rows.Single();

        scratch.IsChecked = true;
        await panel.OnScratchRowCheckedAsync(scratch).ConfigureAwait(true);

        // After the successful Save-As the row now bears the saved path, is no longer scratch, and stays checked.
        MultiDocRowViewModel resolved = panel.MultiDoc.Rows.Single();
        Assert.Equal(savedPath, resolved.Path);
        Assert.False(resolved.IsScratch);
        Assert.True(resolved.IsChecked);
        Assert.True(resolved.IsEffectivelyWritable); // in-root, checked, writable — a normal grant.
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FR-MD-6(b): IsReadOnly is populated TRUTHFULLY from the file's real on-disk read-only attribute, and a
    // read-only row is never presented as writable even when checked.
    // ─────────────────────────────────────────────────────────────────────────
    [AvaloniaFact]
    public void ReadOnlyFileYieldsReadOnlyRowAndNeverWritable()
    {
        // The production read-only probe reads the REAL on-disk attribute.
        string tmp = Path.Combine(Path.GetTempPath(), "hoardpad-ro-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(tmp, "locked");
        File.SetAttributes(tmp, File.GetAttributes(tmp) | FileAttributes.ReadOnly);
        try
        {
            Assert.True(App.IsFileReadOnly(tmp), "a read-only file must report read-only (truthful probe).");

            // A writable temp file (and a null scratch path) must NOT report read-only.
            string writable = Path.Combine(Path.GetTempPath(), "hoardpad-rw-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(writable, "open");
            try
            {
                Assert.False(App.IsFileReadOnly(writable));
                Assert.False(App.IsFileReadOnly(null)); // a scratch buffer is never read-only.
            }
            finally { File.Delete(writable); }

            // The read-only fact flows through to the row's effective-writable affordance: a checked read-only
            // row is NOT writable (the affordance stays truthful, FR-MD-6(b)).
            var selection = new MultiDocSelection();
            selection.Rearm(
                new List<OpenDocumentInfo> { new(tmp, Path.GetFileName(tmp), IsFocused: false, IsDirty: false, IsReadOnly: App.IsFileReadOnly(tmp)) },
                sandboxRoot: Path.GetTempPath());
            MultiDocRowViewModel row = selection.Rows.Single();
            Assert.True(row.IsReadOnly);
            row.IsChecked = true;
            Assert.False(row.IsEffectivelyWritable);
        }
        finally
        {
            File.SetAttributes(tmp, File.GetAttributes(tmp) & ~FileAttributes.ReadOnly);
            File.Delete(tmp);
        }
    }

    // Build a bare AiPanelViewModel with the shared test fakes (no real backend / clock / dialog).
    private static AiPanelViewModel BuildPanel() =>
        new(
            runner: new ScriptedTurnRunner([]),
            confirm: new ScriptedConfirmService(),
            hookHost: new ScriptedHookHost(selfTestPasses: true),
            backend: new FakeAgentBackend(),
            time: TimeProvider.System);
}
