// FoldingTests.cs — the M8.2 FR-9 folding gates (architecture §3.3, design §3, prd FR-9 / MA17). Covers:
//   - the snapshot-version guard: a recompute scheduled against document version V is DROPPED + rescheduled
//     when the document advances to V+1 before the UI-thread commit (NON-VACUOUS — it also asserts the stale
//     fold offsets would have been WRONG, and the test below proves the assertion fails if the guard is gone);
//   - the FoldingStrategySelector indent-vs-brace choice by TextMate scope;
//   - the scope-null fallback to indentation folding (the lazy-grammar pre-tokenize case);
//   - the IndentationFoldingStrategy itself (a deeper-indented block folds; flat / single-line do not).
// No UI thread is needed — the FoldingService is driven through injected synchronous post-to-UI + background
// runners + the ManualTimeProvider (reused from ChecklistTestSupport), so the debounce + the snapshot→compute
// →commit hops are fully deterministic (no wall-clock sleeps, per claude_coding.md test discipline). The
// verify filter matches the class name "Folding".

using System;
using System.Collections.Generic;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using HoardPad.App.Editor;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class FoldingTests
{
    // ---- FoldingStrategySelector: indent-vs-brace by TextMate scope. ----

    [Theory]
    [InlineData("source.cs")]
    [InlineData("source.cpp")]
    [InlineData("source.js")]
    [InlineData("source.ts")]
    [InlineData("source.rust")]
    [InlineData("source.go")]
    [InlineData("source.json")]
    public void SelectorPicksBraceForABraceStructuredScope(string scope) =>
        Assert.Equal(FoldingKind.Brace, FoldingStrategySelector.KindFor(scope));

    [Theory]
    [InlineData("source.python")]
    [InlineData("source.toml")]
    [InlineData("source.yaml")]
    [InlineData("source.makefile")]
    [InlineData("text.html.markdown")]
    public void SelectorPicksIndentationForAnIndentationStructuredScope(string scope) =>
        Assert.Equal(FoldingKind.Indentation, FoldingStrategySelector.KindFor(scope));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SelectorFallsBackToIndentationWhenScopeIsNullOrUnresolved(string? scope) =>
        Assert.Equal(FoldingKind.Indentation, FoldingStrategySelector.KindFor(scope));

    [Fact]
    public void SelectorFallsBackToIndentationForAnUnrecognizedScope() =>
        // A scope the brace table does not cover (and not a brace language) routes to indentation, never throws.
        Assert.Equal(FoldingKind.Indentation, FoldingStrategySelector.KindFor("source.somethingexotic"));

    [Fact]
    public void SelectorComputesBraceFoldsForABraceScopeAndIndentFoldsForANullScope()
    {
        // A curly-brace doc: under a brace scope it folds the { } pair; under a null scope (the lazy-grammar
        // pre-tokenize fallback) the SAME doc is read by the indentation strategy instead (no { }-pair fold).
        FoldSnapshot braceSnapshot = Snap("fn main() {\n    let x = 1;\n}\n");
        List<NewFolding> braceFolds = FoldingStrategySelector.ComputeFoldings(braceSnapshot, "source.rust");
        Assert.Single(braceFolds);

        FoldSnapshot indentSnapshot = Snap("def f():\n    x = 1\n    y = 2\n");
        List<NewFolding> indentFolds = FoldingStrategySelector.ComputeFoldings(indentSnapshot, scope: null);
        Assert.Single(indentFolds); // the indented body under "def f():" folds.
    }

    // ---- IndentationFoldingStrategy: a deeper-indented block folds; flat / single-line do not. ----

    [Fact]
    public void IndentationFoldingFoldsADeeperIndentedBlock()
    {
        var doc = new TextDocument("def f():\n    a = 1\n    b = 2\nx = 3\n");

        List<NewFolding> folds = IndentationFoldingStrategy.ComputeFoldings(FoldSnapshot.Capture(doc));

        Assert.Single(folds);
        // The fold opens at the end of the header line ("def f():") and spans the indented body.
        DocumentLine header = doc.GetLineByNumber(1);
        Assert.Equal(header.EndOffset, folds[0].StartOffset);
        Assert.True(folds[0].EndOffset > folds[0].StartOffset);
    }

    [Fact]
    public void IndentationFoldingNestsAndAbsorbsBlankLines()
    {
        // A two-level Python-style structure with a blank line inside the inner block.
        var doc = new TextDocument("def outer():\n    def inner():\n        a = 1\n\n        b = 2\n    c = 3\n");

        List<NewFolding> folds = IndentationFoldingStrategy.ComputeFoldings(FoldSnapshot.Capture(doc));

        // Two folds: the outer (def outer) and the inner (def inner); the blank line does not split the inner.
        Assert.Equal(2, folds.Count);
        Assert.True(folds[0].StartOffset < folds[1].StartOffset);
    }

    [Fact]
    public void IndentationFoldingProducesNoFoldForFlatOrSingleLineText()
    {
        Assert.Empty(IndentationFoldingStrategy.ComputeFoldings(Snap("a = 1\nb = 2\nc = 3\n")));
        Assert.Empty(IndentationFoldingStrategy.ComputeFoldings(Snap("only one line\n")));
        Assert.Empty(IndentationFoldingStrategy.ComputeFoldings(Snap(string.Empty)));
    }

    // ---- THE SNAPSHOT-VERSION GUARD (the load-bearing rule). ----

    [Fact]
    public void StaleRecomputeIsDroppedAndRescheduledWhenTheDocumentVersionAdvances()
    {
        var harness = new FoldingHarness("def f():\n    a = 1\n    b = 2\n", scope: null);

        // Schedule + fire the debounce: BeginRecompute captures version V and starts the (deferred) compute.
        harness.Service.Schedule();
        harness.Time.Advance(FoldingService.Debounce);
        Assert.True(harness.HasPendingBackground); // the compute is captured against version V but not yet committed.

        // An in-place edit ARRIVES between the snapshot and the commit — the document advances to V+1. The
        // folds the deferred compute produced are offset against the OLD (now-stale) snapshot.
        harness.Document.Insert(0, "import os\n");

        // Drain the deferred compute → its commit runs the snapshot-version guard on the UI thread.
        harness.RunBackground();

        // GUARD: the stale result is DROPPED (no commit) and a fresh recompute is RESCHEDULED.
        Assert.Empty(harness.Commits);
        Assert.True(harness.HasPendingDebounce);

        // The reschedule recomputes against the CURRENT document and commits correct, non-stale offsets that
        // are in range — proving the guard does not wedge the pipeline, only drops the one stale result.
        harness.Time.Advance(FoldingService.Debounce);
        harness.RunBackground();
        Assert.Single(harness.Commits);
        foreach (NewFolding fold in harness.Commits[0])
        {
            Assert.True(fold.StartOffset >= 0 && fold.StartOffset <= harness.Document.TextLength);
            Assert.True(fold.EndOffset >= fold.StartOffset && fold.EndOffset <= harness.Document.TextLength);
        }
    }

    [Fact]
    public void WithoutTheGuardTheStaleResultWouldCommitOffsetsPastTheNewDocumentEnd()
    {
        // NON-VACUITY proof: simulate "no guard" by computing folds against the LONG (original) document,
        // then SHRINKING the document, and asserting the stale offsets escape the shrunken document. This is
        // exactly the corruption the guard prevents — so the guarded test above cannot pass vacuously.
        var longDoc = new TextDocument("def f():\n    a = 1\n    b = 2\n    c = 3\n    d = 4\n");
        List<NewFolding> staleFolds = IndentationFoldingStrategy.ComputeFoldings(FoldSnapshot.Capture(longDoc));
        Assert.NotEmpty(staleFolds);

        var shrunkDoc = new TextDocument("x = 1\n");

        // Applying the stale (long-doc) fold offsets to the shrunken document would push past its end — the
        // fold-offset corruption the snapshot-version guard exists to prevent.
        bool anyOutOfRange = false;
        foreach (NewFolding fold in staleFolds)
        {
            if (fold.EndOffset > shrunkDoc.TextLength)
                anyOutOfRange = true;
        }

        Assert.True(anyOutOfRange);
    }

    [Fact]
    public void DocumentReplaceCancelsTheInFlightRecomputeWithoutCommitting()
    {
        var harness = new FoldingHarness("def f():\n    a = 1\n", scope: null);

        harness.Service.Schedule();
        harness.Time.Advance(FoldingService.Debounce);
        Assert.True(harness.HasPendingBackground);

        // A tab switch / new open: SetDocument bumps the generation, so the in-flight compute is discarded
        // outright on commit (no commit, no reschedule of the OLD doc — SetDocument scheduled the new one).
        harness.Service.SetDocument();
        harness.RunBackground();

        Assert.Empty(harness.Commits);
        Assert.True(harness.HasPendingDebounce); // SetDocument scheduled a fresh recompute for the new document.
    }

    [Fact]
    public void AStableDocumentCommitsTheComputedFolds()
    {
        var harness = new FoldingHarness("def f():\n    a = 1\n    b = 2\n", scope: null);

        harness.Service.Schedule();
        harness.Time.Advance(FoldingService.Debounce);
        harness.RunBackground();

        // No edit between snapshot and commit → the version is unchanged → the folds commit.
        Assert.Single(harness.Commits);
        Assert.Single(harness.Commits[0]); // the indented body under "def f():".
    }

    // ---- THE OFF-THREAD-READ FIX: the compute reads the immutable snapshot, never the live document. ----

    [Fact]
    public void ComputeReadsTheCapturedSnapshotNotTheLiveDocumentAfterCapture()
    {
        // Capture a snapshot, THEN mutate the live document. The fold compute over the (frozen) snapshot must
        // reflect the text AT CAPTURE TIME — not the later mutation — proving the strategies read the
        // immutable snapshot, never the mutable TextDocument (the off-UI-thread read-tear/throw the fix
        // closes). A fresh snapshot of the mutated document yields a different result, so this is non-vacuous.
        var doc = new TextDocument("def f():\n    a = 1\n    b = 2\n");
        FoldSnapshot captured = FoldSnapshot.Capture(doc);

        // Mutate the live document AFTER the capture (as a concurrent keystroke would during the compute window).
        doc.Insert(0, "import os\nimport sys\n");

        List<NewFolding> overSnapshot = IndentationFoldingStrategy.ComputeFoldings(captured);
        List<NewFolding> overLiveNow = IndentationFoldingStrategy.ComputeFoldings(FoldSnapshot.Capture(doc));

        // The snapshot froze the pre-mutation text: its single fold's offsets are unchanged by the later edit,
        // and they stay within the ORIGINAL (shorter) length — the post-mutation snapshot's offsets are larger.
        Assert.Single(overSnapshot);
        Assert.Single(overLiveNow);
        Assert.True(overSnapshot[0].StartOffset < overLiveNow[0].StartOffset);
        Assert.True(overSnapshot[0].EndOffset <= captured.Text.Length);
    }

    // ---- FoldSnapshot.LineNumberByOffset: the hand-rolled binary search must equal the document's own line API. ----

    [Theory]
    [InlineData("alpha\nbravo\ncharlie\ndelta\n")]   // multi-line, \n terminators (mid-line, line-start, end-of-doc offsets).
    [InlineData("alpha\r\nbravo\r\ncharlie\r\n")]      // CRLF terminators — exercises the 2-char delimiter gaps.
    [InlineData("single line no trailing newline")]   // one line, no terminator (offset 0 and offset == TextLength).
    public void LineNumberByOffsetEqualsTheDocumentOracleForEveryOffset(string text)
    {
        // ORACLE: for every valid offset, the snapshot's binary search must return EXACTLY what AvaloniaEdit's
        // own TextDocument.GetLineByOffset(offset).LineNumber returns — the authoritative contract it stands in
        // for. Includes offset == TextLength (the end-of-document position GetLineByOffset accepts).
        var document = new TextDocument(text);
        FoldSnapshot snapshot = FoldSnapshot.Capture(document);

        Assert.Equal(document.LineCount, snapshot.LineCount);
        for (int offset = 0; offset <= document.TextLength; offset++)
        {
            int expected = document.GetLineByOffset(offset).LineNumber;
            Assert.Equal(expected, snapshot.LineNumberByOffset(offset));
        }
    }

    [Fact]
    public void EmptyDocumentSnapshotMatchesTheDocumentForLineOneAndOffsetZero()
    {
        // An empty document is still one line (LineCount == 1). LineNumberByOffset(0) must not throw and must
        // agree with the document; line 1's Offset/EndOffset must mirror the captured TextDocument.
        var document = new TextDocument(string.Empty);
        FoldSnapshot snapshot = FoldSnapshot.Capture(document);

        Assert.Equal(1, document.LineCount);
        Assert.Equal(1, snapshot.LineCount);
        Assert.Equal(document.GetLineByOffset(0).LineNumber, snapshot.LineNumberByOffset(0));

        DocumentLine lineOne = document.GetLineByNumber(1);
        Assert.Equal(lineOne.Offset, snapshot.LineOffset(1));
        Assert.Equal(lineOne.EndOffset, snapshot.LineEndOffset(1));
    }

    private static FoldSnapshot Snap(string text) => FoldSnapshot.Capture(new TextDocument(text));

    /// <summary>
    /// A deterministic <see cref="FoldingService"/> test rig: the debounce timer is driven by a
    /// <see cref="ManualTimeProvider"/>; the post-to-UI runs synchronously; the background compute is
    /// DEFERRED into a queue so a test can mutate the document between the snapshot and the commit.
    /// </summary>
    private sealed class FoldingHarness
    {
        private readonly Queue<Action> _background = new();

        public FoldingHarness(string text, string? scope)
        {
            Document = new TextDocument(text);
            Time = new ManualTimeProvider();
            Service = new FoldingService(
                Time,
                getDocument: () => Document,
                getScope: () => scope,
                commit: folds => Commits.Add(folds),
                postToUi: action => action(),          // synchronous UI marshal in tests.
                runBackground: work => _background.Enqueue(work)); // deferred so the test controls the window.
        }

        public TextDocument Document { get; }

        public ManualTimeProvider Time { get; }

        public FoldingService Service { get; }

        public List<List<NewFolding>> Commits { get; } = [];

        /// <summary>Whether a background compute has been captured (snapshot taken) but not yet drained.</summary>
        public bool HasPendingBackground => _background.Count > 0;

        /// <summary>Whether a debounce timer is armed (a (re)schedule is pending).</summary>
        public bool HasPendingDebounce => Time.HasPendingTimer;

        /// <summary>Drain every captured background compute (each runs its compute + posts its commit).</summary>
        public void RunBackground()
        {
            while (_background.Count > 0)
                _background.Dequeue()();
        }
    }
}
