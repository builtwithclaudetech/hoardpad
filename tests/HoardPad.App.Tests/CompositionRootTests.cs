// CompositionRootTests.cs — the M6.1 DI-resolution smoke test (architecture §8.1).
//
// Builds the EXACT production registration set (CompositionRoot.ConfigureServices) and asserts
// every HoardPad.Core seam abstraction resolves to its intended HoardPad.Host concrete. This is
// the "composition root resolves all seams" done-criterion. No Avalonia UI thread is required —
// the seam map is plain DI — so these are ordinary [Fact]s (the Bootstrap_* Skia-headless test
// covers the app-graph wiring). The verify filter matches the class name "Composition".
// Method names are PascalCase to satisfy CA1707 under TreatWarningsAsErrors (house convention).

using HoardPad.App.Hosting;
using HoardPad.App.ViewModels;
using HoardPad.Core.Host.Abstractions;
using HoardPad.Host;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class CompositionRootTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        CompositionRoot.ConfigureServices(services);
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    [Fact]
    public void ValidateOnBuildSucceedsForTheProductionRegistrationSet()
    {
        // BuildServiceProvider with ValidateOnBuild throws if any registration is unresolvable.
        using ServiceProvider provider = BuildProvider();
        Assert.NotNull(provider);
    }

    [Theory]
    [InlineData(typeof(IAtomicFileStore), typeof(AtomicFileStore))]
    [InlineData(typeof(IPathResolver), typeof(PathResolver))]
    [InlineData(typeof(IAppDataPaths), typeof(AppDataPaths))]
    [InlineData(typeof(IAgentBackend), typeof(AgentBackend))]
    [InlineData(typeof(TimeProvider), typeof(SystemTimeProvider))]
    [InlineData(typeof(IToolPermissionHookHost), typeof(ToolPermissionHookHost))]
    [InlineData(typeof(ICapStateStore), typeof(CapStateStore))]
    public void EachSeamResolvesToItsHostConcrete(Type abstraction, Type expectedConcrete)
    {
        using ServiceProvider provider = BuildProvider();

        object? resolved = provider.GetService(abstraction);

        Assert.NotNull(resolved);
        Assert.IsType(expectedConcrete, resolved);
    }

    [Fact]
    public void EncodingDetectorResolvesToTheHostUtfUnknownDetector()
    {
        // M8.3: the production seam binds the UTF.Unknown-backed Host EncodingDetector (it supersedes
        // the M6.1 interim UtfDefaultEncodingDetector). A UTF-8 BOM sniff returns utf-8 at confidence 1.0.
        using ServiceProvider provider = BuildProvider();

        var detector = provider.GetRequiredService<IEncodingDetector>();
        byte[] utf8Bom = { 0xEF, 0xBB, 0xBF, 0x48, 0x69 };
        EncodingCandidate candidate = detector.Detect(utf8Bom);

        Assert.IsType<EncodingDetector>(detector);
        Assert.Equal("utf-8", candidate.EncodingName);
        Assert.Equal(1.0, candidate.Confidence);
    }

    [Fact]
    public void SeamsAreSingletons()
    {
        // §8.1: the seams are the single place platform meets domain — one instance each.
        using ServiceProvider provider = BuildProvider();

        Assert.Same(
            provider.GetRequiredService<IAtomicFileStore>(),
            provider.GetRequiredService<IAtomicFileStore>());
        Assert.Same(
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public void ProductionInjectsARealFileGrantsWriter()
    {
        // PANEL-A2 (feature-ai-multidoc-panel §5.3 Path A) wiring guard: the production seam must bind the
        // Host FileGrantsWriter so an armed multi-doc turn actually delivers HOARDPAD_FILE_GRANTS to the
        // claude -p hook. This project has been bitten by comp-root-never-injected dead features (a green
        // fake-injected test while production wires null → the feature is silently inert). If this ever
        // reverts, the Archetype-A grant leg is dead in the real app despite passing unit tests.
        using ServiceProvider provider = BuildProvider();

        var writer = provider.GetRequiredService<IFileGrantsWriter>();
        Assert.IsType<HoardPad.Host.FileGrantsWriter>(writer);
    }

    [Fact]
    public void ProductionPanelHasReplayDependenciesWired()
    {
        // MF1.4 regression guard: the production AiPanelViewModel must have all three FR-39 replay
        // seams ctor-injected (IBeforeImageReader, IAtomicFileStore, IAppDataPaths). If CompositionRoot
        // ever stops passing them, ReplayDependenciesWired is false, CanApplyPending is permanently false,
        // and the Apply button is silently dead in the real app despite tests passing. This test makes
        // the mis-wiring a build-gate failure rather than a silent dead-feature.
        using ServiceProvider provider = BuildProvider();

        AiPanelViewModel panel = provider.GetRequiredService<AiPanelViewModel>();

        Assert.True(panel.ReplayDependenciesWired,
            "The production AiPanelViewModel must have beforeImageReader + replayStore + replayPaths " +
            "ctor-injected by CompositionRoot. Without them, the FR-39 Apply button is permanently " +
            "disabled in the real app — a dead feature invisible to unit tests that inject seams directly.");
    }

    [Fact]
    public void ProductionInjectsARealUnionBeforeImageReader()
    {
        // OQ-MD-5 option ii (feature-ai-multidoc-panel §5.5) wiring guard: the production seam must bind the
        // Host UnionBeforeImageReader so a parked out-of-folder GRANTED op can read its before-image at replay
        // and apply through the union-gated service. Without it, a granted replay falls back to the root-only
        // reader ⇒ Refused ⇒ Blocked — the feature is silently inert (the dead-feature trap this project has
        // been bitten by). If this reverts, the granted-replay path is dead despite passing unit tests.
        using ServiceProvider provider = BuildProvider();

        var reader = provider.GetRequiredService<IUnionBeforeImageReader>();
        Assert.IsType<HoardPad.Host.UnionBeforeImageReader>(reader);
    }

    [Fact]
    public void ProductionPanelHasUnionReplayReaderWired()
    {
        // The production AiPanelViewModel must have the union-aware before-image reader ctor-injected. If
        // CompositionRoot stops passing it, UnionReplayReaderWired is false and a granted out-of-folder replay
        // is silently Blocked in the real app despite the granted-replay unit tests passing (they inject the
        // union reader directly). This makes the mis-wiring a build-gate failure.
        using ServiceProvider provider = BuildProvider();

        AiPanelViewModel panel = provider.GetRequiredService<AiPanelViewModel>();

        Assert.True(panel.UnionReplayReaderWired,
            "The production AiPanelViewModel must have unionBeforeImageReader ctor-injected by " +
            "CompositionRoot. Without it, a parked out-of-folder granted op is Refused at replay — a dead " +
            "feature invisible to unit tests that inject the union reader directly.");
    }
}
