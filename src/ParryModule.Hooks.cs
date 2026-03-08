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
