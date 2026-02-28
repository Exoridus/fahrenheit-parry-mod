namespace Fahrenheit.Mods.Parry.Logic;

public readonly record struct SimulationFrame(
    bool HasCue,
    byte AttackerId,
    byte CueIndex,
    uint PartyMask,
    bool IsMagic,
    bool InputPressed,
    bool CuePersistedForLead);

public readonly record struct SimulationState(
    bool WindowActive,
    bool AwaitingTurnEnd,
    bool LeadPending,
    int LeadFramesRemaining,
    int WindowFramesRemaining,
    byte CurrentAttackerId,
    byte CurrentCueIndex,
    uint CurrentPartyMask,
    int DebounceFrames);

public readonly record struct SimulationTrace(int Frame, string Event, SimulationState State);

public static class ParrySimulationHarness {
    public static IReadOnlyList<SimulationTrace> Run(
        IReadOnlyList<SimulationFrame> frames,
        int leadPhysicalFrames,
        int leadMagicFrames,
        int initialWindowFrames,
        int debounceFramesOnSuccess = 1) {
        var traces = new List<SimulationTrace>(frames.Count * 2);
        var state = new SimulationState(
            WindowActive: false,
            AwaitingTurnEnd: false,
            LeadPending: false,
            LeadFramesRemaining: 0,
            WindowFramesRemaining: 0,
            CurrentAttackerId: 0,
            CurrentCueIndex: 0,
            CurrentPartyMask: 0,
            DebounceFrames: 0);

        for (int i = 0; i < frames.Count; i++) {
            var frame = frames[i];
            bool hasEnemyCue = false;

            if (state.DebounceFrames > 0) {
                state = state with { DebounceFrames = state.DebounceFrames - 1 };
            }

            // 1) Mirror monitor_attack_cues() ordering from runtime.
            if (frame.HasCue) {
                hasEnemyCue = true;

                if (state.AwaitingTurnEnd && !state.WindowActive && !state.LeadPending) {
                    bool transitionedToNewCue =
                        frame.AttackerId != state.CurrentAttackerId
                        || frame.CueIndex != state.CurrentCueIndex
                        || frame.PartyMask != state.CurrentPartyMask;
                    if (transitionedToNewCue) {
                        state = state with { AwaitingTurnEnd = false };
                        traces.Add(new SimulationTrace(i, "awaiting_turn_cleared_new_cue", state));
                    }
                }

                var action = ParryDecisionPlanner.PlanStartAction(
                    hasCue: true,
                    attackerId: frame.AttackerId,
                    cueIndex: frame.CueIndex,
                    partyMask: frame.PartyMask,
                    isMagic: frame.IsMagic,
                    parryWindowActive: state.WindowActive,
                    leadPending: state.LeadPending,
                    awaitingTurnEnd: state.AwaitingTurnEnd,
                    debounceFrames: state.DebounceFrames,
                    leadPhysicalFrames: leadPhysicalFrames,
                    leadMagicFrames: leadMagicFrames,
                    initialWindowFrames: initialWindowFrames);

                if (action.Kind == ParryStartActionKind.IgnoreCueNoPartyTargets) {
                    traces.Add(new SimulationTrace(i, "cue_ignored_no_party_targets", state));
                }
                else if (action.Kind == ParryStartActionKind.StartLead) {
                    state = state with {
                        LeadPending = true,
                        LeadFramesRemaining = action.LeadFrames,
                        AwaitingTurnEnd = true,
                        CurrentAttackerId = action.AttackerId,
                        CurrentCueIndex = action.CueIndex,
                        CurrentPartyMask = action.PartyMask
                    };
                    traces.Add(new SimulationTrace(i, "lead_started", state));
                }
                else if (action.Kind == ParryStartActionKind.OpenWindow) {
                    state = state with {
                        WindowActive = true,
                        WindowFramesRemaining = action.InitialWindowFrames,
                        AwaitingTurnEnd = true,
                        CurrentAttackerId = action.AttackerId,
                        CurrentCueIndex = action.CueIndex,
                        CurrentPartyMask = action.PartyMask
                    };
                    traces.Add(new SimulationTrace(i, "window_opened", state));
                }
            }
            else {
                if (state.WindowActive) {
                    state = state with { WindowActive = false, WindowFramesRemaining = 0 };
                    traces.Add(new SimulationTrace(i, "window_closed_cue_cleared", state));
                }

                if (state.LeadPending) {
                    state = state with {
                        LeadPending = false,
                        LeadFramesRemaining = 0
                    };
                    traces.Add(new SimulationTrace(i, "lead_canceled_cue_disappeared", state));
                }

                if (state.AwaitingTurnEnd) {
                    state = state with { AwaitingTurnEnd = false };
                    traces.Add(new SimulationTrace(i, "awaiting_turn_cleared", state));
                }
            }

            // 2) Mirror process_lead_pending() ordering from runtime.
            if (state.LeadPending) {
                if (!frame.CuePersistedForLead || !frame.HasCue || frame.AttackerId != state.CurrentAttackerId) {
                    state = state with {
                        LeadPending = false,
                        LeadFramesRemaining = 0,
                        AwaitingTurnEnd = false
                    };
                    traces.Add(new SimulationTrace(i, "lead_canceled_attacker_lost", state));
                }
                else {
                    int nextLead = state.LeadFramesRemaining - 1;
                    if (nextLead > 0) {
                        state = state with { LeadFramesRemaining = nextLead };
                    }
                    else if (frame.PartyMask == 0) {
                        state = state with {
                            LeadPending = false,
                            LeadFramesRemaining = 0,
                            AwaitingTurnEnd = false
                        };
                        traces.Add(new SimulationTrace(i, "lead_canceled_no_targets", state));
                    }
                    else {
                        state = state with {
                            LeadPending = false,
                            LeadFramesRemaining = 0,
                            WindowActive = true,
                            WindowFramesRemaining = initialWindowFrames,
                            CurrentPartyMask = frame.PartyMask
                        };
                        traces.Add(new SimulationTrace(i, "window_opened_after_lead", state));
                    }
                }
            }

            // 3) Mirror runtime window decay + success/timeout checks.
            if (state.WindowActive) {
                int nextWindow = state.WindowFramesRemaining - 1;
                state = state with { WindowFramesRemaining = nextWindow };

                if (nextWindow <= 0) {
                    state = state with {
                        WindowActive = false,
                        WindowFramesRemaining = 0
                    };
                    traces.Add(new SimulationTrace(i, "parry_timeout", state));
                }
                else if (frame.InputPressed) {
                    state = state with {
                        WindowActive = false,
                        WindowFramesRemaining = 0,
                        DebounceFrames = Math.Max(debounceFramesOnSuccess, 1)
                    };
                    traces.Add(new SimulationTrace(i, "parry_success", state));
                }
            }

            // 4) Mirror runtime post-window awaiting clear check.
            if (!state.WindowActive && !state.LeadPending && state.AwaitingTurnEnd && !hasEnemyCue) {
                state = state with { AwaitingTurnEnd = false };
                traces.Add(new SimulationTrace(i, "awaiting_turn_cleared_postupdate", state));
            }
        }

        return traces;
    }
}
