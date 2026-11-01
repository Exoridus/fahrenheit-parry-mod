namespace Fahrenheit.Mods.Parry;

public sealed partial class ParryModule
{
    private void end_enemy_action(string reason)
    {
        resolve_streak_at_cue_clear();
        resolve_overdrive_learning_at_cue_clear(_runtime.LastParriedTargetMask);

        Array.Clear(_parryFeedbackPending);
        _runtime.LastParriedTargetMask = 0;
        _parryResolvedAtImpactMask = 0;
        _dodgeResolvedAtImpactMask = 0;
        Array.Clear(_preHitHpSnapshot);

        log_debug($"Enemy action ended ({reason}) — per-action parry state cleared.");
    }

    private void reset_runtime_state(string timingReason, bool clearFeedbackFlashes, bool clearDamageFlags)
    {
        stop_audio_playback();
        if (_runtime.ParryWindowActive || _runtime.CurrentPartyTargetMask != 0)
            trace_native_validation_lifecycle("reset", timingReason);

        clear_parry_slot_windows();
        Array.Clear(_parryFeedbackPending);
        Array.Clear(_preHitHpSnapshot);
        _internalInterceptedMask = 0;
        Array.Clear(_internalInterceptedAttackerId);
        _latePreOpenP5ZeroCommitMask = 0;
        Array.Clear(_latePreOpenP5ZeroCommitAttackerId);
        _parryResolvedAtImpactMask = 0;
        _runtime.TurnImpactMissedSeen = false;
        _runtime.TurnImpactMissedAttackerId = 0;
        _runtime.InputState = ParryInputState.Ready;
        _runtime.ParryWindowActive = false;
        _runtime.CurrentAttackerId = 0;
        _runtime.CurrentCueIndex = 0;
        _runtime.CurrentPartyTargetMask = 0;
        _runtime.CurrentCueSignature = 0;
        _runtime.ParryWindowRemainingTicks = 0;
        _runtime.WhiffLockoutRemainingTicks = 0;
        _runtime.WhiffLockoutTotalTicks = 0;
        _runtime.AwaitingTurnEnd = false;
        _runtime.ParryWindowElapsedTicks = 0;
        _runtime.ParryWindowSucceeded = false;
        _runtime.SuccessIndicatorActive = false;
        _runtime.LastDispatchConsumedFrame = 0;
        _runtime.LastDispatchConsumedAttackerId = 0;
        _runtime.LastDispatchConsumedQueueIndex = 0xFF;
        _runtime.LastCorrelationSkipFrame = 0;
        _runtime.CueFirstSeenFrame = 0;
        _runtime.WindowOpenFrame = 0;
        _runtime.WindowOpenTimestampSeconds = 0f;
        _runtime.WindowDurationSecondsAtOpen = 0f;
        _dodgeWindowActive = false;
        _dodgeWindowRemainingTicks = 0;
        _dodgeCooldownRemainingTicks = 0;
        _dodgeArmedAttackerId = 0;
        _dodgeArmedCueFrame = 0;
        _dodgeArmedTargetMask = 0;
        _dodgeResolvedAtImpactMask = 0;
        _impactCorrelationMatchedCount = 0;
        _impactCorrelationRejectedCount = 0;
        _impactCorrelationLastRejectReason = "None";
        _impactCorrelationLastSummaryFrame = 0;
        _impactCorrelationRejectCounts.Clear();
        // Streak counter is per-slot and persists across turns; only the full
        // runtime reset (battle end / mod toggled off) clears it.
        Array.Clear(_consecutiveParriesPerSlot);
        Array.Clear(_blockMultiHitParriedPerSlot);

        if (clearFeedbackFlashes)
        {
            _runtime.ParriedTextRemainingSeconds = 0f;
            _runtime.ParryMissedTextRemainingSeconds = 0f;
            _runtime.StatusBlockTextRemainingSeconds = 0f;
            _runtime.StatusBlockLabel = string.Empty;
            _runtime.LastParriedTargetMask = 0;
        }

        if (clearDamageFlags)
        {
            Array.Clear(_damageEventActive);
        }
    }
}
