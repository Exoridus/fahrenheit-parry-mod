namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule
{
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

    /// <summary>
    ///     Experimental Phase 0 spike: pass-through hook on MsSetDamage using the
    ///     confirmed int __cdecl MsSetDamage(byte param_1, int param_2, int param_3) signature.
    ///     Logs parameter values and return value for signature validation.
    ///     Does NOT modify any damage values or affect gameplay behavior.
    /// </summary>
    private int h_ms_set_damage(byte param_1, int param_2, int param_3)
    {
        int result = _hMsSetDamage.orig_fptr.Invoke(param_1, param_2, param_3);

        if (!_optionDebugOverlay && !_optionLogging)
            return result;

        ulong frame = _debugFrameIndex;
        bool parryWindowActive = _runtime.ParryWindowActive;
        byte currentAttackerId = _runtime.CurrentAttackerId;
        bool awaitingTurnEnd = _runtime.AwaitingTurnEnd;
        bool sameAsLast =
            _msSetDamageLogLastFrame != 0
            && _msSetDamageLogLastP1 == param_1
            && _msSetDamageLogLastP2 == param_2
            && _msSetDamageLogLastP3 == param_3
            && _msSetDamageLogLastResult == result
            && _msSetDamageLogLastParryWindowActive == parryWindowActive
            && _msSetDamageLogLastAttackerId == currentAttackerId
            && _msSetDamageLogLastAwaitingTurnEnd == awaitingTurnEnd;

        if (sameAsLast && frame - _msSetDamageLogLastFrame < 30)
            return result;

        _msSetDamageLogLastFrame = frame;
        _msSetDamageLogLastP1 = param_1;
        _msSetDamageLogLastP2 = param_2;
        _msSetDamageLogLastP3 = param_3;
        _msSetDamageLogLastResult = result;
        _msSetDamageLogLastParryWindowActive = parryWindowActive;
        _msSetDamageLogLastAttackerId = currentAttackerId;
        _msSetDamageLogLastAwaitingTurnEnd = awaitingTurnEnd;

        log_debug($"[MsSetDamage] f={frame} p1={param_1} p2={param_2} p3={param_3} ret={result} parry={parryWindowActive} atk={currentAttackerId} await={awaitingTurnEnd}");

        return result;
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
