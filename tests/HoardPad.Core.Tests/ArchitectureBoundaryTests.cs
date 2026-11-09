using System.Reflection;
using NetArchTest.Rules;
using Xunit;

namespace HoardPad.Core.Tests;

/// <summary>
/// Part 2 of M0.2 — mechanical C-9 boundary enforcement (architecture §2.1, §2.3).
/// Asserts the layering invariants over the BUILT assemblies (NetArchTest reads the
/// emitted metadata via Mono.Cecil — evidence-based, not stubbed):
///   1. HoardPad.Core references no HoardPad.Host assembly   (§2.1 invariant 2 — no Core → Host).
///   2. HoardPad.Core references no Avalonia assembly         (§2.1 invariant 1 / 4).
///   3. HoardPad.Core.Ai and HoardPad.Core.Checklist do not reference each other (§2.1 invariant 3).
///   4. MF2.1 — System.Diagnostics.Stopwatch is confined to the HoardPad.App.Bench namespace
///      across all five production assemblies (architecture §3.5/§8.1, M10.2 finding L-2).
///
/// The five product assemblies are loaded by simple name from the test output dir
/// (the test-only ProjectReferences in this .csproj copy them there) — they expose
/// only internal types, so a typeof(...) anchor is unavailable; loading by name is
/// the evidence-based handle NetArchTest scans.
/// </summary>
public sealed class ArchitectureBoundaryTests
{
    private static Assembly Load(string simpleName) => Assembly.Load(simpleName);

    private static readonly Assembly Core = Load("HoardPad.Core");
    private static readonly Assembly CoreAi = Load("HoardPad.Core.Ai");
    private static readonly Assembly CoreChecklist = Load("HoardPad.Core.Checklist");
    private static readonly Assembly Host = Load("HoardPad.Host");
    private static readonly Assembly App = Load("HoardPad.App");

    [Fact]
    public void CoreDoesNotReferenceHost()
    {
        TestResult result = Types.InAssembly(Core)
            .ShouldNot()
            .HaveDependencyOn("HoardPad.Host")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe("HoardPad.Core", "HoardPad.Host", result));
    }

    [Fact]
    public void CoreDoesNotReferenceAvalonia()
    {
        TestResult result = Types.InAssembly(Core)
            .ShouldNot()
            .HaveDependencyOn("Avalonia")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe("HoardPad.Core", "Avalonia", result));
    }

    [Theory]
    [InlineData("HoardPad.Core")]
    [InlineData("HoardPad.Core.Ai")]
    [InlineData("HoardPad.Core.Checklist")]
    public void CoreFamilyDoesNotReferenceUtfUnknown(string assemblyName)
    {
        // OQ-ARCH-4 / §2.1 invariant 1 (mechanical guard): the UTF.Unknown charset package lives
        // ONLY behind the IEncodingDetector seam in HoardPad.Host (EncodingDetector). It must never
        // leak into any Core assembly — the namespace it ships under is "UtfUnknown". This is the same
        // C-9 build-metadata enforcement as the no-Host / no-Avalonia facts above.
        TestResult result = Types.InAssembly(Load(assemblyName))
            .ShouldNot()
            .HaveDependencyOn("UtfUnknown")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(assemblyName, "UtfUnknown", result));
    }

    [Fact]
    public void CoreAiDoesNotReferenceCoreChecklist()
    {
        TestResult result = Types.InAssembly(CoreAi)
            .ShouldNot()
            .HaveDependencyOn("HoardPad.Core.Checklist")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe("HoardPad.Core.Ai", "HoardPad.Core.Checklist", result));
    }

    [Fact]
    public void CoreChecklistDoesNotReferenceCoreAi()
    {
        TestResult result = Types.InAssembly(CoreChecklist)
            .ShouldNot()
            .HaveDependencyOn("HoardPad.Core.Ai")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe("HoardPad.Core.Checklist", "HoardPad.Core.Ai", result));
    }

    /// <summary>
    /// Pins the exact set of <c>InternalsVisibleTo</c> grantees on <c>HoardPad.Core</c>:
    /// only <c>HoardPad.Host</c> (sole runtime producer of <see cref="NormcasedPath"/>) and
    /// <c>HoardPad.Core.Tests</c> (regression access to <c>FromResolved</c>). Any future
    /// widening — e.g. granting <c>Core.Ai</c> or <c>App</c> — is a contract violation caught here.
    /// </summary>
    [Fact]
    public void CoreInternalsVisibleToIsExactlyHostAndCoreTests()
    {
        // InternalsVisibleToAttribute is emitted on the assembly; read it via reflection.
        System.Runtime.CompilerServices.InternalsVisibleToAttribute[] attrs =
            Core.GetCustomAttributes<System.Runtime.CompilerServices.InternalsVisibleToAttribute>()
                .ToArray();

        HashSet<string> grantees = attrs
            .Select(a => a.AssemblyName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        HashSet<string> expected = new(StringComparer.OrdinalIgnoreCase)
        {
            "HoardPad.Host",
            "HoardPad.Core.Tests",
        };

        Assert.True(
            grantees.SetEquals(expected),
            $"HoardPad.Core InternalsVisibleTo grantees must be exactly {{HoardPad.Host, HoardPad.Core.Tests}}. " +
            $"Actual: {{{string.Join(", ", grantees.OrderBy(s => s))}}}. " +
            "Adding a grantee requires a reviewer sign-off (architecture §1.2 / NormcasedPath sole-producer rule).");
    }

    /// <summary>
    /// MF2.1 — Stopwatch-confinement guard (M10.2 finding L-2, architecture §3.5/§8.1).
    /// <c>System.Diagnostics.Stopwatch</c> is the sanctioned non-<see cref="System.TimeProvider"/>
    /// measurement clock in the product, but its use is ONLY legitimate in the bench surface:
    /// <list type="bullet">
    ///   <item><description><c>HoardPad.App.Bench</c> namespace (inside HoardPad.App) — the named-pipe
    ///     echo instrumentation (inert in every non-bench run).</description></item>
    ///   <item><description><c>harness/HoardPad.Bench</c> — the external perf-launcher (a separate
    ///     assembly, not a shipped production binary).</description></item>
    /// </list>
    /// This fact asserts that across ALL FIVE production assemblies (Core, Core.Ai, Core.Checklist,
    /// Host, App), NO type OUTSIDE the <c>HoardPad.App.Bench</c> namespace has a dependency on
    /// <c>System.Diagnostics.Stopwatch</c>. A stray <c>Stopwatch</c> in a product timer, debounce, or
    /// startup path would silently bypass the deterministic <see cref="System.TimeProvider"/> discipline;
    /// this guard catches it mechanically at the IL level (NetArchTest / Mono.Cecil).
    ///
    /// Non-vacuity was verified (2026-06-24): planting <c>Stopwatch.StartNew()</c> in a non-bench
    /// production type turned this test RED; removing the plant returned it GREEN.
    /// </summary>
    [Fact]
    public void StopwatchConfinedToBenchSurface()
    {
        // The five production assemblies, checked as a single pool. NetArchTest detects IL-level type
        // dependencies, so any method body that references Stopwatch registers as a dependency on
        // System.Diagnostics.Stopwatch — compile-time-removed code does not escape the check.
        Assembly[] productionAssemblies = [Core, CoreAi, CoreChecklist, Host, App];

        // Collect every type from ALL production assemblies, then filter to those that live OUTSIDE
        // the bench surface (HoardPad.App.Bench). The bench types are the ONE sanctioned exception;
        // everything else must have zero dependency on System.Diagnostics.Stopwatch.
        TestResult result = Types.InAssemblies(productionAssemblies)
            .That()
            .DoNotResideInNamespace("HoardPad.App.Bench")
            .ShouldNot()
            .HaveDependencyOn("System.Diagnostics.Stopwatch")
            .GetResult();

        string offenders = result.FailingTypeNames is { Count: > 0 }
            ? string.Join(", ", result.FailingTypeNames)
            : "(none reported)";

        Assert.True(
            result.IsSuccessful,
            $"System.Diagnostics.Stopwatch must only appear inside the HoardPad.App.Bench namespace " +
            $"(architecture §3.5/§8.1, MF2.1). Stopwatch on a product timer/debounce/startup path " +
            $"silently bypasses the TimeProvider discipline. Offending types: {offenders}.");
    }

    private static string Describe(string from, string forbidden, TestResult result)
    {
        string offenders = result.FailingTypeNames is { Count: > 0 }
            ? string.Join(", ", result.FailingTypeNames)
            : "(none reported)";
        return $"{from} must not depend on {forbidden} (C-9 / architecture §2.1). Offending types: {offenders}.";
    }
}
