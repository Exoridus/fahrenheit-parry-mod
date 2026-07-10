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
}
