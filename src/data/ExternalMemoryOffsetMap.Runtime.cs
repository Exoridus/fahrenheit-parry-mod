namespace Fahrenheit.Mods.Parry;

public static partial class ExternalMemoryOffsetMap
{
    public static class FrameAndRng
    {
        // Global frame counter.
        public const int FrameCounter = 0x0088FDD8;

        // RNG index table base (4-byte entries).
        public const int RngBase = 0x00D35ED8;
    }
}
