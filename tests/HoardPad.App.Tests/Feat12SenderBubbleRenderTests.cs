// Feat12SenderBubbleRenderTests.cs — FEAT-12: AI Panel History — visually distinguish AI messages from
// the user's own messages (a different border/background color per sender, brand.md §13 tokens, both
// themes).
//
// TranscriptBubbleConverter (Views/AiPanelConverters.cs) maps a transcript line's Kind to a bubble
// Background/Border/Padding: a user message renders on an accent-bordered overlay-surface fill; an AI
// reply renders on a primary (jewel)-bordered surface-alt fill — the same jewel tone as the panel's own
// "AI" header label. Every other kind (file-op chip, safety chip, system note) resolves a transparent
// border/background, so it renders exactly as it did before this change.
//
// This test hosts a REAL AiPanelView in a REAL headless window, injects one user line + one assistant
// line, and for BOTH themes (dark + light, brand.md §0 lockstep):
//   (a) locates the two rendered bubble Borders (by their child TextBlock's Text) and asserts their
//       Background AND BorderBrush colors differ from each other and from Transparent — a non-vacuous,
//       render-level proof the two senders are visually distinct, not just a ViewModel-level claim.
//   (b) captures a PNG for visual (vision-review) confirmation.
// The class name contains "Feat12" so `--filter "FullyQualifiedName~Feat12"` resolves it.

using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HoardPad.App.Ai;
using HoardPad.App.Settings;
using HoardPad.App.Theme;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class Feat12SenderBubbleRenderTests
{
    private const string UserText = "refactor main() to split the parser out";
    private const string AssistantText = "I'll extract the parser into parser.py.";

    private static string ResolveScreenshotDir()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HoardPad.sln")))
            dir = dir.Parent;
        string root = dir?.FullName ?? AppContext.BaseDirectory;
        string path = Path.Combine(root, "artifacts", "feat12-smoke", "screenshots");
        Directory.CreateDirectory(path);
        return path;
    }

    [AvaloniaTheory]
    [InlineData(AppTheme.Dark, "dark")]
    [InlineData(AppTheme.Light, "light")]
    public void UserAndAssistantBubblesRenderWithDistinctBackgroundAndBorderColors(AppTheme theme, string label)
    {
        new ThemeService().Switch(theme);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        AiPanelViewModel vm = AiPanelTestFactory.New(new ScriptedTurnRunner([]));
        vm.Transcript.Add(new TranscriptLine { Kind = TranscriptKind.UserMessage, Text = UserText });
        vm.Transcript.Add(new TranscriptLine { Kind = TranscriptKind.AssistantMessage, Text = AssistantText });

        var view = new AiPanelView { DataContext = vm };
        var window = new Window { Content = view, Width = 300, Height = 600 };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Border userBubble = FindBubbleFor(view, UserText);
        Border assistantBubble = FindBubbleFor(view, AssistantText);

        Color userBg = SolidColor(userBubble.Background);
        Color userBorder = SolidColor(userBubble.BorderBrush);
        Color assistantBg = SolidColor(assistantBubble.Background);
        Color assistantBorder = SolidColor(assistantBubble.BorderBrush);

        // Neither sender resolves to a see-through bubble — both got a real brand.md §13 token.
        Assert.NotEqual(Colors.Transparent, userBg);
        Assert.NotEqual(Colors.Transparent, userBorder);
        Assert.NotEqual(Colors.Transparent, assistantBg);
        Assert.NotEqual(Colors.Transparent, assistantBorder);

        // The two senders are visually distinct from each other (background AND border differ) — the
        // FEAT-12 deliverable, proven at the render level for this theme.
        Assert.NotEqual(userBg, assistantBg);
        Assert.NotEqual(userBorder, assistantBorder);

        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        string path = Path.Combine(ResolveScreenshotDir(), $"ai-panel-sender-bubbles-{label}.png");
        frame?.Save(path);
        Assert.True(File.Exists(path), $"expected the {label}-theme sender-bubble screenshot at {path}");

        window.Close();
    }

    private static Border FindBubbleFor(Visual root, string text)
    {
        TextBlock messageBlock = root.GetVisualDescendants()
            .OfType<TextBlock>()
            .First(t => t.Text == text);
        return Assert.IsType<Border>(messageBlock.Parent);
    }

    private static Color SolidColor(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;
}
