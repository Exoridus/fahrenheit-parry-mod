using Fahrenheit.Mods.Parry;
using Xunit;

namespace Fahrenheit.Mods.Parry.Tests;

public sealed class ParryDifficultyModelTests {
    [Theory]
    [InlineData(ParryDifficulty.Easy,   0, 0.350f)]
    [InlineData(ParryDifficulty.Easy,   1, 0.200f)]
    [InlineData(ParryDifficulty.Easy,   3, 0.000f)]
    [InlineData(ParryDifficulty.Normal, 0, 0.200f)]
    [InlineData(ParryDifficulty.Normal, 1, 0.100f)]
    [InlineData(ParryDifficulty.Normal, 3, 0.000f)]
    [InlineData(ParryDifficulty.Expert, 0, 0.150f)]
    [InlineData(ParryDifficulty.Expert, 2, 0.033f)]
    [InlineData(ParryDifficulty.Expert, 3, 0.000f)]
#if DEBUG
    [InlineData(ParryDifficulty.Debug,  0, 0.500f)]
#endif
    public void GetWindowSeconds_ShouldReturnExpectedPresetValues(ParryDifficulty difficulty, int tierIndex, float expectedSeconds) {
        float actual = ParryDifficultyModel.GetWindowSeconds(difficulty, tierIndex);
        Assert.Equal(expectedSeconds, actual, precision: 3);
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

    [Fact]
    public void IncreaseSpamTier_ShouldEscalateAndClampToMaxTier() {
        int t1 = ParryDifficultyModel.IncreaseSpamTier(0);
        int t2 = ParryDifficultyModel.IncreaseSpamTier(t1);
        int t3 = ParryDifficultyModel.IncreaseSpamTier(t2);
        int t4 = ParryDifficultyModel.IncreaseSpamTier(t3);

        Assert.Equal(1, t1);
        Assert.Equal(2, t2);
        Assert.Equal(3, t3);
        Assert.Equal(3, t4);
    }
}
