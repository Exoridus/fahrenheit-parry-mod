using Xunit;

namespace Fahrenheit.Mods.Parry.Tests;

/// <summary>
///     Contract for the overdrive-mask → set-bit-index list used by the read-only
///     save probe. The probe compares this list against the in-game Overdrive menu
///     to decide whether the derived offsets are trustworthy, so the mapping (bit N
///     = mode index N, ascending, "none" when empty) must stay stable.
/// </summary>
public sealed class OverdriveMaskFormatterTests
{
    [Fact]
    public void FormatSetBits_Empty_Mask_Reads_None()
    {
        Assert.Equal("none", OverdriveMaskFormatter.FormatSetBits(0u));
    }

    [Fact]
    public void FormatSetBits_Single_Bit_Reports_That_Index()
    {
        // Tidus at game start: Slayer == mode index 8 == bit 8 == 0x100.
        Assert.Equal("8", OverdriveMaskFormatter.FormatSetBits(0x00000100u));
    }

    [Fact]
    public void FormatSetBits_Lists_Indices_Ascending()
    {
        // bits 0, 8 and 17 (0x11, the custom-mode target).
        uint mask = (1u << 0) | (1u << 8) | (1u << 17);
        Assert.Equal("0, 8, 17", OverdriveMaskFormatter.FormatSetBits(mask));
    }

    [Fact]
    public void FormatSetBits_Handles_High_Bit()
    {
        Assert.Equal("31", OverdriveMaskFormatter.FormatSetBits(0x80000000u));
    }

    [Fact]
    public void WithModeBitSet_Sets_Unset_Bit_And_Preserves_Others()
    {
        // Yuna's live mask was 0x00000004 (bit 2). Unlocking custom overdrive mode
        // index 0x11 (bit 17) must set bit 17 while leaving bit 2 untouched.
        uint before = 0x00000004u;
        uint after = OverdriveMaskFormatter.WithModeBitSet(before, 0x11);
        Assert.Equal(0x00020004u, after);
    }

    [Fact]
    public void WithModeBitSet_Already_Set_Bit_Is_NoOp()
    {
        // Bit 17 already set: the returned mask must equal the input exactly, so the
        // caller can compare before == after and skip the write.
        uint before = 0x00020105u;
        uint after = OverdriveMaskFormatter.WithModeBitSet(before, 0x11);
        Assert.Equal(before, after);
    }
}
