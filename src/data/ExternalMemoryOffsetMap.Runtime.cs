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

    public static class StartupState
    {
        // Expected type: byte
        public const int MenuState = 0x00F407E4;
        
        // Expected type: uint
        public const int MoviePlay = 0x00D2A008;

        // Expected type: uint
        public const int StateD36FA0 = 0x00D36FA0;

        // Expected type: uint
        public const int StateD36FA4 = 0x00D36FA4;
    }

    public static class Functions
    {
        // Expected type: Action<byte, int, int> (MsDamageSetMotion)
        public const int MsDamageSetMotion = 0x0038CAE0;
        
        // Expected type: Func<Chr*, Chr*, Command*, int, int*, int, int> (DmgCalcArmored)
        public const int DmgCalcArmored = 0x0038AB80;
        
        // Expected type: Func<int, nint, int, nint, nint, int, nint, nint, nint, nint, int, int> (MsCalcDamageInternal)
        public const int MsCalcDamageInternal = 0x0038E680;

        // Expected type: Action<uint> (AtelEventSetUp)
        public const int AtelEventSetUp = 0x00472e90;

        // Expected type: Func<int> (NeedShowJapanLogo)
        public const int NeedShowJapanLogo = 0x00387450;
        
        // Expected type: Func<uint, char*> (AtelGetEventName)
        public const int AtelGetEventName = 0x004796e0;
    }
}
