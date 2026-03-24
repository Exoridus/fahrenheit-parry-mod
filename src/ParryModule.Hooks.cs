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
    ///       param_2 = target party slot (>= 0) for the per-target staging call;
    ///                 -5 for setup (p3=0) and finalization (p3=0x400) calls
    ///       param_3 = 0 for setup/per-target calls; 0x400 for finalization (triggers MsAfterDamageProcess)
    ///
    ///     The p2=target call stages damage_hp/mp/ctb for display but does NOT reduce ram.current_hp.
    ///     The p3=0x400 finalization call is where MsAfterDamageProcess reads from its internal buffer
    ///     and applies the actual HP reduction.
    ///
    ///     Damage negation strategy: h_ms_calc_damage returns 0 (skipping the native call) when
    ///     a party slot has an active parry expiry timestamp. This prevents damage from entering
    ///     the native pipeline for ALL attack paths.
    ///
    ///     Anfunkeln-style attacks (no p2=target call): the hook never fires for a per-target resolve.
    ///     These are detected at finalization (p3=0x400) when the parry window is still open but
    ///     LastParriedTargetMask is zero, and the parry feedback is resolved there.
    /// </summary>
    private int h_ms_set_damage(byte param_1, int param_2, int param_3)
    {
        // p2=target: snapshot HP when a parry window is active so we can restore on impact.
        // The hook is the authoritative impact detection path — it fires at native damage time,
        // before the poll path in on_pre_update. When the window is active and the attacker
        // matches, resolve the parry directly from the hook to ensure feedback (text + sound)
        // fires at impact time rather than being deferred to the poll path.
        bool isPartyTargetCall = param_2 >= 0 && param_2 < PartyActorCapacity;
        bool isActiveParry = isPartyTargetCall
            && _optionEnabled
            && _runtime.ParryWindowActive
            && param_1 == _runtime.CurrentAttackerId
            && (_runtime.CurrentPartyTargetMask & (1u << param_2)) != 0;

        Chr* parryTarget = null;

        if (isActiveParry)
        {
            Chr* party = _battleAdapter.GetPlayerCharacters();
            Chr* candidate = party != null ? party + param_2 : null;
            if (candidate != null && candidate->stat_exist_flag && !is_target_non_parryable(candidate))
            {
                parryTarget = candidate;
            }
        }

        // p3=0x400 finalization: if the parry window is still open, close it and handle
        // Anfunkeln-style attacks (no p2=target calls) that need feedback resolved here.
        // h_ms_calc_damage return-0 has already prevented the HP reduction — no
        // snapshot/restore needed.
        bool isFinalizationWithParry = param_3 == 0x400
            && _optionEnabled
            && _runtime.ParryWindowActive
            && param_1 == _runtime.CurrentAttackerId;

        int result = _hMsSetDamage.orig_fptr.Invoke(param_1, param_2, param_3);

        // p2=target with active parry: resolve the parry directly from the hook.
        // This bypasses the poll-based on_impact_detected path (which may fire a frame late
        // or reject via correlation if the cue was consumed before MsSetDamage ran).
        // The hook is authoritative: the game is literally processing this damage right now.
        if (parryTarget != null && _runtime.ParryWindowActive)
        {
            string attackerLabel = format_actor_slot(param_1);
            string targetLabel = format_actor_slot((byte)param_2);
            log_debug($"Hook impact: {attackerLabel} -> {targetLabel}, resolving parry at impact time.");
            resolve_successful_parry(param_2, parryTarget, "ms_set_damage", closeWindow: false);

            // Mark the slot as handled so the poll path (monitor_damage_resolves) does not
            // re-detect this impact and fire a spurious "missed" or double-resolve.
            _damageEventActive[param_2] = true;
        }

        if (isFinalizationWithParry)
        {
            if (_runtime.LastParriedTargetMask != 0)
            {
                // Regular attack: each target was already resolved by the h_ms_calc_damage
                // return-0 path or the p2=target hook path — nothing to restore.
                log_debug($"Finalization complete for {format_actor_slot(param_1)} ({BitOperations.PopCount(_runtime.LastParriedTargetMask)} target(s) parried).");
                end_parry_window("finalization_complete");
            }
            else if (_runtime.TurnImpactMissedSeen)
            {
                // A poll-detected impact for this turn was already marked as missed (window was
                // closed when it hit). The player opened the window after the fact — that is not
                // a successful parry. Skip the Anfunkeln fallback to avoid a false PARRIED.
                log_debug($"Anfunkeln-style finalization skipped for {format_actor_slot(param_1)}: turn already missed.");
                end_parry_window("anfunkeln_missed_turn");
            }
            else
            {
                // Anfunkeln-style: no p2=target calls fired and no prior missed detection.
                // h_ms_calc_damage return-0 blocked the damage. Resolve parry feedback
                // now for all targeted party members.
                Chr* party = _battleAdapter.GetPlayerCharacters();
                if (party != null)
                {
                    uint mask = _runtime.CurrentPartyTargetMask;
                    while (mask != 0)
                    {
                        int slot = BitOperations.TrailingZeroCount(mask);
                        mask &= mask - 1;
                        Chr* candidate = party + slot;
                        if (candidate->stat_exist_flag && !is_target_non_parryable(candidate))
                        {
                            log_debug($"Anfunkeln-style finalization: resolving parry for {format_actor_slot((byte)slot)}.");
                            resolve_successful_parry(slot, candidate, "anfunkeln_final", closeWindow: false);
                        }
                    }
                }
                end_parry_window("anfunkeln_finalization_complete");
            }
        }

        log_hook_ms_set_damage(param_1, param_2, param_3, result);
        return result;
    }

    /// <summary>
    ///     Writes MsSetDamage hook telemetry to the session log file only (not the overlay).
    ///     High-frequency hook data is useful for post-session analysis but clutters the
    ///     real-time debug overlay with low-signal noise.
    /// </summary>
    private void log_hook_ms_set_damage(byte param_1, int param_2, int param_3, int result)
    {
        if (!_optionLogging)
            return;

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
            return;

        _msSetDamageLogLastFrame = frame;
        _msSetDamageLogLastP1 = param_1;
        _msSetDamageLogLastP2 = param_2;
        _msSetDamageLogLastP3 = param_3;
        _msSetDamageLogLastResult = result;
        _msSetDamageLogLastParryWindowActive = parryWindowActive;
        _msSetDamageLogLastAttackerId = currentAttackerId;
        _msSetDamageLogLastAwaitingTurnEnd = awaitingTurnEnd;

        // Route to session file log only — not to overlay ring buffer.
        write_session_hook_entry($"[MsSetDamage] f={frame} p1={param_1} p2={param_2} p3={param_3} ret={result} parry={parryWindowActive} atk={currentAttackerId} await={awaitingTurnEnd}");
    }

    /// <summary>
    ///     Active hook on MsCalcDamage (community-confirmed 11-param signature, March 2026).
    ///     Fires with per-target info for ALL attack types — physical and magic alike — making
    ///     it the authoritative damage interception point for parry resolution.
    ///
    ///     When a party slot has an active parry expiry timestamp, the hook skips the native
    ///     MsCalcDamage call entirely and returns 0 — preventing damage from entering the
    ///     native pipeline. This replaces the previous IMMUNITY_HP_DAMAGE flag mechanism.
    ///
    ///     The check MUST happen BEFORE orig is called. If orig runs, damage enters the
    ///     native buffer and cannot be recalled.
    ///
    ///     The MsSetDamage p2=target path remains as a secondary feedback path for physical
    ///     attacks. The Anfunkeln finalization fallback remains as a safety net for attacks
    ///     that bypass MsCalcDamage entirely.
    /// </summary>
    private int h_ms_calc_damage(
        int user_id, nint user_chr, int target_id, nint target_chr,
        nint command, int command_id,
        nint p7, nint p8, nint p9, nint p10, int p11)
    {
        // Check parry expiry BEFORE calling orig — skipping orig is how we prevent damage
        // from entering the native buffer. Once orig runs, damage cannot be recalled.
        bool isPartyTarget = target_id >= 0 && target_id < PartyActorCapacity;
        bool shouldIntercept = isPartyTarget
            && _optionEnabled
            && DateTime.UtcNow.Ticks < _parryExpiry[target_id]
            && (_runtime.LastParriedTargetMask & (1u << target_id)) == 0;

        if (shouldIntercept)
        {
            Chr* party = _battleAdapter.GetPlayerCharacters();
            Chr* candidate = party != null ? party + target_id : null;
            if (candidate != null && candidate->stat_exist_flag && !is_target_non_parryable(candidate))
            {
                log_debug($"MsCalcDamage intercepted: {format_actor_slot((byte)user_id)} -> {format_actor_slot((byte)target_id)} (cmd={command_id}), returning 0.");
                resolve_successful_parry(target_id, candidate, "ms_calc_damage", closeWindow: false);
                _damageEventActive[target_id] = true;
                log_hook_ms_calc_damage(user_id, target_id, command_id, p11, 0, command);
                return 0;
            }
        }

        int result = _hMsCalcDamage.orig_fptr.Invoke(
            user_id, user_chr, target_id, target_chr,
            command, command_id,
            p7, p8, p9, p10, p11);

        log_hook_ms_calc_damage(user_id, target_id, command_id, p11, result, command);
        return result;
    }

    private void log_hook_ms_calc_damage(int user_id, int target_id, int command_id, int p11, int result, nint command)
    {
        if (!_optionLogging)
            return;

        ulong frame = _debugFrameIndex;
        bool sameAsLast =
            _msCalcDamageLogLastFrame != 0
            && _msCalcDamageLogLastUserId == user_id
            && _msCalcDamageLogLastTargetId == target_id
            && _msCalcDamageLogLastCommandId == command_id
            && _msCalcDamageLogLastHitCount == p11
            && _msCalcDamageLogLastResult == result;

        if (sameAsLast && frame - _msCalcDamageLogLastFrame < 30)
            return;

        _msCalcDamageLogLastFrame = frame;
        _msCalcDamageLogLastUserId = user_id;
        _msCalcDamageLogLastTargetId = target_id;
        _msCalcDamageLogLastCommandId = command_id;
        _msCalcDamageLogLastHitCount = p11;
        _msCalcDamageLogLastResult = result;

        // Route to session file log only — high-frequency hook data clutters the overlay.
        write_session_hook_entry($"[MsCalcDamage] f={frame} user={user_id} target={target_id} cmd={command_id} hits={p11} ret={result} cmd_ptr=0x{command:X}");
    }

    private void h_ms_damage_set_motion(byte target, int p2, int p3)
    {
        _hMsDamageSetMotion.orig_fptr.Invoke(target, p2, p3);

        if (_optionLogging)
        {
            write_session_hook_entry($"[MsDamageSetMotion] f={_debugFrameIndex} target={target} p2={p2} p3={p3} parry={_runtime.ParryWindowActive}");
        }
    }

    private int h_dmg_calc_armored(Chr* user, Chr* target, Command* command, int p4, int* p5, int damage)
    {
        int result = _hDmgCalcArmored.orig_fptr.Invoke(user, target, command, p4, p5, damage);

        if (_optionLogging)
        {
            write_session_hook_entry($"[DmgCalcArmored] f={_debugFrameIndex} damage_in={damage} damage_out={result} parry={_runtime.ParryWindowActive}");
        }

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
