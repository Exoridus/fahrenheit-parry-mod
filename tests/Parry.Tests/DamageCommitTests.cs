using Xunit;

namespace Fahrenheit.Mods.Parry;

public unsafe sealed class DamageCommitTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PriorUnparriedApplyDoesNotAuthorizeSkippingOuterCommit(bool windowOpen)
    {
        using var module = new ParryModule { WindowOpen = windowOpen };
        module.Stage(0, 100);
        Assert.False(module.OuterCommitWouldSkip(resolved: false, committedBeforeWindow: true));
        Assert.True(module.OuterCommitWouldSkip(resolved: true, committedBeforeWindow: true));
        Assert.Equal(windowOpen, module.OuterCommitWouldSkip(resolved: false, committedBeforeWindow: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnparriedFinalizationConsumesTheNextPayload(bool openAfterFirstCommit)
    {
        using var module = new ParryModule();
        module.Stage(0, 100, 150);
        Assert.Equal(3, module.Commit(0));
        Assert.Equal(900, module.Target->ram.hp);
        module.WindowOpen = openAfterFirstCommit;

        Assert.Equal(2, module.Commit(0x400));

        Assert.Equal(750, module.Target->ram.hp);
        Assert.Equal(2, module.OriginalCalls);
        Assert.Equal(2, module.Bank()[0]);
        Assert.Equal(255, module.Bank()[2]);
        Assert.Equal(0, module.Successes);
    }

    [Fact]
    public void UnparriedFinalizationReleasesExhaustedBanksThroughNativeContinuation()
    {
        using var module = new ParryModule();
        module.Stage(0, 100);
        module.Commit(0);
        module.Commit(0x400);

        Assert.Equal(900, module.Target->ram.hp);
        Assert.Equal(2, module.OriginalCalls);
        Assert.Equal(255, module.Bank()[2]);
    }

    [Fact]
    public void UnparriedFinalizationProcessesBothMatchingBanks()
    {
        using var module = new ParryModule();
        module.Stage(0, 100, 150);
        module.Stage(1, 50, 75);
        module.Commit(0);
        module.Commit(0x400);

        Assert.Equal(625, module.Target->ram.hp);
        Assert.Equal(2, module.Bank(0)[0]);
        Assert.Equal(2, module.Bank(1)[0]);
        Assert.Equal(0, module.Successes);
    }

    [Fact]
    public void SuccessfulParryStillBlocksItsCorrelatedLateCommit()
    {
        using var module = new ParryModule { WindowOpen = true };
        module.Stage(0, 100);
        module.Commit(0);
        module.WindowOpen = false;
        module.Commit(0x400);

        Assert.Equal(1000, module.Target->ram.hp);
        Assert.Equal(0, module.OriginalCalls);
        Assert.Equal(1, module.Successes);
    }
}
