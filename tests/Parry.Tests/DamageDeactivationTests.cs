using Xunit;

namespace Fahrenheit.Mods.Parry;

public unsafe sealed class DamageDeactivationTests
{
    [Theory]
    [InlineData(10)]
    [InlineData(22)]
    [InlineData(31)]
    public void DisabledModuleForwardsNonPartyMotion(byte target)
    {
        using var module = new ParryModule { Enabled = false };
        module.Motion(target);
        Assert.Equal(1, module.NativeMotionCalls);
        Assert.Equal(0, module.Successes);
    }

    [Fact]
    public void DisabledModuleDoesNotUseStaleResolvedMarkerToSuppressOuterCommit()
    {
        using var module = new ParryModule { WindowOpen = true };
        module.Stage(0, 100);
        module.Commit(0);
        module.Enabled = false;
        module.Stage(0, 150);

        module.Dispatch(0, target: 0);

        Assert.Equal(850, module.Target->ram.hp);
        Assert.Equal(1, module.NativeDispatchCalls);
        Assert.Equal(1, module.OriginalCalls);
        Assert.Equal(1, module.Successes);
    }

    [Fact]
    public void DisabledModuleDoesNotResolveStaleDeferredFeedbackAtMotion()
    {
        using var module = new ParryModule { WindowOpen = true };
        module.Calculate(false, 22);
        Assert.True(module.FeedbackPending);
        module.Enabled = false;

        module.Motion();

        Assert.Equal(1, module.NativeMotionCalls);
        Assert.Equal(0, module.Successes);
    }
}
