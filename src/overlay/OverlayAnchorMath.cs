using System;
using System.Numerics;

namespace Fahrenheit.Mods.Parry;

/// <summary>
///     Pure helpers for interpreting the per-actor screen projection that
///     <c>MsCalcCursorPos</c> (FFX.exe+0x79F3A0) writes into every <c>Chr</c> once per
///     battle-draw frame, in a 512x416 virtual viewport.
///
///     Kept pure so the behind-camera predicate is testable. The predicate matters:
///     the projection is NaN for reserve members who are not on the field, and a naive
///     <c>Math.Abs(x) &lt; 1e6</c> check only rejects NaN as a side effect of IEEE
///     comparison rules, which made the two cases indistinguishable in the logs.
/// </summary>
public static class OverlayAnchorMath
{
    /// <summary>Width of the engine's virtual battle viewport (TOMakePktScissor 0x200).</summary>
    public const float VirtualWidth = 512f;

    /// <summary>Height of the engine's virtual battle viewport (TOMakePktScissor 0x1a0).</summary>
    public const float VirtualHeight = 416f;

    /// <summary>
    ///     True when the actor has no usable projection: either the engine's
    ///     behind-camera sentinel (<c>(float)0xfffffe00</c>, stored when <c>1/w &lt;= 0</c>)
    ///     or NaN propagated from an uninitialised actor.
    /// </summary>
    public static bool IsBehindCamera(float sentinel)
        => float.IsNaN(sentinel) || MathF.Abs(sentinel) >= 1_000_000f;

    /// <summary>
    ///     True when a projected point lies inside the virtual viewport, edges included.
    ///     Used to decide whether the unclamped engine anchor is usable.
    /// </summary>
    public static bool IsWithinVirtualViewport(int virtX, int virtY)
        => virtX >= 0 && virtX <= (int)VirtualWidth
        && virtY >= 0 && virtY <= (int)VirtualHeight;

    /// <summary>Scales a virtual-viewport point to the current display resolution.</summary>
    public static Vector2 ToScreen(int virtX, int virtY, Vector2 displaySize)
        => new(
            virtX * (displaySize.X / VirtualWidth),
            virtY * (displaySize.Y / VirtualHeight));
}
