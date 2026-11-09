// EditorRightEdgeClipTests.cs — F-EDIT2: the editor's right-edge character clip.
//
// Root cause (hostile-review-confirmed): AvaloniaEdit's built-in caret-follow horizontal auto-scroll
// (decompiled AvaloniaEdit 11.4.1 — TextArea.PerformTextInput calls Caret.BringCaretToView() exactly once
// per committed keystroke) settles a CONSTANT ~7.83px short of true max scroll during a real per-character
// typing loop, hard-clipping the just-typed trailing glyph against the editor's right edge. It does not
// self-correct on idle and is independent of editor width. The fix (EditorView.axaml.cs) re-issues
// BringCaretToView() on TextArea.TextEntered, which closes the undershoot to a steady ~2.5px baseline —
// well under one monospace glyph cell — measured clean across a font-size / Padding / window-width /
// prefix-length sweep.
//
// The oracle here is a MEASUREMENT, not a structural assertion: it types a long line via real
// per-character KeyTextInput (not a programmatic CaretOffset assignment — the two exercise different
// AvaloniaEdit auto-scroll bookkeeping) into the REAL production MainWindow/EditorView built by the real
// CompositionRoot, then asserts the settled undershoot stays comfortably under one glyph cell width — a
// bound DERIVED at test time from the editor's own font metrics (TextView.WideSpaceWidth), not a frozen
// magic pixel count. Confirmed to fail (undershoot ~7.83px, well over the derived bound) with the
// TextEntered hook removed from EditorView.axaml.cs, and to pass (~2.5px) with it present — see F-EDIT2
// build report for the quoted RED/GREEN runs.
//
// This does not double-cover the other two BringCaretToView() call sites AvaloniaEdit itself owns
// (CaretNavigationCommandHandler's End-key family, SelectionMouseHandler's mouse-drag select) or this
// file's own SelectRange (Find/Replace navigation) — see the scope-decision comment on the TextEntered
// subscription in EditorView.axaml.cs's constructor (note: OnExternalWriteApplied does not call
// BringCaretToView() at all — that is a separate, pre-existing, out-of-scope gap, not a fourth site).

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using HoardPad.App.Hosting;
using HoardPad.App.Session;
using HoardPad.App.Settings;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace HoardPad.App.Tests;

public sealed class EditorRightEdgeClipTests
{
    private readonly ITestOutputHelper _output;

    public EditorRightEdgeClipTests(ITestOutputHelper output) => _output = output;

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

    /// <summary>
    /// Build the REAL production window (CompositionRoot -> MainWindow -> EditorView, the same wiring the
    /// app ships) with a fresh document, then click into the editor so real <see cref="Window.KeyTextInput"/>
    /// reaches it. <paramref name="fontSize"/>/<paramref name="rightPadding"/> override the control instance
    /// directly (never the shared XAML/brand resources) so the sweep can cover configs beyond the shipped
    /// defaults (Brand.Font.CodeSize=13, Padding right=10 per F-EDIT1) without touching production markup.
    /// </summary>
    private static (Window window, TextEditor editor) BuildFocusedEditor(
        ServiceProvider provider, double width, double fontSize, double rightPadding)
    {
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        var workspace = provider.GetRequiredService<WorkspaceService>();

        var window = new MainWindow { DataContext = vm, Width = width, Height = 700 };
        window.Show();
        workspace.NewDocument();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var editorSurface = window.FindControl<EditorView>("EditorSurface")!;
        var editor = editorSurface.FindControl<TextEditor>("Editor")!;
        editor.FontSize = fontSize;
        editor.Padding = new Thickness(0, 0, rightPadding, 0);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Point mid = WindowOffsetOf(editor, window) + new Point(editor.Bounds.Width / 2, 20);
        window.MouseDown(mid, MouseButton.Left);
        window.MouseUp(mid, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(editor.TextArea.IsFocused, "editor must be focused for KeyTextInput to reach it");

        return (window, editor);
    }

    /// <summary>
    /// Type <paramref name="prefix"/> then <paramref name="charCount"/> more characters ONE AT A TIME via
    /// real <see cref="Window.KeyTextInput"/> events (the actual code path a typing user exercises — a
    /// programmatic <c>CaretOffset</c> assignment does not drive the same auto-scroll bookkeeping), settling
    /// the render after each keystroke, then returns the settled caret-follow undershoot
    /// (<c>maxScrollX - Offset.X</c>; 0 means auto-scroll reached true max scroll, no undershoot).
    /// </summary>
    private static double MeasureUndershootAfterTyping(
        Window window, TextEditor editor, int charCount, string prefix = "row_", string ch = "x")
    {
        var scrollViewer = editor.GetVisualDescendants().OfType<ScrollViewer>().First();

        window.KeyTextInput(prefix);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        for (int i = 0; i < charCount; i++)
        {
            window.KeyTextInput(ch);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        double maxScrollX = Math.Max(0, scrollViewer.Extent.Width - scrollViewer.Viewport.Width);
        return maxScrollX - scrollViewer.Offset.X;
    }

    /// <summary>
    /// The frozen oracle. Types 130 characters past the right edge and asserts the settled undershoot stays
    /// well under one glyph cell — a bound DERIVED from this run's own font metrics
    /// (<c>TextView.WideSpaceWidth</c>, a real measured advance width, not a frozen literal), so the test
    /// tracks whatever font/size a given config actually renders with instead of assuming DejaVu Sans Mono
    /// 13px. 75% of one glyph cell comfortably separates the fixed steady-state (~2.5px) from the unfixed one
    /// (~7.83px, at or beyond a full glyph cell) without being so tight that ordinary render-timing jitter
    /// could flip the verdict.
    ///
    /// Sweep: the production default (font 13 / right-Padding 10), one larger font size, one larger
    /// right-Padding, and the user-reachable font-size FLOOR (<see cref="HoardPadSettings.MinEditorFontSize"/>,
    /// settable via Settings) at both Padding values — a smaller font shrinks the derived glyph-cell bound
    /// while the flat ~2.5px undershoot stays constant, so the floor is the direction most likely to expose
    /// a config where the fixed undershoot stops being "well under one glyph cell". Per the F-EDIT2 review's
    /// residual-risk requirement (pitch-phase: a settled 2.5px undershoot is provably safe only if no config
    /// lets a glyph's paint start within that margin of the boundary) — every config below converged to the
    /// same clean, unclipped result.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(13.0, 10.0)] // production default: Brand.Font.CodeSize=13, Padding right=10 (F-EDIT1).
    [InlineData(18.0, 10.0)] // larger font size.
    [InlineData(13.0, 24.0)] // larger right Padding.
    [InlineData(HoardPadSettings.MinEditorFontSize, 10.0)] // user-reachable font-size floor.
    [InlineData(HoardPadSettings.MinEditorFontSize, 24.0)] // floor + larger right Padding.
    public void TypingNearRightEdgeSettlesWellUnderOneGlyphCell(double fontSize, double rightPadding)
    {
        using ServiceProvider provider = CompositionRoot.Build();
        (Window window, TextEditor editor) = BuildFocusedEditor(provider, width: 1000, fontSize, rightPadding);

        double undershoot = MeasureUndershootAfterTyping(window, editor, charCount: 130);
        double wideSpaceWidth = editor.TextArea.TextView.WideSpaceWidth;

        _output.WriteLine(
            $"fontSize={fontSize} rightPadding={rightPadding} undershoot={undershoot:F3}px " +
            $"wideSpaceWidth={wideSpaceWidth:F3}px");

        // Fail closed: a degenerate (zero/near-zero) metric would make the bound below vacuously easy to
        // satisfy, so require the derived glyph width to be a real, non-trivial measurement first.
        Assert.True(wideSpaceWidth > 1.0,
            $"sanity: TextView.WideSpaceWidth ({wideSpaceWidth}) must be a real measured glyph width");

        Assert.True(undershoot < wideSpaceWidth * 0.75,
            $"caret-follow auto-scroll undershot {undershoot:F3}px, >= 75% of one glyph cell " +
            $"({wideSpaceWidth:F3}px) — the trailing typed character risks being clipped against the " +
            "editor's right edge (F-EDIT2 regression)");
    }
}
