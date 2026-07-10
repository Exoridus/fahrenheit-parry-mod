namespace Fahrenheit.Mods.Parry;

/// <summary>
///     Formats a <c>limit_modes_obtained</c> bitmask into the ascending list of
///     set bit indices (= learned overdrive-mode indices).
///
///     Extracted from the read-only overdrive probe in <c>ParryModule.Debug.cs</c>
///     so the mask → indices mapping is unit-testable without the runtime memory
///     read. The probe correlates these indices against the in-game Overdrive menu,
///     so the list contract must stay stable.
/// </summary>
internal static class OverdriveMaskFormatter
{
    /// <summary>
    ///     Returns the ascending, comma-separated list of set bit indices in
    ///     <paramref name="mask"/> (bit 0 first). Returns <c>"none"</c> when no bit
    ///     is set, so an empty mask is never ambiguous in the log.
    /// </summary>
    public static string FormatSetBits(uint mask)
    {
        if (mask == 0) return "none";

        var indices = new System.Collections.Generic.List<int>(32);
        for (int bit = 0; bit < 32; bit++)
        {
            if ((mask & (1u << bit)) != 0) indices.Add(bit);
        }

        return string.Join(", ", indices);
    }
}
