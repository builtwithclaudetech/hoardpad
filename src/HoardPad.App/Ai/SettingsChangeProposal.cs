// SettingsChangeProposal.cs — FEAT-14 (architecture AI file-op safety + FR boundaries).
//
// The AI's own file-write tools (Write/Read/Edit) are scoped by SandboxPolicy to the OPEN-FOLDER root
// (FR-26) — settings.json lives under IAppDataPaths, outside that root, so the sandboxed file-op path
// can never reach it (by construction, not by an added check). FEAT-14 therefore does NOT grant the AI
// a new file target; it lets the AI PROPOSE a settings change through its ordinary text response, using
// one prescribed marker format this pure parser recognizes. AiPanelViewModel is the only caller: it
// prepends CapabilityInstruction to the turn prompt (ONLY when the turn is write-capable — the exact
// same agency gate a file write uses), then scans the completed assistant response for the marker and
// routes any match through the SAME approve-each/auto-apply confirm gate as a file write (never a
// silent apply). No I/O here — pure text in, a candidate (field, value) pair out.

using System.Text.Json;
using System.Text.RegularExpressions;
using DragonScrub;

namespace HoardPad.App.Ai;

/// <summary>
/// Parses an AI assistant response for the FEAT-14 settings-change marker. Pure — no I/O, no allow-list
/// enforcement (that is <c>HoardPad.App.Settings.SettingsChangeApplier</c>'s job, run by the caller
/// AFTER a successful parse). A response with no marker, a malformed marker, or more than the recognized
/// shape simply yields <c>false</c> — never throws.
/// </summary>
public static class SettingsChangeProposalParser
{
    /// <summary>
    /// Prepended to the turn prompt (by <c>AiPanelViewModel.SendAsync</c>) ONLY when the turn is
    /// write-capable (<c>AgencyMapping.IsWriteCapable(EffectiveMode)</c> — the same gate a file write
    /// uses, architecture §4.2 step 2). Tells the assistant the exact marker shape to emit when — and
    /// only when — the user explicitly asked for a settings change. The allowed-fields list here MUST
    /// stay in lockstep with <c>HoardPad.App.Settings.SettingsChangeApplier</c>'s switch. QA4-17 broadens
    /// this from the original FEAT-14 twelve font/color fields to the full <c>HoardPadSettings</c> surface
    /// EXCEPT Model, Effort, the three FS-5 safety fields (Confirm-large-home-folder, agency mode,
    /// large-root threshold), and AnthropicApiKey (QA4-17 hostile-round CRITICAL — a credential marker
    /// value would render verbatim in the transcript AND be re-injected into every later turn's prompt by
    /// AiConversationContextBuilder, so no "masked display" can make this field safe to write via the
    /// marker) — those six are deliberately NEVER listed here, and even if the model hallucinated one of
    /// their field names anyway, <c>SettingsChangeApplier</c> has no switch case for them (fail closed
    /// independent of this prompt text).
    /// </summary>
    public const string CapabilityInstruction =
        "[HoardPad capability] If — and ONLY if — the user's message explicitly asks you to change "
        + "a HoardPad application setting (for example \"make the editor font bigger\", \"change the AI "
        + "response text color\", \"switch to the light theme\", or \"raise the auto-save interval to 10 "
        + "seconds\"), answer normally AND additionally include exactly one fenced block naming ONE of the "
        + "allowed fields, in this exact form:\n"
        + "```hoardpad-settings\n"
        + "{\"field\": \"EditorFontSize\", \"value\": \"16\"}\n"
        + "```\n"
        + "Allowed fields — font sizes are whole numbers in points; colors are a hex string like "
        + "\"#E5B93D\" or the word \"default\" to reset to the theme color: EditorFontFamily, "
        + "EditorFontSize, UiFontFamily, UiFontSize, AiInputFontFamily, AiInputFontSize, "
        + "AiResponseFontFamily, AiResponseFontSize, AiResponseColor, AiUserFontFamily, AiUserFontSize, "
        + "AiUserColor. ActiveBackend (one of: ClaudeCli, AnthropicApi, Local). Theme (one of: Dark, "
        + "Light). DefaultLanguage (one of the FR-7 recognized language names, e.g. \"Markdown\", "
        + "\"Python\", \"Plain Text\"). AutoSaveSeconds (1-30). NoReplySeconds (whole seconds, minimum 1). "
        + "CreateCap, DestructiveCap, FilesTouchedCap, DestructiveMagnitudePercent (whole numbers; a value "
        + "that would disable the AI safety backstop is rejected, not applied). AiPanelHotkey (free text). "
        + "ClaudeCliPath, AnthropicModel, LocalEndpoint, LocalModel (free text, or \"default\" to clear). "
        + "Never propose changing AnthropicApiKey, or any credential/secret, through this mechanism under "
        + "any circumstances — it is not supported, even if asked directly. Do not include this block for "
        + "any other request, never propose a field not in this list, and never include more than one "
        + "block.";

    private static readonly Regex Marker = new(
        "```hoardpad-settings\\s*(?<json>\\{.*?\\})\\s*```",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Extract the FIRST <c>hoardpad-settings</c> marker from <paramref name="assistantText"/> (one
    /// settings-change proposal per turn, matching the "on request" singular use case). Returns
    /// <c>false</c> with both out params <c>null</c> when no marker is present or it is malformed —
    /// never throws on attacker-controlled/garbled model output.
    /// </summary>
    public static bool TryParse(string? assistantText, out string? field, out string? rawValue)
    {
        field = null;
        rawValue = null;
        if (string.IsNullOrEmpty(assistantText))
            return false;

        Match match = Marker.Match(assistantText);
        if (!match.Success)
            return false;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(match.Groups["json"].Value);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            if (!doc.RootElement.TryGetProperty("field", out JsonElement fieldEl)
                || fieldEl.ValueKind != JsonValueKind.String)
                return false;
            if (!doc.RootElement.TryGetProperty("value", out JsonElement valueEl))
                return false;

            string? parsedField = fieldEl.GetString();
            string? parsedValue = valueEl.ValueKind switch
            {
                JsonValueKind.String => valueEl.GetString(),
                JsonValueKind.Number => valueEl.GetRawText(),
                _ => null,
            };

            if (string.IsNullOrEmpty(parsedField) || parsedValue is null)
                return false;

            field = parsedField;
            rawValue = parsedValue;
            return true;
        }
        catch (JsonException)
        {
            return false; // malformed JSON in the marker — declined, not a crash (FR-36 fail-safe posture).
        }
    }

    /// <summary>
    /// QA4-17 hostile-round fix (4th round, root-cause): remove EVERY <c>hoardpad-settings</c> marker block
    /// from <paramref name="assistantText"/> — LINE-BASED, content-independent, never regex-sniffing the
    /// JSON boundary (the 3rd-round regex+<c>IndexOf</c> version is GONE — see remarks for why it was
    /// still exploitable). Walks lines: a line whose TRIMMED content starts with the opening token
    /// <c>```hoardpad-settings</c> (case-INSENSITIVE — <see cref="StringComparison.OrdinalIgnoreCase"/>)
    /// enters "dropping" mode and is itself dropped; every following line is dropped UNCONDITIONALLY until
    /// one is found whose TRIMMED content is EXACTLY the bare closing fence <c>```</c> (that line is also
    /// dropped, then normal keeping resumes); if end-of-text is reached while still dropping, everything
    /// from the opening fence onward was dropped (the unclosed/streaming-cutoff case). Scanning continues
    /// for further opening fences after each closed block, so multiple markers are all stripped, and no
    /// value's CONTENT is ever consulted to decide where a block ends.
    ///
    /// Why the regex approach (3rd round) was still exploitable, and why line-based content-independent
    /// scanning closes it (4th-round security-round PoCs):
    ///   - PoC #1 (lazy-JSON premature match): the old <c>\{.*?\}</c> ... <c>```</c> regex matched the
    ///     FIRST <c>}  ``` </c> sequence it saw — a value string containing an embedded
    ///     <c>} ``` secret ``` {"</c> made the regex terminate at that FAKE close, leaving the real secret
    ///     as unstripped tail text. Line-based scanning never inspects value CONTENT for a `}` — it only
    ///     ends a block on a line that, after trimming, is EXACTLY <c>```</c> and nothing else; an embedded
    ///     fake fence sitting mid-line inside a JSON value is never a standalone line, so it can never end
    ///     the block early.
    ///   - PoC #2 (case-sensitivity): a non-canonical-cased fence (e.g. <c>```HoardPad-Settings</c>) matched
    ///     neither the case-sensitive regex nor the case-sensitive <c>IndexOf</c> unclosed-fence fallback, so
    ///     the whole block survived untouched. The opening-token comparison here is explicitly
    ///     case-insensitive.
    ///   - Bonus fix (spec-round Warning): the OLD unclosed-fence fallback dropped from the FIRST surviving
    ///     bare opening-token occurrence to END-OF-TEXT unconditionally — over-truncating any LEGITIMATE
    ///     trailing prose after a closed-but-braceless/malformed fence (which the regex, requiring a JSON
    ///     object shape, would not have matched as "closed" even though it visually was). Line-based
    ///     scanning ends the block at the standalone <c>```</c> close line regardless of what's inside, so
    ///     trailing prose after a real close is always preserved.
    ///
    /// The caller MUST call this UNCONDITIONALLY — on every terminal path of a turn (clean completion in
    /// ANY agency mode including ReadOnly, cancellation, and fault), not only after a successful/allowed
    /// parse and not only in write-capable mode — so a proposal (parseable, malformed, unclosed, smuggled,
    /// or targeting an excluded field) never lingers in the rendered/persisted transcript in ANY outcome,
    /// and is never re-injected into a LATER turn's prompt by <c>AiConversationContextBuilder</c> (QA4-12),
    /// which reads prior <c>TranscriptLine.Text</c> verbatim. A no-op (returns <paramref name="assistantText"/>
    /// unchanged) if no marker-shaped fence (closed or unclosed) is present.
    ///
    /// DS-3 (docs/decisions.md DR-DS-1): the hand-rolled line scanner above is retired. This now delegates
    /// to DragonScrub's <see cref="BlockScanner.RedactBlock"/> — the shared engine's block-granular
    /// primitive, confirmed by <c>DragonScrubCapabilityProbeTests</c> (DS-2) to close every evasion in the
    /// 5-round bypass catalog documented above (case/obfuscation-tolerant open-token match reusing
    /// <c>TokenScanner.TryMatchAt</c>'s contract, content-independent standalone-close-line walk, unclosed
    /// fence drops to end-of-text, fixed-point re-scan for seam-split tokens) PLUS the 3 new F8 encoding
    /// evasions the old scanner missed (leading char glued to the open fence, the open token itself split
    /// across a line break, and a zero-width codepoint spliced inside the open token). Retained text is
    /// returned byte-identical to the corresponding span of <paramref name="assistantText"/> — no blank-run
    /// collapsing is applied because the block-removal walk already drops the marker's own newlines cleanly.
    /// </summary>
    public static string StripMarker(string assistantText) => StripMarker(assistantText, out _);

    /// <summary>
    /// DS-6 (docs/decisions.md DR-DS-3): same behavior as <see cref="StripMarker(string)"/>, plus the
    /// DragonScrub block-redaction count (<paramref name="blocksRemoved"/>) so the caller can surface a
    /// findings badge/log entry whenever at least one <c>hoardpad-settings</c> block was actually removed.
    /// Zero when nothing was stripped (no marker-shaped fence present) — the signal a findings badge
    /// should key off, matching how <c>AiConversationContextBuilder</c>'s <c>WasModified</c> gates the
    /// DS-5 side of the same surface.
    /// </summary>
    public static string StripMarker(string assistantText, out int blocksRemoved)
    {
        if (string.IsNullOrEmpty(assistantText))
        {
            blocksRemoved = 0;
            return assistantText;
        }

        BlockScrubResult result = BlockScanner.RedactBlock(assistantText, "```hoardpad-settings", "```");
        blocksRemoved = result.BlocksRemoved;
        return result.RedactedText;
    }
}
