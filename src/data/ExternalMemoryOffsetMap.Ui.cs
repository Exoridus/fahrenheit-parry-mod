namespace Fahrenheit.Mods.Parry;

public static partial class ExternalMemoryOffsetMap
{
    public static class BattleUi
    {
        // Main battle menu active flag.
        public const int MainBattleMenu = 0x00F3C911;

        // Battle command cursor in main menu.
        public const int BattleMenuCursor = 0x00F3C926;

        // Selected target id in battle.
        public const int BattleTargetId = 0x00F3D1B4;

        // Current target side/line (0 friendly / 1 enemy in many research notes).
        public const int BattleLineTarget = 0x00F3CA42;
    }

    public static class Formation
    {
        // Active battle formation slots.
        public const int ActiveFormationSlot1 = 0x00F3F76C;
        public const int ActiveFormationSlot2 = 0x00F3F76E;
        public const int ActiveFormationSlot3 = 0x00F3F770;
    }
}
