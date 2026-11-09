// MarkdownPreviewControl.cs — the FR-15 read-only preview surface (design §4 / §3.6).
//
// Hosts the Candidate-A rendered control tree (MarkdownRenderer) inside a scroll viewer. It is a SEPARATE
// read-only visual tree built from the parse — no caret, no buffer reference — so SC-4 (toggle-back
// byte-identical buffer) holds by construction (FR-15). The surface is created once and re-rendered on
// demand via Render(source); the editor's TextEditor (the buffer) is never touched.
//
// NFR-9 (design §3.6 / OQ-5): above the directional ~100–200 KB preview threshold the document is NOT
// auto-rendered — instead a calm, non-error notice with a [Render anyway] override is shown (DP-6). The
// override renders the full document at the user's responsiveness risk.

using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Controls.Primitives;

namespace HoardPad.App.Markdown;

/// <summary>
/// The FR-15 rendered Markdown preview — a read-only, scrollable surface over the Candidate-A control tree
/// (design §4). It exposes no caret and never mutates the source buffer (SC-4). Re-rendered via
/// <see cref="Render"/>; above the NFR-9 threshold it shows a [Render anyway] notice instead of auto-rendering.
/// </summary>
internal sealed class MarkdownPreviewControl : UserControl
{
    /// <summary>
    /// The NFR-9 preview auto-render threshold (design §3.6 / OQ-5). DIRECTIONAL / single-sample — the exact
    /// boundary is Phase-8-confirmed (SPIKE-1 found preview layout scales supra-linearly, ~282 ms @ 100 KB).
    /// The design requirement is that suppression is visible + user-overridable, NOT the precise byte boundary.
    /// 192 KB sits mid the ~100–200 KB band. Measured against the UTF-8 byte length of the source.
    /// </summary>
    internal const int PreviewAutoRenderMaxBytes = 192 * 1024;

    private readonly ScrollViewer _scroll;
    private string _source = string.Empty;

    public MarkdownPreviewControl()
    {
        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Avalonia.Thickness(16),
        };
        // The preview surface paints the brand surface so it reads as a document page (brand.md §4 body).
        this.Bind(BackgroundProperty, new DynamicResourceExtension("Brand.Surface.Brush"));
        Content = _scroll;
    }

    /// <summary>The byte length the NFR-9 threshold is compared against (UTF-8 — the FR-6 default encoding).</summary>
    internal static int SourceByteLength(string source) =>
        System.Text.Encoding.UTF8.GetByteCount(source ?? string.Empty);

    /// <summary>
    /// Render <paramref name="source"/> into the preview (FR-15/FR-16). Above the NFR-9 threshold the
    /// document is NOT auto-rendered — the calm [Render anyway] notice is shown instead (design §3.6). The
    /// preview always scrolls to the top on a fresh render (design §4.4). Never touches the source buffer.
    /// </summary>
    public void Render(string source)
    {
        _source = source ?? string.Empty;
        int bytes = SourceByteLength(_source);

        _scroll.Content = bytes > PreviewAutoRenderMaxBytes
            ? BuildLargeDocNotice(bytes)
            : MarkdownRenderer.Render(_source);

        _scroll.Offset = default; // open scrolled to the top of the document (design §4.4).
    }

    /// <summary>
    /// The NFR-9 large-Markdown notice (design §3.6): "Preview not auto-rendered for large Markdown (N KB).
    /// [Render anyway]". Calm, non-error (muted text, DP-6); the button force-renders at the user's risk.
    /// </summary>
    private StackPanel BuildLargeDocNotice(int bytes)
    {
        int kb = (int)Math.Ceiling(bytes / 1024.0);

        var message = new TextBlock
        {
            Text = $"Preview not auto-rendered for large Markdown ({kb.ToString(CultureInfo.InvariantCulture)} KB).",
            TextWrapping = TextWrapping.Wrap,
        };
        message.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("Brand.Muted.Brush"));

        var renderAnyway = new Button { Content = "Render anyway" };
        renderAnyway.Click += (_, _) =>
        {
            // Force the full render at the user's responsiveness risk (NFR-9 override honored — design §3.6).
            _scroll.Content = MarkdownRenderer.Render(_source);
            _scroll.Offset = default;
        };

        return new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children = { message, renderAnyway },
        };
    }
}
