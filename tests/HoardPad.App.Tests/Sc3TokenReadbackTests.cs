// Sc3TokenReadbackTests.cs — the SC-3 acceptance gate (PRD §5 SC-3, FR-7 20-language coverage, A-6
// read-back harness). M9.2. Owner: unit-test-engineer.
//
// SC-3 read-back mechanism (B8, binding): drive the TextMateSharp tokenizer DIRECTLY, HEADLESS (no UI
// render), through the PRODUCTION HoardPadRegistryOptions overlay — the same IRegistryOptions the editor
// installs (EditorView.InstallTextMate(vm.RegistryOptions)) and the same scope resolution the app uses
// (HoardPadRegistryOptions.ResolveScopeForExtension, called per open file). Assert on the RETURNED SCOPE
// STACKS at a handful of MARKED REGIONS per language, reduced to COARSE token classes (Sc3CoarseTokenClass)
// — never exhaustive per-character scope matching.
//
// WHY THE PRODUCTION REGISTRY (not a stock parallel one): TOML is supplemental (not in stock
// TextMateSharp.Grammars); only HoardPadRegistryOptions overlays it (and resolves the .toml extension).
// Building a bare stock RegistryOptions here would make TOML fail — driving the app's registry is exactly
// what makes the supplemental grammar + lazy registration get exercised under the gate.
//
// SC-3 pass condition (PRD §5): all 20 fixtures assert their expected coarse classes at their marked
// regions AND — for each language whose grammar resolves — there are no un-tokenized fallback regions (the
// grammar genuinely loaded, not a plaintext fallback). The latter is asserted as: every marked region
// classifies to a real coarse class (non-null) AND the full sample carries at least one scope deeper than
// the bare root scope (proof the grammar tokenized, not a single whole-line default token).
//
// Assertions use raw xUnit Assert.* to match the project's existing test convention (EditorSurfaceTests),
// not FluentAssertions (not referenced in this project).

using System.Collections.Generic;
using System.Linq;
using HoardPad.App.Editor;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;
using Xunit;

namespace HoardPad.App.Tests;

/// <summary>SC-3 token read-back across all 20 FR-7 languages, headless, through the production registry.</summary>
public sealed class Sc3TokenReadbackTests
{
    // Theory data: one row per language fixture. The display name is the language so a failure points
    // straight at the offending grammar.
    public static IEnumerable<object[]> Languages() =>
        Sc3Fixtures.All.Select(f => new object[] { f.Language });

    [Theory]
    [MemberData(nameof(Languages))]
    public void EveryMarkedRegionTokenizesToItsExpectedCoarseClass(string language)
    {
        LanguageFixture fixture = Sc3Fixtures.All.Single(f => f.Language == language);

        // 1) PRODUCTION scope resolution — the same lookup EditorView/LanguageCatalog use per open file.
        var options = new HoardPadRegistryOptions(ThemeName.OneDark);
        string? scope = options.ResolveScopeForExtension(fixture.Extension);
        Assert.True(scope == fixture.ExpectedScope,
            $"{language} ({fixture.Extension}) must resolve to '{fixture.ExpectedScope}' through the production overlay; got '{scope ?? "(null)"}'.");

        // 2) Drive the REAL tokenizer through that same overlay (TOML loads its supplemental grammar here).
        var registry = new Registry(options);
        IGrammar grammar = registry.LoadGrammar(scope!);

        TokenizedSample lines = SampleTokenizer.Tokenize(grammar, fixture.Source);

        // 3) Every marked region reduces to its expected coarse class.
        foreach (MarkedRegion region in fixture.Regions)
        {
            IReadOnlyList<string> stack = lines.ScopeStackAt(fixture.Source, region);
            CoarseClass? actual = Sc3CoarseTokenClass.Classify(stack);

            Assert.True(actual == region.Expected,
                $"{language}: region '{region.Needle}' (#{region.Occurrence}) expected {region.Expected} but was "
                + $"{(actual?.ToString() ?? "(no coarse class)")}; scope stack = [{string.Join(" / ", stack)}].");
        }

        // 4) No un-tokenized fallback for this (resolving) grammar: the full sample carries a scope deeper
        //    than the bare root — proof the grammar genuinely tokenized rather than returning a plaintext /
        //    single-default-token fallback. (Step 3 already proved each marked region classified non-null.)
        bool hasNonDefaultScope = lines.AllTokenStacks
            .Any(s => s.Any(scopeName => scopeName != scope));
        Assert.True(hasNonDefaultScope,
            $"{language}: the grammar must produce language-specific scopes (no plaintext fallback).");
    }

    [Fact]
    public void All20Fr7LanguagesAreCovered()
    {
        // Guards against silently dropping a language from the fixture set — SC-3 requires all 20.
        string[] expected =
        {
            "Markdown", "HTML", "JavaScript", "CSS", "Python", "C#", "XML", "JSON", "SQL", "Rust",
            "TypeScript", "YAML", "TOML", "Shell", "PowerShell", "Dockerfile", "C", "C++", "Java", "Go",
        };

        string[] actual = Sc3Fixtures.All.Select(f => f.Language).ToArray();
        Assert.Equal(expected.OrderBy(x => x), actual.OrderBy(x => x));
        Assert.Equal(20, actual.Length);
        Assert.Equal(20, Sc3Fixtures.All.Select(f => f.Extension).Distinct().Count());
    }
}
