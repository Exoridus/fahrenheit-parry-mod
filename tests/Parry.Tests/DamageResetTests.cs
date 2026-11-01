using Xunit;

namespace Fahrenheit.Mods.Parry;

public unsafe sealed class DamageResetTests
{
    [Fact]
    public void NewActionCannotResolveOutgoingCalculationFeedback()
    {
        using var module = new ParryModule { WindowOpen = true };
        module.Calculate(false, 22);
        Assert.True(module.FeedbackPending);

        module.NextAction(23);
        module.Motion();

        Assert.Equal(0, module.Successes);
        Assert.Equal(0u, module.ResolvedMask);
        Assert.False(module.FeedbackPending);
    }

    [Fact]
    public void RuntimeResetRevokesImpactMarkerAndHpSnapshot()
    {
        using var module = new ParryModule { WindowOpen = true };
        module.Calculate(false, 22);
        Assert.Equal(1000u, module.HpSnapshot);
        module.MarkImpactOnly();

        module.Reset();
        module.CurrentAttacker = 22;
        module.Stage(0, 150);
        module.Commit(1024);

        Assert.Equal(850, module.Target->ram.hp);
        Assert.Equal(0u, module.HpSnapshot);
    }

    [Fact]
    public void RuntimeResetRevokesLateCommitWithoutDiscardingNativeBanks()
    {
        using var module = new ParryModule();
        module.Stage(0, 100, 150);
        module.Commit(0);
        Assert.True(module.HasLateCommit(22));

        module.Reset();

        Assert.False(module.HasLateCommit(22));
        Assert.Equal(1, module.Bank()[0]);
        Assert.Equal(22, module.Bank()[2]);
        module.Commit(1024);
        Assert.Equal(750, module.Target->ram.hp);
    }

    [Fact]
    public void RuntimeResetRevokesStoredDelayedCommitBeforeActorReuse()
    {
        using var module = new ParryModule { WindowOpen = true };
        module.Stage(0, 100);
        module.Commit(0);
        module.Reset();
        module.NextAction(22);
        module.Stage(0, 150);

        module.Commit(1024);

        Assert.Equal(850, module.Target->ram.hp);
        Assert.Equal(1, module.OriginalCalls);
        Assert.Equal(0u, module.ResolvedMask);
    }

    [Fact]
    public void RuntimeResetRevokesPendingCalculationFeedback()
    {
        using var module = new ParryModule { WindowOpen = true };
        module.Calculate(false, 22);
        Assert.True(module.FeedbackPending);

        module.Reset();
        module.Motion();

        Assert.False(module.FeedbackPending);
        Assert.Equal(0, module.Successes);
        Assert.Equal(1, module.NativeMotionCalls);
    }
}
