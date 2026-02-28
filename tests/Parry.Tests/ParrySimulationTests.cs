using Fahrenheit.Mods.Parry.Logic;
using Xunit;

namespace Fahrenheit.Mods.Parry.Tests;

public sealed class ParrySimulationTests {
    [Fact]
    public void ClampCueCount_ShouldCapUnsafeValues() {
        Assert.Equal(0, ParryDecisionPlanner.ClampCueCount(-3, 64));
        Assert.Equal(12, ParryDecisionPlanner.ClampCueCount(12, 64));
        Assert.Equal(64, ParryDecisionPlanner.ClampCueCount(200, 64));
    }

    [Fact]
    public void PlanStartAction_ShouldIgnoreCueWithoutPartyTargets() {
        var action = ParryDecisionPlanner.PlanStartAction(
            hasCue: true,
            attackerId: 14,
            cueIndex: 0,
            partyMask: 0,
            isMagic: false,
            parryWindowActive: false,
            leadPending: false,
            awaitingTurnEnd: false,
            debounceFrames: 0,
            leadPhysicalFrames: 1,
            leadMagicFrames: 2,
            initialWindowFrames: 10);

        Assert.Equal(ParryStartActionKind.IgnoreCueNoPartyTargets, action.Kind);
    }

    [Fact]
    public void ShouldCloseOnDamageResolve_ShouldRespectTargetMask() {
        Assert.True(ParryDecisionPlanner.ShouldCloseOnDamageResolve(
            parryWindowActive: true,
            resolveMode: true,
            currentPartyMask: 0b0100,
            slotIndex: 2,
            fallbackPartyMask: 0b1111));

        Assert.False(ParryDecisionPlanner.ShouldCloseOnDamageResolve(
            parryWindowActive: true,
            resolveMode: true,
            currentPartyMask: 0b0100,
            slotIndex: 1,
            fallbackPartyMask: 0b1111));
    }

    [Fact]
    public void Harness_ShouldOpenWindowInSameFrameWhenLeadReachesZero() {
        var frames = new[] {
            new SimulationFrame(HasCue: true, AttackerId: 10, CueIndex: 0, PartyMask: 0x1, IsMagic: false, InputPressed: false, CuePersistedForLead: true),
            new SimulationFrame(HasCue: true, AttackerId: 10, CueIndex: 0, PartyMask: 0x1, IsMagic: false, InputPressed: true, CuePersistedForLead: true),
            new SimulationFrame(HasCue: false, AttackerId: 0, CueIndex: 0, PartyMask: 0, IsMagic: false, InputPressed: false, CuePersistedForLead: false)
        };

        var trace = ParrySimulationHarness.Run(
            frames,
            leadPhysicalFrames: 1,
            leadMagicFrames: 2,
            initialWindowFrames: 3,
            debounceFramesOnSuccess: 2);

        Assert.Contains(trace, x => x.Frame == 0 && x.Event == "lead_started");
        Assert.Contains(trace, x => x.Frame == 0 && x.Event == "window_opened_after_lead");
        Assert.Contains(trace, x => x.Frame == 1 && x.Event == "parry_success");
    }

    [Fact]
    public void Harness_ShouldCancelLeadWhenCueDisappears() {
        var frames = new[] {
            new SimulationFrame(HasCue: true, AttackerId: 11, CueIndex: 0, PartyMask: 0x1, IsMagic: true, InputPressed: false, CuePersistedForLead: true),
            new SimulationFrame(HasCue: false, AttackerId: 0, CueIndex: 0, PartyMask: 0, IsMagic: true, InputPressed: false, CuePersistedForLead: false)
        };

        var trace = ParrySimulationHarness.Run(
            frames,
            leadPhysicalFrames: 1,
            leadMagicFrames: 2,
            initialWindowFrames: 3);

        Assert.Contains(trace, x => x.Event == "lead_started");
        Assert.Contains(trace, x => x.Event == "lead_canceled_cue_disappeared" || x.Event == "lead_canceled_attacker_lost");
    }

    [Fact]
    public void Harness_ShouldTimeoutWindowWithoutInput() {
        var frames = new[] {
            new SimulationFrame(HasCue: true, AttackerId: 10, CueIndex: 0, PartyMask: 0x1, IsMagic: false, InputPressed: false, CuePersistedForLead: true),
            new SimulationFrame(HasCue: true, AttackerId: 10, CueIndex: 0, PartyMask: 0x1, IsMagic: false, InputPressed: false, CuePersistedForLead: true),
            new SimulationFrame(HasCue: false, AttackerId: 0, CueIndex: 0, PartyMask: 0, IsMagic: false, InputPressed: false, CuePersistedForLead: false)
        };

        var trace = ParrySimulationHarness.Run(
            frames,
            leadPhysicalFrames: 0,
            leadMagicFrames: 0,
            initialWindowFrames: 2);

        Assert.Contains(trace, x => x.Event == "window_opened");
        Assert.Contains(trace, x => x.Event == "parry_timeout");
    }

    [Fact]
    public void Harness_ShouldNotReopenWhileAwaitingTurnEnd() {
        var frames = new[] {
            new SimulationFrame(HasCue: true, AttackerId: 10, CueIndex: 0, PartyMask: 0x1, IsMagic: false, InputPressed: false, CuePersistedForLead: true),
            new SimulationFrame(HasCue: true, AttackerId: 10, CueIndex: 0, PartyMask: 0x1, IsMagic: false, InputPressed: true, CuePersistedForLead: true),
            new SimulationFrame(HasCue: true, AttackerId: 10, CueIndex: 0, PartyMask: 0x1, IsMagic: false, InputPressed: false, CuePersistedForLead: true),
            new SimulationFrame(HasCue: true, AttackerId: 10, CueIndex: 0, PartyMask: 0x1, IsMagic: false, InputPressed: false, CuePersistedForLead: true)
        };

        var trace = ParrySimulationHarness.Run(
            frames,
            leadPhysicalFrames: 0,
            leadMagicFrames: 0,
            initialWindowFrames: 3,
            debounceFramesOnSuccess: 2);

        Assert.Contains(trace, x => x.Frame == 1 && x.Event == "parry_success");
        Assert.DoesNotContain(trace, x => x.Frame == 2 && x.Event == "window_opened");
        Assert.DoesNotContain(trace, x => x.Frame == 3 && x.Event == "window_opened");
    }

    [Fact]
    public void Harness_ShouldReopenAfterTurnEndAndDebounceExpires() {
        var frames = new[] {
            new SimulationFrame(HasCue: true, AttackerId: 10, CueIndex: 0, PartyMask: 0x1, IsMagic: false, InputPressed: false, CuePersistedForLead: true),
            new SimulationFrame(HasCue: true, AttackerId: 10, CueIndex: 0, PartyMask: 0x1, IsMagic: false, InputPressed: true, CuePersistedForLead: true),
            new SimulationFrame(HasCue: false, AttackerId: 0, CueIndex: 0, PartyMask: 0, IsMagic: false, InputPressed: false, CuePersistedForLead: false),
            new SimulationFrame(HasCue: true, AttackerId: 12, CueIndex: 0, PartyMask: 0x1, IsMagic: false, InputPressed: false, CuePersistedForLead: true),
            new SimulationFrame(HasCue: true, AttackerId: 12, CueIndex: 0, PartyMask: 0x1, IsMagic: false, InputPressed: false, CuePersistedForLead: true)
        };

        var trace = ParrySimulationHarness.Run(
            frames,
            leadPhysicalFrames: 0,
            leadMagicFrames: 0,
            initialWindowFrames: 3,
            debounceFramesOnSuccess: 2);

        Assert.Contains(trace, x => x.Frame == 1 && x.Event == "parry_success");
        Assert.Contains(trace, x => x.Frame == 2 && (x.Event == "awaiting_turn_cleared" || x.Event == "awaiting_turn_cleared_postupdate"));
        Assert.Contains(trace, x => x.Frame == 3 && x.Event == "window_opened");
    }

    [Fact]
    public void Harness_ShouldOpenForNextEnemyCueWithoutNoCueGap() {
        var frames = new[] {
            new SimulationFrame(HasCue: true, AttackerId: 10, CueIndex: 0, PartyMask: 0x1, IsMagic: false, InputPressed: false, CuePersistedForLead: true),
            new SimulationFrame(HasCue: true, AttackerId: 10, CueIndex: 0, PartyMask: 0x1, IsMagic: false, InputPressed: true, CuePersistedForLead: true),
            new SimulationFrame(HasCue: true, AttackerId: 11, CueIndex: 0, PartyMask: 0x2, IsMagic: false, InputPressed: false, CuePersistedForLead: true)
        };

        var trace = ParrySimulationHarness.Run(
            frames,
            leadPhysicalFrames: 0,
            leadMagicFrames: 0,
            initialWindowFrames: 3,
            debounceFramesOnSuccess: 1);

        Assert.Contains(trace, x => x.Frame == 1 && x.Event == "parry_success");
        Assert.Contains(trace, x => x.Frame == 2 && x.Event == "awaiting_turn_cleared_new_cue");
        Assert.Contains(trace, x => x.Frame == 2 && x.Event == "window_opened");
    }
}
