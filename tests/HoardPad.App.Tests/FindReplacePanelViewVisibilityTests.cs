// FindReplacePanelViewVisibilityTests.cs — BUG-1 View-level regression guard.
//
// FindReplaceTests.cs's existing "BUG-1" cases only assert the FindReplaceViewModel's own IsVisible
// property, which was ALWAYS false by default — that was never the bug. The actual defect was in
// MainWindow.axaml: the real <views:FindReplaceView> Control carries Avalonia's IsVisible default of
// true from the moment the Window is constructed, and (per the debug finding logged against BUG-1)
// DataContext="{Binding FindReplace}" resolves to null until AttachFindReplace runs post-construction,
// so the panel's own inner IsVisible="{Binding IsVisible}" binding had no source to hide it with. Those
// VM-only tests would stay green even if the outer Control-level IsVisible binding in MainWindow.axaml
// were deleted entirely. This file drives the real MainWindow + FindReplaceView Controls headless to
// close that gap.
//
// Class name contains "FindReplace" so the verify filter "FullyQualifiedName~FindReplace" resolves it.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using AvaloniaEdit;
using HoardPad.App.Editor;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using TextMateSharp.Grammars;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class FindReplacePanelViewVisibilityTests
{
    [AvaloniaFact]
    public void PanelControlIsHiddenAtWindowConstructionBeforeFindReplaceIsAttached()
    {
        // Mirrors production startup ordering exactly (App.axaml.cs sets DataContext = shell before
        // AttachFindReplace runs): the shell's FindReplace is still null when the Window is built.
        var shell = new MainWindowViewModel();
        Assert.Null(shell.FindReplace);

        var window = new MainWindow { DataContext = shell };
        window.Show();
        window.UpdateLayout();

        var panel = window.FindControl<FindReplaceView>("FindReplacePanel");
        Assert.NotNull(panel);
        Assert.False(panel!.IsVisible); // BUG-1: this Control used to default to visible=true here.
    }

    [AvaloniaFact]
    public void PanelControlStaysHiddenAfterAttachThenShowsOnShowFindCommand()
    {
        var shell = new MainWindowViewModel();
        var window = new MainWindow { DataContext = shell };
        window.Show();
        window.UpdateLayout();

        var panel = window.FindControl<FindReplaceView>("FindReplacePanel")!;
        Assert.False(panel.IsVisible);

        var ws = new FakeFindReplaceWorkspace();
        var frVm = new FindReplaceViewModel(ws, ScriptedReviewPrompt.ConfirmAll());
        shell.AttachFindReplace(frVm);
        window.UpdateLayout();
        Assert.False(panel.IsVisible); // still hidden immediately after attach (default IsVisible=false).

        shell.ShowFindCommand.Execute(null);
        window.UpdateLayout();
        Assert.True(panel.IsVisible); // Ctrl+F reveals the real Control, not just the VM property.
    }

    // ---- BUG-2: Esc must close the panel regardless of AttachFindReplace timing. ----
    //
    // FindReplaceTests.HideCommandHidesPanel only asserted the VM's own IsVisible flag by calling
    // HideCommand directly — it never exercised the real MainWindow.axaml.cs wiring path, so it stayed
    // green even when _findReplace (the field OnWindowKeyDown reads) was never assigned. This test
    // mirrors production startup ordering exactly (App.axaml.cs sets DataContext = shell BEFORE calling
    // AttachFindReplace) — the ordering the BUG-2 fix spec addresses — then drives a real Esc KeyEventArgs
    // at the Window itself. This proves the OnWindowKeyDown wiring is correct in isolation, but — as the
    // live-repro below demonstrates — is NOT sufficient by itself: it never routes the event through a
    // focused editor, so it cannot see the second BUG-2 defect (AvaloniaEdit's stock SearchPanel).
    [AvaloniaFact]
    public void EscapeAtTheWindowClosesTheVisiblePanel()
    {
        var shell = new MainWindowViewModel();
        Assert.Null(shell.FindReplace);

        var window = new MainWindow { DataContext = shell };
        window.Show();
        window.UpdateLayout();

        var ws = new FakeFindReplaceWorkspace();
        var frVm = new FindReplaceViewModel(ws, ScriptedReviewPrompt.ConfirmAll());
        shell.AttachFindReplace(frVm);
        window.UpdateLayout();

        shell.ShowFindCommand.Execute(null);
        window.UpdateLayout();

        var panel = window.FindControl<FindReplaceView>("FindReplacePanel")!;
        Assert.True(frVm.IsVisible);
        Assert.True(panel.IsVisible);

        window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        window.UpdateLayout();

        Assert.False(frVm.IsVisible); // BUG-2: this used to stay true — _findReplace was never wired.
        Assert.False(panel.IsVisible);
    }

    /// <summary>A real mouse click at the target's midpoint — proven pattern (RedoRebindTests,
    /// FindReplaceUndoTests) for giving AvaloniaEdit's <see cref="TextArea"/> genuine keyboard focus; a
    /// direct <c>.Focus()</c> call on the composite, templated <see cref="TextEditor"/> is not the same as
    /// the real input path a user's click drives.</summary>
    private static void ClickToFocus(Window window, Visual target)
    {
        Point offset = default;
        Visual? current = target;
        while (current is not null && !ReferenceEquals(current, window))
        {
            if (current is Control control)
                offset += control.Bounds.Position;
            current = current.GetVisualParent();
        }
        Point midpoint = offset + new Point(((Control)target).Bounds.Width / 2, ((Control)target).Bounds.Height / 2);
        window.MouseDown(midpoint, MouseButton.Left);
        window.MouseUp(midpoint, MouseButton.Left);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// BUG-2 LIVE-REPRO root cause (found only via live Xvfb smoke testing — see build-context.md's
    /// non-negotiable rule 2 — this is exactly the class of defect headless-only coverage misses): the test
    /// above proves <c>OnWindowKeyDown</c>'s wiring is sound, but on a real desktop Esc still did not close
    /// the panel whenever the EDITOR (not the panel) held keyboard focus — the common case, since clicking
    /// into the document to look at a match is the normal Find workflow. Root cause: AvaloniaEdit's
    /// <c>TextEditor.OnApplyTemplate</c> unconditionally installs its own stock <c>SearchPanel</c> (never
    /// shown — design §3.5 forbids it; <c>FindReplaceView</c> supersedes it), whose
    /// <c>SearchCommands.CloseSearchPanel</c> CommandBinding has a default gesture of bare Escape.
    /// <c>TextAreaInputHandler</c>'s KeyDown dispatch matches ANY CommandBinding's gesture and
    /// unconditionally marks the event <c>Handled</c> once executed — even though
    /// <c>ExecuteCloseSearchPanel</c>/<c>Close()</c> is a no-op when that permanently-invisible panel isn't
    /// open — so Escape was silently swallowed at the TextArea level before it could ever bubble to
    /// <c>MainWindow.OnWindowKeyDown</c>. The fix (<c>EditorView.axaml.cs</c>) uninstalls the stock
    /// SearchPanel the moment its template is applied. <c>Avalonia.Headless</c>'s <c>KeyPressQwerty</c>
    /// drives the SAME built-in AvaloniaEdit CommandBindings machinery a live window does (proven by
    /// <c>RedoRebindTests</c>'s analogous Ctrl+Y-swallow coverage), which the Window-raised test above
    /// cannot reach.
    /// </summary>
    [AvaloniaFact]
    public async Task EscapeClosesThePanelWhileTheEditorHasRealFocus()
    {
        var registry = new HoardPadRegistryOptions(ThemeName.OneDark);
        var fileService = new EditorFileService(
            new RealAtomicFileStore(), new HoardPad.Host.PathResolver(), new HoardPad.App.Hosting.UtfDefaultEncodingDetector());
        var editorVm = new EditorViewModel(fileService, registry, new LanguageCatalog(registry));

        string path = Path.Combine(Path.GetTempPath(), "bug2-escape-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(path, "hello world").ConfigureAwait(true);
        try
        {
            await editorVm.OpenAsync(path, CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        var shell = new MainWindowViewModel(editorVm);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        window.UpdateLayout();

        var ws = new FakeFindReplaceWorkspace();
        var frVm = new FindReplaceViewModel(ws, ScriptedReviewPrompt.ConfirmAll());
        shell.AttachFindReplace(frVm);
        window.UpdateLayout();

        shell.ShowFindCommand.Execute(null);
        window.UpdateLayout();
        Assert.True(frVm.IsVisible);

        TextEditor editor = window.GetVisualDescendants().OfType<TextEditor>().Single();
        ClickToFocus(window, editor);
        Assert.True(editor.TextArea.IsFocused);

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.UpdateLayout();

        Assert.False(frVm.IsVisible); // BUG-2 live repro: AvaloniaEdit's stock SearchPanel used to swallow this.

        window.Close();
    }
}
