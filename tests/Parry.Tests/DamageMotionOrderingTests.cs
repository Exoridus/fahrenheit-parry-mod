using Xunit;

namespace Fahrenheit.Mods.Parry;

public unsafe sealed class DamageMotionOrderingTests
{
    [Fact]
    public void MotionAfterUnblockedFinalizationCannotPromoteALateParry()
    {
        using var module = new ParryModule { NativeCommitEmitsMotion = true };
        module.Stage(0, 100, 150);
        module.Commit(0);
        module.WindowOpen = true;

        module.Commit(1024);

        Assert.Equal(750, module.Target->ram.hp);
        Assert.Equal(0, module.Successes);
        Assert.Equal(0u, module.ResolvedMask);
        Assert.Equal(2, module.NativeMotionCalls);
    }

    [Fact]
    public void MotionAfterUnblockedStatusCommitDoesNotRestoreOnlyHp()
    {
        using var module = new ParryModule();
        module.Stage(0, 100, 150);
        module.Commit(0);
        module.WindowOpen = true;
        module.DuringNativeCommit = () =>
        {
            module.Target->ram.status_suffer = StatusPermanentFlags.CONFUSION;
            module.Motion();
        };

        module.Commit(1024);

        Assert.Equal(750, module.Target->ram.hp);
        Assert.Equal(StatusPermanentFlags.CONFUSION, module.Target->ram.status_suffer);
        Assert.Equal(0, module.Successes);
        Assert.Equal(1, module.NativeMotionCalls);
    }

    [Fact]
    public void NativeExceptionRestoresCommitContextBeforeIndependentMotion()
    {
        using var module = new ParryModule();
        module.Stage(0, 100);
        module.DuringNativeCommit = () => throw new InvalidOperationException("native continuation");
        Assert.Throws<InvalidOperationException>(() => module.Commit(0));
        Assert.Null(module.ActiveCommitAttacker(0));

        module.WindowOpen = true;
        module.Motion();
        Assert.Equal(1000, module.Target->ram.hp);
        Assert.Equal(1, module.Successes);
    }

    [Fact]
    public void NestedCommitRestoresExactTargetAndAttackerContext()
    {
        using var module = new ParryModule { WindowOpen = true, TargetMask = 3 };
        module.Stage(0, 100);
        module.Bank()[2] = 23;
        module.Actor(1)->stat_exist_flag = true;
        module.Actor(1)->ram.hp = 900;
        module.Actor(1)->damage_hp = 100;
        int nesting = 0;
        module.DuringNativeCommit = () =>
        {
            if (nesting++ == 0)
            {
                Assert.Equal(23, module.ActiveCommitAttacker(0));
                Assert.Null(module.ActiveCommitAttacker(1));
                module.Commit(0, attacker: 24, slot: 1);
                Assert.Equal(23, module.ActiveCommitAttacker(0));
                Assert.Null(module.ActiveCommitAttacker(1));
                module.Motion(1);
                module.Motion(0);
            }
            else
            {
                Assert.Equal(23, module.ActiveCommitAttacker(0));
                Assert.Equal(24, module.ActiveCommitAttacker(1));
                module.Motion(1);
                Assert.Equal(900, module.Actor(1)->ram.hp);
            }
        };

        module.Commit(0, attacker: 23);

        Assert.Equal(900, module.Target->ram.hp);
        Assert.Equal(1000, module.Actor(1)->ram.hp);
        Assert.Equal(1, module.Successes);
        Assert.Null(module.ActiveCommitAttacker(0));
        Assert.Null(module.ActiveCommitAttacker(1));
    }

    [Fact]
    public void SameTargetNestedFailureRestoresOuterAttackerUntilReturn()
    {
        using var module = new ParryModule { WindowOpen = true };
        module.Stage(0, 100);
        module.Bank()[2] = 23;
        int nesting = 0;
        module.DuringNativeCommit = () =>
        {
            if (nesting++ == 0)
            {
                Assert.Throws<InvalidOperationException>(() => module.Commit(0, attacker: 24));
                Assert.Equal(23, module.ActiveCommitAttacker(0));
                module.Motion();
            }
            else
            {
                Assert.Equal(24, module.ActiveCommitAttacker(0));
                throw new InvalidOperationException("nested continuation");
            }
        };

        module.Commit(0, attacker: 23);

        Assert.Equal(900, module.Target->ram.hp);
        Assert.Equal(0, module.Successes);
        Assert.Null(module.ActiveCommitAttacker(0));
    }
}
