using System.Numerics;

namespace Fahrenheit.Mods.Parry;

/// <summary>
///     Fill colours for the DODGE / PARRIED / PERFECT combat labels.
///
///     Approximated from a Clair Obscur: Expedition 33 screenshot, NOT sampled from the
///     game's assets. Treat the exact channel values as a design choice, not as measured
///     truth.
///
///     Both fills sit close to white: on a busy battlefield the labels have to read as text
///     first and as a signal second. The gold on PARRIED and PERFECT is a tint, not a colour —
///     in Expedition 33 a strong gold flare marks the Jump prompt, i.e. an attack that can be
///     neither dodged nor parried, so a saturated gold reward would invert that meaning.
/// </summary>
public static class CombatLabelPalette
{
    /// <summary>Near-white with a trace of warmth. Used by DODGE.</summary>
    public static readonly Vector4 Plain = new(0.98f, 0.97f, 0.94f, 1.0f);

    /// <summary>Near-white with a gold tint. Used by PARRIED and PERFECT — a hit met on time.</summary>
    public static readonly Vector4 PreciseTiming = new(1.00f, 0.96f, 0.84f, 1.0f);

    /// <param name="preciseTiming">
    ///     <c>true</c> for PARRIED and PERFECT — a hit answered inside the tight parry window.
    ///     <c>false</c> for a plain DODGE.
    /// </param>
    public static Vector4 GetFill(bool preciseTiming) => preciseTiming ? PreciseTiming : Plain;
}
