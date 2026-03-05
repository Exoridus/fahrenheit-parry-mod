namespace Fahrenheit.Mods.Parry;

public static partial class ExternalMemoryOffsetMap {
    /// <summary>
    /// Candidate offsets/functions mentioned in Discord reverse-engineering threads.
    /// Treat as unverified until validated against runtime behavior in this project.
    /// </summary>
    public static class DiscordCandidates {
        // Mentioned as queued/active command id in battle context: *(T_XCommandId*)((byte*)btl + 0x3A8)
        public const int BtlOffsetLikelyQueuedCommandId = 0x03A8;

        // Mentioned in overdrive-mode checks in arena-related discussion.
        public const int BtlOffsetLikelyArenaContextFlag = 0x2115;

        // Mentioned in overdrive-learning flow discussion.
        public const int BtlOffsetLikelyOverdriveLearnPopupFlag = 0x175B;

        // Mentioned as active-monster count/state during monster init hooks.
        public const int GlobalLikelyInitializedMonsterCount = 0x00D2CA80;

        // Suggested range for monster/battle-related bytes in one debugging thread.
        public const int GlobalLikelyBattleRangeStart = 0x00D2CA90;
        public const int GlobalLikelyBattleRangeEnd = 0x00D33350;

        // Suggested function offsets from Discord hooks (FFX.exe + offset).
        public const int FnDmgCalcArmored = 0x0038AB80;
        public const int FnEiAbmParaGet = 0x00A54860;

        // Mentioned as FFX input raw address in Fahrenheit-dev discussion.
        public const int InputRawAddress = 0x00F27080;

        // Mentioned global candidate in Fahrenheit-dev discussion.
        public const int GlobalCandidateD35DF8 = 0x00D35DF8;
    }
}
