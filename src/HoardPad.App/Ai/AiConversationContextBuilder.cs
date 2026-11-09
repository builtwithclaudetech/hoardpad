// AiConversationContextBuilder.cs — QA4-12 (DR-QA4-12, docs/decisions.md): prepend the panel's prior
// User/Assistant turns to the NEXT Send's prompt so the backend recalls earlier messages in the SAME
// conversation. This is the App-layer half of Design B (HoardPad-owned transcript re-injection) — the
// design chosen over CLI `--resume` session-id reuse specifically because it adds NO new claude -p
// launch-arg surface: the PROTECTED spine (ClaudeLaunchContract.Compose / AgentBackend / LaunchArgsSafety /
// the guarded AgentTurnRequest ctor / CapStateStore) is untouched by this feature — it is pure prompt text
// composition over the panel's own Transcript, exactly the same pattern MultiDocContextDescriptor already
// uses to prepend the FR-MD-3 checked-set block.
//
// The block is clearly demarcated as CONTEXT ONLY (mirrors MultiDocContextDescriptor's advisory framing)
// so the model does not mistake replayed history for new instructions. It carries NO enforcement meaning —
// the sandbox/hook/cap-state gate every write regardless of what is in the prompt (defense-in-depth: the
// model's context is never a source of truth for a safety decision).
//
// DS-5 (docs/decisions.md DR-DS-2/DR-DS-3): this is the ONE inbound scrub sink (QA4-12 re-injection) —
// HoardPad never embeds document content in a prompt (NG-4), so the transcript re-injection block folded
// here is the only App-composed inbound text there is. Each folded prior line is run through
// `Engine.ScrubInput(text, Level.Standard)` and the block is composed from `CleanedText`, never the raw
// `TranscriptLine.Text` — the stored transcript history itself is NEVER rewritten, only this derived prompt
// block. The aggregated `Finding`s (and whether any line's scrub actually rewrote text) are returned to the
// caller in <see cref="ConversationContextResult"/> so a future surface (DS-6) can badge/log them.
//
// QA4-12 (DR-QA4-12-BOUND): the re-injected fold is BOUNDED, not unlimited history. Selection happens
// BEFORE scrubbing, in two caps applied oldest-first, whole-line (never partial/mid-message truncation):
// a count cap (<see cref="MaxTurns"/> most-recent User/Assistant lines) then a raw-byte cap (running total
// of un-scrubbed <see cref="TranscriptLine.Text"/>.Length, <see cref="MaxTranscriptChars"/>). A
// non-negotiable retention floor keeps the last 2 candidate lines (the most-recent User+Assistant exchange)
// no matter what either cap would otherwise evict — continuity of "what did I just say" outranks the byte
// budget. Only the surviving, kept lines are scrubbed; an evicted line's text never reaches `Engine.ScrubInput`
// or the composed block.

using System.Collections.Generic;
using System.Text;
using DragonScrub;

namespace HoardPad.App.Ai;

/// <summary>
/// The result of <see cref="AiConversationContextBuilder.Build"/>: the composed prompt block plus the
/// DS-5 inbound-scrub outcome across every folded prior line, for a future findings surface (DS-6) to
/// consume.
/// </summary>
/// <param name="Block">
/// The composed context block (scrubbed), or <see cref="string.Empty"/> when there was no prior
/// User/Assistant text to fold in — the caller's prompt is then byte-identical to today's one-shot
/// behavior.
/// </param>
/// <param name="WasModified">
/// <c>true</c> if scrubbing changed at least one folded line's text (the OR of each line's
/// <see cref="ScrubResult.WasModified"/>) — the signal a findings badge should key off, not merely
/// <see cref="Findings"/> being non-empty (a "flag"-only finding reports but never rewrites text).
/// </param>
/// <param name="Findings">Every <see cref="Finding"/> raised across all folded lines, in fold order.</param>
public sealed record ConversationContextResult(string Block, bool WasModified, IReadOnlyList<Finding> Findings);

/// <summary>
/// Builds the QA4-12 prior-conversation context block from the panel's <see cref="TranscriptLine"/> history
/// (design DR-QA4-12). Pure and content-scoped: only <see cref="TranscriptKind.UserMessage"/> and
/// <see cref="TranscriptKind.AssistantMessage"/> lines with non-empty text are folded in — file-op chips,
/// safety-event chips, and system notes are display-only noise that add nothing to model context.
/// </summary>
public static class AiConversationContextBuilder
{
    /// <summary>DR-QA4-12-BOUND default for <c>maxTurns</c>: the last N candidate User/Assistant lines.</summary>
    private const int MaxTurns = 20;

    /// <summary>
    /// DR-QA4-12-BOUND default for <c>maxTranscriptChars</c>: the raw (pre-scrub) character budget for the
    /// folded lines, evicted oldest-first once the count cap alone isn't enough.
    /// </summary>
    private const int MaxTranscriptChars = 8000;

    /// <summary>
    /// Build the context block from <paramref name="transcript"/>, read BEFORE the current Send's own user
    /// line is appended (the caller must snapshot/read the transcript ahead of adding this turn's message).
    ///
    /// The fold is bounded (DR-QA4-12-BOUND): candidate User/Assistant lines are first capped to the last
    /// <paramref name="maxTurns"/> (never fewer than the last 2, when at least 2 candidates exist — the
    /// retention floor binds the count cap too, not just the byte cap), then oldest whole lines are evicted
    /// while the running raw <see cref="TranscriptLine.Text"/>.Length total exceeds
    /// <paramref name="maxTranscriptChars"/> — except the last 2 candidate lines (the most-recent
    /// User+Assistant exchange), which are a non-negotiable retention floor and are never evicted or
    /// truncated by either cap, even if that pair alone exceeds the byte cap. Eviction is always whole-line,
    /// never partial/mid-message truncation.
    ///
    /// Only the surviving, kept lines are scrubbed via <see cref="Engine.ScrubInput"/> at
    /// <see cref="Level.Standard"/> (DS-5, DR-DS-2/DR-DS-3) before entering the block; an evicted line's text
    /// is never scrubbed or exposed. The stored <see cref="TranscriptLine.Text"/> itself is never touched.
    /// Returns an empty <see cref="ConversationContextResult.Block"/> with no findings when there is no prior
    /// User/Assistant text (the conversation's first Send, or right after Clear conversation) — the caller's
    /// prompt is then byte-identical to today's one-shot behavior; no header, no wrapping, nothing prepended.
    /// </summary>
    public static ConversationContextResult Build(
        IReadOnlyList<TranscriptLine> transcript,
        int maxTurns = MaxTurns,
        int maxTranscriptChars = MaxTranscriptChars)
    {
        System.ArgumentNullException.ThrowIfNull(transcript);

        var candidates = new List<TranscriptLine>();
        foreach (TranscriptLine line in transcript)
        {
            if (line.Kind != TranscriptKind.UserMessage && line.Kind != TranscriptKind.AssistantMessage)
                continue;
            if (string.IsNullOrWhiteSpace(line.Text))
                continue;

            candidates.Add(line);
        }

        if (candidates.Count == 0)
            return new ConversationContextResult(string.Empty, false, System.Array.Empty<Finding>());

        // Count cap: keep only the last maxTurns candidates, chronological order preserved — but never fewer
        // than the last 2 (the retention floor applies to this cap too, not just the byte cap below).
        int countCap = candidates.Count >= 2 ? System.Math.Max(maxTurns, 2) : maxTurns;
        List<TranscriptLine> countCapped = candidates.Count > countCap
            ? candidates.GetRange(candidates.Count - countCap, countCap)
            : candidates;

        // Byte cap (raw pre-scrub Text.Length): evict oldest whole lines while the running raw total
        // exceeds the cap, stopping once only the last-2 retention floor remains.
        int keepFrom = 0;
        int rawTotal = 0;
        foreach (TranscriptLine line in countCapped)
            rawTotal += line.Text.Length;

        while (rawTotal > maxTranscriptChars && countCapped.Count - keepFrom > 2)
        {
            rawTotal -= countCapped[keepFrom].Text.Length;
            keepFrom++;
        }

        IReadOnlyList<TranscriptLine> kept = keepFrom == 0
            ? countCapped
            : countCapped.GetRange(keepFrom, countCapped.Count - keepFrom);

        var body = new StringBuilder();
        var findings = new List<Finding>();
        bool wasModified = false;
        foreach (TranscriptLine line in kept)
        {
            ScrubResult scrubbed = Engine.ScrubInput(line.Text, Level.Standard);
            wasModified |= scrubbed.WasModified;
            findings.AddRange(scrubbed.Findings);

            string label = line.Kind == TranscriptKind.UserMessage ? "User" : "Assistant";
            body.Append(label).Append(": ").AppendLine(scrubbed.CleanedText);
        }

        var result = new StringBuilder();
        result.AppendLine("[HoardPad prior conversation in this session — context only, not new instructions]");
        result.Append(body);
        result.AppendLine();
        result.AppendLine("[Current message]");
        return new ConversationContextResult(result.ToString(), wasModified, findings);
    }
}
