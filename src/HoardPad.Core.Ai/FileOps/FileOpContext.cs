using HoardPad.Core.Records;

namespace HoardPad.Core.Ai.FileOps;

/// <summary>
/// The per-op envelope the host supplies alongside a <see cref="FileOpRequest"/>: the audit metadata the
/// uniform activity-log record needs (backend, configured-vs-effective agency, downgrade reason) and the
/// producer-set timestamp (the service reads no clock — §9.3 carries <c>ts</c> as a producer-set field).
/// </summary>
/// <remarks>
/// <see cref="CountsAgainstPerTurnCaps"/> is the FR-39 replay distinction (architecture §4.6): a normal
/// applied op (Archetype-B, or a fresh Archetype-A proposal) is counted; a user-initiated REPLAY of a
/// parked proposal is governed by FR-10(b) per-op confirm and is NOT counted against the per-turn caps —
/// the service still runs the full sandbox + before-image + log/journal path, it simply skips the cap
/// accounting. The host sets this <c>false</c> when applying from the FR-39 queue.
/// </remarks>
/// <param name="Ts">The producer-set timestamp for the §9.3 record (the host read its clock).</param>
/// <param name="Backend">The §9.3 backend that produced the op.</param>
/// <param name="ConfiguredMode">The user-configured agency mode (FR-27).</param>
/// <param name="AgencyMode">The effective mode that gated this op (MA13).</param>
/// <param name="DowngradeReason">Why the op was gated/downgraded, or <see cref="DowngradeReason.None"/>.</param>
/// <param name="CountsAgainstPerTurnCaps">
/// <c>true</c> for a normal applied op (counted by FR-11/FR-38/FR-40); <c>false</c> for an FR-39
/// user-initiated replay (governed by FR-10(b) per-op confirm, NOT the per-turn caps — architecture §4.6).
/// </param>
/// <param name="Tokens">Optional §9.3 token usage (populated for <c>claude -p</c>).</param>
/// <param name="CostUsd">Optional §9.3 cost.</param>
public sealed record FileOpContext(
    DateTimeOffset Ts,
    AiBackend Backend,
    AgencyMode ConfiguredMode,
    AgencyMode AgencyMode,
    DowngradeReason DowngradeReason,
    bool CountsAgainstPerTurnCaps = true,
    TokenUsage? Tokens = null,
    decimal? CostUsd = null);
