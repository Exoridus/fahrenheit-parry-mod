using Xunit;

namespace Fahrenheit.Mods.Parry.Tests;

/// <summary>
///     Unit coverage for the per-probe per-frame fire-rate cap that wraps
///     each Stage-1 hook body. The throttle is meant to keep a misbehaving
///     probe from draining the deferred ring inside one frame.
/// </summary>
public sealed class PerFrameProbeThrottleTests
{
    [Fact]
    public void ShouldEmit_Returns_True_Up_To_Cap()
    {
        var throttle = new PerFrameProbeThrottle();

        for (int i = 0; i < 4; i++)
            Assert.True(throttle.ShouldEmit(currentFrame: 1, maxPerFrame: 4));

        Assert.Equal(4, throttle.FiredThisFrame);
        Assert.Equal(0, throttle.DroppedThisFrame);
    }

    [Fact]
    public void ShouldEmit_Returns_False_Past_Cap_And_Counts_Drops()
    {
        var throttle = new PerFrameProbeThrottle();

        for (int i = 0; i < 3; i++)
            Assert.True(throttle.ShouldEmit(currentFrame: 5, maxPerFrame: 3));

        Assert.False(throttle.ShouldEmit(5, 3));
        Assert.False(throttle.ShouldEmit(5, 3));

        Assert.Equal(3, throttle.FiredThisFrame);
        Assert.Equal(2, throttle.DroppedThisFrame);
    }

    [Fact]
    public void ShouldEmit_Resets_Counters_On_New_Frame()
    {
        var throttle = new PerFrameProbeThrottle();

        // Saturate frame 10.
        for (int i = 0; i < 5; i++)
            throttle.ShouldEmit(10, maxPerFrame: 2);

        Assert.Equal(2, throttle.FiredThisFrame);
        Assert.Equal(3, throttle.DroppedThisFrame);

        // Advance to frame 11 — counters reset, full budget available.
        Assert.True(throttle.ShouldEmit(11, maxPerFrame: 2));
        Assert.Equal(11ul, throttle.LastFrame);
        Assert.Equal(1, throttle.FiredThisFrame);
        Assert.Equal(0, throttle.DroppedThisFrame);
    }

    [Fact]
    public void ShouldEmit_Tracks_LastFrame_Even_When_Past_Cap()
    {
        var throttle = new PerFrameProbeThrottle();

        Assert.True(throttle.ShouldEmit(100, maxPerFrame: 1));
        Assert.False(throttle.ShouldEmit(100, maxPerFrame: 1));

        Assert.Equal(100ul, throttle.LastFrame);
    }

    [Fact]
    public void ShouldEmit_Cap_Of_Zero_Always_Drops()
    {
        var throttle = new PerFrameProbeThrottle();

        Assert.False(throttle.ShouldEmit(1, maxPerFrame: 0));
        Assert.False(throttle.ShouldEmit(1, maxPerFrame: 0));

        Assert.Equal(0, throttle.FiredThisFrame);
        Assert.Equal(2, throttle.DroppedThisFrame);
    }
}
