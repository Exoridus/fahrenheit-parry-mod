using System.Numerics;

namespace Fahrenheit.Mods.Parry;

/// <summary>
///     Fill colours for the DODGE / PARRIED / PERFECT combat labels.
///
///     Approximated from a Clair Obscur: Expedition 33 screenshot, NOT sampled from the
///     game's assets. Treat the exact channel values as a design choice, not as measured
///     truth.
///
///     The gold tint is deliberately faint. In Expedition 33 a strong gold flare marks
///     the Jump prompt, i.e. an attack that can be neither dodged nor parried; using that
///     same signal colour for a reward would invert its meaning.
/// </summary>
public static class CombatLabelPalette
{
    /// <summary>Warm cream. Used by DODGE — a solid block, no overdrive boost.</summary>
    public static readonly Vector4 Plain = new(0.96f, 0.93f, 0.86f, 1.0f);

    /// <summary>Cream with a gold tint. Used by PARRIED and PERFECT, which both grant the boost.</summary>
    public static readonly Vector4 PreciseTiming = new(0.98f, 0.89f, 0.68f, 1.0f);

    /// <param name="preciseTiming">
    ///     <c>true</c> for PARRIED and PERFECT — a hit answered inside the tight parry window.
    ///     <c>false</c> for a plain DODGE.
    /// </param>
    public static Vector4 GetFill(bool preciseTiming) => preciseTiming ? PreciseTiming : Plain;
}
