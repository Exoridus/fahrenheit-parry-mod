using Xunit;

namespace Fahrenheit.Mods.Parry;

public unsafe sealed class DamageDispatchOrderingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OuterFinalizationPreservesRecordedPrecommitSuccess(bool interceptedAtCalculation)
    {
        using var module = new ParryModule { WindowOpen = true, NativeCommitEmitsMotion = true };
        module.Stage(0, 100);
        if (interceptedAtCalculation) module.Calculate(false, 22);
        else module.Commit(0);

        module.Dispatch(1024);

        Assert.Equal(1000, module.Target->ram.hp);
        Assert.Equal(1, module.Successes);
        Assert.Equal(0, module.OriginalCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OuterFinalizationCannotInventSuccessAfterUnblockedApply(bool emitMotion)
    {
        using var module = new ParryModule { NativeCommitEmitsMotion = emitMotion };
        module.Stage(0, 100, 150);
        module.Commit(0);
        module.WindowOpen = true;

        module.Dispatch(1024);

        Assert.Equal(750, module.Target->ram.hp);
        Assert.Equal(0, module.Successes);
        Assert.Equal(0u, module.ResolvedMask);
        Assert.Equal(2, module.OriginalCalls);
        Assert.Equal(1, module.NativeDispatchCalls);
    }
}
