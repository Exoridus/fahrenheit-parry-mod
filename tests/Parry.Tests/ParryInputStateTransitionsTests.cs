using Xunit;

namespace Fahrenheit.Mods.Parry.Tests;

/// <summary>
///     Unit coverage for <see cref="ParryInputStateTransitions"/>. The decision
///     logic for the parry input state machine lives in the static helper so
///     that the gates can be exercised without instantiating <c>ParryModule</c>
///     (which is FFX-coupled). The production code in
///     <c>ParryModule.Combat.cs</c> calls into the same helper, so passing
///     tests here are a guarantee against silent regressions in the gates.
/// </summary>
public sealed class ParryInputStateTransitionsTests
{
    // ── DecidePress: accept paths ────────────────────────────────────────────

    [Fact]
    public void DecidePress_FromReady_WithCue_AcceptsAndOpens()
    {
        var d = ParryInputStateTransitions.DecidePress(ParryInputState.Ready, hasParryableCue: true);
        Assert.True(d.Accepted);
        Assert.Equal(ParryInputState.Open, d.NextState);
        Assert.Equal(string.Empty, d.RejectReason);
    }

    // ── DecidePress: reject paths ────────────────────────────────────────────

    [Theory]
    [InlineData(ParryInputState.Ready)]
    [InlineData(ParryInputState.Open)]
    [InlineData(ParryInputState.Resolved)]
    [InlineData(ParryInputState.WhiffLockout)]
    public void DecidePress_WithoutCue_RejectsAndPreservesState(ParryInputState start)
    {
        var d = ParryInputStateTransitions.DecidePress(start, hasParryableCue: false);
        Assert.False(d.Accepted);
        Assert.Equal(start, d.NextState);
        Assert.Equal("no_parryable_cue", d.RejectReason);
    }

    [Fact]
    public void DecidePress_FromOpen_RejectsWindowAlreadyOpen()
    {
        var d = ParryInputStateTransitions.DecidePress(ParryInputState.Open, hasParryableCue: true);
        Assert.False(d.Accepted);
        Assert.Equal(ParryInputState.Open, d.NextState);
        Assert.Equal("window_already_open", d.RejectReason);
    }

    [Fact]
    public void DecidePress_FromResolved_RejectsAlreadyParried()
    {
        var d = ParryInputStateTransitions.DecidePress(ParryInputState.Resolved, hasParryableCue: true);
        Assert.False(d.Accepted);
        Assert.Equal(ParryInputState.Resolved, d.NextState);
        Assert.Equal("current_attack_already_parried", d.RejectReason);
    }

    [Fact]
    public void DecidePress_FromWhiffLockout_RejectsInGuardRecovery()
    {
        var d = ParryInputStateTransitions.DecidePress(ParryInputState.WhiffLockout, hasParryableCue: true);
        Assert.False(d.Accepted);
        Assert.Equal(ParryInputState.WhiffLockout, d.NextState);
        Assert.Equal("in_guard_recovery", d.RejectReason);
    }

    // ── DecideWindowExpiry: Open → WhiffLockout / Ready ──────────────────────

    [Theory]
    [InlineData(0.001f)]
    [InlineData(0.450f)]
    [InlineData(0.600f)]
    [InlineData(0.750f)]
    public void DecideWindowExpiry_PositiveLockout_TransitionsToWhiffLockout(float lockoutSeconds)
    {
        ParryInputState next = ParryInputStateTransitions.DecideWindowExpiry(lockoutSeconds);
        Assert.Equal(ParryInputState.WhiffLockout, next);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void DecideWindowExpiry_ZeroOrNegativeLockout_TransitionsToReady(float lockoutSeconds)
    {
        ParryInputState next = ParryInputStateTransitions.DecideWindowExpiry(lockoutSeconds);
        Assert.Equal(ParryInputState.Ready, next);
    }

    // ── DecideOnResolve: Open → Resolved (only) ──────────────────────────────

    [Fact]
    public void DecideOnResolve_FromOpen_TransitionsToResolved()
    {
        ParryInputState next = ParryInputStateTransitions.DecideOnResolve(ParryInputState.Open);
        Assert.Equal(ParryInputState.Resolved, next);
    }

    [Theory]
    [InlineData(ParryInputState.Ready)]
    [InlineData(ParryInputState.Resolved)]
    [InlineData(ParryInputState.WhiffLockout)]
    public void DecideOnResolve_FromNonOpen_PreservesState(ParryInputState start)
    {
        ParryInputState next = ParryInputStateTransitions.DecideOnResolve(start);
        Assert.Equal(start, next);
    }

    // ── DecideLockoutComplete: WhiffLockout → Ready ──────────────────────────

    [Fact]
    public void DecideLockoutComplete_AlwaysReturnsReady()
    {
        Assert.Equal(ParryInputState.Ready, ParryInputStateTransitions.DecideLockoutComplete());
    }

    // ── Documented full-cycle scenario ───────────────────────────────────────

    [Fact]
    public void FullCycle_Ready_Open_Resolved_Ready_FromTurnEnd()
    {
        // 1. Ready: press with cue → Open.
        var press = ParryInputStateTransitions.DecidePress(ParryInputState.Ready, hasParryableCue: true);
        Assert.True(press.Accepted);
        Assert.Equal(ParryInputState.Open, press.NextState);

        // 2. Open: a hit lands → Resolved.
        ParryInputState afterResolve = ParryInputStateTransitions.DecideOnResolve(press.NextState);
        Assert.Equal(ParryInputState.Resolved, afterResolve);

        // 3. Resolved: a fresh press is rejected (player can't double-resolve).
        var pressInResolved = ParryInputStateTransitions.DecidePress(afterResolve, hasParryableCue: true);
        Assert.False(pressInResolved.Accepted);
        Assert.Equal("current_attack_already_parried", pressInResolved.RejectReason);

        // 4. Turn ends — clear_awaiting_turn_end flips Resolved → Ready in
        //    production code. The decision helper does not own that transition
        //    (it depends on cue/turn state, not the input machine), so we don't
        //    assert it here. See ParryModule.Combat.clear_awaiting_turn_end for
        //    the production equivalent.
    }

    [Fact]
    public void FullCycle_Ready_Open_WhiffLockout_Ready()
    {
        // 1. Ready: press → Open.
        var press = ParryInputStateTransitions.DecidePress(ParryInputState.Ready, hasParryableCue: true);
        Assert.True(press.Accepted);
        Assert.Equal(ParryInputState.Open, press.NextState);

        // 2. Open: window expires without a hit, lockout enabled → WhiffLockout.
        ParryInputState afterExpiry = ParryInputStateTransitions.DecideWindowExpiry(lockoutSeconds: 0.6f);
        Assert.Equal(ParryInputState.WhiffLockout, afterExpiry);

        // 3. WhiffLockout: a fresh press is rejected.
        var pressInLockout = ParryInputStateTransitions.DecidePress(afterExpiry, hasParryableCue: true);
        Assert.False(pressInLockout.Accepted);
        Assert.Equal("in_guard_recovery", pressInLockout.RejectReason);

        // 4. WhiffLockout: recovery timer elapses → Ready.
        ParryInputState afterRecovery = ParryInputStateTransitions.DecideLockoutComplete();
        Assert.Equal(ParryInputState.Ready, afterRecovery);

        // 5. Ready: next press accepted.
        var pressAfterRecovery = ParryInputStateTransitions.DecidePress(afterRecovery, hasParryableCue: true);
        Assert.True(pressAfterRecovery.Accepted);
        Assert.Equal(ParryInputState.Open, pressAfterRecovery.NextState);
    }

    [Fact]
    public void FullCycle_Ready_Open_Expiry_LockoutDisabled_Ready()
    {
        // Lockout-disabled path: window expires → Ready (no recovery commitment).
        var press = ParryInputStateTransitions.DecidePress(ParryInputState.Ready, hasParryableCue: true);
        Assert.Equal(ParryInputState.Open, press.NextState);

        ParryInputState afterExpiry = ParryInputStateTransitions.DecideWindowExpiry(lockoutSeconds: 0f);
        Assert.Equal(ParryInputState.Ready, afterExpiry);

        var pressAfter = ParryInputStateTransitions.DecidePress(afterExpiry, hasParryableCue: true);
        Assert.True(pressAfter.Accepted);
    }
}
