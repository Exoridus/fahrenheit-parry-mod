using Xunit;

namespace Fahrenheit.Mods.Parry.Tests;

/// <summary>
///     Contract for the overdrive <c>limit_mode_counters</c> helper used by the
///     read-only save probe. Each counter is a per-mode learn <b>countdown</b>: its
///     start value was the learn threshold, it decrements per qualifying event, and
///     it reaches <c>0</c> when the mode is learned. <c>0xFFFF</c> (<c>-1</c> as a
///     signed short) means the character can never learn that mode. The probe reads
///     these to calibrate a custom "learn by parrying" mode against the game's own
///     numbers, so the classification and the min/median/max over learnable counters
///     must stay stable.
/// </summary>
public sealed class OverdriveCounterFormatterTests
{
    [Fact]
    public void Classify_Zero_Is_Learned()
    {
        Assert.Equal(OverdriveCounterFormatter.CounterClass.Learned, OverdriveCounterFormatter.Classify(0));
    }

    [Fact]
    public void Classify_NegativeOne_Is_NotApplicable()
    {
        // 0xFFFF read as a signed short is -1: "this character can never learn this mode".
        Assert.Equal(OverdriveCounterFormatter.CounterClass.NotApplicable, OverdriveCounterFormatter.Classify(unchecked((short)0xFFFF)));
        Assert.Equal(OverdriveCounterFormatter.CounterClass.NotApplicable, OverdriveCounterFormatter.Classify(-1));
    }

    [Fact]
    public void Classify_Ordinary_Value_Passes_Through_As_Remaining()
    {
        Assert.Equal(OverdriveCounterFormatter.CounterClass.Remaining, OverdriveCounterFormatter.Classify(7));
    }

    [Fact]
    public void FormatValue_Marks_The_Two_Special_Cases_And_Shows_The_Raw_Number()
    {
        Assert.Equal("learned", OverdriveCounterFormatter.FormatValue(0));
        Assert.Equal("n/a", OverdriveCounterFormatter.FormatValue(unchecked((short)0xFFFF)));
        Assert.Equal("12", OverdriveCounterFormatter.FormatValue(12));
    }

    [Fact]
    public void TryComputeStats_Ignores_Learned_And_NotApplicable()
    {
        // Remaining set is {3, 5, 7}: median 5, min 3, max 7. The 0 and 0xFFFF entries
        // must not drag min to 0 or -1 nor skew the median.
        short[] counters = { 0, unchecked((short)0xFFFF), 3, 5, 7 };
        bool ok = OverdriveCounterFormatter.TryComputeStats(counters, out int min, out double median, out int max);

        Assert.True(ok);
        Assert.Equal(3, min);
        Assert.Equal(7, max);
        Assert.Equal(5d, median);
    }

    [Fact]
    public void TryComputeStats_Even_Count_Averages_The_Two_Middle_Values()
    {
        // Remaining {2, 4, 6, 8}: median = (4 + 6) / 2 = 5.
        short[] counters = { 2, 4, 6, 8 };
        bool ok = OverdriveCounterFormatter.TryComputeStats(counters, out int min, out double median, out int max);

        Assert.True(ok);
        Assert.Equal(2, min);
        Assert.Equal(8, max);
        Assert.Equal(5d, median);
    }

    [Fact]
    public void TryComputeStats_All_Special_Yields_No_Statistics()
    {
        // Every entry is learned (0) or n/a (0xFFFF): there is nothing to calibrate
        // against, so the helper reports "no statistics" rather than a bogus zero.
        short[] counters = { 0, 0, unchecked((short)0xFFFF), unchecked((short)0xFFFF) };
        bool ok = OverdriveCounterFormatter.TryComputeStats(counters, out int min, out double median, out int max);

        Assert.False(ok);
        Assert.Equal(0, min);
        Assert.Equal(0, max);
        Assert.Equal(0d, median);
    }

    [Fact]
    public void TryComputeStats_Empty_Input_Yields_No_Statistics()
    {
        bool ok = OverdriveCounterFormatter.TryComputeStats(System.Array.Empty<short>(), out _, out _, out _);
        Assert.False(ok);
    }
}
