// BenchHarness.cs — the M9.1 perf-bench wiring (architecture §3.5, §1.6; SPIKE-1 §A/§B/§C).
//
// PERF INSTRUMENTATION ONLY, and INERT by default. The entire bench path is reached only when the bench
// launcher sets HOARDPAD_BENCH_PIPE in the spawned app's environment (HoardPad.Bench does this; an
// ordinary launch and every `dotnet test` run leave it unset, so TryStart returns immediately and not a
// single line below runs). It does NOT touch the AI spine (no cap-state, no hook, no AF_UNIX channel),
// does NOT change the guarded ctor, and adds no work to a non-bench startup.
//
// What it does on a bench run:
//   1. Start the named-pipe echo channel (BenchEchoChannel) so the launcher can connect and read the
//      24-byte Stopwatch stop record (keystroke instant + TextChanged-rendered instant + frequency,
//      architecture §3.5).
//   2. Open the bench fixture file (HOARDPAD_BENCH_FILE) as a normal editor tab — the SAME production
//      materialization path RestoreAsync uses (EditorViewModelFactory → OpenAsync → DocumentTab.ForEditor
//      → Tabs.Add → ActivateTab), so the cold-start cost measured is the real editor surface, real
//      TextMate grammar load, real layout (SPIKE-1's intent).
//   3. Once the editor has rendered the loaded document (its first LayoutUpdated = SC-1 "first render
//      ready"), fire ONE synthetic startup keystroke and emit the single stop echo when THAT keystroke has
//      rendered (the next LayoutUpdated), matching SPIKE-1's layout-as-paint-proxy stop. The keystroke is
//      app-injected (one char into the editor document → a genuine TextChanged → re-colorize/preview
//      re-render) rather than an OS-level xdotool key, because the headless X11 backend rejects programmatic
//      keyboard focus so an OS key cannot reach the nested editor (SPIKE-1's sole-content editor got default
//      focus; HoardPad's nested editor does not). This is the PRD R3-mi10 reduced-scope input leg — the real
//      editor TextChanged→layout/render path is still exercised; only OS-key focus-routing is bypassed.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using HoardPad.App.Session;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using HoardPad.Core.Recovery;
using Microsoft.Extensions.DependencyInjection;

namespace HoardPad.App.Bench;

/// <summary>
/// Bootstraps the M9.1 perf bench when (and only when) the bench launcher requests it via
/// <c>HOARDPAD_BENCH_PIPE</c>. All wiring here is perf instrumentation gated behind that env var;
/// <see cref="TryStart"/> is a no-op in a normal/test run.
/// </summary>
internal static class BenchHarness
{
    internal const string PipeEnvVar = "HOARDPAD_BENCH_PIPE";
    internal const string FileEnvVar = "HOARDPAD_BENCH_FILE";

    /// <summary>
    /// If the bench launcher set <see cref="PipeEnvVar"/>, start the echo channel, open the bench file as
    /// an editor tab, and arm the one-shot stop emitter on the next post-load <c>TextChanged</c>. Returns
    /// silently (doing nothing) on a non-bench run. The returned channel is owned by the caller for the
    /// process lifetime (disposed at shutdown); null when the bench is not active.
    /// </summary>
    public static BenchEchoChannel? TryStart(IServiceProvider services, Window window)
    {
        string? pipeName = Environment.GetEnvironmentVariable(PipeEnvVar);
        if (string.IsNullOrEmpty(pipeName))
            return null; // not a bench run — the product is unaffected.

        var channel = new BenchEchoChannel(pipeName);
        channel.StartListening();

        // Open the bench fixture (if supplied + present) and arm the emitter once the window is shown so
        // the editor surface is realized and its initial load-mirror has settled — only the launcher's
        // synthetic keystroke (the next TextChanged) then triggers the single stop write.
        string? benchFile = Environment.GetEnvironmentVariable(FileEnvVar);
        window.Opened += (_, _) => Dispatcher.UIThread.Post(() => _ = OpenAndArmAsync(services, window, benchFile, channel));

        return channel;
    }

    private static async Task OpenAndArmAsync(IServiceProvider services, Window window, string? benchFile, BenchEchoChannel channel)
    {
        var shell = services.GetRequiredService<MainWindowViewModel>();

        if (!string.IsNullOrEmpty(benchFile) && File.Exists(benchFile))
        {
            // Same production path RestoreAsync uses to materialize a text tab (architecture §9.2).
            var factory = services.GetRequiredService<EditorViewModelFactory>();
            EditorViewModel editor = factory.Create();
            await editor.OpenAsync(benchFile, CancellationToken.None).ConfigureAwait(true);

            DocumentTab tab = DocumentTab.ForEditor(Guid.NewGuid().ToString("N"), TabKind.File, editor);
            shell.Tabs.Add(tab);
            shell.ActivateTab(tab);
        }

        // Drive the layout-aware synthetic keystroke on the dispatcher (after the open + bind). The editor
        // fires its first LayoutUpdated when the loaded document is rendered (SC-1 "first render ready"),
        // then the keystroke + its render produce the stop instant (MA15: exactly one emission).
        Dispatcher.UIThread.Post(() => ArmAndFire(window, channel), DispatcherPriority.Background);
    }

    /// <summary>
    /// Run the layout-aware synthetic startup keystroke and emit the single SC-1/SC-2 stop echo when the
    /// keystroke is RENDERED (architecture §3.5; SPIKE-1's layout-as-paint-proxy stop). The keystroke is
    /// app-injected (one char into the editor document) rather than an OS-level key because the headless
    /// X11 backend rejects programmatic keyboard focus (PRD R3-mi10 reduced-scope input leg); the editor's
    /// real TextChanged→layout/render path is still exercised end-to-end.
    /// </summary>
    private static void ArmAndFire(Window window, BenchEchoChannel channel)
    {
        var editorView = window.FindControl<EditorView>("EditorSurface");
        if (editorView is null)
            return;

        editorView.RunBenchKeystroke(
            markKeystroke: channel.MarkKeystroke,         // SC-2 start (in-process), just before the insert.
            onRendered: channel.EmitStopTimestamp);       // SC-1/SC-2 stop (render complete) → emit the echo.
    }
}
