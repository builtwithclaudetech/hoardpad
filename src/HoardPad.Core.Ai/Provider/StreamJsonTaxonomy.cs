using System.Collections.Immutable;
using System.Text.Json;
using HoardPad.Core.Host.Abstractions;

namespace HoardPad.Core.Ai.Provider;

/// <summary>
/// A WRITE tool-use event the agent emitted, parsed from one <c>stream-json</c> line (architecture §4.2 step 6,
/// TD §4.7, SPIKE-3 I). This is the §4.2 stream-json event taxonomy living in the provider domain — it parses
/// <see cref="AgentStreamEvent.RawJsonLine"/> rather than burdening the host transport with it. Only WRITE tools
/// (<c>Write</c> / <c>Edit</c>) are surfaced — those are the ops MA14 liveness-matches against a hook callback;
/// a <c>Read</c> tool-use mutates nothing and is not gated for liveness.
/// </summary>
/// <param name="ToolUseId">The CLI's <c>tool_use_id</c> — the MA14 correlation key against the hook callback.</param>
/// <param name="ToolName">The write tool name as emitted (<c>Write</c> / <c>Edit</c>).</param>
/// <param name="RawInputJson">
/// The raw JSON string of the <c>tool_use.input</c> block, or <c>null</c> when the block is absent/unparseable.
/// Used by the FR-39 replay path (MF1.4) to rebuild the target path + proposed content bytes from the stream-json
/// record rather than the hook channel (which deliberately omits the content). The JSON is preserved verbatim —
/// callers extract specific fields (e.g. <c>file_path</c> / <c>content</c> / <c>new_string</c>) as needed.
/// </param>
public sealed record WriteToolUseEvent(string ToolUseId, string ToolName, string? RawInputJson = null);

/// <summary>
/// The pure parser turning <c>stream-json</c> lines into the WRITE <see cref="WriteToolUseEvent"/>s MA14
/// correlates (TD §4.7). The host (M5.2) parses the CLI's stdout into <see cref="AgentStreamEvent"/>s carrying the
/// raw line; this domain logic extracts the write tool-use events from those lines. Platform-free: System.Text.Json
/// over a string, no I/O, no clock.
/// </summary>
/// <remarks>
/// <para>
/// Stream-json shape (SPIKE-3 §A/I): an <c>assistant</c> message event carries a <c>message.content[]</c> array
/// whose <c>tool_use</c> blocks each have an <c>id</c> (the <c>tool_use_id</c>) and a <c>name</c>. The parser
/// walks those blocks and keeps the ones whose <c>name</c> is a recognized WRITE tool.
/// </para>
/// <para>
/// Resilience: a malformed / non-JSON / non-tool-use line yields no events (it is not the parser's job to police
/// the stream — MA14 degrades on an <em>unmatched write</em>, never on a parse miss). The recognized write-tool
/// names mirror the FR-24 offered roster (<c>Write</c>, <c>Edit</c>); <c>Read</c> is excluded by design.
/// </para>
/// </remarks>
public static class StreamJsonTaxonomy
{
    private static readonly ImmutableHashSet<string> WriteToolNames =
        ImmutableHashSet.Create(StringComparer.Ordinal, "Write", "Edit");

    /// <summary>True when <paramref name="toolName"/> is a recognized WRITE tool (<c>Write</c> / <c>Edit</c>).</summary>
    public static bool IsWriteTool(string toolName) => WriteToolNames.Contains(toolName);

    /// <summary>
    /// Parse the WRITE tool-use events from one <c>stream-json</c> line. Returns empty for any line that is not a
    /// well-formed assistant-message event containing write <c>tool_use</c> blocks (malformed input is benign — it
    /// simply contributes no events).
    /// </summary>
    public static ImmutableArray<WriteToolUseEvent> ParseWriteToolUses(string rawJsonLine)
    {
        if (string.IsNullOrWhiteSpace(rawJsonLine))
            return ImmutableArray<WriteToolUseEvent>.Empty;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawJsonLine);
        }
        catch (JsonException)
        {
            return ImmutableArray<WriteToolUseEvent>.Empty; // a non-JSON / partial line yields no events.
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return ImmutableArray<WriteToolUseEvent>.Empty;

            // The tool_use blocks live under message.content[]; tolerate content[] at the root too.
            if (!TryGetContentArray(root, out var content))
                return ImmutableArray<WriteToolUseEvent>.Empty;

            var builder = ImmutableArray.CreateBuilder<WriteToolUseEvent>();
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object)
                    continue;
                if (!block.TryGetProperty("type", out var type) ||
                    type.ValueKind != JsonValueKind.String ||
                    !string.Equals(type.GetString(), "tool_use", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!block.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
                    continue;
                var toolName = name.GetString()!;
                if (!IsWriteTool(toolName))
                    continue;

                if (!block.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
                    continue;
                var toolUseId = id.GetString();
                if (string.IsNullOrEmpty(toolUseId))
                    continue;

                // Extract the raw input JSON for the FR-39 replay path (MF1.4). The input block carries the
                // file_path + content/new_string — the only place the proposed content is available in
                // stream-json (the hook channel's _ask_message deliberately omits it). Preserve verbatim;
                // a missing or non-object input block yields null (the replay path handles the null case).
                string? rawInputJson = null;
                if (block.TryGetProperty("input", out var inputBlock) && inputBlock.ValueKind == JsonValueKind.Object)
                    rawInputJson = inputBlock.GetRawText();

                builder.Add(new WriteToolUseEvent(toolUseId, toolName, rawInputJson));
            }

            return builder.ToImmutable();
        }
    }

    private static bool TryGetContentArray(JsonElement root, out JsonElement content)
    {
        // assistant message event: { "type":"assistant", "message": { "content":[...] } }
        if (root.TryGetProperty("message", out var message) &&
            message.ValueKind == JsonValueKind.Object &&
            message.TryGetProperty("content", out content) &&
            content.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        // Tolerate a flattened shape: { "content":[...] }
        if (root.TryGetProperty("content", out content) && content.ValueKind == JsonValueKind.Array)
            return true;

        content = default;
        return false;
    }
}
