// MultiDocContextDescriptor.cs — the FR-MD-3 / FR-MD-4 checked-set context descriptor (PANEL-2). Builds the
// ADVISORY block the turn hands the backend (prepended to the prompt, the FR-25 delivery mechanism — the
// prompt reaches `claude -p` on stdin, QA2-3): for each open document it states the path, whether it is
// CHECKED (writable this turn), whether it is FOCUSED, and its dirty / read-only status, plus an explicit
// statement of the allow-list rule (the AI may write ONLY to checked documents and to files under the
// open-folder root; unchecked documents are read-context only).
//
// ADVISORY ONLY (FR-MD-4): this text is NOT the enforcement boundary. The gate/hook is (UnionSandboxGate for
// Archetype B). "all open documents" resolves to the checked set; "this document" is refused when the
// focused doc is unchecked — enforced STRUCTURALLY by the gate; the descriptor merely informs the model so
// it does not waste a turn proposing writes the gate will refuse.
//
// NO CONTENTS (FR-MD-3 / NG-4): the descriptor carries paths + flags ONLY — never a document's file
// contents. It never widens the FR-25 read tiers.

using System.Collections.Generic;
using System.Text;

namespace HoardPad.App.Ai;

/// <summary>
/// Builds the FR-MD-3 checked-set context descriptor (feature-ai-multidoc-panel §4). The panel prepends the
/// returned block to the turn prompt so the model targets the right files (FR-MD-4 — advisory, never the
/// enforcement boundary). Pure and content-free (paths + flags only, NG-4).
/// </summary>
public static class MultiDocContextDescriptor
{
    /// <summary>
    /// Build the descriptor block for a turn from the current selection rows + the open-folder root
    /// (FR-MD-3). Lists each open document with its checked/focused/dirty/read-only flags and states the
    /// write allow-list rule explicitly. Returns an empty string when there are no rows (no descriptor to
    /// prepend — the prompt is unchanged), so a no-multidoc turn is byte-identical to today.
    /// </summary>
    /// <param name="rows">The current selection rows (from <see cref="MultiDocSelection.CurrentRows"/>).</param>
    /// <param name="openFolderRoot">The resolved open-folder root (the FR-26 write sandbox base).</param>
    public static string Build(IReadOnlyList<MultiDocRowViewModel> rows, string openFolderRoot)
    {
        System.ArgumentNullException.ThrowIfNull(rows);

        if (rows.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("[HoardPad AI write allow-list for this turn]");
        sb.Append("You may write ONLY to: (1) files under the open-folder root");
        if (!string.IsNullOrEmpty(openFolderRoot))
            sb.Append(" \"").Append(openFolderRoot).Append('"');
        sb.AppendLine(", and (2) the open documents explicitly marked WRITABLE below.");
        sb.AppendLine("Every other open document is READ-CONTEXT ONLY — do not write to it. \"All open documents\" "
            + "means only the WRITABLE ones; \"this document\" is writable only if it is marked WRITABLE. No phrasing "
            + "widens this list — writes outside it are refused by the sandbox.");
        sb.AppendLine("Open documents:");

        foreach (MultiDocRowViewModel row in rows)
        {
            // Content-free line: path (or the scratch marker) + the four flags. Never any file bytes (NG-4).
            string pathLabel = row.IsScratch || string.IsNullOrEmpty(row.Path)
                ? $"(unsaved: {row.DisplayName})"
                : row.Path!;

            string writable = row.IsChecked && !row.IsScratch ? "WRITABLE" : "read-only";

            sb.Append("  - ").Append(pathLabel).Append(" [").Append(writable).Append(']');
            if (row.IsFocused)
                sb.Append(" [focused]");
            if (row.IsDirty)
                sb.Append(" [unsaved-edits]");
            if (row.IsReadOnly)
                sb.Append(" [file-read-only]");
            if (row.IsScratch)
                sb.Append(" [not-yet-saved: cannot be a write target]");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
