using System.Text.Json;
using HoardPad.Core.Records;

namespace HoardPad.Core.Ai.FileOps;

/// <summary>
/// Serializes the §9.3 activity-log record and the §9.4 undo-journal entry to the single-line UTF-8 JSON
/// PAYLOAD that <see cref="HoardPad.Core.Host.Abstractions.IAtomicFileStore.AppendRecordAsync"/> frames
/// (<c>[4B LE len][payload][4B CRC-32]</c>). This is the C-8 uniform-structure boundary: both record
/// types serialize through the SAME options so the on-disk shape is uniform.
/// </summary>
/// <remarks>
/// <b>Control-char / log-forging defense (M2.2 Gate-1 W3, carried forward — binding).</b> Paths are
/// serialized as JSON STRINGS via <see cref="System.Text.Json"/>, which escapes <c>\n</c>, <c>\r</c>, and
/// every other control character inside the string. A path containing an embedded newline therefore
/// produces exactly ONE NDJSON record line, never two — an attacker-supplied path cannot forge a second
/// log record. We deliberately do NOT use <c>UnsafeRelaxedJsonEscaping</c> here (that encoder is for the
/// <c>.hoard</c> document body, TD §3.2 — where literal non-ASCII is the canonical form); the activity
/// log / undo journal use the STRICT default encoder so control chars stay escaped. The record is
/// written WITHOUT a trailing newline: the length-prefixed frame, not a newline, delimits records on disk.
/// </remarks>
public static class RecordSerialization
{
    // Strict default escaping (control chars + HTML-sensitive chars escaped) — the log-forging defense.
    // Compact (no indentation) so the framed payload is one line. No trailing newline (the frame delimits).
    private static readonly JsonSerializerOptions LogOptions = new()
    {
        WriteIndented = false,
        // Default (strict) encoder: \n / \r / \t / other control chars are escaped inside string values.
    };

    /// <summary>Serialize one §9.3 activity-log record to its single-line UTF-8 JSON payload bytes.</summary>
    public static byte[] SerializeActivity(ActivityLogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return JsonSerializer.SerializeToUtf8Bytes(record, LogOptions);
    }

    /// <summary>Serialize one §9.4 undo-journal entry to its single-line UTF-8 JSON payload bytes.</summary>
    public static byte[] SerializeJournal(UndoJournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return JsonSerializer.SerializeToUtf8Bytes(entry, LogOptions);
    }

    /// <summary>Decode a payload back to its UTF-8 string (test/round-trip helper).</summary>
    // System.Text.Encoding is fully qualified: HoardPad.Core has an 'Encoding' namespace that would
    // otherwise shadow System.Text.Encoding under ImplicitUsings.
    public static string ToText(ReadOnlyMemory<byte> payload) =>
        System.Text.Encoding.UTF8.GetString(payload.Span);
}
