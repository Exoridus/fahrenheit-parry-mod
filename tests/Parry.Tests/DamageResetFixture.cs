namespace Fahrenheit.Mods.Parry;

// The linked reset assigns fields whose other consumers are outside this fixture.
#pragma warning disable CS0414
public sealed partial class ParryModule
{
    private int _dodgeWindowRemainingTicks, _dodgeCooldownRemainingTicks;
    private int _impactCorrelationMatchedCount, _impactCorrelationRejectedCount;
    private string _impactCorrelationLastRejectReason = "None";
    private ulong _impactCorrelationLastSummaryFrame;
    private readonly Dictionary<string, int> _impactCorrelationRejectCounts = [];
    private readonly byte[] _consecutiveParriesPerSlot = new byte[PartyActorCapacity];
    private readonly bool[] _blockMultiHitParriedPerSlot = new bool[PartyActorCapacity];

    public void Reset() => reset_runtime_state("test", clearFeedbackFlashes: true, clearDamageFlags: true);
    private void stop_audio_playback() { }
    private void resolve_streak_at_cue_clear() { }
    private void resolve_overdrive_learning_at_cue_clear(uint mask) { }
    private void trace_native_validation_lifecycle(string stage, string reason) { }
    private void clear_parry_slot_windows()
    {
        WindowOpen = false;
        Array.Clear(_parryArmedAttackerId);
    }
}
