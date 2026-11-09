// LineEnding.cs — the FR-6 line-ending style (status-bar EOL segment, design §3.4). Detected on open
// from the buffer's first delimiter; LF is HoardPad's new-buffer default (NFR-9).

namespace HoardPad.App.Editor;

/// <summary>The buffer's line-ending style (FR-6 EOL display).</summary>
internal enum LineEnding
{
    /// <summary>Unix LF (<c>\n</c>) — HoardPad's new-buffer default (NFR-9).</summary>
    Lf,

    /// <summary>Windows CRLF (<c>\r\n</c>).</summary>
    Crlf,

    /// <summary>Classic-Mac CR (<c>\r</c>).</summary>
    Cr,
}

/// <summary>EOL detection + display helpers.</summary>
internal static class LineEndings
{
    /// <summary>Detect the dominant line ending from raw text (first delimiter wins; LF default for none).</summary>
    public static LineEnding Detect(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
                return i + 1 < text.Length && text[i + 1] == '\n' ? LineEnding.Crlf : LineEnding.Cr;
            if (c == '\n')
                return LineEnding.Lf;
        }
        return LineEnding.Lf;
    }

    /// <summary>The status-bar label for an EOL style ("LF" / "CRLF" / "CR").</summary>
    public static string DisplayLabel(LineEnding ending) => ending switch
    {
        LineEnding.Crlf => "CRLF",
        LineEnding.Cr => "CR",
        _ => "LF",
    };
}
