namespace Fahrenheit.Mods.Parry;

public static partial class ExternalMemoryOffsetMap {
    public static class BattleFlags {
        // 1 == battle active
        public const int BattleActive = 0x00D2A8E0;

        // General battle state byte used by many tools.
        public const int BattleState = 0x00D2C9F1;
    }

    public static class TurnQueue {
        // Turn queue entries: queue[i] at TurnQueueBase + i * 4 (byte actor id at each slot).
        public const int TurnQueueBase = 0x00D2AA00;

        // Current acting battler id.
        public const int CurrentTurnActor = 0x00D36A68;
    }

    public static class BattlerStruct {
        // Pointer to battler array base. Per-battler stride is 0xF90.
        public const int BattlerArrayPointer = 0x00D334CC;
        public const int BattlerStride = 0x0F90;

        // Common per-battler field offsets from battler base + BattlerStride * index.
        public const int OffsetMaxHp = 0x0594;
        public const int OffsetCurrentHp = 0x05D0;
        public const int OffsetCurrentMp = 0x05D4;
        public const int OffsetOverdriveGauge = 0x05BC;
        public const int OffsetStatusByte0 = 0x0606;
        public const int OffsetStatusByte1 = 0x0607;
        public const int OffsetStatusByte2 = 0x0608;
        public const int OffsetStatusByte3 = 0x0609;
        public const int OffsetFlags617 = 0x0617;

        // Used by TAS research for "last hit" / per-enemy transition checks.
        public const int OffsetLastHitValue = 0x07AC;
    }
}
