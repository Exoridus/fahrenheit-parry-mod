using System.Numerics;
using Xunit;

namespace Fahrenheit.Mods.Parry.Tests;

/// <summary>
///     Unit coverage for <see cref="OverlayAnchorMath"/>. The engine projects each battle
///     actor into a 512x416 virtual viewport once per battle-draw frame. These helpers
///     interpret that data; keeping them pure lets us test the behind-camera predicate,
///     which previously conflated NaN with a valid coordinate.
/// </summary>
public sealed class OverlayAnchorMathTests
{
    [Fact]
    public void IsBehindCamera_Nan_IsTrue()
    {
        // Reserve party members are not on the field and their projection is uninitialised.
        // The old guard used Math.Abs(x) < 1e6, which is false for NaN only by accident.
        Assert.True(OverlayAnchorMath.IsBehindCamera(float.NaN));
    }

    [Fact]
    public void IsBehindCamera_EngineSentinel_IsTrue()
    {
        // MsCalcCursorPos stores (float)0xfffffe00 when 1/w <= 0.
        Assert.True(OverlayAnchorMath.IsBehindCamera(4.294966e9f));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(212f)]
    [InlineData(-317f)]
    public void IsBehindCamera_OrdinaryValues_IsFalse(float sentinel)
    {
        Assert.False(OverlayAnchorMath.IsBehindCamera(sentinel));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(256, 208)]
    [InlineData(512, 416)]
    public void IsWithinVirtualViewport_InsideOrOnEdge_IsTrue(int x, int y)
    {
        Assert.True(OverlayAnchorMath.IsWithinVirtualViewport(x, y));
    }

    [Theory]
    [InlineData(-674, -3)]   // measured from the log with the old 0xf34/0xf38 read
    [InlineData(-185, 40)]
    [InlineData(513, 200)]
    [InlineData(200, 417)]
    public void IsWithinVirtualViewport_Outside_IsFalse(int x, int y)
    {
        Assert.False(OverlayAnchorMath.IsWithinVirtualViewport(x, y));
    }

    [Fact]
    public void ToScreen_ScalesVirtualCoordsToDisplay()
    {
        Vector2 screen = OverlayAnchorMath.ToScreen(256, 208, new Vector2(2560f, 1440f));
        Assert.Equal(1280f, screen.X, 3);
        Assert.Equal(720f, screen.Y, 3);
    }
}
