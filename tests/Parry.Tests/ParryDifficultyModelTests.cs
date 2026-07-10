using Fahrenheit.Mods.Parry;
using Xunit;

namespace Fahrenheit.Mods.Parry.Tests;

public sealed class ParryDifficultyModelTests
{
    // Windows are tiered again. These reproduce the millisecond values the mod shipped with:
    // the old code closed after ceil(ms / 33.33) ticks, so Easy's 350 ms bought eleven ticks
    // and Expert's 150 ms bought five.
    [Theory]
    [InlineData(ParryDifficulty.Easy,   11)]
    [InlineData(ParryDifficulty.Normal, 6)]
    [InlineData(ParryDifficulty.Expert, 5)]
#if DEBUG
    [InlineData(ParryDifficulty.Debug,  15)]
#endif
    public void GetParryWindowTicks_IsTieredAndTightensWithDifficulty(ParryDifficulty difficulty, int expectedTicks)
    {
        Assert.Equal(expectedTicks, ParryDifficultyModel.GetParryWindowTicks(difficulty));
    }

    [Theory]
    [InlineData(ParryDifficulty.Easy,   11)]
    [InlineData(ParryDifficulty.Normal, 9)]
    [InlineData(ParryDifficulty.Expert, 7)]
#if DEBUG
    [InlineData(ParryDifficulty.Debug,  24)]
#endif
    public void GetDodgeWindowTicks_IsAtLeastAsWideAsTheParryWindow(ParryDifficulty difficulty, int expectedTicks)
    {
        Assert.Equal(expectedTicks, ParryDifficultyModel.GetDodgeWindowTicks(difficulty));
        Assert.True(ParryDifficultyModel.GetDodgeWindowTicks(difficulty)
                 >= ParryDifficultyModel.GetParryWindowTicks(difficulty),
                    "the dodge is the safer option; its window may never be the tighter one");
    }

    [Theory]
    [InlineData(ParryDifficulty.Easy,   14)]
    [InlineData(ParryDifficulty.Normal, 18)]
    [InlineData(ParryDifficulty.Expert, 15)]
#if DEBUG
    [InlineData(ParryDifficulty.Debug,  9)]
#endif
    public void GetWhiffLockoutTicks_IsAuthoredPerTier(ParryDifficulty difficulty, int expectedTicks)
    {
        Assert.Equal(expectedTicks, ParryDifficultyModel.GetWhiffLockoutTicks(difficulty));
        Assert.True(ParryDifficultyModel.GetWhiffLockoutTicks(difficulty) > 0, "a whiff must cost something");
    }

    // Expert used to pay twice: the tightest window AND the longest recovery (767 ms against
    // Normal's 600). Guard against that returning.
    [Fact]
    public void Expert_DoesNotPayTwice()
    {
        Assert.True(ParryDifficultyModel.GetWhiffLockoutTicks(ParryDifficulty.Expert)
                  < ParryDifficultyModel.GetWhiffLockoutTicks(ParryDifficulty.Normal),
                    "Expert already has the tightest window; it must not also carry the longest recovery");
    }

    [Theory]
    [InlineData(ParryDifficulty.Easy,   9)]
    [InlineData(ParryDifficulty.Normal, 12)]
    [InlineData(ParryDifficulty.Expert, 15)]
#if DEBUG
    [InlineData(ParryDifficulty.Debug,  0)]
#endif
    public void GetDodgeCooldownTicks_PacesMultiPress(ParryDifficulty difficulty, int expectedTicks)
    {
        Assert.Equal(expectedTicks, ParryDifficultyModel.GetDodgeCooldownTicks(difficulty));
    }

    [Theory]
    [InlineData(ParryDifficulty.Easy)]
    [InlineData(ParryDifficulty.Normal)]
    [InlineData(ParryDifficulty.Expert)]
    public void TotalCommitment_IsWindowPlusLockout(ParryDifficulty difficulty)
    {
        Assert.Equal(
            ParryDifficultyModel.GetParryWindowTicks(difficulty) + ParryDifficultyModel.GetWhiffLockoutTicks(difficulty),
            ParryDifficultyModel.GetTotalCommitmentTicks(difficulty));
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
