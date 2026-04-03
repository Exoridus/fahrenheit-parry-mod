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
        // p2=target: snapshot HP when a parry expiry is active for this slot so we can restore
        // on impact. The hook is the authoritative impact detection path — it fires at native
        // damage time, before the poll path in on_pre_update. When the per-slot expiry is still
        // live and the attacker matches, resolve the parry directly from the hook to ensure
        // feedback (text + sound) fires at impact time rather than being deferred to the poll path.
        // Use _parryExpiry[param_2] (same gate as h_ms_calc_damage and h_ms_damage_set_motion)
        // rather than _runtime.ParryWindowActive (10s backstop), so all resolution paths agree
        // on parry validity using the same wall-clock window the player actually pressed.
        bool isPartyTargetCall = param_2 >= 0 && param_2 < PartyActorCapacity;
        bool isActiveParry = isPartyTargetCall
            && _optionEnabled
            && DateTime.UtcNow.Ticks < _parryExpiry[param_2]
            && param_1 == _runtime.CurrentAttackerId
            && (_runtime.CurrentPartyTargetMask & (1u << param_2)) != 0;

        Chr* parryTarget = null;

        if (isActiveParry)
        {
            Chr* party = _battleAdapter.GetPlayerCharacters();
            Chr* candidate = party != null ? party + param_2 : null;
            if (candidate != null && candidate->stat_exist_flag)
            {
                parryTarget = candidate;
            }
        }

        if (isPartyTargetCall)
            _attackTelemetry[param_2].SetDamageTargetFired = true;

        long nowTicks = DateTime.UtcNow.Ticks;
        bool anyExpiryActive = false;
        uint currentMask = _runtime.CurrentPartyTargetMask;
        for (int i = 0; i < PartyActorCapacity; i++)
        {
            if ((currentMask & (1u << i)) != 0 && nowTicks < _parryExpiry[i])
            {
                anyExpiryActive = true;
                break;
            }
        }

        // p3=0x400 finalization: if the parry window is still open, close it and handle
        // Anfunkeln-style attacks (no p2=target calls) that need feedback resolved here.
        // h_ms_calc_damage return-0 has already prevented the HP reduction — no
        // snapshot/restore needed.
        bool isFinalizationWithParry = param_3 == 0x400
            && _optionEnabled
            && param_1 == _runtime.CurrentAttackerId
            && (anyExpiryActive || _runtime.LastParriedTargetMask != 0)
            && try_get_enemy_attack_cue(out AttackCue cue, out byte cueIndex, out _)
            && cueIndex == _runtime.CurrentCueIndex;

        // Detect whether the motion hook already handled this exact hit.
        // For 0x40A0-class attacks, the animation system calls MsDamageSetMotion directly
        // BEFORE MsSetDamage p2=target fires (~14 frames later). The motion hook sets
        // LastParriedTargetMask when it handles the hit. By the time MsSetDamage fires, the
        // parry window has expired and isActiveParry is false — parryTarget is null — so
        // skipOrigForParry would not fire. But FUN_0078f0b0 must still be blocked to prevent
        // it from re-applying damage from the native hit-record buffer via MsSubHP.
        bool alreadyParriedThisHit = isPartyTargetCall
            && (_runtime.LastParriedTargetMask & (1u << param_2)) != 0;

        // If the motion hook already handled this hit (window may have since expired),
        // parryTarget is null because isActiveParry failed the expiry check. Reconstruct
        // the target pointer so the skip logic and lethal-restore path below can use it.
        if (alreadyParriedThisHit && parryTarget == null)
        {
            Chr* party = _battleAdapter.GetPlayerCharacters();
            Chr* candidate = party != null ? party + param_2 : null;
            if (candidate != null && candidate->stat_exist_flag)
                parryTarget = candidate;
        }

        // Skip orig when the window is still live and damage is pending (standard reactive
        // path), OR when the motion hook already handled this exact hit (durable marker that
        // survives window expiry). In both cases parryTarget must be valid.
        // NOTE: do NOT check damage_hp > 0 for the alreadyParriedThisHit branch — the motion
        // hook calls negate_damage_on_impact which zeroes damage_hp before MsSetDamage fires,
        // but FUN_0078f0b0 reads damage from the native hit-record buffer (separate from
        // damage_hp) and will still call MsSubHP with the original computed damage.
        bool skipOrigForParry = parryTarget != null
            && _optionNegateDamage
            && !is_target_non_parryable(parryTarget)
            && (alreadyParriedThisHit
                || (DateTime.UtcNow.Ticks < _parryExpiry[param_2] && parryTarget->damage_hp > 0));

        // MagicProbe: snapshot HP before orig for per-target call.
        uint setDamageHpBefore = 0;
        if (isPartyTargetCall && _optionLogging)
        {
            Chr* probeParty = _battleAdapter.GetPlayerCharacters();
            if (probeParty != null)
                setDamageHpBefore = (uint)(probeParty + param_2)->ram.hp;
        }

        // Telemetry: snapshot HP for all targeted party slots before finalization.
        bool isFinalization = param_3 == 0x400;
        if (isFinalization)
        {
            Chr* party = _battleAdapter.GetPlayerCharacters();
            if (party != null)
            {
                uint mask = _runtime.CurrentPartyTargetMask;
                for (int i = 0; i < PartyActorCapacity; i++)
                {
                    if ((mask & (1u << i)) != 0 && (party + i)->stat_exist_flag)
                        _attackTelemetry[i].HpBeforeFinalization = (uint)(party + i)->ram.hp;
                }
            }
        }

        int result;
        if (skipOrigForParry)
        {
            // Orig skipped — native HP apply blocked. Resolve parry feedback now.
            result = 0;
            log_debug($"Parry blocked p2=target orig for {format_actor_slot((byte)param_2)} (damage_hp={parryTarget->damage_hp}, hp={(uint)parryTarget->ram.hp}).");

            if (!alreadyParriedThisHit)
            {
                string attackerLabel = format_actor_slot(param_1);
                string targetLabel   = format_actor_slot((byte)param_2);
                log_debug($"Hook impact: {attackerLabel} -> {targetLabel}, resolving parry at impact time.");
                resolve_successful_parry(param_2, parryTarget, "physical", closeWindow: false);
                _damageEventActive[param_2]   = true;
                _parryFeedbackPending[param_2] = false;
            }
            else
            {
                log_debug($"Parry blocked orig (motion-hook already resolved feedback for {format_actor_slot((byte)param_2)}).");
            }

            // Lethal restore: an unknown native reducer (likely MsSetDamageInternal) can clamp
            // ram.hp to 0 before this hook fires for killing blows, even though we skip orig here.
            // If HP is already 0 and we have a pre-hit snapshot, restore it and clear stat_will_die.
            if (parryTarget->ram.hp <= 0)
            {
                uint snap = _preHitHpSnapshot[param_2];
                if (snap > 0)
                {
                    // Diagnostic: read death-state fields before restore to determine
                    // whether the death latch (0xDCC et al.) is already set at this point.
                    byte*   chrB    = (byte*)parryTarget;
                    byte    dcc_pre = chrB[0xDCC];
                    ushort  s606_pre = *(ushort*)(chrB + 0x606);
                    ushort  s700_pre = *(ushort*)(chrB + 0x700);
                    ushort  s702_pre = *(ushort*)(chrB + 0x702);
                    byte    dee_pre  = chrB[0xDEE];
                    byte    dd0_pre  = chrB[0xDD0];
                    byte    dd1_pre  = chrB[0xDD1];
                    byte    f5f_pre  = chrB[0xF5F];
                    log_debug($"[LethalDiag PRE ] slot={param_2} hp={parryTarget->ram.hp} dmg_hp={parryTarget->damage_hp} wdie={parryTarget->stat_will_die} " +
                              $"0xDCC={dcc_pre} 0x606=0x{s606_pre:X4} 0x700=0x{s700_pre:X4} 0x702=0x{s702_pre:X4} " +
                              $"0xDEE={dee_pre} 0xDD0={dd0_pre} 0xDD1={dd1_pre} 0xF5F={f5f_pre}");

                    parryTarget->ram.hp = (int)snap;
                    parryTarget->stat_will_die = 0;

                    // Clear confirmed death-latch fields set by MsDamageCheckDeath.
                    // Evidence: LethalDiag PRE consistently shows 0xDCC=2 and 0x606=0x0001.
                    // MsGetChrStatDeath returns 1 when chr+0xDCC != 0, gating all downstream
                    // death processing. 0x606 bit-0 is the dead-status bit ORed in by the
                    // same function. Clearing both here prevents death from winning despite
                    // the HP restore.
                    chrB[0xDCC] = 0;
                    *(ushort*)(chrB + 0x606) &= unchecked((ushort)~1u);

                    byte    dcc_post = chrB[0xDCC];
                    ushort  s606_post = *(ushort*)(chrB + 0x606);
                    ushort  s700_post = *(ushort*)(chrB + 0x700);
                    ushort  s702_post = *(ushort*)(chrB + 0x702);
                    byte    dee_post  = chrB[0xDEE];
                    byte    dd0_post  = chrB[0xDD0];
                    byte    dd1_post  = chrB[0xDD1];
                    byte    f5f_post  = chrB[0xF5F];
                    log_debug($"[LethalDiag POST] slot={param_2} hp={parryTarget->ram.hp} dmg_hp={parryTarget->damage_hp} wdie={parryTarget->stat_will_die} " +
                              $"0xDCC={dcc_post} 0x606=0x{s606_post:X4} 0x700=0x{s700_post:X4} 0x702=0x{s702_post:X4} " +
                              $"0xDEE={dee_post} 0xDD0={dd0_post} 0xDD1={dd1_post} 0xF5F={f5f_post}");

                    log_debug($"Parry lethal restore for {format_actor_slot((byte)param_2)}: HP restored to {snap} (was 0), stat_will_die cleared.");
                }
            }
        }
        else
        {
            result = _hMsSetDamage.orig_fptr.Invoke(param_1, param_2, param_3);
        }

        // MagicProbe: emit per-target HP probe after orig (or after skip).
        if (isPartyTargetCall && _optionLogging)
        {
            Chr* probeParty = _battleAdapter.GetPlayerCharacters();
            uint setDamageHpAfter = probeParty != null ? (uint)(probeParty + param_2)->ram.hp : 0;
            int setDamageHpDelta = (int)setDamageHpAfter - (int)setDamageHpBefore;
            write_session_hook_entry($"[MagicProbe/SetDamage] frame={_debugFrameIndex} p1={param_1} p2={param_2} p3={param_3} skipped={skipOrigForParry} hp_before={setDamageHpBefore} hp_after={setDamageHpAfter} hp_delta={setDamageHpDelta}");
        }

        // Telemetry: snapshot HP after finalization.
        if (isFinalization)
        {
            Chr* party = _battleAdapter.GetPlayerCharacters();
            if (party != null)
            {
                uint mask = _runtime.CurrentPartyTargetMask;
                for (int i = 0; i < PartyActorCapacity; i++)
                {
                    if ((mask & (1u << i)) != 0 && (party + i)->stat_exist_flag)
                        _attackTelemetry[i].HpAfterFinalization = (uint)(party + i)->ram.hp;
                }
            }
        }

        if (isFinalizationWithParry)
        {
            if (_runtime.LastParriedTargetMask != 0)
            {
                // Safety net: resolve any remaining deferred feedback that MsDamageSetMotion
                // did not consume (e.g. magic.dll attacks where MsDamageSetMotion may not fire).
                Chr* partyFallback = _battleAdapter.GetPlayerCharacters();
                if (partyFallback != null)
                {
                    for (int i = 0; i < PartyActorCapacity; i++)
                    {
                        if (!_parryFeedbackPending[i]) continue;
                        Chr* fb = partyFallback + i;
                        if (fb->stat_exist_flag && !is_target_non_parryable(fb))
                        {
                            // Additive restore — same semantics as motion and p2=target paths.
                            if (_optionNegateDamage && fb->damage_hp > 0)
                            {
                                int restored = (int)fb->ram.hp + fb->damage_hp;
                                uint snap = _preHitHpSnapshot[i];
                                if (snap > 0 && (uint)restored > snap) restored = (int)snap;
                                fb->ram.hp = restored;
                                log_debug($"Parry restored {fb->damage_hp} HP for {format_actor_slot((byte)i)} at fallback (hp now={(uint)fb->ram.hp}).");
                            }

                            resolve_successful_parry(i, fb, "fallback", closeWindow: false);
                        }
                        _parryFeedbackPending[i] = false;
                    }
                }

                // Regular attack: each target was already resolved by the h_ms_calc_damage
                // return-0 path or the p2=target hook path — nothing to restore.
                log_debug($"Turn complete — {BitOperations.PopCount(_runtime.LastParriedTargetMask)} target(s) parried.");
                end_parry_window("finalization_complete");
            }
            else if (_runtime.TurnImpactMissedSeen)
            {
                // A poll-detected impact for this turn was already marked as missed (window was
                // closed when it hit). The player opened the window after the fact — that is not
                // a successful parry. Skip the Anfunkeln fallback to avoid a false PARRIED.
                log_debug($"Magic/special finalization skipped for {format_actor_slot(param_1)}: turn already missed.");
                end_parry_window("missed_turn");
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

                        // AnfunkelProbe: log status fields and parry gate results at the
                        // exact branching point for Anfunkeln-style finalization. This
                        // distinguishes status-carrying attacks (e.g. confuse 0x0100 in
                        // 0x606) that fail is_target_non_parryable from statusless ones
                        // that resolve successfully.
                        if (_optionLogging && candidate->stat_exist_flag)
                        {
                            byte*  anfB    = (byte*)candidate;
                            ushort anf_606 = *(ushort*)(anfB + 0x606);
                            byte   anf_617 = anfB[0x617];
                            bool   anfNonP = is_target_non_parryable(candidate);
                            write_session_hook_entry(
                                $"[AnfunkelProbe] f={_debugFrameIndex} slot={slot} " +
                                $"cmdId=0x{(_attackTelemetry[slot].CommandId):X4} " +
                                $"hp={(uint)candidate->ram.hp} dmg_hp={candidate->damage_hp} " +
                                $"0x606=0x{anf_606:X4} 0x617=0x{anf_617:X2} " +
                                $"parryActive={_runtime.ParryWindowActive} " +
                                $"nonParryable={anfNonP} " +
                                $"success={(!anfNonP && candidate->damage_hp > 0 ? 1 : 0)}");
                        }

                        if (candidate->stat_exist_flag && candidate->damage_hp > 0 && !is_target_non_parryable(candidate))
                        {
                            // Additive restore — same semantics as motion and p2=target paths.
                            if (_optionNegateDamage)
                            {
                                int restored = (int)candidate->ram.hp + candidate->damage_hp;
                                uint snap = _preHitHpSnapshot[slot];
                                if (snap > 0 && (uint)restored > snap) restored = (int)snap;
                                candidate->ram.hp = restored;
                                log_debug($"Parry restored {candidate->damage_hp} HP for {format_actor_slot((byte)slot)} at magic_finalization (hp now={(uint)candidate->ram.hp}).");
                            }

                            resolve_successful_parry(slot, candidate, "magic_impact", closeWindow: false);

                            // StatusProbe: read 0x606 and 0x617 immediately after parry resolution
                            // to determine whether the confuse bit (0x0100) and surrounding status
                            // fields are committed to effective game state at this point.
                            if (_optionLogging)
                            {
                                byte*  chrB   = (byte*)candidate;
                                ushort s606   = *(ushort*)(chrB + 0x606);
                                byte   s617   = chrB[0x617];
                                write_session_hook_entry($"[StatusProbe/magic_finalization_post] f={_debugFrameIndex} slot={slot} hp={(uint)candidate->ram.hp} 0x606=0x{s606:X4} 0x617=0x{s617:X2}");
                            }
                        }
                    }
                }
                end_parry_window("magic_finalization");
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

        if (isPartyTarget)
        {
            _attackTelemetry[target_id].CalcDamageFired = true;
            if (_attackTelemetry[target_id].CommandId == 0)
                _attackTelemetry[target_id].CommandId = command_id;
        }

        Chr* targetChrPtr = isPartyTarget && target_chr != 0 ? (Chr*)target_chr : null;
        uint calcHpBefore = targetChrPtr != null ? (uint)targetChrPtr->ram.hp : 0;

        // Unconditional pre-hit HP snapshot for all party targets — captured before orig runs.
        // Reactive parries (R1 pressed after calc fires) never enter shouldIntercept, so this
        // is the only site that captures snapshot for reactive-parry lethal restore.
        // Guard == 0: preserve first-hit value on multi-hit attacks (don't overwrite with 0).
        if (isPartyTarget && targetChrPtr != null && targetChrPtr->stat_exist_flag
            && _preHitHpSnapshot[target_id] == 0)
        {
            _preHitHpSnapshot[target_id] = (uint)targetChrPtr->ram.hp;
        }

        if (shouldIntercept)
        {
            Chr* party = _battleAdapter.GetPlayerCharacters();
            Chr* candidate = party != null ? party + target_id : null;
            if (candidate != null && candidate->stat_exist_flag && !is_target_non_parryable(candidate))
            {
                _attackTelemetry[target_id].CalcDamageIntercepted = true;
                _runtime.LastParriedTargetMask |= 1u << target_id;
                _parryFeedbackPending[target_id] = true;
                _damageEventActive[target_id] = true;
                log_debug($"MsCalcDamage intercepted: {format_actor_slot((byte)user_id)} -> {format_actor_slot((byte)target_id)} (cmd={command_id}), damage blocked, feedback deferred. HP snapshot={_preHitHpSnapshot[target_id]}");
                log_hook_ms_calc_damage(user_id, target_id, command_id, p11, 0, command);
                return 0;
            }
        }

        int result = _hMsCalcDamage.orig_fptr.Invoke(
            user_id, user_chr, target_id, target_chr,
            command, command_id,
            p7, p8, p9, p10, p11);

        if (isPartyTarget && _optionLogging)
        {
            uint calcHpAfter = targetChrPtr != null ? (uint)targetChrPtr->ram.hp : 0;
            int calcHpDelta = (int)calcHpAfter - (int)calcHpBefore;
            write_session_hook_entry($"[MagicProbe/CalcDamage] frame={_debugFrameIndex} user={user_id} target={target_id} cmd=0x{command_id:X4} result={result} hp_before={calcHpBefore} hp_after={calcHpAfter} hp_delta={calcHpDelta}");
        }

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
        // Skip orig when a parry expiry is active and this is a damage motion (p3=1).
        // MsDamageSetMotion orig reads damage_hp and reduces ram.hp for some attack commands
        // (e.g. 0x4026) before our negate logic runs. Skipping orig prevents both the HP
        // reduction and the flinch animation — correct, since a parried hit should not flinch.
        bool targetIsParty = target < PartyActorCapacity;
        bool parryActive   = targetIsParty
                             && _optionEnabled
                             && DateTime.UtcNow.Ticks < _parryExpiry[target]
                             && _parryArmedAttackerId[target] == _runtime.CurrentAttackerId
                             && p3 == 1;

        // When parry is active but the enemy missed (damage_hp=0), do NOT suppress the flinch —
        // let the native miss/dodge animation play normally. Only suppress when actual damage
        // was pending, meaning we intercepted a real hit.
        bool suppressFlinch = false;
        if (parryActive)
        {
            Chr* flinchParty = _battleAdapter.GetPlayerCharacters();
            Chr* flinchTarget = flinchParty != null ? flinchParty + target : null;
            suppressFlinch = flinchTarget != null && flinchTarget->stat_exist_flag && flinchTarget->damage_hp > 0;
        }

        // Lethal-death-visual diagnostic: log death-state fields before/after orig for
        // damage motions on party targets. This answers whether 0xDCC is latched and the
        // death motion is dispatched INSIDE MsDamageSetMotion orig — before the later
        // h_ms_set_damage lethal restore path has any opportunity to intervene.
        bool isLethalDiagTarget = _optionLogging && targetIsParty && p3 == 1;
        Chr* lethalDiagChr = null;
        if (isLethalDiagTarget)
        {
            Chr* diagParty = _battleAdapter.GetPlayerCharacters();
            lethalDiagChr = diagParty != null ? diagParty + target : null;
            if (lethalDiagChr != null && lethalDiagChr->stat_exist_flag)
            {
                byte*  diagB        = (byte*)lethalDiagChr;
                byte   dcc_pre      = diagB[0xDCC];
                ushort s606_pre     = *(ushort*)(diagB + 0x606);
                write_session_hook_entry(
                    $"[MotionDeathDiag PRE ] f={_debugFrameIndex} slot={target} parryActive={parryActive} suppress={suppressFlinch} " +
                    $"dmg_hp={lethalDiagChr->damage_hp} hp={lethalDiagChr->ram.hp} wdie={lethalDiagChr->stat_will_die} " +
                    $"0xDCC={dcc_pre} 0x606=0x{s606_pre:X4}");

                // LethalProbe: fires for ALL damage motions on party targets, regardless of
                // whether the suppress/restore path runs later. Captures true lethal attempts
                // that escape logging when the restore path does not fire (e.g. parryActive is
                // false, or suppressFlinch is false because damage_hp was already zeroed).
                bool lethalProbeIsLethal = lethalDiagChr->damage_hp >= lethalDiagChr->ram.hp
                    || lethalDiagChr->ram.hp <= 0;
                write_session_hook_entry(
                    $"[LethalProbe/pre-orig] f={_debugFrameIndex} slot={target} hp={lethalDiagChr->ram.hp} " +
                    $"dmg_hp={lethalDiagChr->damage_hp} lethal={(lethalProbeIsLethal ? 1 : 0)} " +
                    $"wdie={lethalDiagChr->stat_will_die} 0xDCC={dcc_pre} 0x606=0x{s606_pre:X4} " +
                    $"parryActive={parryActive} suppress={suppressFlinch}");
            }
        }

        if (!parryActive || !suppressFlinch)
        {
            _hMsDamageSetMotion.orig_fptr.Invoke(target, p2, p3);

            // POST: read same fields after orig to detect whether death latch fires inside orig.
            if (isLethalDiagTarget && lethalDiagChr != null && lethalDiagChr->stat_exist_flag)
            {
                byte*  diagB        = (byte*)lethalDiagChr;
                byte   dcc_post     = diagB[0xDCC];
                ushort s606_post    = *(ushort*)(diagB + 0x606);
                write_session_hook_entry(
                    $"[MotionDeathDiag POST] f={_debugFrameIndex} slot={target} parryActive={parryActive} suppress={suppressFlinch} " +
                    $"dmg_hp={lethalDiagChr->damage_hp} hp={lethalDiagChr->ram.hp} wdie={lethalDiagChr->stat_will_die} " +
                    $"0xDCC={dcc_post} 0x606=0x{s606_post:X4}");
            }
        }
        else
        {
            log_debug($"Parry suppressed flinch for {format_actor_slot(target)} (MsDamageSetMotion skipped).");
            if (isLethalDiagTarget && lethalDiagChr != null && lethalDiagChr->stat_exist_flag)
            {
                byte*  diagB     = (byte*)lethalDiagChr;
                byte   dcc_skip  = diagB[0xDCC];
                ushort s606_skip = *(ushort*)(diagB + 0x606);
                write_session_hook_entry(
                    $"[MotionDeathDiag SKIP] f={_debugFrameIndex} slot={target} orig skipped by parry suppress " +
                    $"hp={lethalDiagChr->ram.hp} dmg_hp={lethalDiagChr->damage_hp} wdie={lethalDiagChr->stat_will_die} " +
                    $"0xDCC={dcc_skip} 0x606=0x{s606_skip:X4}");
            }

            // Additive restore: ram.hp += damage_hp undoes whatever the native pipeline
            // already applied. For reactive parries the snapshot is never set (calc fired
            // before R1 press), so we must not gate on snapshot > 0.
            // If the snapshot WAS set (proactive interception), cap to it to prevent
            // over-restore when a lethal hit clamped HP to 0 before our hook fired.
            //
            // Lethal restore: if HP was already ≤ 0 when our hook fires (the unknown native
            // reducer applied damage before MsDamageSetMotion was called), the same native path
            // may have also set the death latch (0xDCC) and dead-status bit (0x606 bit 0).
            // Clear both after restoring HP — same fields cleared by the h_ms_set_damage
            // skipOrigForParry path, moved here because that path is dead code for production
            // windows (MsSetDamage fires ~2.5s after press, well past the 200ms window).
            if (_optionNegateDamage)
            {
                Chr* party = _battleAdapter.GetPlayerCharacters();
                Chr* targetChr = party != null ? party + target : null;
                if (targetChr != null && targetChr->stat_exist_flag && targetChr->damage_hp > 0)
                {
                    bool wasLethal = targetChr->ram.hp <= 0;
                    int restored = (int)targetChr->ram.hp + targetChr->damage_hp;
                    uint snap = _preHitHpSnapshot[target];
                    if (snap > 0 && (uint)restored > snap) restored = (int)snap;
                    targetChr->ram.hp = restored;

                    if (wasLethal && targetChr->ram.hp > 0)
                    {
                        byte* chrB = (byte*)targetChr;
                        chrB[0xDCC] = 0;
                        *(ushort*)(chrB + 0x606) &= unchecked((ushort)~1u);
                        targetChr->stat_will_die = 0;

                        if (_optionLogging)
                        {
                            byte*  rlB    = (byte*)targetChr;
                            byte   rl_dcc = rlB[0xDCC];
                            ushort rl_606 = *(ushort*)(rlB + 0x606);
                            write_session_hook_entry(
                                $"[LethalProbe/post-restore] f={_debugFrameIndex} slot={target} hp={targetChr->ram.hp} " +
                                $"dmg_hp={targetChr->damage_hp} wdie={targetChr->stat_will_die} " +
                                $"0xDCC={rl_dcc} 0x606=0x{rl_606:X4} snap={snap}");
                        }

                        log_debug($"Parry lethal restore (motion) for {format_actor_slot(target)}: HP restored to {(uint)targetChr->ram.hp}, death-latch cleared.");
                    }
                    else
                    {
                        log_debug($"Parry restored {targetChr->damage_hp} HP for {format_actor_slot(target)} (hp now={(uint)targetChr->ram.hp}).");
                    }
                }
            }
        }

        // p3=1 signals a damage-involved motion for party targets (confirmed from session log
        // analysis: p2=5 for standard physical attacks, p2=1 for ramming attacks — both with p3=1).
        // p3=0 calls are cosmetic/non-damage motions and must not trigger parry resolution.
        bool isDamageMotion = p3 == 1 && targetIsParty;

        if (isDamageMotion)
            _attackTelemetry[target].SetMotionFired = true;

        if (isDamageMotion)
        {
            bool pendingDeferred = _parryFeedbackPending[target];
            Chr* reactiveCandidate = null;
            bool reactiveParry   = false;
            if (!pendingDeferred
                && _optionEnabled
                && parryActive
                && (_runtime.CurrentPartyTargetMask & (1u << target)) != 0
                && (_runtime.LastParriedTargetMask  & (1u << target)) == 0)
            {
                Chr* rParty = _battleAdapter.GetPlayerCharacters();
                reactiveCandidate = rParty != null ? rParty + target : null;
                // Only resolve reactively if actual damage is pending — damage_hp=0 means
                // the enemy missed/was evaded, not a successful parry. Also skip if the target
                // was already non-parryable (confused, berserk, etc.) before this hit.
                reactiveParry = reactiveCandidate != null
                    && reactiveCandidate->damage_hp > 0
                    && !is_target_non_parryable(reactiveCandidate);
            }

            if (pendingDeferred || reactiveParry)
            {
                // Set the durable impact marker for this slot so that h_ms_set_damage_internal
                // p5=1024 can skip the authoritative HP/death commit without re-checking the
                // wall-clock window. The marker survives window expiry — critical for attacks
                // (Anfunkeln, Blitzra) where the commit pass is delayed past the timing window.
                // Guard: only set once per slot per turn.
                if ((_parryResolvedAtImpactMask & (1u << target)) == 0)
                {
                    _parryResolvedAtImpactMask |= 1u << target;
                    write_session_hook_entry(
                        $"[ReactiveImpactMarker] f={_debugFrameIndex} slot={target} " +
                        $"attacker={_runtime.CurrentAttackerId} deferred={pendingDeferred} " +
                        $"reactive={reactiveParry} marker=SET");
                }

                Chr* party = _battleAdapter.GetPlayerCharacters();
                Chr* candidate = party != null ? party + target : null;
                if (candidate != null && candidate->stat_exist_flag)
                {
                    string source = pendingDeferred ? "deferred" : "visual";
                    resolve_successful_parry(target, candidate, source, closeWindow: false);

                    // StatusProbe: read 0x606 and 0x617 immediately after parry resolution
                    // to determine whether the confuse bit (0x0100) seen in MotionDeathDiag PRE
                    // is committed to effective game state after the full motion hook path runs.
                    if (_optionLogging)
                    {
                        byte*  chrB   = (byte*)candidate;
                        ushort s606   = *(ushort*)(chrB + 0x606);
                        byte   s617   = chrB[0x617];
                        write_session_hook_entry($"[StatusProbe/motion_suppress_post] f={_debugFrameIndex} slot={target} hp={(uint)candidate->ram.hp} 0x606=0x{s606:X4} 0x617=0x{s617:X2}");
                    }
                }
                _parryFeedbackPending[target] = false;
            }
        }

        if (_optionLogging)
        {
            write_session_hook_entry($"[MsDamageSetMotion] f={_debugFrameIndex} target={target} p2={p2} p3={p3} parry={_runtime.ParryWindowActive}");

            if (target < PartyActorCapacity)
            {
                Chr* motionParty = _battleAdapter.GetPlayerCharacters();
                uint motionHp = motionParty != null ? (uint)(motionParty + target)->ram.hp : 0;
                write_session_hook_entry($"[MagicProbe/SetMotion] frame={_debugFrameIndex} target={target} p2={p2} p3={p3} hp={motionHp}");
            }
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

    private int h_ms_calc_damage_internal(
        int user_id, nint user_chr, int target_id, nint target_chr,
        nint command, int command_id,
        nint p7, nint p8, nint p9, nint p10, int p11)
    {
        // Inner interception: check parry expiry BEFORE calling orig.
        // This is the safety net for attacks where the outer h_ms_calc_damage interception
        // fails because LastParriedTargetMask was already set by MsDamageSetMotion.
        // Do NOT check LastParriedTargetMask here — that is the entire reason this inner
        // hook exists. The outer already checks it; when outer passes through, inner must
        // still fire based on expiry alone.
        bool isPartyTarget = target_id >= 0 && target_id < PartyActorCapacity;
        bool innerShouldIntercept = isPartyTarget
            && _optionEnabled
            && DateTime.UtcNow.Ticks < _parryExpiry[target_id];

        if (innerShouldIntercept)
        {
            // Snapshot HP if not already captured by the outer h_ms_calc_damage hook.
            if (_preHitHpSnapshot[target_id] == 0 && target_chr != nint.Zero)
            {
                Chr* snapTarget = (Chr*)target_chr;
                if (snapTarget->stat_exist_flag)
                    _preHitHpSnapshot[target_id] = (uint)snapTarget->ram.hp;
            }
            _parryFeedbackPending[target_id] = true;
            _runtime.LastParriedTargetMask |= 1u << target_id;
            _damageEventActive[target_id] = true;
            write_session_hook_entry(
                $"[DmgCalcInternal] INTERCEPTED frame={_debugFrameIndex} user={user_id} target={target_id} cmd=0x{command_id:X4}");
            return 0;
        }

        int damageHpBefore = 0;
        int ramHpBefore = 0;
        bool readOk = false;

        if (_optionLogging && target_chr != nint.Zero)
        {
            try
            {
                Chr* tgt = (Chr*)target_chr;
                damageHpBefore = tgt->damage_hp;
                ramHpBefore = tgt->ram.hp;
                readOk = true;
            }
            catch
            {
                // target_chr may not be a valid Chr* — swallow and log what we can
            }
        }

        int result = _hMsCalcDamageInternal.orig_fptr.Invoke(
            user_id, user_chr, target_id, target_chr,
            command, command_id,
            p7, p8, p9, p10, p11);

        if (_optionLogging)
        {
            int damageHpAfter = 0;
            int ramHpAfter = 0;

            if (readOk && target_chr != nint.Zero)
            {
                try
                {
                    Chr* tgt = (Chr*)target_chr;
                    damageHpAfter = tgt->damage_hp;
                    ramHpAfter = tgt->ram.hp;
                }
                catch
                {
                    readOk = false;
                }
            }

            if (readOk)
            {
                write_session_hook_entry(
                    $"[DmgCalcInternal] f={_debugFrameIndex} user={user_id} target={target_id} cmd=0x{command_id:X4} result={result}" +
                    $" ramHp_before={ramHpBefore} ramHp_after={ramHpAfter}" +
                    $" damageHp_before={damageHpBefore} damageHp_after={damageHpAfter}" +
                    $" hits={p11}");
            }
            else
            {
                write_session_hook_entry(
                    $"[DmgCalcInternal] f={_debugFrameIndex} user={user_id} target={target_id} cmd=0x{command_id:X4} result={result}" +
                    $" (target_chr read failed) hits={p11}");
            }
        }

        return result;
    }

    /// <summary>
    ///     Phase 2 reactive interception at MsSetDamageInternal (FUN_0078f0b0 at FFX.exe+0x38F0B0).
    ///
    ///     Architecture (revised):
    ///     - p5=0 is NOT the authoritative HP/death commit pass. Do not intercept there.
    ///     - p5=1024 is the confirmed commit/finalization pass for the relevant failing attack classes.
    ///     - Timing decision is made earlier at MsDamageSetMotion (visual impact), where
    ///       _parryResolvedAtImpactMask is set when the parry window was valid at impact.
    ///     - Here we only check the durable marker — NOT the live wall-clock window.
    ///       This correctly handles delayed-finalization attacks (Anfunkeln, Blitzra) where
    ///       the p5=1024 call arrives long after the timing window has expired.
    ///     - Feedback (text/sound) was already emitted at MsDamageSetMotion; no second signal here.
    /// </summary>
    private int h_ms_set_damage_internal(int param_1, byte param_2, int param_3, int param_4, int param_5)
    {
        bool isPartySlot = param_3 >= 0 && param_3 < PartyActorCapacity;

        // Phase 2: authoritative commit skip via durable impact marker.
        // Only act on p5=1024. p5=0 is not the HP-commit pass — do not intercept there.
        if (isPartySlot && _optionEnabled && _optionNegateDamage && param_5 == 1024)
        {
            bool markerSet         = (_parryResolvedAtImpactMask & (1u << param_3)) != 0;
            bool notAlreadyConsumed = (_internalInterceptedMask  & (1u << param_3)) == 0;

            if (markerSet && notAlreadyConsumed)
            {
                _parryResolvedAtImpactMask     &= ~(1u << param_3);
                _internalInterceptedMask        |= 1u << param_3;
                _runtime.LastParriedTargetMask  |= 1u << param_3;

                write_session_hook_entry(
                    $"[MsSetDamageInternal] f={_debugFrameIndex} slot={param_3} p5=1024 marker=1 -> SKIP " +
                    $"attacker={param_1} cmd={param_2}");
                log_debug($"MsSetDamageInternal p5=1024 commit skipped for {format_actor_slot((byte)param_3)} (impact marker).");
                return 0;
            }
        }

        if (_optionLogging)
        {
            // Slot-level pre-commit state: log BEFORE calling orig so we see pre-commit snapshot.
            if (isPartySlot)
            {
                Chr* party = _battleAdapter.GetPlayerCharacters();
                if (party != null)
                {
                    Chr* slot = party + param_3;
                    if (slot->stat_exist_flag)
                    {
                        byte* slotB = (byte*)slot;
                        write_session_hook_entry(
                            $"[MsSetDamageInternal/slot] f={_debugFrameIndex} slot={param_3} " +
                            $"hp={(uint)slot->ram.hp} dmg_hp={slot->damage_hp} " +
                            $"0x606=0x{*(ushort*)(slotB + 0x606):X4} 0xDCC={slotB[0xDCC]}");
                    }
                }
            }

            // Raw params — always logged when logging is enabled.
            write_session_hook_entry(
                $"[MsSetDamageInternal] f={_debugFrameIndex} p1={param_1} p2={param_2} p3={param_3} p4={param_4} p5={param_5} " +
                $"attacker={_runtime.CurrentAttackerId} targetMask={_runtime.CurrentPartyTargetMask} windowActive={_runtime.ParryWindowActive}");
        }

        return _hMsSetDamageInternal.orig_fptr.Invoke(param_1, param_2, param_3, param_4, param_5);
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
