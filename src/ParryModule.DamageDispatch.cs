namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule
{
    /// <summary>
    /// Handles target selection and finalization around the native staged-damage dispatcher.
    /// Successful interception evidence is required before suppressing damage or resolving feedback.
    /// The 0x400 flag permits pending payload application as well as bank release.
    /// </summary>
    private int h_ms_set_damage(byte param_1, int param_2, int param_3)
    {
        // p2=target: snapshot HP when a parry tick deadline is active for this slot so we can restore
        // on impact. The hook is the authoritative impact detection path — it fires at native
        // damage time, before the poll path in on_pre_update. When the per-slot expiry is still
        // live and the attacker matches, resolve the parry directly from the hook to ensure
        // feedback (text + sound) fires at impact time rather than being deferred to the poll path.
        // Every native gate uses the same simulation deadline, including after resolution
        // stops the input-state countdown. Wall time must not expire a stalled simulation.
        bool isPartyTargetCall = param_2 >= 0 && param_2 < PartyActorCapacity;
        bool isActiveParry = isPartyTargetCall
            && _optionEnabled
            && is_parry_damage_window_open(param_1, param_2)
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

        bool anyExpiryActive = false;
        uint currentMask = _runtime.CurrentPartyTargetMask;
        for (int i = 0; i < PartyActorCapacity; i++)
        {
            if ((currentMask & (1u << i)) != 0 && is_parry_damage_window_open(param_1, i))
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
            && has_parry_action_marker(_runtime.LastParriedTargetMask, param_1, param_2);
        bool latePreOpenCommitted = isPartyTargetCall
            && is_late_preopen_p5_zero_commit(param_2, param_1)
            && has_live_enemy_damage_context_for_slot(param_1, param_2);

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
        bool skipOrigForParry = should_skip_set_damage_for_parry(param_2, parryTarget, alreadyParriedThisHit, latePreOpenCommitted);

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
                    // whether the death latch (chr+OffsetDeathLatch et al.) is already set
                    // at this point. See ExternalMemoryOffsetMap.ChrStruct for evidence.
                    byte*   chrB    = (byte*)parryTarget;
                    byte    dcc_pre = chrB[ExternalMemoryOffsetMap.ChrStruct.OffsetDeathLatch];
                    ushort  s606_pre = *(ushort*)(chrB + ExternalMemoryOffsetMap.ChrStruct.OffsetStatusBits);
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
                    // Evidence: LethalDiag PRE consistently shows OffsetDeathLatch=2 and
                    // OffsetStatusBits bit 0 set. MsGetChrStatDeath returns 1 when
                    // chr+OffsetDeathLatch != 0, gating all downstream death processing;
                    // bit 0 of chr+OffsetStatusBits is the dead-status bit ORed in by the
                    // same function. Clearing both here prevents death from winning despite
                    // the HP restore. See ExternalMemoryOffsetMap.ChrStruct.
                    chrB[ExternalMemoryOffsetMap.ChrStruct.OffsetDeathLatch] = 0;
                    *(ushort*)(chrB + ExternalMemoryOffsetMap.ChrStruct.OffsetStatusBits)
                        &= unchecked((ushort)~ExternalMemoryOffsetMap.ChrStruct.DeadStatusBitMask);

                    byte    dcc_post = chrB[ExternalMemoryOffsetMap.ChrStruct.OffsetDeathLatch];
                    ushort  s606_post = *(ushort*)(chrB + ExternalMemoryOffsetMap.ChrStruct.OffsetStatusBits);
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
            result = orig_ms_set_damage(param_1, param_2, param_3);
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
                // Branch on whether any targeted slot received a per-target MsSetDamage p2=slot
                // call before this p3=0x400 finalization. Attacks that go through the per-target
                // call path set SetDamageTargetFired; attacks that commit entirely through
                // MsSetDamageInternal p5=1024 (delayed finalization) do not.
                // When attacks with per-target calls arrive here, it means damage_hp was 0 at
                // p5=0 time (staging latency gap after reactive R1 press), so resolve_successful_parry
                // was skipped even though _internalInterceptedMask was set and HP is protected.
                // This distinction matches all currently observed attack paths. If a hybrid path
                // (per-target calls AND delayed p5=1024 commit) is discovered, revisit this branch.
                bool hasPerTargetSetDamageCalls = false;
                {
                    uint tmask = _runtime.CurrentPartyTargetMask;
                    for (int i = 0; i < PartyActorCapacity; i++)
                    {
                        if ((tmask & (1u << i)) != 0 && _attackTelemetry[i].SetDamageTargetFired)
                        {
                            hasPerTargetSetDamageCalls = true;
                            break;
                        }
                    }
                }

                if (!hasPerTargetSetDamageCalls)
                {
                    // Missing per-target notifications do not prove that calculation was blocked.
                    // Finalization feedback needs the same interception evidence as a physical hit.
                    Chr* party = _battleAdapter.GetPlayerCharacters();
                    if (party != null)
                    {
                        uint mask = _runtime.CurrentPartyTargetMask;
                        while (mask != 0)
                        {
                            int slot = BitOperations.TrailingZeroCount(mask);
                            mask &= mask - 1;
                            if ((_internalInterceptedMask & (1u << slot)) == 0
                                || _internalInterceptedAttackerId[slot] != param_1) continue;
                            Chr* candidate = party + slot;

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
                            }
                        }
                    }
                    end_parry_window("magic_finalization");
                }
                else
                {
                    // Per-target call path: SetDamageTargetFired was observed for at least one
                    // targeted slot. damage_hp was 0 at p5=0 time, so resolve_successful_parry
                    // was not called from h_ms_set_damage_internal. HP is protected via
                    // _internalInterceptedMask. Emit feedback for any slot where the interception
                    // evidence matches the current attacker and the parry has not already been
                    // acknowledged.
                    //
                    // Important: do NOT close the window on this branch unless this exact
                    // finalization pass actually resolves at least one parry.
                    // Some E11 physical follow-up sequences can reach p3=0x400 while the
                    // newly opened window has not yet reached a valid physical resolve path.
                    // Closing without a real resolution causes false early Open=>Waiting.
                    bool resolvedAtPhysicalFinalization = false;
                    Chr* party = _battleAdapter.GetPlayerCharacters();
                    if (party != null)
                    {
                        uint mask = _runtime.CurrentPartyTargetMask;
                        for (int i = 0; i < PartyActorCapacity; i++)
                        {
                            if ((mask & (1u << i)) == 0) continue;
                            bool intercepted = (_internalInterceptedMask & (1u << i)) != 0
                                && _internalInterceptedAttackerId[i] == (byte)param_1;
                            bool resolved = (_runtime.LastParriedTargetMask & (1u << i)) != 0;
                            if (!intercepted || resolved) continue;
                            Chr* candidate = party + i;
                            if (candidate->stat_exist_flag && !is_target_non_parryable(candidate))
                            {
                                resolve_successful_parry(i, candidate, "physical_finalization", closeWindow: false);
                                resolvedAtPhysicalFinalization = true;
                            }
                        }
                    }

                    if (resolvedAtPhysicalFinalization)
                    {
                        end_parry_window("physical_finalization");
                    }
                    else
                    {
                        log_debug($"Physical finalization deferred for {format_actor_slot(param_1)}: no resolved slot in this pass.");
                    }
                }
            }
        }

        log_hook_ms_set_damage(param_1, param_2, param_3, result);
        return result;
    }
}
