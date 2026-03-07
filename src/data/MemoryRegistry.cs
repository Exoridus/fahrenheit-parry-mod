namespace Fahrenheit.Mods.Parry;

/// <summary>
/// Immutable registry wrappers for offset constants.
/// This gives named descriptors for tooling/diagnostics without changing the stable offset map API.
/// </summary>
internal static class MemoryRegistry
{
    internal static class Battle
    {
        public static readonly MemoryLocation BattleActive =
            new(nameof(BattleActive), ExternalMemoryOffsetMap.BattleFlags.BattleActive);
        public static readonly MemoryLocation BattleState =
            new(nameof(BattleState), ExternalMemoryOffsetMap.BattleFlags.BattleState);
        public static readonly MemoryLocation TurnQueueBase =
            new(nameof(TurnQueueBase), ExternalMemoryOffsetMap.TurnQueue.TurnQueueBase);
        public static readonly MemoryLocation CurrentTurnActor =
            new(nameof(CurrentTurnActor), ExternalMemoryOffsetMap.TurnQueue.CurrentTurnActor);
        public static readonly MemoryLocation BattlerArrayPointer =
            new(nameof(BattlerArrayPointer), ExternalMemoryOffsetMap.BattlerStruct.BattlerArrayPointer);
    }

    internal static class Ui
    {
        public static readonly MemoryLocation MainBattleMenu =
            new(nameof(MainBattleMenu), ExternalMemoryOffsetMap.BattleUi.MainBattleMenu);
        public static readonly MemoryLocation BattleMenuCursor =
            new(nameof(BattleMenuCursor), ExternalMemoryOffsetMap.BattleUi.BattleMenuCursor);
        public static readonly MemoryLocation BattleTargetId =
            new(nameof(BattleTargetId), ExternalMemoryOffsetMap.BattleUi.BattleTargetId);
        public static readonly MemoryLocation BattleLineTarget =
            new(nameof(BattleLineTarget), ExternalMemoryOffsetMap.BattleUi.BattleLineTarget);
        public static readonly MemoryLocation ActiveFormationSlot1 =
            new(nameof(ActiveFormationSlot1), ExternalMemoryOffsetMap.Formation.ActiveFormationSlot1);
        public static readonly MemoryLocation ActiveFormationSlot2 =
            new(nameof(ActiveFormationSlot2), ExternalMemoryOffsetMap.Formation.ActiveFormationSlot2);
        public static readonly MemoryLocation ActiveFormationSlot3 =
            new(nameof(ActiveFormationSlot3), ExternalMemoryOffsetMap.Formation.ActiveFormationSlot3);
    }

    internal static class Actor
    {
        public static readonly MemoryLocation ActorArraySize =
            new(nameof(ActorArraySize), ExternalMemoryOffsetMap.ActorArray.ActorArraySize);
        public static readonly MemoryLocation ActorArrayPointer =
            new(nameof(ActorArrayPointer), ExternalMemoryOffsetMap.ActorArray.ActorArrayPointer);
        public static readonly MemoryLocation ActorStride =
            new(nameof(ActorStride), ExternalMemoryOffsetMap.ActorArray.ActorStride);
        public static readonly MemoryLocation ActorId =
            new(nameof(ActorId), ExternalMemoryOffsetMap.ActorArray.ActorArrayPointer, ExternalMemoryOffsetMap.ActorArray.OffsetActorId);
        public static readonly MemoryLocation PositionX =
            new(nameof(PositionX), ExternalMemoryOffsetMap.ActorArray.ActorArrayPointer, ExternalMemoryOffsetMap.ActorArray.OffsetPosX);
        public static readonly MemoryLocation PositionY =
            new(nameof(PositionY), ExternalMemoryOffsetMap.ActorArray.ActorArrayPointer, ExternalMemoryOffsetMap.ActorArray.OffsetPosY);
        public static readonly MemoryLocation PositionZ =
            new(nameof(PositionZ), ExternalMemoryOffsetMap.ActorArray.ActorArrayPointer, ExternalMemoryOffsetMap.ActorArray.OffsetPosZ);
    }

    internal static class Runtime
    {
        public static readonly MemoryLocation FrameCounter =
            new(nameof(FrameCounter), ExternalMemoryOffsetMap.FrameAndRng.FrameCounter);
        public static readonly MemoryLocation RngBase =
            new(nameof(RngBase), ExternalMemoryOffsetMap.FrameAndRng.RngBase);
    }

    internal static class Candidates
    {
        public static readonly MemoryLocation QueuedCommandOffset =
            new(nameof(QueuedCommandOffset), ExternalMemoryOffsetMap.DiscordCandidates.BtlOffsetLikelyQueuedCommandId);
        public static readonly MemoryLocation ArenaContextFlag =
            new(nameof(ArenaContextFlag), ExternalMemoryOffsetMap.DiscordCandidates.BtlOffsetLikelyArenaContextFlag);
        public static readonly MemoryLocation OverdriveLearnPopupFlag =
            new(nameof(OverdriveLearnPopupFlag), ExternalMemoryOffsetMap.DiscordCandidates.BtlOffsetLikelyOverdriveLearnPopupFlag);
        public static readonly MemoryLocation InitializedMonsterCount =
            new(nameof(InitializedMonsterCount), ExternalMemoryOffsetMap.DiscordCandidates.GlobalLikelyInitializedMonsterCount);
        public static readonly MemoryLocation InputRawAddress =
            new(nameof(InputRawAddress), ExternalMemoryOffsetMap.DiscordCandidates.InputRawAddress);
    }
}
