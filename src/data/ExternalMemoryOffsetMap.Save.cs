namespace Fahrenheit.Mods.Parry;

public static partial class ExternalMemoryOffsetMap
{
    /// <summary>
    ///     Persisted party-save (<c>PlySave</c>) offsets used by the read-only
    ///     overdrive-mode diagnostic probe.
    ///
    ///     <para>
    ///         Provenance: triangulated on 2026-07-10 from three sources that agree —
    ///         the FFX decompilation, a Ghidra struct export, and Cheat-Engine
    ///         absolute addresses. All values below are <b>derived, not
    ///         live-verified</b>. Nobody has read this memory from the running
    ///         process yet, so treat every constant here as a hypothesis under test.
    ///         The overdrive-read probe exists to confirm these against the in-game
    ///         Overdrive menu before any write is ever considered.
    ///     </para>
    ///
    ///     <para>
    ///         Consistency anchor (must hold): the base RVA plus the
    ///         <c>limit_modes_obtained</c> field offset equals the RVA forensics
    ///         independently named for char 0's mask —
    ///         <c>0x00D3205C + 0x88 == 0x00D320E4</c>.
    ///     </para>
    ///
    ///     These are raw addresses per repository rule §9; nothing in gameplay code
    ///     may hard-code them. Resolve through <c>FhUtil.ptr_at&lt;T&gt;</c>, which
    ///     maps an RVA to a live pointer via <c>FhEnvironment.BaseAddr + rva</c>.
    /// </summary>
    public static class SaveData
    {
        /// <summary>
        ///     RVA of the first <c>PlySave</c> entry (<c>ply_arr[0]</c>, char id 0 =
        ///     Tidus). Absolute VA in the reference capture was <c>0x0113205C</c>;
        ///     image base <c>0x400000</c> gives this RVA. Derived, not live-verified.
        /// </summary>
        public const int PlyArr0 = 0x00D3205C;

        /// <summary>
        ///     Byte stride between consecutive <c>PlySave</c> entries. Char id N lives
        ///     at <see cref="PlyArr0"/> + N * <see cref="PlySaveStride"/>.
        /// </summary>
        public const int PlySaveStride = 0x94;

        /// <summary>
        ///     Offset of <c>limit_modes_obtained</c> within a <c>PlySave</c> entry.
        ///     4 bytes, little-endian bitmask; bit N = overdrive mode index N learned.
        ///     Custom overdrive mode work targets index <c>0x11</c> (bit 17).
        /// </summary>
        public const int LimitModesObtained = 0x88;

        /// <summary>
        ///     Offset of <c>limit_mode_index</c> within a <c>PlySave</c> entry (the
        ///     persisted selected overdrive mode). 1 byte. Read alongside
        ///     <see cref="LimitModesObtained"/> to confirm the stride from a second
        ///     field angle.
        /// </summary>
        public const int LimitModeIndex = 0x38;
    }
}
