// MarkdownRenderer.cs — the Candidate-A FR-15/FR-16 preview renderer (design §4, tech-design §1.6/§5.1).
//
// A custom walker over Markdig's MarkdownDocument AST that emits a READ-ONLY Avalonia control tree — NO
// WebView/HTML/CEF at any layer (CLAUDE.md §2 / FR-15). The produced tree is a separate visual tree built
// from the parse: it has no caret and never touches the editor buffer, so SC-4 (toggle-back byte-identical
// buffer) holds by construction (FR-15). The acceptance basis is the FR-16 per-feature control-tree shape
// (design §4.3), NOT pixel-parity with VS Code (tech-design §1.6).
//
// Parsing: Markdig CORE + .UseAdvancedExtensions() ONLY — zero extra Markdig packages. UseAdvancedExtensions
// covers the full FR-16 GFM set: tables, task-lists, footnotes, autolinks, strikethrough (tech-design §1.6).
//
// Brand binding: every colour/font is a DynamicResource against the brand.md §4 preview tokens (added to
// Theme/BrandTheme.axaml), so the preview switches dark↔light in lockstep with the chrome (FR-37). Body +
// headings use the UI font (Inter); code uses Brand.Font.Code (brand.md §5). No hex is hardcoded here.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Markdig;
using Markdig.Extensions.Abbreviations;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using AutolinkInline = Markdig.Syntax.Inlines.AutolinkInline;

// The Markdig and Avalonia inline types collide on the simple name "Inline"; alias the Markdig AST one.
using MdInline = Markdig.Syntax.Inlines.Inline;

namespace HoardPad.App.Markdown;

/// <summary>
/// Renders a Markdown source string into a read-only Avalonia control tree (the Candidate-A FR-15 preview).
/// The returned control is a vertical stack of block controls; it never mutates the source and exposes no
/// caret (FR-15 / SC-4). Brand colours/fonts bind via <c>DynamicResource</c> so the live theme switch
/// propagates (FR-37). Stateless — one parse + one tree per call (tech-design §5.1: the build is cheap).
/// </summary>
internal static class MarkdownRenderer
{
    // The single Markdig pipeline (FR-16): core CommonMark + the advanced (GFM) extension bundle ONLY.
    // .UseAdvancedExtensions() pulls in tables, task-lists, footnotes, autolinks, and strikethrough — the
    // exact FR-16 set — with ZERO extra Markdig.Extensions.* NuGet packages (tech-design §1.6, build-context).
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    // ----- Typographic scale (design §4.3 "VS Code-grade" feature presence, not pixel-parity). -----
    private const double BodyFontSize = 14;
    private static readonly double[] HeadingSizes = { 26, 22, 18, 16, 15, 14 }; // h1..h6
    private const double BlockSpacing = 8;

    /// <summary>
    /// Parse <paramref name="markdown"/> and build the read-only preview control tree (FR-15/FR-16). The
    /// result is a panel of block controls in document order; it holds no reference to the source buffer.
    /// </summary>
    public static Control Render(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        MarkdownDocument document = Markdig.Markdown.Parse(markdown, Pipeline);

        var root = new StackPanel { Spacing = BlockSpacing };
        foreach (Block block in document)
            if (RenderBlock(block) is { } control)
                root.Children.Add(control);

        return root;
    }

    // ===================================================================================================
    //  Block-level mapping (AST Block -> Avalonia control). Returns null for a block with no visual.
    // ===================================================================================================

    private static Control? RenderBlock(Block block) => block switch
    {
        HeadingBlock heading => RenderHeading(heading),
        ParagraphBlock paragraph => RenderParagraph(paragraph),
        ListBlock list => RenderList(list),
        QuoteBlock quote => RenderQuote(quote),
        ThematicBreakBlock => RenderThematicBreak(),
        FencedCodeBlock fenced => RenderFencedCode(fenced),
        CodeBlock code => RenderIndentedCode(code), // an indented (non-fenced) code block.
        Table table => RenderTable(table),
        FootnoteGroup footnotes => RenderFootnoteGroup(footnotes),
        _ => RenderContainerFallback(block), // any other container (e.g. nested) renders its children.
    };

    private static TextBlock RenderHeading(HeadingBlock heading)
    {
        int level = Math.Clamp(heading.Level, 1, 6);
        // brand.md §4: h1–h3 carry the amethyst jewel (md.heading); h4–h6 the sapphire (md.heading-minor).
        string brushKey = level <= 3 ? "Brand.Md.Heading.Brush" : "Brand.Md.HeadingMinor.Brush";

        var text = new TextBlock
        {
            FontSize = HeadingSizes[level - 1],
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        text.Inlines = BuildInlines(heading.Inline);
        BindForeground(text, brushKey);
        return text;
    }

    private static TextBlock RenderParagraph(ParagraphBlock paragraph)
    {
        var text = new TextBlock
        {
            FontSize = BodyFontSize,
            TextWrapping = TextWrapping.Wrap,
        };
        text.Inlines = BuildInlines(paragraph.Inline);
        BindForeground(text, "Brand.OnSurface.Brush"); // body text = on-surface (brand.md §4).
        return text;
    }

    private static StackPanel RenderList(ListBlock list)
    {
        var panel = new StackPanel { Spacing = 2 };
        int ordinal = list.IsOrdered ? ParseOrderedStart(list.OrderedStart) : 0;

        foreach (Block item in list)
        {
            if (item is not ListItemBlock listItem)
                continue;

            // The marker: an ordered "1." / unordered "•" in muted (brand.md §4 — de-emphasised markers).
            // A task-list item carries its glyph inline (the TaskList inline), so suppress the bullet there.
            bool isTask = ListItemIsTask(listItem);
            string marker = isTask ? string.Empty : list.IsOrdered ? $"{ordinal}." : "•";

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            if (marker.Length > 0)
            {
                var markerText = new TextBlock { Text = marker, FontSize = BodyFontSize, Margin = new Thickness(0, 0, 6, 0) };
                BindForeground(markerText, "Brand.Muted.Brush");
                Grid.SetColumn(markerText, 0);
                row.Children.Add(markerText);
            }

            var content = new StackPanel { Spacing = 2 };
            foreach (Block child in listItem)
                if (RenderBlock(child) is { } c)
                    content.Children.Add(c);
            Grid.SetColumn(content, 1);
            row.Children.Add(content);

            panel.Children.Add(row);
            if (list.IsOrdered)
                ordinal++;
        }

        return panel;
    }

    private static Grid RenderQuote(QuoteBlock quote)
    {
        // brand.md §4: a full-height teal left bar (structural, not the banned one-sided card stripe) + muted text.
        var bar = new Border { Width = 3, Margin = new Thickness(0, 0, 8, 0) };
        BindBackground(bar, "Brand.Md.QuoteBar.Brush");

        var content = new StackPanel { Spacing = BlockSpacing };
        foreach (Block child in quote)
            if (RenderBlock(child) is { } c)
            {
                if (c is TextBlock tb)
                    BindForeground(tb, "Brand.Muted.Brush"); // quote text is muted (brand.md §4).
                content.Children.Add(c);
            }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(bar, 0);
        Grid.SetColumn(content, 1);
        grid.Children.Add(bar);
        grid.Children.Add(content);
        return grid;
    }

    private static Border RenderThematicBreak()
    {
        // brand.md §4: the <hr> rule is the gold-fire (md.rule) brand signal — a sparing section divider.
        var rule = new Border { Height = 2, Margin = new Thickness(0, 6, 0, 6), HorizontalAlignment = HorizontalAlignment.Stretch };
        BindBackground(rule, "Brand.Md.Rule.Brush");
        return rule;
    }

    private static Border RenderFencedCode(FencedCodeBlock fenced)
    {
        // FR-16 (2): a code block displaying its LANGUAGE LABEL + the code in plain mono. Preview code-block
        // syntax colouring is explicitly NOT an FR-16 requirement (tech-design §1.6) — plain mono only.
        string language = (fenced.Info ?? string.Empty).Trim();
        string code = ExtractCode(fenced);
        return BuildCodeBlock(language, code);
    }

    private static Border RenderIndentedCode(CodeBlock code) => BuildCodeBlock(string.Empty, ExtractCode(code));

    private static Border BuildCodeBlock(string language, string code)
    {
        var panel = new StackPanel();

        if (language.Length > 0)
        {
            var label = new TextBlock
            {
                Text = language,
                FontSize = 11,
                FontFamily = FontFamily.Default, // the label is chrome → UI font.
                Margin = new Thickness(8, 4, 8, 2),
            };
            BindForeground(label, "Brand.Muted.Brush");
            panel.Children.Add(label);
        }

        var codeText = new TextBlock
        {
            Text = code,
            FontSize = BodyFontSize,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(8, language.Length > 0 ? 0 : 8, 8, 8),
        };
        BindFontFamily(codeText, "Brand.Font.Code"); // brand.md §5 mono.
        BindForeground(codeText, "Brand.OnSurface.Brush");
        panel.Children.Add(codeText);

        var border = new Border { Child = panel, CornerRadius = new CornerRadius(4) };
        BindBackground(border, "Brand.Md.CodeBlock.Brush"); // fenced code-block tint (brand.md §4).
        return border;
    }

    private static MarkdownTableGrid RenderTable(Table table)
    {
        // FR-16 (1): a grid with the EXPECTED ROW/COLUMN COUNT. Built as an Avalonia Grid; the
        // ColumnCount/RowCount are exposed on MarkdownTableGrid so the fidelity test asserts them directly.
        var rows = table.OfType<TableRow>().ToList();
        int columnCount = rows.Count == 0 ? 0 : rows.Max(r => r.OfType<TableCell>().Count());

        var grid = new MarkdownTableGrid { ColumnCount = columnCount, RowCount = rows.Count };
        for (int c = 0; c < columnCount; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (int r = 0; r < rows.Count; r++)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (int r = 0; r < rows.Count; r++)
        {
            TableRow row = rows[r];
            var cells = row.OfType<TableCell>().ToList();
            for (int c = 0; c < cells.Count; c++)
            {
                Control cellContent = RenderCellContent(cells[c], row.IsHeader);
                var cellBorder = new Border
                {
                    Child = cellContent,
                    Padding = new Thickness(8, 4),
                    BorderThickness = new Thickness(1),
                };
                BindBorderBrush(cellBorder, "Brand.OverlaySurface.Brush");
                Grid.SetRow(cellBorder, r);
                Grid.SetColumn(cellBorder, c);
                grid.Children.Add(cellBorder);
            }
        }

        return grid;
    }

    private static StackPanel RenderCellContent(TableCell cell, bool isHeader)
    {
        var stack = new StackPanel { Spacing = 2 };
        foreach (Block child in cell)
        {
            if (child is ParagraphBlock paragraph)
            {
                var text = new TextBlock { FontSize = BodyFontSize, TextWrapping = TextWrapping.Wrap };
                if (isHeader)
                    text.FontWeight = FontWeight.SemiBold;
                text.Inlines = BuildInlines(paragraph.Inline);
                BindForeground(text, "Brand.OnSurface.Brush");
                stack.Children.Add(text);
            }
            else if (RenderBlock(child) is { } c)
            {
                stack.Children.Add(c);
            }
        }
        return stack;
    }

    private static StackPanel RenderFootnoteGroup(FootnoteGroup group)
    {
        // FR-16 footnotes: a footnotes SECTION (a gold-fire rule + the numbered notes). The in-text markers
        // are rendered inline by the FootnoteLink handler in BuildInlines; this is the section at the tail.
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(RenderThematicBreak());

        foreach (Block item in group)
        {
            if (item is not Footnote footnote)
                continue;

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var marker = new TextBlock
            {
                Text = footnote.Order.ToString(CultureInfo.InvariantCulture) + ".",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Top,
            };
            BindForeground(marker, "Brand.Muted.Brush");
            row.Children.Add(marker);

            var body = new StackPanel { Spacing = 2 };
            foreach (Block child in footnote)
            {
                // Skip the trailing back-link-only artifacts; render the note's prose blocks.
                if (RenderBlock(child) is { } c)
                    body.Children.Add(c);
            }
            row.Children.Add(body);
            panel.Children.Add(row);
        }

        return panel;
    }

    private static StackPanel? RenderContainerFallback(Block block)
    {
        if (block is not ContainerBlock container)
            return null;
        var panel = new StackPanel { Spacing = BlockSpacing };
        foreach (Block child in container)
            if (RenderBlock(child) is { } c)
                panel.Children.Add(c);
        return panel.Children.Count == 0 ? null : panel;
    }

    // ===================================================================================================
    //  Inline-level mapping (AST Inline -> Avalonia Inline runs).
    // ===================================================================================================

    private static InlineCollection BuildInlines(ContainerInline? container)
    {
        var collection = new InlineCollection();
        if (container is not null)
            foreach (MdInline inline in container)
                AppendInline(collection, inline, bold: false, italic: false, strike: false);
        return collection;
    }

    private static void AppendInline(InlineCollection target, MdInline inline, bool bold, bool italic, bool strike)
    {
        switch (inline)
        {
            case LiteralInline literal:
                target.Add(StyledRun(literal.Content.ToString(), bold, italic, strike));
                break;

            case EmphasisInline emphasis:
                {
                    // DelimiterChar discriminates: '*'/'_' = emphasis (1 = italic, 2 = bold); '~' = strikethrough.
                    bool nextBold = bold || (emphasis.DelimiterChar != '~' && emphasis.DelimiterCount >= 2);
                    bool nextItalic = italic || (emphasis.DelimiterChar != '~' && emphasis.DelimiterCount == 1);
                    bool nextStrike = strike || emphasis.DelimiterChar == '~';
                    foreach (MdInline child in emphasis)
                        AppendInline(target, child, nextBold, nextItalic, nextStrike);
                    break;
                }

            case CodeInline code:
                {
                    // brand.md §4: inline code = mono on the code tint (md.code-inline-fg on md.code-tint).
                    var run = new Run(code.Content) { FontSize = BodyFontSize };
                    BindRunFontFamily(run, "Brand.Font.Code");
                    BindRunForeground(run, "Brand.OnSurface.Brush");
                    BindRunBackground(run, "Brand.Md.CodeTint.Brush");
                    target.Add(run);
                    break;
                }

            case TaskList task:
                // FR-16 (3): a NON-INTERACTIVE display-only checkbox glyph ([x]/[ ]). It is a drawn control
                // (a hit-test-disabled CheckBox via an InlineUIContainer), never a raw Unicode codepoint that
                // could fall back to a missing-glyph box. It cannot toggle (the preview is read-only).
                target.Add(new InlineUIContainer(BuildTaskGlyph(task.Checked)));
                break;

            case FootnoteLink footnoteLink when !footnoteLink.IsBackLink:
                {
                    // The in-text footnote MARKER (superscript-style "[n]"). The back-link variant (in the
                    // footnote group) is suppressed — the section renders its own ordinal.
                    int order = footnoteLink.Footnote?.Order ?? footnoteLink.Index;
                    var run = new Run($"[{order.ToString(CultureInfo.InvariantCulture)}]") { FontSize = 11, BaselineAlignment = BaselineAlignment.Top };
                    BindRunForeground(run, "Brand.Accent.Brush");
                    target.Add(run);
                    break;
                }

            case LinkInline { IsImage: true } image:
                {
                    // FR-16: images are not rendered (M17 deferred — no local-relative image loading in M6.4).
                    // Show the alt text (the link's literal child text) rather than silently dropping the node
                    // or emitting a type-name string via ToString() (C1 fix).
                    string alt = string.Concat(image.OfType<LiteralInline>().Select(l => l.Content.ToString()));
                    if (alt.Length > 0)
                        target.Add(StyledRun(alt, bold, italic, strike));
                    break;
                }

            case LinkInline link:
                {
                    // FR-16 (5): links + autolinks are link-styled (accent + UNDERLINED, never colour-alone —
                    // brand.md §4) but NAVIGATION IS OUT OF SCOPE (NFR-7) — no click handler, no browser launch.
                    var run = new Run(LinkDisplayText(link)) { TextDecorations = TextDecorations.Underline };
                    if (bold) run.FontWeight = FontWeight.Bold;
                    if (italic) run.FontStyle = FontStyle.Italic;
                    BindRunForeground(run, "Brand.Accent.Brush");
                    target.Add(run);
                    break;
                }

            case AutolinkInline auto:
                {
                    // W2 fix: bracketed autolinks <https://x> parse as AutolinkInline (a LeafInline), NOT
                    // LinkInline, so they bypass the link case without this explicit arm. Apply the same
                    // FR-16 (5) link styling — accent + underline, no browser navigation (NFR-7).
                    var run = new Run(auto.Url) { TextDecorations = TextDecorations.Underline };
                    if (bold) run.FontWeight = FontWeight.Bold;
                    if (italic) run.FontStyle = FontStyle.Italic;
                    BindRunForeground(run, "Brand.Accent.Brush");
                    target.Add(run);
                    break;
                }

            case HtmlEntityInline entity:
                // C1 fix: HtmlEntityInline.Transcoded is a StringSlice with the decoded character (e.g.
                // "&amp;" → "&", "&copy;" → "©"). Never call ToString() on the Markdig inline itself —
                // it returns the fully-qualified type name (Markdig.Syntax.Inlines.HtmlEntityInline).
                target.Add(StyledRun(entity.Transcoded.ToString(), bold, italic, strike));
                break;

            case HtmlInline html:
                // C1 fix: raw inline HTML (e.g. <b>text</b>) — display the literal markup as plain text.
                // We cannot render HTML in the Avalonia control tree without a WebView (FR-15 constraint);
                // the honest passthrough shows the source without interpreting it and avoids the type-name
                // garbage that inline.ToString() would emit.
                target.Add(StyledRun(html.Tag.ToString(), bold, italic, strike));
                break;

            case FootnoteLink { IsBackLink: true }:
                // C2 fix: the back-link variant (the ↩ at the end of a footnote body paragraph) must be
                // dropped here — the footnote section renders its own ordinal. Without this explicit arm it
                // fell to default → ToString() → type-name garbage appended after every footnote note.
                break;

            case LineBreakInline:
                target.Add(new LineBreak());
                break;

            case AbbreviationInline abbr:
                // UseAdvancedExtensions() produces AbbreviationInline for abbreviated words defined via
                // "*[HTML]: ..." syntax. Abbreviation.Label is the display word ("HTML"). The tooltip/title
                // expansion is an HTML <abbr title=...> concept that has no Avalonia equivalent in the
                // read-only preview — render the word as plain body text and do not drop it silently.
                target.Add(StyledRun(abbr.Abbreviation?.Label ?? string.Empty, bold, italic, strike));
                break;

            case ContainerInline nested:
                foreach (MdInline child in nested)
                    AppendInline(target, child, bold, italic, strike);
                break;

            default:
                // Safe fallback for genuinely textless or unrecognised nodes: emit nothing rather than
                // risking a type-name string from ToString(). We have explicit arms for all known inline
                // types; any true unknown has no sensible text representation (C1 fix).
                break;
        }
    }

    private static Run StyledRun(string text, bool bold, bool italic, bool strike)
    {
        // brand.md §4: <strong>/<em> are weight/italic only — NO recolour (they inherit on-surface).
        var run = new Run(text) { FontSize = BodyFontSize };
        if (bold) run.FontWeight = FontWeight.Bold;
        if (italic) run.FontStyle = FontStyle.Italic;
        if (strike) run.TextDecorations = TextDecorations.Strikethrough;
        return run;
    }

    private static CheckBox BuildTaskGlyph(bool isChecked) => new CheckBox
    {
        IsChecked = isChecked,
        IsEnabled = false,          // display-only — cannot toggle (FR-16 (3) / FR-15 read-only).
        IsHitTestVisible = false,   // belt-and-braces: no pointer interaction reaches the preview glyph.
        Focusable = false,
        Margin = new Thickness(0, 0, 4, 0),
        MinWidth = 0,
        Padding = new Thickness(0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static string LinkDisplayText(LinkInline link)
    {
        // A labelled link shows its label text; a bare autolink shows the URL itself (brand.md §4 / FR-16).
        string label = string.Concat(link.OfType<LiteralInline>().Select(l => l.Content.ToString()));
        return label.Length > 0 ? label : link.Url ?? string.Empty;
    }

    // ===================================================================================================
    //  Small helpers.
    // ===================================================================================================

    private static int ParseOrderedStart(string? orderedStart) =>
        int.TryParse(orderedStart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 1;

    private static bool ListItemIsTask(ListItemBlock listItem) =>
        listItem.OfType<ParagraphBlock>()
            .Any(p => p.Inline is not null && p.Inline.OfType<TaskList>().Any());

    private static string ExtractCode(LeafBlock code)
    {
        var lines = code.Lines.Lines;
        return string.Join("\n", Enumerable.Range(0, code.Lines.Count).Select(i => lines[i].Slice.ToString()));
    }

    // ----- Brand DynamicResource binding (so the preview switches theme in lockstep — FR-37). -----

    private static void BindForeground(TextBlock target, string brushKey) =>
        target.Bind(TextBlock.ForegroundProperty, BrushResource(brushKey));

    private static void BindBackground(Border target, string brushKey) =>
        target.Bind(Border.BackgroundProperty, BrushResource(brushKey));

    private static void BindBorderBrush(Border target, string brushKey) =>
        target.Bind(Border.BorderBrushProperty, BrushResource(brushKey));

    private static void BindFontFamily(TextBlock target, string fontKey) =>
        target.Bind(TextBlock.FontFamilyProperty, new DynamicResourceExtension(fontKey));

    private static void BindRunForeground(Run run, string brushKey) =>
        run.Bind(Run.ForegroundProperty, BrushResource(brushKey));

    private static void BindRunBackground(Run run, string brushKey) =>
        run.Bind(Run.BackgroundProperty, BrushResource(brushKey));

    private static void BindRunFontFamily(Run run, string fontKey) =>
        run.Bind(Run.FontFamilyProperty, new DynamicResourceExtension(fontKey));

    private static DynamicResourceExtension BrushResource(string key) => new(key);
}

/// <summary>
/// A Markdown table rendered as a <see cref="Grid"/>, carrying the parsed row/column counts so the FR-16
/// fidelity test asserts the table's structure directly (design §4.3 (1)). Read-only display control.
/// </summary>
internal sealed class MarkdownTableGrid : Grid
{
    /// <summary>The number of columns the source table declared (the widest row).</summary>
    public int ColumnCount { get; init; }

    /// <summary>The number of rows the source table declared (header + body).</summary>
    public int RowCount { get; init; }
}
