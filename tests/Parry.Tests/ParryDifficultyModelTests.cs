using Fahrenheit.Mods.Parry;
using Xunit;

namespace Fahrenheit.Mods.Parry.Tests;

public sealed class ParryDifficultyModelTests
{
    [Theory]
    [InlineData(ParryDifficulty.Easy,   0.350f)]
    [InlineData(ParryDifficulty.Normal, 0.200f)]
    [InlineData(ParryDifficulty.Expert, 0.150f)]
#if DEBUG
    [InlineData(ParryDifficulty.Debug,  0.500f)]
#endif
    public void GetWindowSeconds_ShouldReturnSingleDifficultyValue(ParryDifficulty difficulty, float expectedSeconds)
    {
        float actual = ParryDifficultyModel.GetWindowSeconds(difficulty);
        Assert.Equal(expectedSeconds, actual, precision: 3);
    }

    [Theory]
    [InlineData(ParryDifficulty.Easy,   0.450f)]
    [InlineData(ParryDifficulty.Normal, 0.600f)]
    [InlineData(ParryDifficulty.Expert, 0.750f)]
#if DEBUG
    [InlineData(ParryDifficulty.Debug,  0.300f)]
#endif
    public void GetWhiffLockoutSeconds_ShouldReturnApproximateRecoveryValue(ParryDifficulty difficulty, float expectedSeconds)
    {
        float actual = ParryDifficultyModel.GetWhiffLockoutSeconds(difficulty);
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
}
