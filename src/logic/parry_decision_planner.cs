namespace Fahrenheit.Mods.Parry.Logic;

public enum ParryStartActionKind {
    None,
    IgnoreCueNoPartyTargets,
    StartLead,
    OpenWindow
}

public readonly record struct ParryStartAction(
    ParryStartActionKind Kind,
    byte AttackerId,
    byte CueIndex,
    uint PartyMask,
    int LeadFrames,
    int InitialWindowFrames,
    bool IsMagic);

public static class ParryDecisionPlanner {
    public static int ClampCueCount(int observedCueCount, int maxSafeCueCount) {
        if (maxSafeCueCount <= 0) return 0;
        if (observedCueCount <= 0) return 0;
        return Math.Min(observedCueCount, maxSafeCueCount);
    }

    public static ParryStartAction PlanStartAction(
        bool hasCue,
        byte attackerId,
        byte cueIndex,
        uint partyMask,
        bool isMagic,
        bool parryWindowActive,
        bool leadPending,
        bool awaitingTurnEnd,
        int debounceFrames,
        int leadPhysicalFrames,
        int leadMagicFrames,
        int initialWindowFrames) {
        if (!hasCue) return new ParryStartAction(ParryStartActionKind.None, 0, 0, 0, 0, 0, false);

        if (partyMask == 0) {
            return new ParryStartAction(ParryStartActionKind.IgnoreCueNoPartyTargets, attackerId, cueIndex, partyMask, 0, 0, isMagic);
        }

        if (parryWindowActive || leadPending || awaitingTurnEnd || debounceFrames > 0) {
            return new ParryStartAction(ParryStartActionKind.None, attackerId, cueIndex, partyMask, 0, 0, isMagic);
        }

        int leadFrames = isMagic ? leadMagicFrames : leadPhysicalFrames;
        if (leadFrames > 0) {
            return new ParryStartAction(ParryStartActionKind.StartLead, attackerId, cueIndex, partyMask, leadFrames, initialWindowFrames, isMagic);
        }

        return new ParryStartAction(ParryStartActionKind.OpenWindow, attackerId, cueIndex, partyMask, 0, initialWindowFrames, isMagic);
    }

    public static bool ShouldCloseOnDamageResolve(
        bool parryWindowActive,
        bool resolveMode,
        uint currentPartyMask,
        int slotIndex,
        uint fallbackPartyMask) {
        if (!parryWindowActive || !resolveMode) return false;
        if (slotIndex < 0 || slotIndex >= 32) return false;

        uint bit = 1u << slotIndex;
        uint mask = currentPartyMask == 0 ? fallbackPartyMask : currentPartyMask;
        return mask == 0 || (mask & bit) != 0;
    }
}
