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
    ///     Active hook on MsSetDamage (int __cdecl MsSetDamage(byte param_1, int param_2, int param_3)).
    ///
    ///     Call semantics confirmed from session log analysis (2026-03-22):
    ///       param_1 = attacker battler slot
    ///       param_2 = target party slot (>= 0) for the actual damage-to-party call;
    ///                 -5 for setup (p3=0) and finalization (p3=0x400) calls
    ///       param_3 = 0 for setup/target calls; 0x400 for finalization (triggers MsAfterDamageProcess)
    ///
    ///     The p2=target call applies HP damage synchronously inside orig — damage_hp is only for display.
    ///     On a successful parry we therefore snapshot HP before calling orig and restore it after,
    ///     guaranteeing the character takes no actual damage regardless of how orig applies it.
    ///     The polling path (monitor_damage_resolves) still handles missed-parry detection.
    /// </summary>
    private int h_ms_set_damage(byte param_1, int param_2, int param_3)
    {
        // For the actual damage-to-party call with an active parry window, snapshot HP fields
        // before orig runs so we can restore them afterward. Orig applies HP damage directly
        // (not only through damage_hp), so we must undo at the source.
        bool isPartyTargetCall = param_2 >= 0 && param_2 < PartyActorCapacity;
        bool isActiveParry = isPartyTargetCall
            && _optionEnabled
            && _runtime.ParryWindowActive
            && param_1 == _runtime.CurrentAttackerId;

        Chr* parryTarget = null;
        int snapHp = 0, snapCurrentHp = 0, snapMp = 0, snapCurrentMp = 0, snapCurrentCtb = 0;

        if (isActiveParry)
        {
            Chr* party = _battleAdapter.GetPlayerCharacters();
            Chr* candidate = party != null ? party + param_2 : null;
            if (candidate != null && candidate->stat_exist_flag && !is_target_non_parryable(candidate))
            {
                parryTarget    = candidate;
                snapHp         = parryTarget->ram.hp;
                snapCurrentHp  = parryTarget->ram.current_hp;
                snapMp         = parryTarget->ram.mp;
                snapCurrentMp  = parryTarget->ram.current_mp;
                snapCurrentCtb = parryTarget->ram.current_ctb;
            }
        }

        int result = _hMsSetDamage.orig_fptr.Invoke(param_1, param_2, param_3);

        if (parryTarget != null)
        {
            // Restore HP to pre-call values to undo direct damage applied by orig.
            // negate_damage_on_impact inside resolve_successful_parry will zero damage_hp/mp/ctb.
            if (_optionNegateDamage)
            {
                parryTarget->ram.hp          = snapHp;
                parryTarget->ram.current_hp  = snapCurrentHp;
                parryTarget->ram.mp          = snapMp;
                parryTarget->ram.current_mp  = snapCurrentMp;
                parryTarget->ram.current_ctb = snapCurrentCtb;
            }
            on_impact_detected(param_2, parryTarget, "ms_set_damage");
        }

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

    /// <summary>
    ///     Pass-through diagnostic hook on MsCalcDamage using the community-confirmed
    ///     11-param signature (March 2026 Discord findings). Fires before MsSetDamage
    ///     for each attack. Logs user_id, target_id, command_id, p11 (hit count), and return value.
    ///     Used for attack identification and coverage gap diagnosis (e.g., 0x80cd60 path).
    /// </summary>
    private int h_ms_calc_damage(
        int user_id, nint user_chr, int target_id, nint target_chr,
        nint command, int command_id,
        nint p7, nint p8, nint p9, nint p10, int p11)
    {
        int result = _hMsCalcDamage.orig_fptr.Invoke(
            user_id, user_chr, target_id, target_chr,
            command, command_id,
            p7, p8, p9, p10, p11);

        if (!_optionDebugOverlay && !_optionLogging)
            return result;

        ulong frame = _debugFrameIndex;
        bool sameAsLast =
            _msCalcDamageLogLastFrame != 0
            && _msCalcDamageLogLastUserId == user_id
            && _msCalcDamageLogLastTargetId == target_id
            && _msCalcDamageLogLastCommandId == command_id
            && _msCalcDamageLogLastHitCount == p11
            && _msCalcDamageLogLastResult == result;

        if (sameAsLast && frame - _msCalcDamageLogLastFrame < 30)
            return result;

        _msCalcDamageLogLastFrame = frame;
        _msCalcDamageLogLastUserId = user_id;
        _msCalcDamageLogLastTargetId = target_id;
        _msCalcDamageLogLastCommandId = command_id;
        _msCalcDamageLogLastHitCount = p11;
        _msCalcDamageLogLastResult = result;

        log_debug($"[MsCalcDamage] f={frame} user={user_id} target={target_id} cmd={command_id} hits={p11} ret={result} cmd_ptr=0x{command:X}");

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
