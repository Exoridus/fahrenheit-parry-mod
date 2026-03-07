namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule
{
    private void h_ms_set_damage()
    {
        bool preApplied = try_apply_phase_b_negation_for_pending_damage("setdamage_pre", resolveOnFirstHit: true);
        _hMsSetDamage.orig_fptr.Invoke();
        bool postApplied = try_apply_phase_b_negation_for_pending_damage("setdamage_post", resolveOnFirstHit: true);

        if (preApplied || postApplied)
        {
            log_debug($"Phase B negation applied around MsSetDamage ({(preApplied ? "pre" : string.Empty)}{(preApplied && postApplied ? "+" : string.Empty)}{(postApplied ? "post" : string.Empty)}).");
        }
    }

    private void h_ms_sub_hp()
    {
        // Best-effort Phase B: intercept around HP subtraction and clear pending damage for
        // currently tracked parry targets while the active input window is open.
        bool preApplied = try_apply_phase_b_negation_for_pending_damage("pre", resolveOnFirstHit: true);
        _hMsSubHp.orig_fptr.Invoke();
        bool postApplied = try_apply_phase_b_negation_for_pending_damage("post", resolveOnFirstHit: true);

        if (preApplied || postApplied)
        {
            log_debug($"Phase B negation applied around MsSubHP ({(preApplied ? "pre" : string.Empty)}{(preApplied && postApplied ? "+" : string.Empty)}{(postApplied ? "post" : string.Empty)}).");
        }
    }

    private void h_ms_btl_read_set_scene()
    {
        _hMsBtlReadSetScene.orig_fptr.Invoke();

        // Protect against duplicate scene refresh notifications within the same frame.
        if (_lastBattleSceneRefreshFrame == _debugFrameIndex)
        {
            return;
        }

        _lastBattleSceneRefreshFrame = _debugFrameIndex;
        _battleSceneRevision++;
        _runtime.AttackCueClampWarned = false;
        _runtime.CurrentCueSignature = 0;
        _runtime.LastDispatchConsumedFrame = 0;
        _runtime.LastDispatchConsumedAttackerId = 0;
        _runtime.LastDispatchConsumedQueueIndex = 0xFF;
        _runtime.LastCorrelationSkipFrame = 0;
        _impactCorrelationMatchedCount = 0;
        _impactCorrelationRejectedCount = 0;
        _impactCorrelationLastRejectReason = "None";
        _impactCorrelationLastSummaryFrame = 0;
        _impactCorrelationRejectCounts.Clear();
        _overlayProjectionMode = OverlayProjectionMode.Unknown;
        _overlayProjectionLastSuccessFrame = 0;
        Array.Clear(_damageEventActive);

        if (_runtime.AwaitingTurnEnd || _runtime.ParryWindowActive)
        {
            clear_awaiting_turn_end($"Battle scene refreshed (rev {_battleSceneRevision}); cleared stale parry context.");
            end_parry_window("battle_scene_refresh");
        }

        _debugCueSnapshots.Clear();
        _debugCueScratch.Clear();
        _debugHookCueScratch.Clear();

        log_debug($"Battle scene data reloaded (rev {_battleSceneRevision}).");
    }

    private void h_ms_exe_input_cue()
    {
        bool hadBefore = try_get_head_cue_snapshot(_debugHookCueScratch, out DebugCueSnapshot before);
        ulong frame = _debugFrameIndex;
        DateTime now = current_gameplay_timestamp();

        _hMsExeInputCue.orig_fptr.Invoke();

        bool hasAfter = try_get_head_cue_snapshot(_debugHookCueScratch, out DebugCueSnapshot after);
        bool changed = !hadBefore || !hasAfter || !before.EqualsSemantic(after);

        if (hadBefore)
        {
            _turnRuntimeEvents.EmitDispatchStarted(
                attackerId: before.AttackerId,
                queueIndex: before.QueueIndex,
                timestampLocal: now,
                frameIndex: frame,
                parryWindowActive: _runtime.ParryWindowActive);
        }

        if (hadBefore && changed)
        {
            _runtime.LastDispatchConsumedFrame = frame;
            _runtime.LastDispatchConsumedAttackerId = before.AttackerId;
            _runtime.LastDispatchConsumedQueueIndex = before.QueueIndex;

            _turnRuntimeEvents.EmitDispatchConsumed(
                attackerId: before.AttackerId,
                queueIndex: before.QueueIndex,
                timestampLocal: now,
                frameIndex: frame,
                reason: "native dispatch");
        }

        if (hasAfter && changed)
        {
            _turnRuntimeEvents.EmitDispatchStarted(
                attackerId: after.AttackerId,
                queueIndex: after.QueueIndex,
                timestampLocal: now,
                frameIndex: frame,
                parryWindowActive: _runtime.ParryWindowActive);
        }
    }

    private bool try_get_head_cue_snapshot(List<DebugCueSnapshot> scratch, out DebugCueSnapshot head)
    {
        scratch.Clear();
        collect_live_cues(scratch, out _);
        if (scratch.Count == 0)
        {
            head = default;
            return false;
        }

        head = scratch[0];
        return true;
    }
}
