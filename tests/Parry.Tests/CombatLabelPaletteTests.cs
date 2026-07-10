using System.Numerics;
using Xunit;

namespace Fahrenheit.Mods.Parry.Tests;

/// <summary>
///     Unit coverage for <see cref="CombatLabelPalette"/>. DODGE reads as a solid block,
///     PARRIED and PERFECT reward precise timing and both grant the overdrive boost, so
///     they share one tint. The distinction used to exist only in a comment.
/// </summary>
public sealed class CombatLabelPaletteTests
{
    [Fact]
    public void GetFill_SelectsADifferentFillPerTimingClass()
    {
        // The bug this guards against: draw_animated_label received the flag and ignored it,
        // so every label rendered identically. Assert the two branches actually diverge.
        Assert.NotEqual(
            CombatLabelPalette.GetFill(preciseTiming: true),
            CombatLabelPalette.GetFill(preciseTiming: false));
    }

    [Fact]
    public void PreciseTimingFill_IsWarmerThanPlain()
    {
        // "Gold tint" means: at least as much red, and measurably less blue.
        Vector4 gold = CombatLabelPalette.GetFill(preciseTiming: true);
        Vector4 cream = CombatLabelPalette.GetFill(preciseTiming: false);

        Assert.True(gold.X >= cream.X, "gold must not be less red than cream");
        Assert.True(gold.Z < cream.Z, "gold must be less blue than cream");
    }

    [Fact]
    public void PreciseTimingFill_StaysFaint_NotASignalColour()
    {
        // Expedition 33 reserves saturated gold for the Jump flare. Keep every channel bright
        // so the tint reads as a warm cream, never as a signal.
        Vector4 gold = CombatLabelPalette.GetFill(preciseTiming: true);

        Assert.True(gold.X > 0.9f && gold.Y > 0.8f && gold.Z > 0.6f,
            $"tint too saturated to read as cream: {gold}");
    }

    [Fact]
    public void BothFills_AreFullyOpaque()
    {
        // draw_animated_label overwrites W with the animation alpha; a non-opaque constant
        // would silently double-fade the label.
        Assert.Equal(1.0f, CombatLabelPalette.GetFill(preciseTiming: false).W, 3);
        Assert.Equal(1.0f, CombatLabelPalette.GetFill(preciseTiming: true).W, 3);
    }
}
