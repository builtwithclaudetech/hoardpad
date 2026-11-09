using HoardPad.Core.Ai.Caps;
using Xunit;

namespace HoardPad.Core.Ai.Tests;

/// <summary>
/// M4.1 — the conversation-keyed cap accounting (architecture §4.4, TD §4.5, PRD FR-11/FR-38/FR-40 + §9.3 mi3):
/// accumulation across an internal tool loop, the no-re-arm sticky-tripped property, conversation-keyed
/// independence, and FR-40 misroute-invariance. All pure — no I/O, no clock. Cap values come from the pinned
/// <see cref="CapConfig.Pinned"/> unless a test deliberately uses a smaller cap to exercise a boundary fast.
/// </summary>
public sealed class CapAccountingTests
{
    private static readonly CapConfig Pinned = CapConfig.Pinned;

    // A small config so a boundary is reached in a handful of ops (the SPIKE-3 E1 "FR11_CAP=3" pattern).
    private static readonly CapConfig Small = new(CreateCap: 3, DestructiveCap: 2, FilesTouchedCap: 4, DestructiveMagnitudePercent: 80);

    private static CapDecision AccountCreate(CapState s, string path, CapConfig cfg) =>
        CapAccounting.Account(s, OpKind.Create, path, cfg);

    private static CapDecision AccountOverwrite(CapState s, string path, CapConfig cfg) =>
        CapAccounting.Account(s, OpKind.DestructiveOverwrite, path, cfg);

    // ---- Cap accumulation across an internal tool loop -------------------------------------------------

    [Fact]
    public void CreateCapAccumulatesAcrossLoopTripsOnTheCapPlusOneOp()
    {
        // SPIKE-3 E1: with the create cap = 3, creates #1–#3 are allowed, #4 is denied/must-confirm.
        var state = CapState.Empty;
        for (var i = 1; i <= 3; i++)
        {
            var d = AccountCreate(state, $"/root/new{i}.cs", Small);
            Assert.False(d.RequiresConfirm); // #1..#3 within cap → silent
            Assert.Equal(CapTrigger.None, d.Trigger);
            state = d.State;
        }

        var fourth = AccountCreate(state, "/root/new4.cs", Small);
        Assert.True(fourth.RequiresConfirm); // #4 is past the cap → confirm
        Assert.Equal(CapTrigger.CreateCap, fourth.Trigger);
        Assert.Equal(4, fourth.State.CreateCount);
    }

    [Fact]
    public void DestructiveCapAccumulatesAcrossLoopTripsOnCapPlusOne()
    {
        var state = CapState.Empty;
        state = AccountOverwrite(state, "/root/a.cs", Small).State; // #1 silent
        var second = AccountOverwrite(state, "/root/b.cs", Small);  // #2 == cap, still within
        Assert.False(second.RequiresConfirm);
        var third = AccountOverwrite(second.State, "/root/c.cs", Small); // #3 past cap
        Assert.True(third.RequiresConfirm);
        Assert.Equal(CapTrigger.DestructiveCap, third.Trigger);
    }

    // ---- No re-arm after a confirm (MA12) ---------------------------------------------------------------

    [Fact]
    public void CreateCapDoesNotReArmAfterConfirmStaysTrippedForSubsequentOps()
    {
        // Drive to the trip, then keep going: every further create must still require a confirm — a confirm
        // never restores a fresh allowance (MA12 / TD §4.5). The counter keeps climbing; it never resets.
        var state = CapState.Empty;
        for (var i = 1; i <= 4; i++) // #4 trips
            state = AccountCreate(state, $"/root/n{i}.cs", Small).State;
        Assert.True(state.CreateCapTripped);

        var fifth = AccountCreate(state, "/root/n5.cs", Small);
        Assert.True(fifth.RequiresConfirm); // STILL must confirm — no re-arm
        Assert.Equal(CapTrigger.CreateCap, fifth.Trigger);
        Assert.Equal(5, fifth.State.CreateCount); // counter did NOT reset after the trip
        Assert.True(fifth.State.CreateCapTripped); // sticky
    }

    [Fact]
    public void TrippedStateIsStickyEvenIfCapValueLaterRaised()
    {
        // Once tripped, the flag stays tripped regardless of a subsequent (larger) cap config — a security
        // property: a tripped cap stays tripped for the conversation's lifetime.
        var state = CapState.Empty;
        for (var i = 1; i <= 4; i++)
            state = AccountCreate(state, $"/root/x{i}.cs", Small).State;
        Assert.True(state.CreateCapTripped);

        // Re-account with the pinned (much larger) cap; the sticky flag must survive.
        var next = AccountCreate(state, "/root/x5.cs", Pinned);
        Assert.True(next.State.CreateCapTripped);
        Assert.True(next.RequiresConfirm);
    }

    // ---- Conversation-keyed lifetime (two ids accrue independently) ------------------------------------

    [Fact]
    public void TwoConversationsAccrueIndependently()
    {
        // Each conversation owns its own CapState (the host keys it by conversation id, §4.4). Tripping one
        // must not affect the other.
        var convA = CapState.Empty;
        var convB = CapState.Empty;

        for (var i = 1; i <= 4; i++) // trip A's create cap
            convA = AccountCreate(convA, $"/root/a{i}.cs", Small).State;
        Assert.True(convA.CreateCapTripped);

        // B is untouched — a fresh create in B is silent.
        var bFirst = AccountCreate(convB, "/root/b1.cs", Small);
        Assert.False(bFirst.RequiresConfirm);
        Assert.False(bFirst.State.CreateCapTripped);
        Assert.Equal(1, bFirst.State.CreateCount);
    }

    [Fact]
    public void ContinuationResumesSameStateDoesNotReArm()
    {
        // A HoardPad-initiated continuation turn resumes the SAME conversation's cap-state (R5-BL1): the host
        // persists the CapState and feeds it back. Modeled here by carrying the same CapState forward — the
        // tripped flag and counter persist; the continuation does not get a fresh allowance.
        var state = CapState.Empty;
        for (var i = 1; i <= 4; i++)
            state = AccountCreate(state, $"/root/c{i}.cs", Small).State;
        Assert.True(state.CreateCapTripped);

        // Simulate a continuation: same persisted state fed into the next op.
        var resumed = AccountCreate(state, "/root/c5.cs", Small);
        Assert.True(resumed.RequiresConfirm); // still denied across the continuation — no re-arm
    }
}
