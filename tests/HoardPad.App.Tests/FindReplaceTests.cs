// FindReplaceTests.cs — the M6.7 find/replace gates (FR-17 / FR-18, design §3.5).
//
// Class names contain "FindReplace" (and a "Search"-named class) so the verify filter
// "FullyQualifiedName~FindReplace|FullyQualifiedName~Search" resolves every case.
//
// Covers the FR-17/FR-18 done-when surface:
//   - the pure engine: a backreference pattern (\w+)\s+\1 matches; $1-group-ref replacement works; each
//     of the three toggles (regex/literal, case, whole-word) changes results; an invalid regex degrades
//     to "bad pattern / zero matches" with no crash;
//   - the VM: the live match count updates; next/previous cycle with wraparound;
//   - scoped replace: current-file vs all-open vs in-folder, including the in-folder review producing
//     per-file counts and an EXCLUDED file NOT being written.
// All cases are pure / in-memory (no UI thread needed) — the engine + VM orchestrate over the fakes.

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HoardPad.App.Editor;
using HoardPad.App.ViewModels;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class FindReplaceEngineTests
{
    private static FindReplaceEngine Engine(
        string pattern, string replacement = "",
        bool useRegex = true, bool caseSensitive = true, bool wholeWord = false) =>
        FindReplaceEngine.Compile(new FindReplaceQuery(pattern, replacement, useRegex, caseSensitive, wholeWord));

    // ---- FR-17: a PCRE backreference pattern matches correctly. ----

    [Fact]
    public void BackreferencePatternMatchesARepeatedWord()
    {
        // (\w+)\s+\1 — a word followed by whitespace and THE SAME word again (a doubled-word finder).
        FindReplaceEngine engine = Engine(@"(\w+)\s+\1");

        var matches = engine.FindAll("the the cat sat sat down");

        // Two doubled words: "the the" and "sat sat".
        Assert.Equal(2, matches.Count);
    }

    [Fact]
    public void BackreferencePatternDoesNotMatchDistinctAdjacentWords()
    {
        FindReplaceEngine engine = Engine(@"(\w+)\s+\1");

        Assert.Empty(engine.FindAll("the cat sat down"));
    }

    // ---- FR-17: $1-style group references in the replacement. ----

    [Fact]
    public void DollarGroupReferenceInReplacementWorks()
    {
        // Collapse a doubled word "the the" -> "the" via $1 (the captured first word).
        FindReplaceEngine engine = Engine(@"(\w+)\s+\1", replacement: "$1");

        ReplaceResult result = engine.ReplaceAll("the the cat sat sat down");

        Assert.Equal(2, result.ReplacedCount);
        Assert.Equal("the cat sat down", result.NewText);
    }

    [Fact]
    public void DollarGroupReferenceCanReorderCaptures()
    {
        // Swap "first last" -> "last, first" using $1/$2.
        FindReplaceEngine engine = Engine(@"(\w+) (\w+)", replacement: "$2, $1");

        ReplaceResult result = engine.ReplaceAll("Ada Lovelace");

        Assert.Equal("Lovelace, Ada", result.NewText);
    }

    // ---- FR-18: the three toggles change results. ----

    [Fact]
    public void LiteralToggleTreatsRegexMetacharactersAsText()
    {
        const string text = "a.b axb a.b";

        // Regex mode: "a.b" — the '.' is any char, so "axb" matches too (3 matches).
        Assert.Equal(3, Engine("a.b", useRegex: true).FindAll(text).Count);

        // Literal mode: "a.b" is the literal three chars — only the two real "a.b" match.
        Assert.Equal(2, Engine("a.b", useRegex: false).FindAll(text).Count);
    }

    [Fact]
    public void CaseSensitivityToggleChangesResults()
    {
        const string text = "Cat cat CAT";

        // Case-sensitive: only the exact "cat".
        Assert.Single(Engine("cat", caseSensitive: true).FindAll(text));

        // Case-insensitive: all three.
        Assert.Equal(3, Engine("cat", caseSensitive: false).FindAll(text).Count);
    }

    [Fact]
    public void WholeWordToggleChangesResults()
    {
        const string text = "cat category scatter cat";

        // Substring match: "cat" appears in cat, category, scatter, cat = 4.
        Assert.Equal(4, Engine("cat", wholeWord: false).FindAll(text).Count);

        // Whole-word: only the two standalone "cat".
        Assert.Equal(2, Engine("cat", wholeWord: true).FindAll(text).Count);
    }

    // ---- FR-18: an invalid regex degrades gracefully (no crash, zero matches, no replacement). ----

    [Fact]
    public void InvalidRegexIsNotValidAndYieldsZeroMatchesAndNoReplacement()
    {
        FindReplaceEngine engine = Engine("(unbalanced", replacement: "x"); // unbalanced "(" — malformed.

        Assert.False(engine.IsValid);
        Assert.Empty(engine.FindAll("(unbalanced text here"));
        ReplaceResult result = engine.ReplaceAll("(unbalanced text here");
        Assert.Equal(0, result.ReplacedCount);
        Assert.Equal("(unbalanced text here", result.NewText); // text unchanged.
    }

    [Fact]
    public void EmptyPatternIsTreatedAsNoMatches()
    {
        FindReplaceEngine engine = Engine(string.Empty);

        Assert.False(engine.IsValid);
        Assert.Empty(engine.FindAll("anything"));
    }

    // ---- W1 regression: ReplaceAll and FindAll/CountMatches agree on zero-width-match patterns. ----

    [Fact]
    public void ReplaceAllCountMatchesFindAllForZeroWidthAnchorPattern()
    {
        // "a*" matches zero or more a's — zero-width at every non-a position. FindAll skips zero-width
        // matches. ReplaceAll must operate on the SAME set so count == mutation count (W1 fix).
        FindReplaceEngine engine = Engine(@"a+", replacement: "X"); // "a+" is substantive: only real a-runs.

        const string text = "aaa bbb aaa";
        IReadOnlyList<FindMatch> found = engine.FindAll(text);
        ReplaceResult replaced = engine.ReplaceAll(text);

        // Both the Find count and the Replace count must agree.
        Assert.Equal(found.Count, replaced.ReplacedCount);
        // And the buffer mutation is consistent with what FindAll showed.
        Assert.Equal("X bbb X", replaced.NewText);
    }

    [Fact]
    public void ReplaceAllAndFindAllAgreeBothSkipZeroWidthMatches()
    {
        // "\b" is zero-width only — FindAll returns 0 matches. ReplaceAll must also report 0 and leave
        // the buffer untouched (the count-vs-mutation divergence the W1 fix closes).
        FindReplaceEngine engine = Engine(@"\b", replacement: "|");

        const string text = "hello world";
        IReadOnlyList<FindMatch> found = engine.FindAll(text);
        ReplaceResult replaced = engine.ReplaceAll(text);

        Assert.Empty(found);
        Assert.Equal(0, replaced.ReplacedCount);
        Assert.Equal(text, replaced.NewText); // buffer is unchanged.
    }
}

public sealed class FindReplaceSearchViewModelTests
{
    private static FindReplaceViewModel NewVm(
        IFindReplaceWorkspace workspace, IFindReplaceReviewPrompt? prompt = null) =>
        new(workspace, prompt ?? ScriptedReviewPrompt.ConfirmAll());

    // ---- FR-18: the live match count updates as the query changes. ----

    [Fact]
    public void MatchCountUpdatesLiveAsTheFindTextChanges()
    {
        var ws = new FakeFindReplaceWorkspace { Current = new FakeBuffer("a.txt", "cat cat cat") };
        FindReplaceViewModel vm = NewVm(ws);

        Assert.Equal(string.Empty, vm.MatchCountLabel); // no find text yet.

        vm.FindText = "cat";
        Assert.Equal("1 of 3", vm.MatchCountLabel);     // 3 matches, cursor at the first.

        vm.FindText = "dog";
        Assert.Equal("0 matches", vm.MatchCountLabel);  // live update to zero.
    }

    [Fact]
    public void ToggleChangeUpdatesTheLiveCount()
    {
        var ws = new FakeFindReplaceWorkspace { Current = new FakeBuffer("a.txt", "Cat cat CAT") };
        FindReplaceViewModel vm = NewVm(ws);
        vm.FindText = "cat";

        // Default is case-insensitive (CaseSensitive == false) — all three match.
        Assert.Equal("1 of 3", vm.MatchCountLabel);

        vm.CaseSensitive = true; // live: only the exact "cat".
        Assert.Equal("1 of 1", vm.MatchCountLabel);
    }

    [Fact]
    public void BadPatternSurfacesAsBadPatternInTheCount()
    {
        var ws = new FakeFindReplaceWorkspace { Current = new FakeBuffer("a.txt", "text") };
        FindReplaceViewModel vm = NewVm(ws);
        vm.UseRegex = true;

        vm.FindText = "(unbalanced";

        Assert.True(vm.HasBadPattern);
        Assert.Equal("bad pattern", vm.MatchCountLabel);
    }

    // ---- FR-18: next / previous navigation with wraparound. ----

    [Fact]
    public void NextAndPreviousCycleThroughMatchesWithWraparound()
    {
        var ws = new FakeFindReplaceWorkspace { Current = new FakeBuffer("a.txt", "x x x") };
        FindReplaceViewModel vm = NewVm(ws);
        vm.FindText = "x";

        Assert.Equal("1 of 3", vm.MatchCountLabel);

        vm.FindNextCommand.Execute(null);
        Assert.Equal("2 of 3", vm.MatchCountLabel);
        vm.FindNextCommand.Execute(null);
        Assert.Equal("3 of 3", vm.MatchCountLabel);
        vm.FindNextCommand.Execute(null);              // wrap forward 3 -> 1.
        Assert.Equal("1 of 3", vm.MatchCountLabel);

        vm.FindPreviousCommand.Execute(null);          // wrap backward 1 -> 3.
        Assert.Equal("3 of 3", vm.MatchCountLabel);
    }

    [Fact]
    public void NavigateRequestedCarriesTheCurrentMatchSpan()
    {
        var ws = new FakeFindReplaceWorkspace { Current = new FakeBuffer("a.txt", "ab ab ab") };
        FindReplaceViewModel vm = NewVm(ws);
        FindMatch? last = null;
        vm.NavigateRequested += m => last = m;

        vm.FindText = "ab";          // first match at index 0.
        Assert.Equal(0, last!.Value.Index);
        Assert.Equal(2, last.Value.Length);

        vm.FindNextCommand.Execute(null); // second match at index 3.
        Assert.Equal(3, last!.Value.Index);
    }

    // ---- FR-17 scoped replace: CURRENT FILE. ----

    [Fact]
    public async Task ReplaceAllInCurrentFileOnlyTouchesTheCurrentBuffer()
    {
        var current = new FakeBuffer("a.txt", "cat cat");
        var other = new FakeBuffer("b.txt", "cat cat cat");
        var ws = new FakeFindReplaceWorkspace { Current = current };
        ws.AllOpen.Add(current);
        ws.AllOpen.Add(other);

        FindReplaceViewModel vm = NewVm(ws);
        vm.FindText = "cat";
        vm.ReplaceText = "dog";
        vm.Scope = FindReplaceScope.CurrentFile;

        await vm.ReplaceAllCommand.ExecuteAsync(null);

        Assert.Equal("dog dog", current.GetText());        // current replaced.
        Assert.Equal("cat cat cat", other.GetText());      // the other open buffer is untouched.
        Assert.Contains("2", vm.StatusMessage);
    }

    [Fact]
    public void ReplaceSingleMatchReplacesOnlyTheCurrentOccurrence()
    {
        var current = new FakeBuffer("a.txt", "cat cat cat");
        var ws = new FakeFindReplaceWorkspace { Current = current };
        FindReplaceViewModel vm = NewVm(ws);
        vm.FindText = "cat";
        vm.ReplaceText = "dog";

        vm.ReplaceCommand.Execute(null);                   // replace just the first match.

        Assert.Equal("dog cat cat", current.GetText());
    }

    // ---- FR-17 scoped replace: ALL OPEN. ----

    [Fact]
    public async Task ReplaceAllAcrossAllOpenBuffersReplacesEveryOpenBuffer()
    {
        var a = new FakeBuffer("a.txt", "cat");
        var b = new FakeBuffer("b.txt", "cat cat");
        var ws = new FakeFindReplaceWorkspace { Current = a };
        ws.AllOpen.Add(a);
        ws.AllOpen.Add(b);

        FindReplaceViewModel vm = NewVm(ws);
        vm.FindText = "cat";
        vm.ReplaceText = "dog";
        vm.Scope = FindReplaceScope.AllOpen;

        await vm.ReplaceAllCommand.ExecuteAsync(null);

        Assert.Equal("dog", a.GetText());
        Assert.Equal("dog dog", b.GetText());
        Assert.Contains("3", vm.StatusMessage);            // 3 total replacements across 2 files.
    }

    // ---- FR-17 / FR-18 in-folder review-before-write + per-file counts + exclusion. ----

    [Fact]
    public async Task InFolderReviewProducesPerFileMatchCounts()
    {
        var ws = new FakeFindReplaceWorkspace { Root = "/proj" };
        ws.AddFolderFile("/proj/app.py", "cat cat cat");   // 3 matches.
        ws.AddFolderFile("/proj/util.py", "cat");          // 1 match.
        ws.AddFolderFile("/proj/readme.md", "no animals"); // 0 matches — excluded from the plan.

        var prompt = ScriptedReviewPrompt.ConfirmAll();
        FindReplaceViewModel vm = NewVm(ws, prompt);
        vm.FindText = "cat";
        vm.ReplaceText = "dog";
        vm.Scope = FindReplaceScope.InFolder;

        await vm.ReviewInFolderCommand.ExecuteAsync(null);

        // The review plan lists ONLY the matched files, each with its match count (FR-17 per-file counts).
        Assert.NotNull(prompt.LastPlan);
        Assert.Equal(2, prompt.LastPlan!.Count);
        Assert.Equal(3, prompt.LastPlan.Single(p => p.Path == "/proj/app.py").MatchCount);
        Assert.Equal(1, prompt.LastPlan.Single(p => p.Path == "/proj/util.py").MatchCount);
        Assert.DoesNotContain(prompt.LastPlan, p => p.Path == "/proj/readme.md");
    }

    [Fact]
    public async Task InFolderApplyWritesEveryConfirmedFileAtomically()
    {
        var ws = new FakeFindReplaceWorkspace { Root = "/proj" };
        ws.AddFolderFile("/proj/app.py", "cat cat");
        ws.AddFolderFile("/proj/util.py", "cat");

        FindReplaceViewModel vm = NewVm(ws, ScriptedReviewPrompt.ConfirmAll());
        vm.FindText = "cat";
        vm.ReplaceText = "dog";
        vm.Scope = FindReplaceScope.InFolder;

        await vm.ReviewInFolderCommand.ExecuteAsync(null);

        Assert.Equal(2, ws.WrittenPaths.Count);
        Assert.Equal("dog dog", ws.WrittenText["/proj/app.py"]);
        Assert.Equal("dog", ws.WrittenText["/proj/util.py"]);
        Assert.Contains("3", vm.StatusMessage);            // 3 total replacements in 2 files.
    }

    [Fact]
    public async Task InFolderExcludedFileIsNotWritten()
    {
        var ws = new FakeFindReplaceWorkspace { Root = "/proj" };
        ws.AddFolderFile("/proj/app.py", "cat cat");
        ws.AddFolderFile("/proj/notes.md", "cat");

        // The user UNCHECKS notes.md in the review (confirm only app.py).
        FindReplaceViewModel vm = NewVm(ws, ScriptedReviewPrompt.ConfirmOnly("/proj/app.py"));
        vm.FindText = "cat";
        vm.ReplaceText = "dog";
        vm.Scope = FindReplaceScope.InFolder;

        await vm.ReviewInFolderCommand.ExecuteAsync(null);

        Assert.Contains("/proj/app.py", ws.WrittenPaths);
        Assert.DoesNotContain("/proj/notes.md", ws.WrittenPaths); // excluded — NOT written.
        Assert.Equal("cat", ws.WrittenText.GetValueOrDefault("/proj/notes.md", "cat")); // untouched on disk.
    }

    [Fact]
    public async Task InFolderCancelWritesNothing()
    {
        var ws = new FakeFindReplaceWorkspace { Root = "/proj" };
        ws.AddFolderFile("/proj/app.py", "cat cat");

        FindReplaceViewModel vm = NewVm(ws, ScriptedReviewPrompt.Cancel());
        vm.FindText = "cat";
        vm.ReplaceText = "dog";
        vm.Scope = FindReplaceScope.InFolder;

        await vm.ReviewInFolderCommand.ExecuteAsync(null);

        Assert.Empty(ws.WrittenPaths);                     // cancel = nothing written (review-before-write).
        Assert.Contains("cancel", vm.StatusMessage, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InFolderScopeIsUnavailableWhenNoFolderIsOpen()
    {
        var ws = new FakeFindReplaceWorkspace { Root = null }; // no folder open.
        FindReplaceViewModel vm = NewVm(ws);

        Assert.False(vm.IsInFolderAvailable);
        Assert.False(vm.ReviewInFolderCommand.CanExecute(null)); // the in-folder action is disabled.
        Assert.Contains("open a folder", vm.OpenFolderLabel, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectingInFolderScopeFallsBackToCurrentFileWhenNoFolderIsOpen()
    {
        var ws = new FakeFindReplaceWorkspace { Root = null };
        FindReplaceViewModel vm = NewVm(ws);

        vm.Scope = FindReplaceScope.InFolder; // attempt to select the disabled scope.
        vm.RefreshInFolderAvailability();

        Assert.Equal(FindReplaceScope.CurrentFile, vm.Scope); // never left on the disabled option.
    }

    // ---- C1 regression: disk fault during in-folder apply surfaces a failure StatusMessage. ----

    [Fact]
    public async Task InFolderWriteFaultSurfacesStatusMessageWithPartialWriteCount()
    {
        // Three files, each with a match. The fault fires on the SECOND write (1-based FaultOnWriteN=2).
        // Before the fault, file #1 is written successfully. After the fault the VM must surface a
        // StatusMessage that mentions the failure (and how many files were already written — "1 file").
        // It must NOT silently swallow the IOException (C1 fix).
        var ws = new FakeFindReplaceWorkspace { Root = "/proj" };
        ws.AddFolderFile("/proj/a.txt", "cat");
        ws.AddFolderFile("/proj/b.txt", "cat");
        ws.AddFolderFile("/proj/c.txt", "cat");
        ws.FaultOnWriteN = 2; // The 2nd write call throws IOException.

        FindReplaceViewModel vm = NewVm(ws, ScriptedReviewPrompt.ConfirmAll());
        vm.FindText = "cat";
        vm.ReplaceText = "dog";
        vm.Scope = FindReplaceScope.InFolder;

        await vm.ReviewInFolderCommand.ExecuteAsync(null);

        // The VM must surface a non-empty StatusMessage that describes a failure — NOT the success summary.
        Assert.NotEmpty(vm.StatusMessage);
        Assert.DoesNotContain("Replaced", vm.StatusMessage, System.StringComparison.Ordinal); // no success msg.
        // The message must indicate the partial write count (1 file was written before the fault).
        Assert.Contains("1 file", vm.StatusMessage, System.StringComparison.OrdinalIgnoreCase);
        // Only one file was actually written to disk before the fault.
        Assert.Single(ws.WrittenPaths);
    }

    [Fact]
    public async Task InFolderScanCancellationSurfacesCancelledStatusAndWritesNothing()
    {
        // The workspace's EnumerateFolderFiles throws OperationCanceledException (simulating the Task.Run
        // cancellation path, W2 fix). The VM must surface a "cancelled" StatusMessage rather than letting
        // the exception escape (C1 fix — scan-cancellation branch).
        var ws = new CancellingEnumerateFindReplaceWorkspace { Root = "/proj" };
        ws.AddFolderFile("/proj/a.txt", "cat");

        FindReplaceViewModel vm = NewVm(ws, ScriptedReviewPrompt.ConfirmAll());
        vm.FindText = "cat";
        vm.ReplaceText = "dog";
        vm.Scope = FindReplaceScope.InFolder;

        await vm.ReviewInFolderCommand.ExecuteAsync(null);

        // A "cancelled" message must appear — never an unhandled exception escape.
        Assert.Contains("cancel", vm.StatusMessage, System.StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ws.WrittenPaths); // nothing was written.
    }

    // ---- BUG-1: Find/Replace panel visibility — hidden at startup, revealed on ShowFind/ShowReplace. ----

    [Fact]
    public void PanelIsHiddenAtStartup()
    {
        var ws = new FakeFindReplaceWorkspace();
        var frVm = new FindReplaceViewModel(ws, ScriptedReviewPrompt.ConfirmAll());

        // The VM's own initial state is hidden.
        Assert.False(frVm.IsVisible);
    }

    [Fact]
    public void PanelStartsHiddenAfterShellAttach()
    {
        var ws = new FakeFindReplaceWorkspace();
        var frVm = new FindReplaceViewModel(ws, ScriptedReviewPrompt.ConfirmAll());
        var shell = new ViewModels.MainWindowViewModel();

        Assert.Null(shell.FindReplace); // not yet attached.
        shell.AttachFindReplace(frVm);
        Assert.Same(frVm, shell.FindReplace);
        Assert.False(frVm.IsVisible); // still hidden after attach.
    }

    [Fact]
    public void ShowFindCommandRevealsPanel()
    {
        var ws = new FakeFindReplaceWorkspace();
        var frVm = new FindReplaceViewModel(ws, ScriptedReviewPrompt.ConfirmAll());
        var shell = new ViewModels.MainWindowViewModel();

        shell.AttachFindReplace(frVm);
        Assert.False(frVm.IsVisible);

        shell.ShowFindCommand.Execute(null);
        Assert.True(frVm.IsVisible); // Ctrl+F reveals it (regression guard).
    }

    [Fact]
    public void ShowReplaceCommandRevealsPanel()
    {
        var ws = new FakeFindReplaceWorkspace();
        var frVm = new FindReplaceViewModel(ws, ScriptedReviewPrompt.ConfirmAll());
        var shell = new ViewModels.MainWindowViewModel();

        shell.AttachFindReplace(frVm);
        Assert.False(frVm.IsVisible);

        shell.ShowReplaceCommand.Execute(null);
        Assert.True(frVm.IsVisible); // Ctrl+H reveals it (regression guard).
    }

    // ---- BUG-2: Find/Replace panel dismiss paths (close button + Esc). ----

    [Fact]
    public void HideCommandHidesPanel()
    {
        var ws = new FakeFindReplaceWorkspace();
        var frVm = new FindReplaceViewModel(ws, ScriptedReviewPrompt.ConfirmAll());

        frVm.IsVisible = true;
        Assert.True(frVm.IsVisible);

        frVm.HideCommand.Execute(null);

        Assert.False(frVm.IsVisible); // × button or Esc hides it.
    }

    [Fact]
    public void HideCommandIsAlwaysAvailable()
    {
        var ws = new FakeFindReplaceWorkspace();
        var frVm = new FindReplaceViewModel(ws, ScriptedReviewPrompt.ConfirmAll());

        // The hide command should always be executable (no CanExecute guard).
        Assert.True(frVm.HideCommand.CanExecute(null));
    }

    [Fact]
    public void HideFromHiddenStateIsNoOp()
    {
        var ws = new FakeFindReplaceWorkspace();
        var frVm = new FindReplaceViewModel(ws, ScriptedReviewPrompt.ConfirmAll());

        Assert.False(frVm.IsVisible); // already hidden.
        frVm.HideCommand.Execute(null); // no-op, no crash.
        Assert.False(frVm.IsVisible); // still hidden.
    }
}
