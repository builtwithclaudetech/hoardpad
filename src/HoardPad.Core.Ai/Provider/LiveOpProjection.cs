using HoardPad.Core.Ai.Caps;
using HoardPad.Core.Ai.FileOps;
using HoardPad.Core.Host.Abstractions;
using HoardPad.Core.Records;

namespace HoardPad.Core.Ai.Provider;

/// <summary>
/// The live cap-meter READING the App shows while a turn streams (M11 / docs/escalation-ai-live-write-path.md
/// §4 "live cap advance"). The three FR-11/FR-38/FR-40 counts the App binds to its meters — projected from a
/// READ of the conversation's O_EXCL cap-state (<see cref="HoardPad.Core.Host.Abstractions.ICapStateStore"/>),
/// NEVER a parallel counter (spec §5 invariant 4). The host reads <see cref="CapStateSnapshot"/> and hands its
/// payload to <see cref="LiveCapMeter.Read"/>; this value is the result.
/// </summary>
/// <param name="Created">FR-11 created count read from the cap-state (<see cref="CapState.CreateCount"/>).</param>
/// <param name="Destructive">FR-38 destructive count read from the cap-state (<see cref="CapState.DestructiveCount"/>).</param>
/// <param name="Touched">FR-40 distinct-files count read from the cap-state (<see cref="CapState.FilesTouchedCount"/>).</param>
public readonly record struct LiveCapMeterReading(int Created, int Destructive, int Touched);

/// <summary>
/// The platform-free file-op CHIP model the App renders for one observed hook op (M11 §4 "file-op chip"). It is
/// a pure projection of a <see cref="HookOpEvent"/> — the op vocabulary the chip shows, the resolved target
/// path, the create/overwrite/edit class, and whether the hook ALLOWED or DENIED it (a deny renders as a
/// blocked chip with NO cap advance). It carries no decision logic — the hook already decided; this only
/// re-shapes the report for the UI.
/// </summary>
/// <param name="Op">The §9.3 op vocabulary the chip shows (create/edit), mapped from the hook's op class.</param>
/// <param name="TargetPath">The resolved target path the chip shows.</param>
/// <param name="WasApplied">
/// <c>true</c> iff the hook ALLOWED the op (eligible to advance the cap-meter read). <c>false</c> for a
/// hook-deny — the chip is rendered blocked and the cap-meter does NOT advance (spec §5 invariant 1/4).
/// </param>
/// <param name="OpClass">The create/overwrite/edit class the hook classified (for the chip detail label).</param>
public readonly record struct LiveOpChip(ActivityOp Op, string TargetPath, bool WasApplied, HookOpClass OpClass);

/// <summary>
/// The M11 platform-free orchestration that maps ONE observed <see cref="HookOpEvent"/> from the
/// <see cref="IHookObservationChannel"/> into the three things the App + the spine need (M11 §4):
/// <list type="number">
/// <item><b>a file-op CHIP</b> (<see cref="ToChip"/>) — pure re-shape of the report for the transcript;</item>
/// <item><b>the MA14 observed-callback feed</b> (<see cref="ToHookCallback"/>) — the
///   <see cref="HookCallbackObservation"/> that, folded into <see cref="ArchetypeAOrchestration.OnHookCallback"/>,
///   correlates the observed op against the stream-json <c>tool_use</c> side (so an observed write with no
///   matching stream-json event — and a stream-json write with no observed callback — degrades per MA14, §7
///   G-live-6);</item>
/// <item><b>the live cap-meter READ</b> (<see cref="LiveCapMeter.Read"/>, the sibling type) — a READ of the
///   already-incremented O_EXCL cap-state, never a counter (spec §5 invariant 4).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Pure + platform-free: a re-shape over an in-memory record — no I/O, no clock, no process. The host owns the
/// channel + the cap-state read; this logic owns the mapping. The MA14 correlation itself is NOT re-implemented
/// here — <see cref="ToHookCallback"/> produces the exact <see cref="HookCallbackObservation"/> the EXISTING
/// M4.2 fold (<see cref="ArchetypeAOrchestration.OnHookCallback"/>) consumes, so the observed-callback side of
/// MA14 reuses the proven liveness logic. The unmatched-stream-json and unmatched-callback degrade cases are
/// resolved by that fold + <see cref="ArchetypeAOrchestration.ResolveLiveness"/>, unchanged.
/// </para>
/// <para>
/// <b>Read tool-uses are not gated for liveness.</b> Only WRITE ops (<c>Write</c>/<c>Edit</c>) feed MA14 —
/// mirroring <see cref="StreamJsonTaxonomy"/>, which surfaces only write tool-use events. A <c>Read</c> hook
/// callback is irrelevant to the verdict (a spare callback is harmless), so <see cref="ToHookCallback"/> returns
/// <c>null</c> for a non-write op rather than feeding a callback that could never match a write.
/// </para>
/// </remarks>
public static class LiveOpProjection
{
    /// <summary>
    /// Map an observed hook op to its transcript CHIP (M11 §4 "file-op chip"). A hook-allow becomes an applied
    /// chip whose op vocabulary reflects the create/edit class; a hook-deny becomes a blocked chip (the App
    /// renders it as a refused write — design §6.7 SC-5(d) — and does NOT advance the cap meter).
    /// </summary>
    public static LiveOpChip ToChip(HookOpEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);

        var applied = ev.HookDecision == HookDecision.Allow;
        var op = ev.OpClass switch
        {
            HookOpClass.Create => ActivityOp.Create,
            HookOpClass.Overwrite => ActivityOp.Edit, // an overwrite is an edit op in the §9.3 vocabulary
            HookOpClass.Edit => ActivityOp.Edit,
            _ => ActivityOp.Edit,
        };

        return new LiveOpChip(op, ev.TargetPath, WasApplied: applied, OpClass: ev.OpClass);
    }

    /// <summary>
    /// Produce the MA14 observed-callback for a WRITE op (M11 §4 "MA14 observed-callback feed", §7 G-live-6).
    /// The returned <see cref="HookCallbackObservation"/> is fed verbatim into
    /// <see cref="ArchetypeAOrchestration.OnHookCallback"/> — its presence proves the hook gated this
    /// <c>tool_use_id</c>, clearing the matching stream-json write from the MA14 pending set. Returns
    /// <c>null</c> for a non-write op (a <c>Read</c> callback does not gate a write and must not feed MA14) and
    /// for an op with an empty <c>tool_use_id</c> (an unkeyable observation cannot correlate).
    /// </summary>
    /// <remarks>
    /// <b>The hook decision does NOT gate whether a callback is produced.</b> A hook-DENY is still a positive
    /// proof that the hook RAN for that op — MA14's degrade signal is the ABSENCE of any callback for an
    /// emitted write (a silently-disarmed hook), not a deny. So a denied write that the hook observed still
    /// produces a callback, correctly clearing it from the MA14 pending set; the deny is surfaced on the chip
    /// (<see cref="ToChip"/>), not by withholding the liveness proof.
    /// </remarks>
    public static HookCallbackObservation? ToHookCallback(HookOpEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);

        if (string.IsNullOrEmpty(ev.ToolUseId))
            return null;
        if (!StreamJsonTaxonomy.IsWriteTool(ev.ToolName))
            return null;

        return new HookCallbackObservation(ev.ToolUseId);
    }
}

/// <summary>
/// The live cap-meter READ (M11 §4 "live cap advance", spec §5 invariant 4). Projects the three
/// FR-11/FR-38/FR-40 counts from a READ of the conversation's O_EXCL cap-state — the SAME state the hook
/// increments and reads. This is a pure read: it parses the cap-state payload and returns the counts. It does
/// NOT increment, does NOT account, and holds NO counter of its own — so the meter can never drift from the
/// authoritative O_EXCL state (no double-count, no re-arm).
/// </summary>
public static class LiveCapMeter
{
    /// <summary>
    /// Read the three live cap counts from a conversation's cap-state snapshot (a
    /// <see cref="HoardPad.Core.Host.Abstractions.ICapStateStore.ReadAsync"/> result). A never-persisted
    /// conversation (null/empty payload) reads as all-zero (<see cref="CapState.Empty"/>). This is a READ of
    /// the already-incremented O_EXCL state — never a parallel counter (spec §5 invariant 4).
    /// </summary>
    /// <param name="snapshot">The cap-state read the host obtained from the conversation-keyed store.</param>
    public static LiveCapMeterReading Read(CapStateSnapshot snapshot)
    {
        CapState state = CapStateSerialization.Deserialize(snapshot.PayloadJson);
        return new LiveCapMeterReading(
            Created: state.CreateCount,
            Destructive: state.DestructiveCount,
            Touched: state.FilesTouchedCount);
    }
}
