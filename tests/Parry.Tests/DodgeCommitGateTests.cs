using Xunit;

namespace Fahrenheit.Mods.Parry.Tests;

/// <summary>
///     Unit coverage for <see cref="DodgeCommitGate"/>. The gate decides whether the
///     authoritative p5=1024 HP/death commit in <c>MsSetDamageInternal</c> is skipped
///     because the slot evaded. It is pure so it can be exercised without
///     <c>ParryModule</c>, which is FFX-coupled.
/// </summary>
public sealed class DodgeCommitGateTests
{
    [Fact]
    public void ShouldSkipCommit_MarkerSetAndAttackerMatches_Skips()
    {
        Assert.True(DodgeCommitGate.ShouldSkipCommit(
            dodgeEnabled: true, markerSet: true, armedAttackerId: 22, commitAttackerId: 22));
    }

    [Fact]
    public void ShouldSkipCommit_DifferentAttacker_DoesNotSkip()
    {
        // A stale marker must never swallow a different attacker's commit.
        Assert.False(DodgeCommitGate.ShouldSkipCommit(
            dodgeEnabled: true, markerSet: true, armedAttackerId: 22, commitAttackerId: 23));
    }

    [Fact]
    public void ShouldSkipCommit_NoMarker_DoesNotSkip()
    {
        Assert.False(DodgeCommitGate.ShouldSkipCommit(
            dodgeEnabled: true, markerSet: false, armedAttackerId: 22, commitAttackerId: 22));
    }

    [Fact]
    public void ShouldSkipCommit_DodgeDisabled_DoesNotSkip()
    {
        Assert.False(DodgeCommitGate.ShouldSkipCommit(
            dodgeEnabled: false, markerSet: true, armedAttackerId: 22, commitAttackerId: 22));
    }

    // MayResolveAtImpact decides whether a slot may FIRST resolve a dodge at impact (p5=0 and
    // the MsDamageSetMotion path). Unlike ShouldSkipCommit — which trusts the durable marker —
    // this predicate must additionally reject slots the attack never targeted, so a shared
    // window/attacker gate cannot resolve an evade for an untargeted party slot.
    [Fact]
    public void MayResolveAtImpact_TargetedSlot_Resolves()
    {
        // Slot 1 targeted, window live, armed attacker matches the impact attacker.
        Assert.True(DodgeCommitGate.MayResolveAtImpact(
            dodgeEnabled: true, windowLiveOrMarker: true,
            armedAttackerId: 22, impactAttackerId: 22,
            armedTargetMask: 0b0010u, slotIndex: 1));
    }

    [Fact]
    public void MayResolveAtImpact_UntargetedSlot_DoesNotResolve()
    {
        // Only slot 0 was targeted; slot 1 must not ride the shared window/attacker gate.
        Assert.False(DodgeCommitGate.MayResolveAtImpact(
            dodgeEnabled: true, windowLiveOrMarker: true,
            armedAttackerId: 22, impactAttackerId: 22,
            armedTargetMask: 0b0001u, slotIndex: 1));
    }

    [Fact]
    public void MayResolveAtImpact_TargetedSlotDifferentAttacker_DoesNotResolve()
    {
        // A different attacker's hit must not resolve as this cue's dodge, even for a targeted slot.
        Assert.False(DodgeCommitGate.MayResolveAtImpact(
            dodgeEnabled: true, windowLiveOrMarker: true,
            armedAttackerId: 22, impactAttackerId: 23,
            armedTargetMask: 0b0010u, slotIndex: 1));
    }

    [Fact]
    public void MayResolveAtImpact_DodgeDisabled_DoesNotResolve()
    {
        Assert.False(DodgeCommitGate.MayResolveAtImpact(
            dodgeEnabled: false, windowLiveOrMarker: true,
            armedAttackerId: 22, impactAttackerId: 22,
            armedTargetMask: 0b0010u, slotIndex: 1));
    }

    [Fact]
    public void MayResolveAtImpact_NoWindowOrMarker_DoesNotResolve()
    {
        Assert.False(DodgeCommitGate.MayResolveAtImpact(
            dodgeEnabled: true, windowLiveOrMarker: false,
            armedAttackerId: 22, impactAttackerId: 22,
            armedTargetMask: 0b0010u, slotIndex: 1));
    }
}
