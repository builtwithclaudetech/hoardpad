// EditorDocument.cs — the open-document / tab model (FR-1, design §2). One per open buffer; the editor
// view binds the active document. Holds the durable identity (path), the buffer's decoded text, and the
// FR-6 encoding / EOL / language facts the status bar shows. The AvaloniaEdit TextDocument itself lives
// in the view; this model carries the open/save state and the on-disk encoding contract.

namespace HoardPad.App.Editor;

/// <summary>
/// An open document. A never-saved scratch buffer has a null <see cref="Path"/> and the
/// new-buffer defaults (UTF-8, LF, Plain Text). Encoding + EOL + BOM are captured on open and
/// preserved on save (FR-6 / NFR-9), so a load→save with no edits round-trips byte-for-byte.
/// </summary>
internal sealed class EditorDocument
{
    /// <summary>The canonical on-disk path, or null for a never-saved scratch buffer.</summary>
    public string? Path { get; set; }

    /// <summary>The decoded buffer text (the source of truth the editor's TextDocument mirrors).</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>The buffer's effective encoding name (FR-6 — the on-save preserve target).</summary>
    public string EncodingName { get; set; } = EncodingDecisionDefault;

    /// <summary>Whether the original file carried a byte-order mark (preserved on save for byte-correctness).</summary>
    public bool HasBom { get; set; }

    /// <summary>The buffer's line-ending style (FR-6 EOL display).</summary>
    public LineEnding LineEnding { get; set; } = LineEnding.Lf;

    /// <summary>The TextMate scope for syntax highlighting (FR-7), or null for Plain Text.</summary>
    public string? GrammarScope { get; set; }

    /// <summary>The status-bar language label (FR-8). "Plain Text" for an unrecognized/scratch buffer.</summary>
    public string LanguageLabel { get; set; } = "Plain Text";

    /// <summary>The display name (file name, or "untitled" for a scratch buffer).</summary>
    public string DisplayName =>
        Path is null ? "untitled" : System.IO.Path.GetFileName(Path);

    /// <summary>UTF-8 — the FR-6 default for new buffers (mirrors <c>EncodingDecision.DefaultEncoding</c>).</summary>
    private const string EncodingDecisionDefault = "utf-8";
}
