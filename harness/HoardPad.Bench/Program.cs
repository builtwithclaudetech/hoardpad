// HoardPad.Bench — the external SC-1 / SC-2 / NFR-5 perf launcher (architecture §1.6, §3.5; SPIKE-1).
//
// This is a SEPARATE process that measures a PUBLISHED HoardPad.App from the OUTSIDE — there is NO
// ProjectReference to HoardPad.App (architecture §1.6 / §2.1 invariant 2: the external-observer design).
// It process-launches the published self-contained R2R app, owns the SC-1 START instant, and reads the
// app's single 24-byte Stopwatch STOP record from a named pipe (the SC-1/SC-2 stop instant, architecture
// §3.5 / MA15). The app fires its own synthetic startup keystroke once the editor is rendered (see below).
//
// The 24-byte echo carries TWO instants + the frequency, so one trial yields BOTH gate timings (the app
// emits exactly one echo, MA15):
//   SC-1 cold-launch→first-render-ready = textChanged_ts − process_start_ts   (process_start owned by this
//                                                                              launcher, immediately before
//                                                                              Process.Start — same host
//                                                                              monotonic Stopwatch domain,
//                                                                              so the cross-process delta is
//                                                                              valid)
//   SC-2 keystroke→render               = textChanged_ts − keystroke_ts       (both in-process in the app,
//                                                                              so the SC-2 delta is exact and
//                                                                              immune to launch/focus jitter)
//
// Synthetic input — IMPORTANT honest caveat (PRD R3-mi10 reduced-scope leg). SPIKE-1 drove the genuine
// xdotool/XTEST OS-keystroke→focused-editor path, but that worked because the spike app's editor was the
// window's sole focusable content and got default focus. HoardPad's editor is nested, and the headless
// X11 backend on the measurement host REJECTS programmatic keyboard focus (Avalonia Focus() returns false
// even with the window X-active under its window manager), so an OS-level key cannot be routed into the editing surface here. The
// app therefore fires the synthetic startup keystroke by inserting one character directly into the editor
// document on the UI thread — a GENUINE editor TextChanged→re-colorize/preview-render→named-pipe-stop path
// (architecture §3.5); only the OS-keystroke focus-routing step is bypassed. This is the R3-mi10 reduced-
// scope input leg, surfaced honestly. Native Wayland input also stays Deferred (SPIKE-1 non-confirmation 2).
//
// NFR-5 idle RSS: a single published instance is launched, left to settle, and its /proc/<pid>/status
// VmRSS sampled (the gate-comparable single-instance figure, SPIKE-1 §D).
//
// MEASURES linux-x64 ONLY. win-x64 perf is Deferred (architecture §7.6) — this launcher builds for both
// RIDs but RUNS on linux-x64 (it relies on /proc). It never asserts a win-x64 number.
//
// Usage:
//   HoardPad.Bench                      — full campaign (SC-1 + SC-2 + NFR-5) against the default published app
//   HoardPad.Bench --app <path> --file <path> --trials <n>
//   HoardPad.Bench rss   --app <path> --file <path>

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;

namespace HoardPad.Bench;

internal static class Program
{
    // SPIKE-1-rebaselined gates (PRD SC-1/SC-2; architecture §3.5 ceilings).
    private const double Sc1TargetMs = 843.5;   // SC-1 cold-launch p90 target.
    private const double Sc2TargetMs = 209.0;   // SC-2 keystroke→render p90 target.
    private const double Nfr5TargetMb = 400.0;  // NFR-5 single-instance idle RSS.

    // X display and auth file for the bench. Overridable via HOARDPAD_BENCH_DISPLAY / HOARDPAD_BENCH_XAUTHORITY;
    // the fallbacks are display :99 and ~/.Xauthority. Deliberately NOT the inherited DISPLAY/XAUTHORITY: a
    // terminal on a desktop session inherits the live display, and the bench injects synthetic input and
    // launches the app there. Point the override at a dedicated test display only.
    private static readonly string Display =
        Environment.GetEnvironmentVariable("HOARDPAD_BENCH_DISPLAY") is { Length: > 0 } display ? display : ":99";

    private static readonly string XAuthority =
        Environment.GetEnvironmentVariable("HOARDPAD_BENCH_XAUTHORITY") is { Length: > 0 } xauthority
            ? xauthority
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Xauthority");

    // The published linux-x64 R2R app (M10.1 output). Overridable via --app.
    private static readonly string DefaultAppPath = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "src", "HoardPad.App", "bin", "Release", "net8.0", "linux-x64", "publish", "HoardPad.App"));

    // The representative ~46 KB fixture reused from SPIKE-1; falls back to a generated one if absent.
    private static readonly string DefaultFixturePath = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "spikes", "spike-1", "fixtures", "representative-50kb.md"));

    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("HoardPad.Bench RUNS on linux-x64 only (win-x64 perf is Deferred, architecture §7.6).");
            return 2;
        }

        string appPath = GetArg(args, "--app") ?? DefaultAppPath;
        string filePath = GetArg(args, "--file") ?? EnsureFixture();
        int trials = int.TryParse(GetArg(args, "--trials"), out int t) ? t : 10;

        if (!File.Exists(appPath))
        {
            Console.Error.WriteLine($"Published app not found: {appPath}");
            Console.Error.WriteLine("Publish it first: dotnet publish src/HoardPad.App/HoardPad.App.csproj -c Release -r linux-x64 -p:PublishReadyToRun=true");
            return 1;
        }

        EnsureXvfb();

        try
        {
            Console.WriteLine("=== HoardPad.Bench — SC-1 / SC-2 / NFR-5 (linux-x64; win-x64 perf Deferred) ===");
            Console.WriteLine($"App  : {appPath}");
            Console.WriteLine($"File : {filePath} ({new FileInfo(filePath).Length / 1024} KB)");
            Console.WriteLine($"Input: app-injected synthetic keystroke → genuine editor TextChanged (PRD R3-mi10 reduced-scope; headless X11 rejects programmatic focus)");
            Console.WriteLine();

            string mode = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "all";
            return mode switch
            {
                "rss" => await RunRss(appPath, filePath),
                _ => await RunCampaign(appPath, filePath, trials),
            };
        }
        finally
        {
            // Deterministic teardown on the normal/exception exit path — the ProcessExit handler registered
            // in EnsureXvfb() is only the backstop for hard exits (Ctrl-C/SIGINT). No-op if EnsureXvfb() found
            // a server already serving Display (i.e. this process never spawned one) — see EnsureXvfb().
            if (_spawnedXvfb is not null)
                Kill(_spawnedXvfb);
        }
    }

    // ---------- full campaign: SC-1 + SC-2 (one keystroke trial yields both) then NFR-5 ----------

    private static async Task<int> RunCampaign(string appPath, string filePath, int trials)
    {
        Console.WriteLine($"# Launch trials: {trials}  (each yields one SC-1 and one SC-2 sample)");
        Console.WriteLine("trial\tsc1_cold_ms\tsc2_keystroke_ms");

        var sc1 = new List<double>(trials);
        var sc2 = new List<double>(trials);

        for (int i = 0; i < trials; i++)
        {
            (double? cold, double? key) = await RunOneTrial(appPath, filePath, i + 1);
            if (cold is { } c && key is { } k)
            {
                sc1.Add(c);
                sc2.Add(k);
                Console.WriteLine($"{i + 1}\t{c:F1}\t{k:F1}");
            }
            else
            {
                Console.Error.WriteLine($"trial {i + 1}: FAILED (timeout or echo not received)");
            }
        }

        Console.WriteLine();
        ReportGate("SC-1 cold-launch->first-render-ready", sc1, Sc1TargetMs);
        ReportGate("SC-2 keystroke->render", sc2, Sc2TargetMs);

        Console.WriteLine();
        await RunRss(appPath, filePath);

        // Exit non-zero if any non-Deferred gate is MISSED, so a CI-less run still signals honestly.
        bool sc1Ok = sc1.Count > 0 && Percentile(sc1, 90) <= Sc1TargetMs;
        bool sc2Ok = sc2.Count > 0 && Percentile(sc2, 90) <= Sc2TargetMs;
        return sc1Ok && sc2Ok ? 0 : 3;
    }

    /// <summary>
    /// One launch trial. Returns (SC-1 cold ms, SC-2 keystroke ms). The app emits exactly one 24-byte echo
    /// (MA15) carrying the in-process keystroke instant + the TextChanged stop instant + the frequency.
    /// SC-1 = stop − this-process-start; SC-2 = stop − keystroke (both app instants, exact).
    /// </summary>
    private static async Task<(double?, double?)> RunOneTrial(string appPath, string filePath, int trialIndex)
    {
        string pipeName = $"hoardpad_bench_{Guid.NewGuid():N}";

        // START (SC-1): immediately before Process.Start (same host monotonic Stopwatch domain as the app).
        long startTs = Stopwatch.GetTimestamp();
        using Process proc = LaunchApp(appPath, filePath, pipeName);

        using var pipeClient = new NamedPipeClientStream(".", pipeName, PipeDirection.In, PipeOptions.Asynchronous);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            // Connect to the app's echo server (it starts listening early in OnFrameworkInitializationCompleted).
            bool connected = await TryConnect(pipeClient, cts.Token);
            if (!connected)
            {
                Console.Error.WriteLine($"  trial {trialIndex}: could not connect to echo pipe within 20s");
                Kill(proc);
                return (null, null);
            }

            // STOP: read the 24-byte echo (the app fires its own synthetic keystroke once the editor is up).
            (long? keystrokeTs, long? stopTs, long? freq) = await ReadEcho(pipeClient, TimeSpan.FromSeconds(15));
            if (keystrokeTs is null || stopTs is null || freq is null)
            {
                Console.Error.WriteLine($"  trial {trialIndex}: echo not received");
                Kill(proc);
                return (null, null);
            }

            double sc1Ms = (stopTs.Value - startTs) * 1000.0 / freq.Value;
            double sc2Ms = (stopTs.Value - keystrokeTs.Value) * 1000.0 / freq.Value;
            Kill(proc);
            return (sc1Ms, sc2Ms);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  trial {trialIndex}: {ex.Message}");
            Kill(proc);
            return (null, null);
        }
    }

    // ---------- NFR-5 single-instance idle RSS ----------

    private static async Task<int> RunRss(string appPath, string filePath)
    {
        Console.WriteLine("# NFR-5 — single-instance idle RSS (gate-comparable, SPIKE-1 §D)");
        string pipeName = $"hoardpad_bench_rss_{Guid.NewGuid():N}";
        using Process proc = LaunchApp(appPath, filePath, pipeName);

        // Connect + let the app reach a steady idle state (window up, file open, one synthetic keystroke
        // self-fired), drain the echo, then settle 6 s before sampling RSS.
        using var pipeClient = new NamedPipeClientStream(".", pipeName, PipeDirection.In, PipeOptions.Asynchronous);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        bool connected = await TryConnect(pipeClient, cts.Token);
        if (connected)
            _ = await ReadEcho(pipeClient, TimeSpan.FromSeconds(15));

        await Task.Delay(TimeSpan.FromSeconds(6));

        long rssKb = ReadVmRssKb(proc.Id);
        double rssMb = rssKb / 1024.0;
        int pid = proc.Id;
        Kill(proc);

        bool ok = rssKb > 0 && rssMb <= Nfr5TargetMb;
        Console.WriteLine($"  idle RSS = {rssMb:F1} MB  (PID {pid})");
        Console.WriteLine($"  NFR-5 (<= {Nfr5TargetMb:F0} MB single-instance): {(ok ? "MET" : "MISSED")}");
        Console.WriteLine();
        return 0;
    }

    // ---------- launching ----------

    private static Process LaunchApp(string appPath, string filePath, string pipeName)
    {
        var psi = new ProcessStartInfo(appPath)
        {
            UseShellExecute = false,
            Environment =
            {
                ["DISPLAY"] = Display,
                ["XAUTHORITY"] = XAuthority,
                // The App-side bench instrumentation is gated on these (inert without them).
                ["HOARDPAD_BENCH_PIPE"] = pipeName,
                ["HOARDPAD_BENCH_FILE"] = filePath,
            },
        };
        return Process.Start(psi) ?? throw new InvalidOperationException("failed to start published app");
    }

    private static async Task<bool> TryConnect(NamedPipeClientStream client, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await client.ConnectAsync(500, ct);
                return true;
            }
            catch (TimeoutException) { /* app not listening yet — keep polling */ }
            catch (OperationCanceledException) { return false; }
        }
        return false;
    }

    /// <summary>Read the 24-byte echo: (keystroke instant, TextChanged stop instant, Stopwatch.Frequency).</summary>
    private static async Task<(long?, long?, long?)> ReadEcho(NamedPipeClientStream client, TimeSpan timeout)
    {
        var buf = new byte[24];
        using var cts = new CancellationTokenSource(timeout);
        int read = 0;
        try
        {
            while (read < 24)
            {
                int n = await client.ReadAsync(buf.AsMemory(read), cts.Token);
                if (n == 0)
                    break;
                read += n;
            }
        }
        catch (OperationCanceledException) { /* fall through to the short-read check */ }

        if (read < 24)
            return (null, null, null);

        long keystrokeTs = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(0, 8));
        long stopTs = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(8, 8));
        long freq = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(16, 8));
        return (keystrokeTs, stopTs, freq);
    }

    private static void Kill(Process proc)
    {
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(3000);
            }
        }
        catch { /* best-effort teardown */ }
    }

    // ---------- Xvfb (the app needs a display to create its window) ----------

    // Set only when THIS process spawns an Xvfb server (never set on the early-return path below, i.e. never
    // set for the pre-existing :99 server). Teardown (Main's finally + the ProcessExit backstop) is a no-op
    // while this stays null, so a pre-existing server is never touched.
    private static Process? _spawnedXvfb;

    /// <summary>
    /// Ensure a headless X server is serving <see cref="Display"/> so the published Avalonia app can create
    /// its window (SPIKE-1 environment). No window manager or xdotool is needed: the synthetic keystroke is
    /// injected by the app itself into the editor document (see BenchHarness), so the bench never has to
    /// route OS input or focus a window.
    /// </summary>
    private static void EnsureXvfb()
    {
        // The X server exposes a unix socket at /tmp/.X11-unix/X<n>. If it exists, a server is already up.
        string socket = $"/tmp/.X11-unix/X{Display.TrimStart(':').Split('.')[0]}";
        if (File.Exists(socket))
            return;

        Console.WriteLine($"# starting Xvfb on {Display} ...");
        var psi = new ProcessStartInfo("Xvfb")
        {
            UseShellExecute = false,
            Environment = { ["XAUTHORITY"] = XAuthority },
        };
        psi.ArgumentList.Add(Display);
        psi.ArgumentList.Add("-screen");
        psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("1920x1080x24");
        psi.ArgumentList.Add("-auth");
        psi.ArgumentList.Add(XAuthority);
        _spawnedXvfb = Process.Start(psi);

        // Hard-exit backstop (Ctrl-C/SIGINT etc.) — registered only here, once, at the point of a successful
        // spawn. A self-spawned Xvfb must never outlive this bench process: an orphaned Xvfb is adopted by
        // the init system and its stale /tmp/.X11-unix/ socket can confuse remote-desktop display discovery.
        // Never touches a pre-existing server — _spawnedXvfb is only non-null when we spawned one.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (_spawnedXvfb is not null)
                Kill(_spawnedXvfb);
        };

        Thread.Sleep(1500); // let the server come up before the first launch.
    }

    // ---------- /proc RSS ----------

    private static long ReadVmRssKb(int pid)
    {
        try
        {
            foreach (string line in File.ReadAllLines($"/proc/{pid}/status"))
            {
                if (line.StartsWith("VmRSS:", StringComparison.Ordinal))
                {
                    string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && long.TryParse(parts[1], out long kb))
                        return kb;
                }
            }
        }
        catch { /* process gone — non-fatal */ }
        return -1;
    }

    // ---------- reporting / stats ----------

    private static void ReportGate(string label, List<double> values, double targetMs)
    {
        if (values.Count == 0)
        {
            Console.WriteLine($"{label}: NO DATA (0 successful trials)");
            return;
        }
        double p50 = Percentile(values, 50);
        double p90 = Percentile(values, 90);
        double p95 = Percentile(values, 95);
        bool met = p90 <= targetMs;
        Console.WriteLine($"{label}");
        Console.WriteLine($"  n={values.Count}  p50={p50:F1}ms  p90={p90:F1}ms  p95={p95:F1}ms  " +
                          $"min={values.Min():F1}ms  max={values.Max():F1}ms");
        Console.WriteLine($"  target p90 <= {targetMs:F1}ms  ->  {(met ? "MET" : "MISSED")}  " +
                          $"(x1.5 ceiling {targetMs * 1.5:F1}ms)");
    }

    private static double Percentile(List<double> values, int p)
    {
        if (values.Count == 0)
            return 0;
        var sorted = values.OrderBy(x => x).ToList();
        double idx = p / 100.0 * (sorted.Count - 1);
        int lo = (int)idx;
        int hi = Math.Min(lo + 1, sorted.Count - 1);
        return sorted[lo] + (idx - lo) * (sorted[hi] - sorted[lo]);
    }

    private static string? GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name)
                return args[i + 1];
        return null;
    }

    /// <summary>The representative ~46 KB fixture, or a generated one written to the temp dir.</summary>
    private static string EnsureFixture()
    {
        if (File.Exists(DefaultFixturePath))
            return DefaultFixturePath;

        string generated = Path.Combine(Path.GetTempPath(), "hoardpad_bench_fixture.md");
        if (!File.Exists(generated))
        {
            using var w = new StreamWriter(generated);
            w.WriteLine("# HoardPad bench fixture");
            for (int i = 0; i < 600; i++)
            {
                w.WriteLine($"## Section {i}");
                w.WriteLine($"Paragraph {i} with some **bold** and `code` and a [link](https://example.com).");
                w.WriteLine("- item one");
                w.WriteLine("- item two");
                w.WriteLine("```csharp");
                w.WriteLine($"int x{i} = {i};");
                w.WriteLine("```");
            }
        }
        return generated;
    }
}
