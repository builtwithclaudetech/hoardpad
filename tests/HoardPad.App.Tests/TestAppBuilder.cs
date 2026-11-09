// TestAppBuilder.cs — the Skia-headless Avalonia test application (architecture §1.6).
//
// Wires Avalonia.Headless.XUnit's [AvaloniaFact] to a real Skia-rendering headless platform:
// UseSkia() + UseHeadlessDrawing=false produces actual pixel output (not the no-draw fake
// backend), which is the "runs Skia-headless" M6.1 done-criterion. The same App type the
// product uses is bootstrapped, so the bootstrap test exercises the real app graph.

using Avalonia;
using Avalonia.Headless;
using HoardPad.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace HoardPad.App.Tests;

/// <summary>The headless Skia AppBuilder backing every <c>[AvaloniaFact]</c> in this assembly.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HoardPad.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                // Real Skia rendering rather than the no-draw fake backend ("Skia-headless").
                UseHeadlessDrawing = false,
            })
            .WithInterFont();
}
