using Xunit;

namespace Fahrenheit.Mods.Parry;

public unsafe sealed class DamageAttackerOwnershipTests
{
    [Fact]
    public void OldContinuationDoesNotEraseNewAttackerLateCommitEvidence()
    {
        using var module = new ParryModule { WindowOpen = true };
        module.Stage(0, 100);
        module.Commit(0);
        module.WindowOpen = false;
        module.NextAction(23);
        module.Stage(0, 150, 175);
        module.Bank()[2] = 23;
        module.Commit(0, attacker: 23);
        Assert.True(module.HasLateCommit(23));

        module.Commit(1024, attacker: 22);

        Assert.True(module.HasLateCommit(23));
        Assert.Equal(850, module.Target->ram.hp);
        Assert.Equal(1, module.Bank()[0]);
    }

    [Fact]
    public void OldAttackerContinuationDoesNotPromoteSuccessIntoTheNewAction()
    {
        using var module = new ParryModule { WindowOpen = true };
        module.Stage(0, 100);
        module.Commit(0);
        module.WindowOpen = false;
        module.NextAction(23);

        module.Commit(1024, attacker: 22);
        Assert.Equal(0u, module.ResolvedMask);
        module.Stage(0, 150);
        module.Bank()[2] = 23;
        module.Commit(0, attacker: 23);

        Assert.Equal(850, module.Target->ram.hp);
        Assert.Equal(1, module.OriginalCalls);
        Assert.Equal(1, module.Successes);
    }

    [Theory]
    [InlineData(false, -1)]
    [InlineData(true, -1)]
    [InlineData(false, 10)]
    [InlineData(true, 10)]
    public void NonPartyCalculationAlwaysContinues(bool inner, int target)
    {
        using var module = new ParryModule { WindowOpen = true };
        Assert.Equal(inner ? 32 : 31, module.Calculate(inner, 22, target));
        Assert.Equal(0u, module.ResolvedMask);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedActionOwnerCannotReuseAnOldWindow(bool inner)
    {
        using var module = new ParryModule { WindowOpen = true, CurrentAttacker = 23 };
        Assert.Equal(inner ? 32 : 31, module.Calculate(inner, 22));
        Assert.Equal(inner ? 32 : 31, module.Calculate(inner, 23));
        Assert.False(module.FeedbackPending);
    }

    [Theory]
    [InlineData(22, 1000, 0)]
    [InlineData(23, 900, 1)]
    public void ImpactMarkerOnlyOwnsItsActionAttacker(int attacker, int hp, int originalCalls)
    {
        using var module = new ParryModule();
        module.Stage(0, 100);
        module.Bank()[2] = (byte)attacker;
        module.MarkImpactOnly();
        module.Commit(1024, attacker);
        Assert.Equal(hp, module.Target->ram.hp);
        Assert.Equal(originalCalls, module.OriginalCalls);
    }

    [Theory]
    [InlineData(false, 23)]
    [InlineData(true, 23)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    public void OpenWindowDoesNotInterceptAnotherActorCalculation(bool inner, int attacker)
    {
        using var module = new ParryModule { WindowOpen = true };
        int result = module.Calculate(inner, attacker);

        Assert.Equal(inner ? 32 : 31, result);
        Assert.Equal(1, module.OriginalCalls);
        Assert.Equal(0u, module.ResolvedMask);
        Assert.False(module.FeedbackPending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WindowStillInterceptsItsArmedAttackerCalculation(bool inner)
    {
        using var module = new ParryModule { WindowOpen = true };
        Assert.Equal(0, module.Calculate(inner, 22));
        Assert.Equal(0, module.OriginalCalls);
        Assert.Equal(1u, module.ResolvedMask);
        Assert.True(module.FeedbackPending);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1024)]
    public void SuccessfulParryDoesNotSuppressAnotherAttackerCommit(int flags)
    {
        using var module = new ParryModule { WindowOpen = true };
        module.Stage(0, 100);
        module.Commit(0);
        module.WindowOpen = false;
        module.Stage(0, 150);
        module.Bank()[2] = 23;

        module.Commit(flags, attacker: 23);

        Assert.Equal(850, module.Target->ram.hp);
        Assert.Equal(1, module.OriginalCalls);
        Assert.Equal(1, module.Successes);
    }
}
