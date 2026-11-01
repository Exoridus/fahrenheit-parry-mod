using Xunit;

namespace Fahrenheit.Mods.Parry;

public unsafe sealed class DamageTargetEligibilityTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    [InlineData(false, 4)]
    [InlineData(true, 4)]
    [InlineData(false, 5)]
    [InlineData(true, 5)]
    public void InnerCalculationPreservesTargetVeto(bool innerOnly, int status)
    {
        using var module = new ParryModule { WindowOpen = true, ChainInnerCalculation = true };
        switch (status)
        {
            case 0: module.Target->stat_exist_flag = false; break;
            case 1: module.Target->stat_death = 1; break;
            case 2: module.Target->stat_stone = 1; break;
            case 3: module.Target->ram.status_suffer_turns_left.sleep = 1; break;
            case 4: module.Target->ram.status_suffer = StatusPermanentFlags.CONFUSION; break;
            case 5: module.Target->ram.status_suffer = StatusPermanentFlags.BERSERK; break;
        }

        Assert.Equal(32, module.Calculate(innerOnly, 22));
        Assert.Equal(innerOnly ? 1 : 2, module.OriginalCalls);
        Assert.Equal(0u, module.ResolvedMask);
        Assert.False(module.FeedbackPending);
    }
}
