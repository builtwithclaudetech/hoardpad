// FolderRootResolutionTests.cs — FS-1 H1 frozen-oracle: the pure 3-tier resolver (FolderRootResolver) and
// the "My Documents" fallback resolution (MyDocumentsResolver), both exercised in isolation with no I/O.
// Class names contain "FolderRootResolution" so the verify filter "FolderRootResolution" resolves them.

using System;
using System.Collections.Generic;
using HoardPad.App.Session;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class FolderRootResolutionTierTests
{
    private static Func<string> NeverCalledMyDocuments() =>
        () => throw new InvalidOperationException("tier (iii) must not fire for this case.");

    // ----- Tier (i): a passed file wins regardless of tabs. -----

    [Theory]
    [InlineData("/proj/notes.txt", "/proj")]
    [InlineData("/home/you/deep/nested/file.md", "/home/you/deep/nested")]
    public void TierOneUsesTheFirstPassedFilesContainingFolder(string passedFile, string expectedRoot)
    {
        FolderRootResolution result = FolderRootResolver.Resolve(
            passedFiles: [passedFile],
            restoredTabsInOrder: [],
            myDocumentsResolver: NeverCalledMyDocuments());

        Assert.Equal(expectedRoot, result.Root);
        Assert.Equal(FolderRootTier.PassedFile, result.Tier);
        Assert.False(result.KeepFolderPaneCollapsed);
    }

    [Fact]
    public void TierOneUsesTheFIRSTPassedFileWhenMultipleAreGiven()
    {
        FolderRootResolution result = FolderRootResolver.Resolve(
            passedFiles: ["/first/dir/a.txt", "/second/dir/b.txt"],
            restoredTabsInOrder: [],
            myDocumentsResolver: NeverCalledMyDocuments());

        Assert.Equal("/first/dir", result.Root);
        Assert.Equal(FolderRootTier.PassedFile, result.Tier);
    }

    [Fact]
    public void TierOneWinsOverAnyRestoredTabsWithPaths()
    {
        FolderRootResolution result = FolderRootResolver.Resolve(
            passedFiles: ["/passed/dir/x.txt"],
            restoredTabsInOrder: [new TabPathFact("/restored/dir/y.txt")],
            myDocumentsResolver: NeverCalledMyDocuments());

        Assert.Equal("/passed/dir", result.Root);
        Assert.Equal(FolderRootTier.PassedFile, result.Tier);
    }

    // ----- Tier (ii): the first path-bearing restored tab, skipping scratch/untitled. -----

    [Fact]
    public void TierTwoUsesTheFirstPathBearingTabSkippingLeadingScratchTabs()
    {
        FolderRootResolution result = FolderRootResolver.Resolve(
            passedFiles: [],
            restoredTabsInOrder:
            [
                new TabPathFact(null),                          // scratch
                new TabPathFact(null),                          // scratch
                new TabPathFact("/proj/a/first.md"),             // FIRST path-bearing tab.
                new TabPathFact("/proj/b/second.md"),
            ],
            myDocumentsResolver: NeverCalledMyDocuments());

        Assert.Equal("/proj/a", result.Root);
        Assert.Equal(FolderRootTier.RestoredTab, result.Tier);
        Assert.False(result.KeepFolderPaneCollapsed);
    }

    [Fact]
    public void TierTwoTreatsAnEmptyStringPathTheSameAsNull()
    {
        // Defensive: a caller-supplied "" (not null) must be skipped exactly like a scratch buffer.
        FolderRootResolution result = FolderRootResolver.Resolve(
            passedFiles: [],
            restoredTabsInOrder: [new TabPathFact(""), new TabPathFact("/proj/real.md")],
            myDocumentsResolver: NeverCalledMyDocuments());

        Assert.Equal("/proj", result.Root);
        Assert.Equal(FolderRootTier.RestoredTab, result.Tier);
    }

    [Fact]
    public void TierTwoFallsThroughToTierThreeWhenNoTabHasAPath()
    {
        FolderRootResolution result = FolderRootResolver.Resolve(
            passedFiles: [],
            restoredTabsInOrder: [new TabPathFact(null), new TabPathFact(null)],
            myDocumentsResolver: () => "/my/documents");

        Assert.Equal("/my/documents", result.Root);
        Assert.Equal(FolderRootTier.MyDocumentsFallback, result.Tier);
        Assert.True(result.KeepFolderPaneCollapsed);
    }

    // ----- Tier (iii): neither a passed file nor a path-bearing tab. -----

    [Fact]
    public void TierThreeFiresForNoPassedFilesAndNoRestoredTabsAtAll()
    {
        FolderRootResolution result = FolderRootResolver.Resolve(
            passedFiles: [],
            restoredTabsInOrder: [],
            myDocumentsResolver: () => "/home/you/Documents");

        Assert.Equal("/home/you/Documents", result.Root);
        Assert.Equal(FolderRootTier.MyDocumentsFallback, result.Tier);
        Assert.True(result.KeepFolderPaneCollapsed);
    }

    [Fact]
    public void MyDocumentsResolverIsInvokedLazilyOnlyWhenTierThreeGenuinelyFires()
    {
        int callCount = 0;
        string MyDocs()
        {
            callCount++;
            return "/my/documents";
        }

        FolderRootResolver.Resolve(["/passed/file.txt"], [], MyDocs);
        Assert.Equal(0, callCount); // tier (i) fired — the resolver must never have been invoked.

        FolderRootResolver.Resolve([], [new TabPathFact("/restored/file.txt")], MyDocs);
        Assert.Equal(0, callCount); // tier (ii) fired — still never invoked.

        FolderRootResolver.Resolve([], [], MyDocs);
        Assert.Equal(1, callCount); // tier (iii) — invoked exactly once now.
    }

    [Fact]
    public void TierThreeAnchorsABareRelativeMyDocumentsResultToTheAppOwnedFallbackFolder()
    {
        // Hostile-review Warning #3/#4: the myDocumentsResolver can, in a genuinely atypical environment
        // (both SpecialFolder.MyDocuments and SpecialFolder.UserProfile empty), return a bare RELATIVE
        // string. FolderRootResolver.EnsureAbsolute must never let that become the AI write boundary —
        // it must anchor to a dedicated, app-owned "HoardPad-Fallback" subfolder under the system temp
        // directory (never the shared /tmp root itself), and the result must always be absolute.
        FolderRootResolution result = FolderRootResolver.Resolve(
            passedFiles: [],
            restoredTabsInOrder: [],
            myDocumentsResolver: () => "Documents"); // a bare relative string — the atypical-env case.

        Assert.True(Path.IsPathRooted(result.Root));
        Assert.Equal(FolderRootTier.MyDocumentsFallback, result.Tier);
        Assert.True(result.KeepFolderPaneCollapsed);
        Assert.EndsWith("HoardPad-Fallback", result.Root);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), result.Root, StringComparison.Ordinal);
        Assert.True(System.IO.Directory.Exists(result.Root)); // created if missing.
    }

    // ----- Property test: derived root == the first path-bearing tab's folder (tier ii), across orders. -----

    public static IEnumerable<object[]> TabOrderPermutations()
    {
        yield return new object[] { new[] { "/a/one.txt", "/b/two.txt", "/c/three.txt" }, 0 };
        yield return new object[] { new[] { "/a/one.txt", "/b/two.txt", "/c/three.txt" }, 1 };
        yield return new object[] { new[] { "/a/one.txt", "/b/two.txt", "/c/three.txt" }, 2 };
    }

    [Theory]
    [MemberData(nameof(TabOrderPermutations))]
    public void PropertyDerivedRootAlwaysEqualsTheFirstPathBearingTabsFolder(string[] paths, int firstPathBearingIndex)
    {
        // Build a tab list where every entry BEFORE firstPathBearingIndex is scratch (null path), and every
        // entry FROM firstPathBearingIndex onward carries a real path — the resolver must pick exactly the
        // one at firstPathBearingIndex, regardless of how many path-bearing tabs follow it.
        var tabs = new List<TabPathFact>();
        for (int i = 0; i < paths.Length; i++)
            tabs.Add(new TabPathFact(i < firstPathBearingIndex ? null : paths[i]));

        FolderRootResolution result = FolderRootResolver.Resolve([], tabs, NeverCalledMyDocuments());

        string expectedFolder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(paths[firstPathBearingIndex]))!;
        Assert.Equal(expectedFolder, result.Root);
        Assert.Equal(FolderRootTier.RestoredTab, result.Tier);
    }

    [Fact]
    public void ResolveThrowsOnNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() =>
            FolderRootResolver.Resolve(null!, [], () => "/x"));
        Assert.Throws<ArgumentNullException>(() =>
            FolderRootResolver.Resolve([], null!, () => "/x"));
        Assert.Throws<ArgumentNullException>(() =>
            FolderRootResolver.Resolve([], [], null!));
    }
}

public sealed class FolderRootResolutionMyDocumentsTests
{
    // The BUG-10 gap this closes: GetFolderPath(SpecialFolder.MyDocuments) returns "" on a bare Linux
    // account with no user-dirs.dirs — the fallback rule must trigger exactly then, and only then.

    [Theory]
    [InlineData("/home/you/Documents", "/home/you", "/home/you/Documents")]   // XDG-honored, non-empty — used as-is.
    [InlineData("/custom/xdg/Docs", "/home/you", "/custom/xdg/Docs")]           // a non-default XDG_DOCUMENTS_DIR — still used as-is.
    public void UsesTheApiResultVerbatimWhenNonEmpty(string apiResult, string userProfile, string expected)
    {
        bool ensureCalled = false;
        string root = MyDocumentsResolver.Resolve(apiResult, userProfile, _ => ensureCalled = true);

        Assert.Equal(expected, root);
        Assert.False(ensureCalled); // the fallback's ensure-exists delegate must never run on the happy path.
    }

    [Theory]
    [InlineData("", "/home/you", "/home/you/Documents")]           // the empty-string edge (BUG-10 gap) — no user-dirs.dirs.
    [InlineData("", "/root", "/root/Documents")]                     // a root-home account — same rule.
    public void FallsBackToUserProfileSlashDocumentsWhenTheApiResultIsEmpty(
        string apiResult, string userProfile, string expected)
    {
        string? ensuredPath = null;
        string root = MyDocumentsResolver.Resolve(apiResult, userProfile, p => ensuredPath = p);

        Assert.Equal(expected, root);
        Assert.Equal(expected, ensuredPath); // the fallback folder is the one passed to ensure-exists.
    }

    [Fact]
    public void FallbackAlwaysCallsEnsureDirectoryExistsExactlyOnce()
    {
        int callCount = 0;
        MyDocumentsResolver.Resolve(string.Empty, "/home/you", _ => callCount++);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void HappyPathNeverCallsEnsureDirectoryExists()
    {
        int callCount = 0;
        MyDocumentsResolver.Resolve("/home/you/Documents", "/home/you", _ => callCount++);
        Assert.Equal(0, callCount);
    }

    [Fact]
    public void ResolveThrowsOnNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => MyDocumentsResolver.Resolve(null!, "/x", _ => { }));
        Assert.Throws<ArgumentNullException>(() => MyDocumentsResolver.Resolve("", null!, _ => { }));
        Assert.Throws<ArgumentNullException>(() => MyDocumentsResolver.Resolve("", "/x", null!));
    }

    [Fact]
    public void ResolveLiveReturnsANonEmptyExistingDirectory()
    {
        // The live OS resolution end-to-end (real Environment + real Directory.CreateDirectory-if-missing).
        // Runs against whatever this machine actually reports — asserts the CONTRACT (non-empty, exists),
        // not a specific path, since that is environment-dependent by design.
        string root = MyDocumentsResolver.ResolveLive();

        Assert.False(string.IsNullOrEmpty(root));
        Assert.True(System.IO.Directory.Exists(root));
    }
}
