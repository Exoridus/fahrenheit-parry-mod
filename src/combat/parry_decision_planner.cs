namespace Fahrenheit.Mods.Parry;

public static class ParryDecisionPlanner {
    public static int ClampCueCount(int observedCueCount, int maxSafeCueCount) {
        if (maxSafeCueCount <= 0) return 0;
        if (observedCueCount <= 0) return 0;
        return Math.Min(observedCueCount, maxSafeCueCount);
    }
}
