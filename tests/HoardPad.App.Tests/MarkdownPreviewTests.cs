// MarkdownPreviewTests.cs — the M6.4 Markdown experience gates (FR-14/15/16 + SC-4 + NFR-9, design §4).
//
// Two mandatory groups, plus the FR-15 Markdown-only gating and the NFR-9 threshold:
//   1. FR-16 per-feature rendering fidelity — a feature-exercising doc rendered through the REAL Candidate-A
//      walker (MarkdownRenderer), asserting against the produced Avalonia CONTROL TREE (not "didn't throw").
//   2. SC-4 byte-identical toggle round-trip — capture buffer text, toggle to preview + back, assert the
//      buffer is byte-identical AND the preview surface exposes no caret / cannot mutate the buffer.
//   3. FR-15 Done-when — the toggle is enabled only for a Markdown document (disabled/absent otherwise).
//
// The control-tree assertions need the Avalonia type/visual system, so they run Skia-headless via the
// shared TestAppBuilder ([AvaloniaFact]). The VM-only gating uses a plain [Fact]. The verify filter
// matches "Markdown" / "Fr16" / "Sc4" in the fully-qualified names below.

using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using HoardPad.App.Editor;
using HoardPad.App.Hosting;
using HoardPad.App.Markdown;
using HoardPad.App.Session;
using HoardPad.App.ViewModels;
using HoardPad.App.Views;
using HoardPad.Core.Recovery;
using HoardPad.Host;
using Microsoft.Extensions.DependencyInjection;
using TextMateSharp.Grammars;
using Xunit;

namespace HoardPad.App.Tests;

public sealed class MarkdownPreviewTests
{
    // A single feature-exercising fixture covering every FR-16 item (design §4.3) + the brand elements.
    private const string Fr16Fixture = """
        # Project Notes

        A paragraph with **bold**, _em_, ~~struck~~, `code`, a [link](http://example.com), and https://auto.example.com .

        | Name   | Status |
        |--------|--------|
        | app.py | done   |
        | api.py | todo   |

        - [x] task one
        - [ ] task two

        ```python
        def main():
            return 42
        ```

        > a quote

        ---

        Body text with a footnote.[^1]

        [^1]: The footnote body.
        """;

    // ---- Fr16 (1): tables — a grid with the expected row/column count. ----

    [AvaloniaFact]
    public void Fr16TableRendersGridWithExpectedRowAndColumnCount()
    {
        Control tree = MarkdownRenderer.Render(Fr16Fixture);

        MarkdownTableGrid table = Descendants(tree).OfType<MarkdownTableGrid>().Single();

        // Header row + 2 body rows = 3 rows; 2 columns.
        Assert.Equal(3, table.RowCount);
        Assert.Equal(2, table.ColumnCount);
        // The grid actually allocated the row/column definitions (a real structural grid, not a stub).
        Assert.Equal(3, table.RowDefinitions.Count);
        Assert.Equal(2, table.ColumnDefinitions.Count);
        // 3 rows * 2 cols = 6 rendered cells.
        Assert.Equal(6, table.Children.OfType<Border>().Count());
    }

    // ---- Fr16 (2): fenced code blocks — language label + literal code in plain mono. ----

    [AvaloniaFact]
    public void Fr16FencedCodeBlockShowsLanguageLabelAndLiteralCode()
    {
        Control tree = MarkdownRenderer.Render(Fr16Fixture);

        // The language label "python" appears as a TextBlock.
        var texts = Descendants(tree).OfType<TextBlock>().ToList();
        Assert.Contains(texts, t => t.Text == "python");

        // The code text is the literal block content (plain mono — coloring is NOT an FR-16 requirement).
        TextBlock codeText = texts.Single(t => t.Text is not null && t.Text.Contains("def main():"));
        Assert.Contains("return 42", codeText.Text);
    }

    // ---- Fr16 (3): task-list checkboxes — non-interactive (disabled / not hit-testable) glyphs. ----

    [AvaloniaFact]
    public void Fr16TaskListRendersNonInteractiveCheckboxesWithCorrectStates()
    {
        Control tree = MarkdownRenderer.Render(Fr16Fixture);

        var boxes = Descendants(tree).OfType<CheckBox>().ToList();
        Assert.Equal(2, boxes.Count);

        // States: "[x] task one" checked, "[ ] task two" unchecked.
        Assert.Equal(new bool?[] { true, false }, boxes.Select(b => b.IsChecked).ToArray());

        // Non-interactive: every checkbox is disabled AND not hit-testable (the preview is read-only).
        Assert.All(boxes, b =>
        {
            Assert.False(b.IsEnabled);
            Assert.False(b.IsHitTestVisible);
        });
    }

    // ---- Fr16 (4): strikethrough — a struck inline run. ----

    [AvaloniaFact]
    public void Fr16StrikethroughProducesAStruckRun()
    {
        Control tree = MarkdownRenderer.Render(Fr16Fixture);

        Run struck = AllRuns(tree).Single(r => r.Text == "struck");
        Assert.NotNull(struck.TextDecorations);
        Assert.Contains(struck.TextDecorations!, d => d.Location == TextDecorationLocation.Strikethrough);
    }

    // ---- Fr16 (5): autolinks + links — link-styled runs that are NOT wired to open a browser. ----

    [AvaloniaFact]
    public void Fr16LinkAndAutolinkAreLinkStyledAndNotBrowserWired()
    {
        Control tree = MarkdownRenderer.Render(Fr16Fixture);
        var runs = AllRuns(tree).ToList();

        // The labelled link shows its label; the autolink shows the bare URL.
        Run link = runs.Single(r => r.Text == "link");
        Run autolink = runs.Single(r => r.Text == "https://auto.example.com");

        // Both are underlined (link-styled, never colour-alone — brand.md §4) and accent-colored.
        foreach (Run r in new[] { link, autolink })
        {
            Assert.NotNull(r.TextDecorations);
            Assert.Contains(r.TextDecorations!, d => d.Location == TextDecorationLocation.Underline);
            // Bound to Brand.Accent via DynamicResource — resolves to an (immutable) solid-color brush.
            Assert.IsAssignableFrom<ISolidColorBrush>(r.Foreground);
        }

        // Navigation is out of scope (NFR-7): the rendered tree contains no HyperlinkButton / no click wiring.
        Assert.Empty(Descendants(tree).OfType<HyperlinkButton>());
    }

    // ---- Fr16 footnotes: an in-text marker + a footnotes section. ----

    [AvaloniaFact]
    public void Fr16FootnoteProducesMarkerAndFootnotesSection()
    {
        Control tree = MarkdownRenderer.Render(Fr16Fixture);
        var texts = Descendants(tree).OfType<TextBlock>().ToList();
        var runs = AllRuns(tree).ToList();

        // The in-text marker "[1]" appears as an inline run.
        Assert.Contains(runs, r => r.Text == "[1]");

        // The footnotes section renders the note body and its ordinal "1.".
        Assert.Contains(runs, r => r.Text != null && r.Text.Contains("The footnote body."));
        Assert.Contains(texts, t => t.Text == "1.");
    }

    // ---- Fr16 headings + brand binding: h1 amethyst, multiple heading levels styled. ----

    [AvaloniaFact]
    public void Fr16HeadingRendersAsStyledTextBlock()
    {
        Control tree = MarkdownRenderer.Render("# Title\n\n#### Minor\n");
        var headings = AllRuns(tree).Where(r => r.Text is "Title" or "Minor").ToList();
        Assert.Equal(2, headings.Count);
        // Headings bind a brush foreground (amethyst/sapphire per brand.md §4) — not the default null.
        Assert.All(Descendants(tree).OfType<TextBlock>().Where(t => t.Inlines is { Count: > 0 }),
            t => Assert.NotNull(t.Foreground));
    }

    // ================================================================================================
    //  SC-4 — byte-identical toggle round-trip + the preview cannot mutate the buffer.
    // ================================================================================================

    [AvaloniaFact]
    public void Sc4PreviewRenderDoesNotMutateTheSourceBuffer()
    {
        // The Render entry point takes the source by value and returns a fresh tree — it cannot reach back
        // into the buffer. Capture, render, and assert the captured string is unchanged (byte-identical).
        string before = Fr16Fixture;
        string captured = new string(before.ToCharArray()); // an independent copy.

        Control tree = MarkdownRenderer.Render(captured);

        Assert.Equal(before, captured);               // the source string is untouched (SC-4).
        Assert.Equal(before.Length, captured.Length); // byte/char-length identical.
        // The preview tree exposes no caret and no editable control (read-only by construction).
        Assert.Empty(Descendants(tree).OfType<TextBox>());
        Assert.Empty(Descendants(tree).OfType<AvaloniaEdit.TextEditor>());
    }

    [Fact]
    public void Sc4ToggleToPreviewAndBackLeavesTheEditorBufferByteIdentical()
    {
        EditorViewModel vm = NewMarkdownEditorViewModel(Fr16Fixture);
        string before = vm.Document.Text;

        // Toggle to preview, then back to source. The buffer is never touched by the preview.
        vm.ToggleMarkdownPreviewCommand.Execute(null);
        Assert.True(vm.IsPreviewVisible);
        vm.ToggleMarkdownPreviewCommand.Execute(null);
        Assert.False(vm.IsPreviewVisible);

        Assert.Equal(before, vm.Document.Text); // byte-identical buffer after the round-trip (SC-4).
    }

    // ================================================================================================
    //  QA4-11 — the wired-up EditorView must actually MOUNT the rendered preview on toggle, and a tab
    //  switch away then back to a tab left in preview must re-render THAT tab's content, not blank.
    // ================================================================================================

    /// <summary>
    /// QA4-11 regression (interaction layer, real <see cref="MainWindow"/> + <see cref="EditorView"/> code-
    /// behind — not just the VM). Root cause: <c>EditorView.OnDataContextChanged</c> called
    /// <c>RenderPreview</c> (which reads the CURRENT <c>_editor.Text</c>) BEFORE <c>SwitchToTabDocument</c>/
    /// <c>MirrorDocumentIntoEditor</c> re-pointed <c>_editor.Document</c> at the newly-activated tab's own
    /// buffer (QA3-22 per-tab <c>TextDocument</c>s). Switching away from a Markdown tab left in preview,
    /// then back, rendered whatever tab was PREVIOUSLY attached — typically a blank scratch tab — which is
    /// exactly the reported "MD Preview never loads / shows nothing" symptom. This test drives two REAL
    /// tabs through the real <see cref="MainWindowViewModel.ActivateTab"/> path and asserts the preview
    /// still shows the active tab's own content after a switch-away-and-back.
    /// </summary>
    [AvaloniaFact]
    public void Qa411TabSwitchAwayAndBackToATabLeftInPreviewRendersThatTabsContentNotBlank()
    {
        using var provider = CompositionRoot.Build();
        var shell = provider.GetRequiredService<MainWindowViewModel>();
        var editorFactory = provider.GetRequiredService<EditorViewModelFactory>();
        var catalog = provider.GetRequiredService<LanguageCatalog>();

        var window = new MainWindow { DataContext = shell };
        window.Show();
        window.UpdateLayout();

        // Tab 1: a Markdown document with distinctive, greppable content.
        const string marker = "Qa411MarkerHeading";
        EditorViewModel md = editorFactory.Create();
        md.Document.Path = "qa4-11.md";
        md.Document.Text = $"# {marker}\n\nBody text.";
        (string? scope, string label) = catalog.Resolve("qa4-11.md");
        md.Document.GrammarScope = scope;
        md.Document.LanguageLabel = label;
        md.SyncSegmentsFromDocument();
        var mdTab = DocumentTab.ForEditor(Guid.NewGuid().ToString("N"), TabKind.Scratch, md);

        // Tab 2: a blank scratch document — the switch-away target whose empty buffer the QA4-11 bug leaked
        // into the preview when switching back to tab 1.
        EditorViewModel plain = editorFactory.Create();
        var plainTab = DocumentTab.ForEditor(Guid.NewGuid().ToString("N"), TabKind.Scratch, plain);

        shell.Tabs.Add(mdTab);
        shell.Tabs.Add(plainTab);

        shell.ActivateTab(mdTab);
        window.UpdateLayout();
        Assert.True(md.IsMarkdown);

        md.ToggleMarkdownPreviewCommand.Execute(null);
        Assert.True(md.IsPreviewVisible);
        window.UpdateLayout();

        // The rendered preview MOUNTED into the real visual/logical tree and shows the marker heading.
        var preview = Descendants(window).OfType<MarkdownPreviewControl>().Single();
        Assert.True(preview.IsVisible);
        Assert.Contains(AllRuns(window), r => r.Text == marker);

        // Switch away, then back — the QA4-11 regression path.
        shell.ActivateTab(plainTab);
        window.UpdateLayout();
        shell.ActivateTab(mdTab);
        window.UpdateLayout();

        // The preview must still show THIS tab's own content — not blank (the QA4-11 bug rendered the
        // just-switched-from blank tab's empty buffer because RenderPreview ran before the document swap).
        Assert.True(md.IsPreviewVisible);
        Assert.Contains(AllRuns(window), r => r.Text == marker);

        // Toggle back to source — the editor returns with the buffer intact (SC-4) and visible again.
        md.ToggleMarkdownPreviewCommand.Execute(null);
        Assert.False(md.IsPreviewVisible);
        window.UpdateLayout();
        Assert.Equal($"# {marker}\n\nBody text.", md.Document.Text);
    }

    // ================================================================================================
    //  FR-15 Done-when — the toggle is enabled only for a Markdown document.
    // ================================================================================================

    [Fact]
    public void Fr15ToggleIsEnabledForAMarkdownDocument()
    {
        EditorViewModel vm = NewMarkdownEditorViewModel("# hi");

        Assert.True(vm.IsMarkdown);
        Assert.True(vm.ToggleMarkdownPreviewCommand.CanExecute(null));
    }

    [Fact]
    public void Fr15ToggleIsDisabledForANonMarkdownDocument()
    {
        // A plain-text / non-Markdown document: no grammar scope resolves to the Markdown scope.
        EditorViewModel vm = NewEditorViewModelFor("notes.txt", "plain text", "Plain Text");

        Assert.False(vm.IsMarkdown);
        Assert.False(vm.ToggleMarkdownPreviewCommand.CanExecute(null));

        // And a non-Markdown doc can never be left showing a stale preview.
        Assert.False(vm.IsPreviewVisible);
    }

    [Fact]
    public void Fr15TogglingDisablesAndClearsPreviewWhenTheDocumentBecomesNonMarkdown()
    {
        EditorViewModel vm = NewMarkdownEditorViewModel("# hi");
        vm.ToggleMarkdownPreviewCommand.Execute(null);
        Assert.True(vm.IsPreviewVisible);

        // Repoint the document at a non-Markdown file (the language changes) and resync.
        vm.Document.Path = "notes.txt";
        vm.Document.GrammarScope = null;
        vm.Document.LanguageLabel = "Plain Text";
        vm.SyncSegmentsFromDocument();

        Assert.False(vm.IsMarkdown);
        Assert.False(vm.IsPreviewVisible); // snapped off — no stale preview on a non-Markdown doc.
        Assert.False(vm.ToggleMarkdownPreviewCommand.CanExecute(null));
    }

    // ================================================================================================
    //  NFR-9 — the large-Markdown threshold notice (design §3.6).
    // ================================================================================================

    [AvaloniaFact]
    public void Nfr9LargeMarkdownShowsTheRenderAnywayNoticeInsteadOfAutoRendering()
    {
        // A document just over the directional threshold: the preview shows the calm notice, not the full tree.
        string large = new string('x', MarkdownPreviewControl.PreviewAutoRenderMaxBytes + 1);
        var preview = new MarkdownPreviewControl();
        preview.Render(large);

        var buttons = Descendants(preview).OfType<Button>().ToList();
        Assert.Contains(buttons, b => (b.Content as string) == "Render anyway");
        var texts = Descendants(preview).OfType<TextBlock>().ToList();
        Assert.Contains(texts, t => t.Text != null && t.Text.StartsWith("Preview not auto-rendered for large Markdown", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void Nfr9SmallMarkdownAutoRendersWithoutTheNotice()
    {
        var preview = new MarkdownPreviewControl();
        preview.Render("# Small\n\nbody");

        var texts = Descendants(preview).OfType<TextBlock>().ToList();
        Assert.DoesNotContain(texts, t => t.Text != null && t.Text.StartsWith("Preview not auto-rendered", StringComparison.Ordinal));
        Assert.Contains(AllRuns(preview), r => r.Text == "Small"); // the heading actually rendered.
    }

    // ================================================================================================
    //  W3/W4 — regression tests for C1 (type-name garbage) and C2 (footnote back-link garbage).
    //  Each test is non-vacuous: revert the corresponding fix and the assertion turns RED.
    // ================================================================================================

    /// <summary>
    /// C1 regression — HTML entities must decode to real characters, NOT to the Markdig type name.
    /// Without the HtmlEntityInline.Transcoded arm, "AT&amp;T" would render "Markdig.Syntax.Inlines.HtmlEntityInline".
    /// </summary>
    [AvaloniaFact]
    public void C1RegressionHtmlEntityDecodesToRealCharacterNotTypeName()
    {
        Control tree = MarkdownRenderer.Render("AT&amp;T and &copy; and &mdash;");

        var runs = AllRuns(tree).Select(r => r.Text ?? string.Empty).ToList();

        // The decoded text must appear.
        Assert.Contains(runs, t => t.Contains('&'));      // "&amp;" → "&"
        Assert.Contains(runs, t => t.Contains('©'));      // "&copy;" → "©"
        Assert.Contains(runs, t => t.Contains('—'));      // "&mdash;" → "—"

        // NO run may begin with "Markdig." — that is the type-name garbage from the old default branch.
        Assert.DoesNotContain(runs, t => t.StartsWith("Markdig.", StringComparison.Ordinal));
    }

    /// <summary>
    /// C1 regression — an image must render its alt text as a PLAIN (non-link-styled) run.
    /// Without the explicit <c>IsImage</c> arm the image falls to the general <c>LinkInline</c> arm,
    /// which ALSO emits "logo" but as an underlined accent run — so a plain Contains("logo") check is
    /// vacuous. The correct guard is: the alt-text run must exist AND must NOT be link-styled (no
    /// underline). Reverting the IsImage arm turns this test RED (the run becomes underlined).
    /// </summary>
    [AvaloniaFact]
    public void C1RegressionImageRendersAltTextAsPlainRunNotLinkStyled()
    {
        Control tree = MarkdownRenderer.Render("![logo](logo.png)");

        var runs = AllRuns(tree).ToList();

        // Alt text "logo" must appear.
        Assert.Contains(runs, r => r.Text == "logo");

        // The alt-text run must NOT be link-styled (no underline, not accent-coloured).
        // If the IsImage arm is reverted, the image falls to the LinkInline arm → underlined → RED.
        Assert.DoesNotContain(
            runs,
            r => r.Text == "logo"
                && r.TextDecorations is { } td
                && td.Any(d => d.Location == TextDecorationLocation.Underline));

        // Must NOT contain any type-name garbage (original C1 guard still present).
        Assert.DoesNotContain(runs.Select(r => r.Text ?? string.Empty), t => t.StartsWith("Markdig.", StringComparison.Ordinal));
    }

    /// <summary>
    /// C2 regression — the footnote section must contain NO run whose text begins with "Markdig.".
    /// Without the explicit FootnoteLink{IsBackLink:true} drop arm, every footnote body paragraph
    /// appended a garbage type-name run after the note text.
    /// </summary>
    [AvaloniaFact]
    public void C2RegressionFootnoteBodyContainsNoTypeNameGarbage()
    {
        const string md = "See note.[^1]\n\n[^1]: The note body text.\n";
        Control tree = MarkdownRenderer.Render(md);

        var allRunTexts = AllRuns(tree).Select(r => r.Text ?? string.Empty).ToList();

        // The note body must appear.
        Assert.Contains(allRunTexts, t => t.Contains("The note body text."));

        // Not a single run may begin with "Markdig." — the back-link drop arm must have eaten it.
        Assert.DoesNotContain(allRunTexts, t => t.StartsWith("Markdig.", StringComparison.Ordinal));
    }

    /// <summary>
    /// W2 regression — bracketed autolinks &lt;https://...&gt; must render as underlined accent runs,
    /// NOT as unstyled plain text. Without the AutolinkInline case they hit the old default branch
    /// (which called ToString() — yielding garbage) or the ContainerInline fallback (no styling).
    /// </summary>
    [AvaloniaFact]
    public void W2RegressionBracketedAutolinkIsLinkStyled()
    {
        Control tree = MarkdownRenderer.Render("Visit <https://example.com> for info.");

        var runs = AllRuns(tree).ToList();

        // The URL must appear as a run.
        Run autolink = runs.Single(r => r.Text == "https://example.com");

        // It must be underlined (link-styled — brand.md §4 / FR-16 (5)).
        Assert.NotNull(autolink.TextDecorations);
        Assert.Contains(autolink.TextDecorations!, d => d.Location == TextDecorationLocation.Underline);

        // It must be accent-coloured (DynamicResource → ISolidColorBrush, same assertion pattern as Fr16).
        Assert.IsAssignableFrom<ISolidColorBrush>(autolink.Foreground);
    }

    /// <summary>
    /// SC-4 meaningful regression — renders a doc containing entities, an image, and a footnote; asserts
    /// (a) the control tree contains NO run starting with "Markdig." (no type-name garbage from any fix),
    /// and (b) the preview exposes no editable control and the source buffer is untouched (SC-4).
    /// </summary>
    [AvaloniaFact]
    public void Sc4MeaningfulRegressionPreviewHasNoTypeNameGarbageAndBufferIsUntouched()
    {
        const string source = "AT&amp;T ![logo](logo.png) and a note.[^1]\n\n[^1]: Footnote.\n";
        string captured = new string(source.ToCharArray()); // independent copy

        Control tree = MarkdownRenderer.Render(captured);

        // SC-4: source untouched.
        Assert.Equal(source, captured);

        // No type-name garbage anywhere in the rendered tree.
        var allRunTexts = AllRuns(tree).Select(r => r.Text ?? string.Empty).ToList();
        Assert.DoesNotContain(allRunTexts, t => t.StartsWith("Markdig.", StringComparison.Ordinal));

        // The preview tree is read-only (SC-4: no caret).
        Assert.Empty(Descendants(tree).OfType<TextBox>());
        Assert.Empty(Descendants(tree).OfType<AvaloniaEdit.TextEditor>());
    }

    /// <summary>
    /// AbbreviationInline regression — an abbreviated word must appear in the preview, NOT be silently
    /// dropped. UseAdvancedExtensions() rewrites "HTML" to AbbreviationInline when a "*[HTML]: ..."
    /// definition is present. Without the explicit arm it hit default: break → the word vanished.
    /// </summary>
    [AvaloniaFact]
    public void AbbreviationInlineRendersDisplayWordNotSilentDrop()
    {
        // The definition line is consumed by the parser; the word "HTML" in the body becomes an
        // AbbreviationInline whose Abbreviation.Label == "HTML".
        const string md = "*[HTML]: HyperText Markup Language\n\nHTML is great.\n";
        Control tree = MarkdownRenderer.Render(md);

        var runs = AllRuns(tree).Select(r => r.Text ?? string.Empty).ToList();

        // The abbreviated word must appear as a run (not dropped, not a type name).
        Assert.Contains(runs, t => t == "HTML");
        Assert.DoesNotContain(runs, t => t.StartsWith("Markdig.", StringComparison.Ordinal));
    }

    // ================================================================================================
    //  Helpers.
    // ================================================================================================

    // Traverse the LOGICAL tree (not the visual tree): the rendered preview is asserted unattached/unmeasured,
    // so the visual tree is empty until a layout pass; logical children are populated as soon as added. Also
    // descend into TextBlock.Inlines so InlineUIContainer-hosted controls (the task-list checkbox glyphs) are
    // reached without a layout pass — they are not logical children of the TextBlock until measured.
    private static System.Collections.Generic.IEnumerable<Control> Descendants(Control root)
    {
        yield return root;

        if (root is TextBlock { Inlines: { } inlines })
            foreach (Inline inline in Flatten(inlines))
                if (inline is InlineUIContainer { Child: Control hosted })
                    foreach (var d in Descendants(hosted))
                        yield return d;

        foreach (var child in root.GetLogicalChildren().OfType<Control>())
            foreach (var d in Descendants(child))
                yield return d;
    }

    private static System.Collections.Generic.IEnumerable<Run> AllRuns(Control root) =>
        Descendants(root)
            .OfType<TextBlock>()
            .Where(t => t.Inlines is not null)
            .SelectMany(t => Flatten(t.Inlines!))
            .OfType<Run>();

    private static System.Collections.Generic.IEnumerable<Inline> Flatten(InlineCollection inlines)
    {
        foreach (Inline inline in inlines)
        {
            yield return inline;
            if (inline is Span span)
                foreach (Inline child in Flatten(span.Inlines))
                    yield return child;
        }
    }

    private static EditorViewModel NewMarkdownEditorViewModel(string text) =>
        NewEditorViewModelFor("notes.md", text, "Markdown");

    private static EditorViewModel NewEditorViewModelFor(string fileName, string text, string label)
    {
        var options = new HoardPadRegistryOptions(ThemeName.OneDark);
        var fileService = new EditorFileService(new AtomicFileStore(), new PathResolver(), new HoardPad.App.Hosting.UtfDefaultEncodingDetector());
        var catalog = new LanguageCatalog(options);
        var doc = new EditorDocument { Path = fileName, Text = text };
        (string? scope, string resolvedLabel) = catalog.Resolve(fileName);
        doc.GrammarScope = scope;
        doc.LanguageLabel = label.Length > 0 ? label : resolvedLabel;
        var vm = new EditorViewModel(fileService, options, catalog, doc);
        vm.SyncSegmentsFromDocument();
        return vm;
    }
}
