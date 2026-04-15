namespace Fahrenheit.Mods.Parry;

public static partial class ExternalMemoryOffsetMap
{
    public static class OptionsStruct
    {
        // RVA of the static pointer cell in FFX.exe that holds the heap
        // address of the dynamically-allocated options settings struct.
        // The struct is an int32[] whose writer is FFX.exe+0x2A09
        // (mov [ecx+eax*4], edx). Dereference to get the heap base, then
        // index by the constants below.
        //
        // Derivation: CE scan for struct base value 0x0EC724E0 (derived from
        // SE Volume address 0x0EC72500 = base + index_8 * 4) returned one hit
        // at FFX.exe+1EFB504 in a session where FFX.exe loaded at 0x00160000.
        // RVA = 0x01EFB504.
        // Cross-check: Ghidra address = 0x01EFB504 + 0x00400000 = 0x021FB504,
        // within SizeOfImage (0x0237D000). FFX.exe has DYNAMIC_BASE set, so
        // all offsets here are RVAs, not session-specific absolute VAs.
        public const int PointerAddress = 0x01EFB504;

        // Expected type: int32. Scale: 0..100 inclusive.
        public const int MasterVolumeIndex = 5;

        // Expected type: int32. Scale: 0..100 inclusive.
        public const int VoiceVolumeIndex = 6;

        // Expected type: int32. Scale: 0..100 inclusive.
        public const int MusicVolumeIndex = 7;

        // Expected type: int32. Scale: 0..100 inclusive.
        public const int SeVolumeIndex = 8;

        public const int VolumeScaleMax = 100;
    }
}
