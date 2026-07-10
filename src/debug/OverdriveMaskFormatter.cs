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

    /// <summary>
    ///     Returns <paramref name="mask"/> with the bit for overdrive mode
    ///     <paramref name="modeIndex"/> set (bit N = mode index N, matching
    ///     <see cref="FormatSetBits"/>). Idempotent and bit-preserving: if the bit is
    ///     already set the identical value is returned, and every other bit is left
    ///     untouched — the caller compares <c>before == after</c> to decide whether a
    ///     write is needed. Kept pure so the save-write's bit logic is unit-tested
    ///     without a live memory read.
    /// </summary>
    /// <exception cref="System.ArgumentOutOfRangeException">
    ///     <paramref name="modeIndex"/> is outside the 0..31 range of a 32-bit mask.
    /// </exception>
    public static uint WithModeBitSet(uint mask, int modeIndex)
    {
        if (modeIndex is < 0 or > 31)
        {
            throw new System.ArgumentOutOfRangeException(
                nameof(modeIndex), modeIndex, "Overdrive mode index must be in the 0..31 range of the 32-bit mask.");
        }

        return mask | (1u << modeIndex);
    }
}
