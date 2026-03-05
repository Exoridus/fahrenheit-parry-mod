namespace Fahrenheit.Mods.Parry;

public static class TurnTimelineStateMachine {
    public static bool CanTransitionLifecycle(TurnTimelineLifecycleState from, TurnTimelineLifecycleState to) {
        if (from == to) return true;
        if (from == TurnTimelineLifecycleState.Completed) return false;

        return (from, to) switch {
            (TurnTimelineLifecycleState.Pending, TurnTimelineLifecycleState.Active) => true,
            (TurnTimelineLifecycleState.Pending, TurnTimelineLifecycleState.Completed) => true,
            (TurnTimelineLifecycleState.Active, TurnTimelineLifecycleState.Pending) => true,
            (TurnTimelineLifecycleState.Active, TurnTimelineLifecycleState.Completed) => true,
            _ => false
        };
    }

    public static bool CanTransitionParry(
        TurnTimelineParryability parryability,
        TurnTimelineParryState from,
        TurnTimelineParryState to) {
        if (from == to) return true;

        if (parryability == TurnTimelineParryability.NonParryable) {
            return to == TurnTimelineParryState.None;
        }

        if (from == TurnTimelineParryState.Parried || from == TurnTimelineParryState.Missed) {
            return false;
        }

        return (from, to) switch {
            (TurnTimelineParryState.Pending, TurnTimelineParryState.Waiting) => true,
            (TurnTimelineParryState.Pending, TurnTimelineParryState.Open) => true,
            (TurnTimelineParryState.Pending, TurnTimelineParryState.Missed) => true,
            (TurnTimelineParryState.Pending, TurnTimelineParryState.Parried) => true,
            (TurnTimelineParryState.Waiting, TurnTimelineParryState.Open) => true,
            (TurnTimelineParryState.Waiting, TurnTimelineParryState.Missed) => true,
            (TurnTimelineParryState.Waiting, TurnTimelineParryState.Parried) => true,
            (TurnTimelineParryState.Open, TurnTimelineParryState.Waiting) => true,
            (TurnTimelineParryState.Open, TurnTimelineParryState.Missed) => true,
            (TurnTimelineParryState.Open, TurnTimelineParryState.Parried) => true,
            _ => false
        };
    }
}
