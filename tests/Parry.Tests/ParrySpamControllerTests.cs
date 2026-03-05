using Fahrenheit.Mods.Parry;
using Xunit;

namespace Fahrenheit.Mods.Parry.Tests;

public sealed class ParrySpamControllerTests {
    [Fact]
    public void PressWithoutRelease_ShouldNotEscalateTier() {
        var controller = new ParrySpamController();

        ParrySpamTransition transition = controller.OnQualifyingPress();

        Assert.False(transition.TierChanged);
        Assert.Equal(0, transition.CurrentTier);
        Assert.False(controller.ReleaseArmed);
    }

    [Fact]
    public void ReleaseThenPress_ShouldEscalateTier() {
        var controller = new ParrySpamController();

        controller.ArmOnQualifyingRelease();
        ParrySpamTransition transition = controller.OnQualifyingPress();

        Assert.True(transition.TierChanged);
        Assert.Equal(0, transition.PreviousTier);
        Assert.Equal(1, transition.CurrentTier);
        Assert.False(controller.ReleaseArmed);
        Assert.True(controller.CalmResetRemainingSeconds > 0f);
    }

    [Fact]
    public void SameTickReleaseThenPress_ShouldEscalateOnceAndRequireAnotherRelease() {
        var controller = new ParrySpamController();

        controller.ArmOnQualifyingRelease();
        ParrySpamTransition first = controller.OnQualifyingPress();
        ParrySpamTransition second = controller.OnQualifyingPress();

        Assert.Equal(1, first.CurrentTier);
        Assert.False(second.TierChanged);
        Assert.Equal(1, second.CurrentTier);
        Assert.False(controller.ReleaseArmed);
    }

    [Fact]
    public void CalmTimeout_ShouldResetTierAndArmedState() {
        var controller = new ParrySpamController();
        controller.ArmOnQualifyingRelease();
        controller.OnQualifyingPress();
        controller.ArmOnQualifyingRelease();

        ParrySpamTransition noResetYet = controller.Tick(ParryDifficultyModel.SpamTierResetCooldownSeconds - 0.01f);
        Assert.False(noResetYet.Reset);
        Assert.True(controller.ReleaseArmed);

        ParrySpamTransition reset = controller.Tick(0.02f);
        Assert.True(reset.Reset);
        Assert.Equal("calm", reset.Reason);
        Assert.Equal(0, controller.TierIndex);
        Assert.False(controller.ReleaseArmed);
        Assert.Equal(0f, controller.CalmResetRemainingSeconds);
    }

    [Fact]
    public void SuccessReset_ShouldResetImmediately() {
        var controller = new ParrySpamController();
        controller.ArmOnQualifyingRelease();
        controller.OnQualifyingPress();
        Assert.Equal(1, controller.TierIndex);

        ParrySpamTransition reset = controller.Reset("success");

        Assert.True(reset.Reset);
        Assert.Equal("success", reset.Reason);
        Assert.Equal(0, controller.TierIndex);
        Assert.False(controller.ReleaseArmed);
        Assert.Equal(0f, controller.CalmResetRemainingSeconds);
    }

    [Fact]
    public void RepeatedTapSpam_ShouldClampAtMaxTier() {
        var controller = new ParrySpamController();

        for (int i = 0; i < 10; i++) {
            controller.ArmOnQualifyingRelease();
            controller.OnQualifyingPress();
        }

        Assert.Equal(ParryDifficultyModel.MaxSpamTierIndex, controller.TierIndex);
    }
}
