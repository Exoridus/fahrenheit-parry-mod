using Fahrenheit.Mods.Parry;
using Xunit;

namespace Fahrenheit.Mods.Parry.Tests;

public sealed class ParryDifficultyModelTests
{
    // Difficulty no longer moves the window: Easy, Normal and Expert share one set of
    // thresholds. Debug is a testing aid, not a difficulty, and keeps its generous ones.
    [Theory]
    [InlineData(ParryDifficulty.Easy,   6)]
    [InlineData(ParryDifficulty.Normal, 6)]
    [InlineData(ParryDifficulty.Expert, 6)]
#if DEBUG
    [InlineData(ParryDifficulty.Debug,  15)]
#endif
    public void GetParryWindowTicks_IsConstantAcrossRealDifficulties(ParryDifficulty difficulty, int expectedTicks)
    {
        Assert.Equal(expectedTicks, ParryDifficultyModel.GetParryWindowTicks(difficulty));
    }

    [Theory]
    [InlineData(ParryDifficulty.Easy,   10)]
    [InlineData(ParryDifficulty.Normal, 10)]
    [InlineData(ParryDifficulty.Expert, 10)]
#if DEBUG
    [InlineData(ParryDifficulty.Debug,  24)]
#endif
    public void GetDodgeWindowTicks_IsWiderThanTheParryWindow(ParryDifficulty difficulty, int expectedTicks)
    {
        Assert.Equal(expectedTicks, ParryDifficultyModel.GetDodgeWindowTicks(difficulty));
        Assert.True(ParryDifficultyModel.GetDodgeWindowTicks(difficulty)
                  > ParryDifficultyModel.GetParryWindowTicks(difficulty));
    }

    // The lockout is derived, not authored: total commitment minus the window it follows.
    // The old model hid this — three presets satisfied `lockout = 800ms - window` and Expert
    // silently broke the pattern at 900ms.
    [Theory]
    [InlineData(ParryDifficulty.Easy)]
    [InlineData(ParryDifficulty.Normal)]
    [InlineData(ParryDifficulty.Expert)]
#if DEBUG
    [InlineData(ParryDifficulty.Debug)]
#endif
    public void WhiffLockout_IsTotalCommitmentMinusTheWindow(ParryDifficulty difficulty)
    {
        int commitment = ParryDifficultyModel.GetTotalCommitmentTicks(difficulty);
        int window     = ParryDifficultyModel.GetParryWindowTicks(difficulty);
        int lockout    = ParryDifficultyModel.GetWhiffLockoutTicks(difficulty);

        Assert.Equal(commitment - window, lockout);
        Assert.True(lockout > 0, "a whiff must cost something");
    }

    // Every window must end BETWEEN two ticks, never on one: a boundary value is decided by
    // float residue and frame pacing. Ticks are integers, so this holds by construction — the
    // test guards the invariant against anyone reintroducing millisecond constants.
    [Theory]
    [InlineData(ParryDifficulty.Easy)]
    [InlineData(ParryDifficulty.Normal)]
    [InlineData(ParryDifficulty.Expert)]
    public void DerivedSeconds_AreExactTickMultiples(ParryDifficulty difficulty)
    {
        float tick = 1f / ParryDifficultyModel.TicksPerSecond;

        Assert.Equal(ParryDifficultyModel.GetParryWindowTicks(difficulty) * tick,
                     ParryDifficultyModel.GetWindowSeconds(difficulty), precision: 5);
        Assert.Equal(ParryDifficultyModel.GetWhiffLockoutTicks(difficulty) * tick,
                     ParryDifficultyModel.GetWhiffLockoutSeconds(difficulty), precision: 5);
    }

    // Difficulty's entire job. Only parryable hits are scaled — see the class remarks.
    [Theory]
    [InlineData(ParryDifficulty.Easy,   0.75f)]
    [InlineData(ParryDifficulty.Normal, 1.00f)]
    [InlineData(ParryDifficulty.Expert, 1.75f)]
#if DEBUG
    [InlineData(ParryDifficulty.Debug,  1.00f)]
#endif
    public void GetParryableDamageScale_IsWhatDifficultyActuallyChanges(ParryDifficulty difficulty, float expected)
    {
        Assert.Equal(expected, ParryDifficultyModel.GetParryableDamageScale(difficulty), precision: 3);
    }

    [Fact]
    public void DefaultDifficulty_ShouldMatchBuildConfiguration()
    {
#if DEBUG
        Assert.Equal(ParryDifficulty.Debug, ParryDifficultyModel.DefaultDifficulty);
#else
        Assert.Equal(ParryDifficulty.Normal, ParryDifficultyModel.DefaultDifficulty);
#endif
    }

    [Fact]
    public void SelectableDifficulties_ShouldMatchBuildConfiguration()
    {
        ParryDifficulty[] selectable = ParryDifficultyModel.GetSelectableDifficulties().ToArray();

#if DEBUG
        Assert.Equal(
            new[] { ParryDifficulty.Debug, ParryDifficulty.Easy, ParryDifficulty.Normal, ParryDifficulty.Expert },
            selectable);
#else
        Assert.Equal(
            new[] { ParryDifficulty.Easy, ParryDifficulty.Normal, ParryDifficulty.Expert },
            selectable);
#endif
    }

    [Fact]
    public void TryParsePersistedDifficulty_ShouldHandleLegacyDebugSelection()
    {
        bool parsed = ParryDifficultyModel.TryParsePersistedDifficulty("Debug", out ParryDifficulty difficulty);
        Assert.True(parsed);
#if DEBUG
        Assert.Equal(ParryDifficulty.Debug, difficulty);
#else
        Assert.Equal(ParryDifficulty.Normal, difficulty);
#endif
    }
}
