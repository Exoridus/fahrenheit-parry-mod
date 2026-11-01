using Fahrenheit.Mods.Vrr;
using Parry = Fahrenheit.Mods.Parry.ParryModule;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

public sealed class VrrParryIntegrationTests
{
    private static (VrrModule Host, Parry Parry) Bind(int simulationHz)
    {
        var host = new VrrModule(simulationHz);
        var parry = new Parry();
        parry.Initialize();
        Assert.True(host.Bind(parry));
        Assert.True(host.Ready);
        parry.DuringUpdate = () => parry.TickWindow();
        return (host, parry);
    }

    [Theory]
    [InlineData(30, 120)]
    [InlineData(30, 144)]
    [InlineData(60, 120)]
    [InlineData(60, 144)]
    public void RealBridgeConsumesOnlyClockStepsAtIndependentImageRates(int simulationHz, int imageHz)
    {
        var (host, parry) = Bind(simulationHz);
        var clock = new FixedSimulationClock(simulationHz);
        clock.Plan(0);
        int windowTicks = simulationHz / 5;
        parry.OpenWindow(windowTicks);
        int dispatched = 0, zeroStepImages = 0;
        for (int image = 1; image <= imageHz; image++)
        {
            var plan = clock.Plan((double)image / imageHz);
            host.BeginImage(1d / imageHz);
            if (plan.Steps == 0)
            {
                zeroStepImages++;
                int before = parry.Updates;
                host.RunLoop(parry, simulation: false, 1f / imageHz);
                Assert.Equal(before, parry.Updates);
            }
            for (int step = 0; step < plan.Steps; step++)
            {
                Assert.Equal(dispatched < windowTicks, parry.WindowAccepts());
                host.RunLoop(parry, simulation: true, 1f / imageHz);
                dispatched++;
                Assert.Equal(dispatched < windowTicks, parry.WindowAccepts());
            }
        }
        Assert.True(zeroStepImages > 0);
        Assert.Equal(simulationHz, dispatched);
        Assert.Equal(dispatched, parry.Updates);
        Assert.InRange(parry.Elapsed, .999f, 1.001f);
        Assert.Equal(1, parry.Whiffs);
        Assert.Null(host.Failure);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void PausedClockPreservesWindowAndCatchUpDispatchesEveryOwedStep(int simulationHz)
    {
        var (host, parry) = Bind(simulationHz);
        var clock = new FixedSimulationClock(simulationHz);
        clock.Plan(0, speed: 0);
        parry.OpenWindow(2);
        for (int image = 1; image <= 12; image++)
        {
            var plan = clock.Plan(image / 120d, speed: 0);
            Assert.Equal(0, plan.Steps);
            host.BeginImage(1d / 120);
            host.RunLoop(parry, simulation: false, 1f / 120);
        }
        Assert.Equal(0, parry.Updates);
        Assert.True(parry.WindowAccepts());
        var catchUp = clock.Plan(.2, speed: 1);
        Assert.Equal(simulationHz / 10, catchUp.Steps);
        host.BeginImage(.1);
        for (int step = 0; step < catchUp.Steps; step++) host.RunLoop(parry, true, .1f);
        Assert.Equal(catchUp.Steps, parry.Updates);
        Assert.False(parry.WindowAccepts());
        Assert.Equal(1, parry.Whiffs);
        Assert.Null(host.Failure);
    }

    [Fact]
    public void EpochAdvanceKeepsWindowFiniteAndConsumerRejectsStaleOrDuplicateIdentity()
    {
        var (host, parry) = Bind(30);
        parry.OpenWindow(2);
        host.BeginImage(.01);
        host.RunLoop(parry, true, .01f);
        long oldEpoch = host.Epoch;
        host.ResetHistory();
        host.BeginImage(.01);
        host.EnterLoop();
        parry.PreUpdate(.01f);
        Assert.Throws<InvalidOperationException>(() => host.ReplayIdentity(oldEpoch - 1, host.Serial + 1, 1f / 30));
        Assert.Throws<InvalidOperationException>(() => host.ReplayIdentity(oldEpoch, host.Serial, 1f / 30));
        Assert.Equal(1, parry.Updates);
        host.CompleteClock(true);
        host.PublishSnapshot();
        host.ExitLoop();
        Assert.Equal(2, parry.Updates);
        Assert.False(parry.WindowAccepts());
        Assert.Null(host.Failure);
    }

    [Fact]
    public void NativeHandoverDispatchesGrantedDeltaOnceAcrossHistoryReset()
    {
        var (host, parry) = Bind(60);
        parry.OpenWindow(1);
        host.BeginImage(.025);
        host.RunLoop(parry, true, .025f, nativeHandover: true);
        Assert.Equal(1, parry.Updates);
        Assert.Equal(.025f, parry.Elapsed);
        Assert.False(parry.WindowAccepts());
        Assert.Null(host.Failure);
    }
}
