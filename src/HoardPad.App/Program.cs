using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;

namespace HoardPad.App;

/// <summary>
/// M0.1 scaffold entry point. Builds the Avalonia desktop app.
///
/// QA3-21: wires a layered top-level unhandled-exception net so an unforeseen UI-thread race (the
/// class of bug the ChecklistViewModel reentrancy guard targets) can never again kill the whole app
/// SILENTLY — every layer writes a crash log before doing anything else:
///   - Dispatcher.UIThread.UnhandledException fires for an exception thrown while the dispatcher is
///     executing a delegate (Invoke/InvokeAsync, and the routed-event dispatch that flows through it).
///     Handled = true lets the app keep running instead of crashing when the dispatcher covers the
///     failing call.
///   - AppDomain.CurrentDomain.UnhandledException is the universal, thread-agnostic last resort — it
///     fires for literally any unhandled exception on any thread. It cannot prevent process
///     termination (IsTerminating is always true for a genuinely unhandled exception), but it
///     guarantees a crash log exists instead of the process just vanishing.
///   - TaskScheduler.UnobservedTaskException catches a fire-and-forget Task's exception that would
///     otherwise surface only during finalization (a distinct, common footgun class).
/// </summary>
internal sealed class Program
{
    // Avalonia configuration; do not remove — the visual designer relies on it.
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            // Dispatcher.UIThread only has a real platform-backed implementation once AppBuilder's
            // platform Setup() has run — touching the Dispatcher singleton any earlier (e.g. directly
            // in Main before this callback) binds it to no platform impl and StartWithClassicDesktopLifetime
            // then throws PlatformNotSupportedException from Dispatcher.MainLoop. AfterSetup runs once
            // Setup() has completed but before the main loop starts, which is the correct hook point.
            .AfterSetup(_ => Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException);

    private static void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception, "Dispatcher.UnhandledException");
        // Swallow it: the dispatcher can keep pumping instead of tearing the whole app down for a
        // single unforeseen UI-thread race. If the exception truly leaves the app in a broken state,
        // the user still sees a "save failed" style signal from the affected feature rather than a
        // silent, total exit.
        e.Handled = true;
    }

    private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e) =>
        WriteCrashLog(e.ExceptionObject as Exception, "AppDomain.UnhandledException");

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception, "TaskScheduler.UnobservedTaskException");
        e.SetObserved();
    }

    /// <summary>
    /// Best-effort crash log write. Deliberately narrow: this runs from a crash handler, so it must
    /// never itself throw (a failing crash log must not mask or replace the original crash).
    /// </summary>
    private static void WriteCrashLog(Exception? exception, string source)
    {
        try
        {
            // Route through IAppDataPaths.AppDataRoot (HoardPad.Host.AppDataPaths) rather than
            // re-deriving LocalApplicationData directly here — that would re-introduce the same
            // CWD-relative gap FR-36 / AppDataPathsTests guards against (an empty or relative
            // resolution silently anchoring to the process CWD via Path.Combine).
            string dir = Path.Combine(new HoardPad.Host.AppDataPaths().AppDataRoot, "crash-logs");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"crash-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.log");
            File.WriteAllText(file, $"{source} at {DateTime.UtcNow:O}{Environment.NewLine}{exception}{Environment.NewLine}");
        }
        catch
        {
            // A crash-log write failure must never mask the original crash or throw out of a
            // crash handler — this is a genuine boundary-of-last-resort per the house error-handling
            // style (catch only at boundaries; never let secondary failures compound a primary one).
        }
    }
}
