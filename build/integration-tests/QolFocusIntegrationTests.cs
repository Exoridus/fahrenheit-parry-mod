using Fahrenheit.Mods.Qol;
using Fahrenheit.Mods.Vrr;
using Parry = Fahrenheit.Mods.Parry.ParryModule;
using Xunit;

public sealed class QolFocusIntegrationTests
{
    [Theory]
    [InlineData(false, 30)]
    [InlineData(false, 60)]
    [InlineData(true, 30)]
    [InlineData(true, 60)]
    public void FocusHoldReleaseAndEpochPreserveEdgesAcrossCooperativeAndStandaloneTicks(bool withVrr, int hz)
    {
        var qol = new FocusInputModule();
        var parry = new Parry();
        var host = new VrrModule(hz);
        qol.Ready();
        parry.Initialize();
        if (withVrr) Assert.True(host.Bind(parry));
        float delta = 1f / hz;
        // Native pad-to-script snapshot translation is modeled; the policy and dispatch are linked source.
        qol.Native = () => parry.Pressed = (qol.Buttons[0] & 0x800) != 0;
        void Tick(uint? refreshedButtons)
        {
            if (refreshedButtons.HasValue) qol.Poll(0, refreshedButtons.Value);
            qol.Publish();
            if (withVrr) { host.BeginImage(delta); host.RunLoop(parry, true, delta); }
            else parry.PreUpdate(delta);
        }

        Tick(0);
        Tick(0x800);
        Assert.Equal(1, parry.Edges);
        for (int cycle = 0; cycle < 3; cycle++)
        {
            qol.Focused = false;
            Tick(0x800);
            qol.Focused = true;
            for (int scan = 0; scan < 5; scan++) Tick(0x800);
            Assert.Equal(cycle + 1, parry.Edges);
            for (int catchUp = 0; catchUp < 4; catchUp++) Tick(null);
            Assert.Equal(cycle + 1, parry.Edges);
            Assert.False(qol.TryGetVrrInputPolicy(1, out _, out _));
            if (withVrr)
            {
                host.ResetHistory();
                int updates = parry.Updates;
                foreach (int imageHz in new[] { 120, 144 })
                {
                    host.BeginImage(1d / imageHz);
                    host.RunLoop(parry, false, 1f / imageHz);
                }
                Assert.Equal(updates, parry.Updates);
            }
            Tick(0);
            Assert.True(qol.TryGetVrrInputPolicy(1, out _, out bool neutralized));
            Assert.False(neutralized);
            Tick(0x800);
            Assert.Equal(cycle + 2, parry.Edges);
        }
        Assert.Null(host.Failure);
    }
}
