using Fahrenheit.Mods.Parry;
using Xunit;

public sealed class ParryWindowTimingTests
{
    [Fact]
    public void StalledSimulationDoesNotExpireNativeWindow()
    {
        var module = new ParryModule();
        module.OpenWindow(1);
        Thread.Sleep(100);
        Assert.True(module.WindowAccepts());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(12)]
    public void NativeWindowAdmitsExactlyAuthoredTicksIncludingPressTick(int ticks)
    {
        var module = new ParryModule();
        module.OpenWindow(ticks);
        module.TickWindow(advance: false);
        for (int sample = 0; sample < ticks; sample++)
        {
            Assert.True(module.WindowAccepts());
            module.TickWindow();
        }
        Assert.False(module.WindowAccepts());
        Assert.Equal(ParryInputState.WhiffLockout, module.WindowState);
        Assert.Equal(1, module.Whiffs);
    }

    [Fact]
    public void ResolvedWindowStillExpiresAtItsOriginalDeadline()
    {
        var module = new ParryModule();
        module.OpenWindow(2);
        module.ResolveWindow();
        Assert.True(module.WindowAccepts());
        module.TickWindow();
        Assert.True(module.WindowAccepts());
        module.TickWindow();
        Assert.False(module.WindowAccepts());
        Assert.Equal(0, module.Whiffs);
    }

    [Fact]
    public void OnlyArmedSlotsAcceptAndClearRevokesWindow()
    {
        var module = new ParryModule();
        Assert.False(module.WindowAccepts());
        module.OpenWindow(6, targets: 0b10);
        Assert.False(module.WindowAccepts(0));
        Assert.True(module.WindowAccepts(1));
        module.ClearWindow();
        Assert.False(module.WindowAccepts(1));
    }

    [Fact]
    public void DeferredRenderUpdatesDoNotConsumeOwnedSimulationWindow()
    {
        var module = new ParryModule();
        module.Initialize();
        Assert.True(module.TryBindVrrSimulation(1, new object(), _ => true, out _, out var tick));
        module.OpenWindow(2);
        module.DuringUpdate = () => module.TickWindow();
        for (int render = 0; render < 10; render++) module.PreUpdate(1f / 120);
        Assert.True(module.WindowAccepts());
        tick!(0, 0, 1f / 30);
        Assert.True(module.WindowAccepts());
        module.PreUpdate(1f / 120);
        tick(0, 1, 1f / 30);
        Assert.False(module.WindowAccepts());
        Assert.Equal(1, module.Whiffs);
    }

    [Fact]
    public void RuntimeResetRevokesDeadlineBeforeAnotherPress()
    {
        var module = new ParryModule();
        module.OpenWindow(6, targets: 0b11);
        module.ResetWindow();
        Assert.False(module.WindowAccepts());
        module.OpenWindow(1, targets: 0b10);
        Assert.False(module.WindowAccepts(0));
        Assert.True(module.WindowAccepts(1));
    }

    [Fact]
    public void WhiffLockoutConsumesOnlyItsAuthoredTicksThenFreshPressRearms()
    {
        var module = new ParryModule();
        Assert.True(module.PressWindow(1));
        module.TickWindow(advance: false);
        module.TickWindow();
        for (int recovery = 0; recovery < 3; recovery++)
        {
            Assert.False(module.PressWindow(1));
            module.TickWindow();
        }
        Assert.Equal(ParryInputState.Ready, module.WindowState);
        Assert.True(module.PressWindow(1));
        Assert.True(module.WindowAccepts());
        Assert.Equal(1, module.Whiffs);
    }
}
