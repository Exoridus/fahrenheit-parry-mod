namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule
{
    private bool has_parry_action_marker(uint mask, int attacker, int target)
        => attacker == _runtime.CurrentAttackerId && (mask & (1u << target)) != 0;

    private bool should_skip_set_damage_for_parry(int target, Chr* parryTarget, bool alreadyParriedThisHit, bool latePreOpenCommitted)
        => parryTarget != null
            && _optionNegateDamage
            && !is_target_non_parryable(parryTarget)
            && (alreadyParriedThisHit
                || (!latePreOpenCommitted && is_parry_slot_window_open(target) && parryTarget->damage_hp > 0));

    /// <summary>
    /// Clears displayed damage and invalidates both banks matching this attacker and command.
    /// This discards all remaining payloads in those banks, not only the current payload.
    /// </summary>
    private void suppress_parried_damage_display(Chr* slot, int slotIndex, byte attackerId, byte commandId, string source)
    {
        if (slot == null || !slot->stat_exist_flag)
            return;

        int hpBefore = slot->damage_hp;
        int mpBefore = slot->damage_mp;
        int ctbBefore = slot->damage_ctb;

        negate_damage_on_impact(slot);

        byte* slotB = (byte*)slot;
        byte btl0AttBefore = slotB[0x776];
        byte btl0CmdBefore = slotB[0x777];
        byte btl1AttBefore = slotB[0xA4E];
        byte btl1CmdBefore = slotB[0xA4F];

        bool clearedBtl0 = false;
        bool clearedBtl1 = false;
        if (slotB[0x776] == attackerId && slotB[0x777] == commandId)
        {
            slotB[0x776] = 0xFF;
            clearedBtl0 = true;
        }

        if (slotB[0xA4E] == attackerId && slotB[0xA4F] == commandId)
        {
            slotB[0xA4E] = 0xFF;
            clearedBtl1 = true;
        }

        if (_optionLogging && (hpBefore != 0 || mpBefore != 0 || ctbBefore != 0 || clearedBtl0 || clearedBtl1))
        {
            write_session_hook_entry(
                $"[ParryUiSuppress] f={_debugFrameIndex} slot={slotIndex} src={source} atk={attackerId} cmd=0x{commandId:X2} " +
                $"dmg_hp={hpBefore}->{slot->damage_hp} dmg_mp={mpBefore}->{slot->damage_mp} dmg_ctb={ctbBefore}->{slot->damage_ctb} " +
                $"btl0={btl0AttBefore}/{btl0CmdBefore:X2}->{slotB[0x776]}/{slotB[0x777]:X2} " +
                $"btl1={btl1AttBefore}/{btl1CmdBefore:X2}->{slotB[0xA4E]}/{slotB[0xA4F]:X2}");
        }
    }

    private void clear_internal_intercepted_slot(int slotIndex)
    {
        if ((uint)slotIndex >= PartyActorCapacity)
            return;

        _internalInterceptedMask &= ~(1u << slotIndex);
        _internalInterceptedAttackerId[slotIndex] = 0;
    }

    private bool is_late_preopen_p5_zero_commit(int slotIndex, int attackerId)
    {
        if ((uint)slotIndex >= PartyActorCapacity)
            return false;

        return (_latePreOpenP5ZeroCommitMask & (1u << slotIndex)) != 0
            && _latePreOpenP5ZeroCommitAttackerId[slotIndex] == (byte)attackerId;
    }

    private void mark_late_preopen_p5_zero_commit(int slotIndex, int attackerId)
    {
        if ((uint)slotIndex >= PartyActorCapacity)
            return;

        _latePreOpenP5ZeroCommitMask |= 1u << slotIndex;
        _latePreOpenP5ZeroCommitAttackerId[slotIndex] = (byte)attackerId;
    }

    private void clear_late_preopen_p5_zero_commit_slot(int slotIndex)
    {
        if ((uint)slotIndex >= PartyActorCapacity)
            return;

        _latePreOpenP5ZeroCommitMask &= ~(1u << slotIndex);
        _latePreOpenP5ZeroCommitAttackerId[slotIndex] = 0;
    }

    private bool has_live_enemy_damage_context_for_slot(int attackerId, int slotIndex)
    {
        if ((uint)slotIndex >= PartyActorCapacity)
            return false;

        if (!_runtime.AwaitingTurnEnd)
            return false;

        if (attackerId != _runtime.CurrentAttackerId)
            return false;

        if ((_runtime.CurrentPartyTargetMask & (1u << slotIndex)) == 0)
            return false;

        if (!try_get_enemy_attack_cue(out AttackCue cue, out byte cueIndex, out _))
            return false;

        if (cueIndex != _runtime.CurrentCueIndex)
            return false;

        uint liveMask = extract_party_target_mask(cue);
        return (liveMask & (1u << slotIndex)) != 0;
    }

    /// <summary>
    /// Intercepts the per-target staged-damage application before HP, status and death work.
    /// Both ordinary and 0x400 calls can consume pending payloads; the latter also releases
    /// exhausted banks. Existing parry/dodge markers remain action-scoped.
    /// </summary>
    private int h_ms_set_damage_internal(int param_1, byte param_2, int param_3, int param_4, int param_5)
    {
        using var validation = begin_native_validation("commit", param_1, param_3, param_2, flags: param_5);

        bool isPartySlot = param_3 >= 0 && param_3 < PartyActorCapacity;

        // Phase 2: authoritative commit skip via durable impact marker or prior resolution.
        // - p5=0: skip if already resolved, OR if the parry window is currently active for this slot.
        // - p5=1024: skip if the durable impact marker is set (handles delayed-finalization).
        if (isPartySlot && _optionEnabled && _optionNegateDamage)
        {
            bool hasLiveEnemyContext = has_live_enemy_damage_context_for_slot(param_1, param_3);
            bool markerSet = has_parry_action_marker(_parryResolvedAtImpactMask, param_1, param_3);
            bool alreadyResolved = has_parry_action_marker(_runtime.LastParriedTargetMask, param_1, param_3);
            bool latePreOpenCommitted = is_late_preopen_p5_zero_commit(param_3, param_1)
                && hasLiveEnemyContext;

            bool isActiveParry = !alreadyResolved
                && is_parry_damage_window_open(param_1, param_3)
                && hasLiveEnemyContext;

            // Dodge: a valid dodge window from this attacker skips the ENTIRE p5=0 commit
            // (HP + status + death + the inner flinch/MsDamageSetMotion). This is what blocks a
            // dodged STATUS attack (e.g. Anfunkeln → Confuse) — status is applied earlier in this
            // same call, before MsDamageSetMotion, so the reactive motion-hook negation was too
            // late. The evade animation was already triggered on press, so skipping is clean.
            // Two ways in: the wall-clock window is still live, or this slot already resolved as
            // evaded earlier in this cue (durable marker). The marker is what carries a dodge
            // across a chargeup longer than the window, and across cue mutation mid-cast. Either
            // way the slot must also be in _dodgeArmedTargetMask (checked by MayResolveAtImpact):
            // the engine drives this p5=0 commit for every party slot, so the window/attacker gate
            // is cue-wide and would otherwise resolve an evade for slots the attack never targeted.
            bool dodgeWindowLive = _dodgeWindowActive
                && _runtime.CueFirstSeenFrame == _dodgeArmedCueFrame;
            bool dodgeMarkerSet = (_dodgeResolvedAtImpactMask & (1u << param_3)) != 0;

            if (param_5 == 0
                && DodgeCommitGate.MayResolveAtImpact(
                    _optionDodgeEnabled,
                    dodgeWindowLive || dodgeMarkerSet,
                    _dodgeArmedAttackerId,
                    (byte)param_1,
                    _dodgeArmedTargetMask,
                    param_3))
            {
                _dodgeEvadeCount++;
                mark_dodge_resolved(param_3);   // durable marker + perfect grade (idempotent per cue)
                _dodgeTextRemainingSeconds = ParriedTextSeconds;
                _dodgeTextSeed = next_label_seed();
                _dodgeTextTargetMask |= 1u << param_3;
                Chr* dodgeParty = _battleAdapter.GetPlayerCharacters();
                Chr* dodgeSlot = dodgeParty != null ? dodgeParty + param_3 : null;
                suppress_parried_damage_display(dodgeSlot, param_3, (byte)param_1, param_2, "dodge");
                log_debug($"MsSetDamageInternal p5=0 skipped for {format_actor_slot((byte)param_3)} (dodge — HP+status+death blocked).");
                if (validation is not null) validation.Completed = true;
                return 0;
            }

            if (param_5 == 0
                && !alreadyResolved
                && !isActiveParry
                && !latePreOpenCommitted
                && hasLiveEnemyContext)
            {
                Chr* partyForLate = _battleAdapter.GetPlayerCharacters();
                Chr* slotForLate = partyForLate != null ? partyForLate + param_3 : null;
                if (slotForLate != null && slotForLate->stat_exist_flag && slotForLate->damage_hp > 0)
                {
                    mark_late_preopen_p5_zero_commit(param_3, param_1);
                    write_session_hook_entry(
                        $"[MsSetDamageInternal] f={_debugFrameIndex} slot={param_3} p5=0 late_preopen=1 observed " +
                        $"attacker={param_1} cmd={param_2}");
                }
            }

            if (param_5 == 0 && (alreadyResolved || (isActiveParry && !latePreOpenCommitted)))
            {
                if (isActiveParry)
                {
                    // If h_ms_set_damage p2=target didn't resolve it (e.g. group attack p2=-5),
                    // resolve it here before skipping the native commit.
                    Chr* party = _battleAdapter.GetPlayerCharacters();
                    Chr* candidate = party != null ? party + param_3 : null;
                    if (candidate != null && candidate->stat_exist_flag && candidate->damage_hp > 0)
                    {
                        resolve_successful_parry(param_3, candidate, "internal_impact", closeWindow: false);
                    }
                }

                // p5=0 commit skip. Record attacker id so that the later p5=1024 skip can
                // require exact attacker match — preventing a different next attacker's p5=1024
                // from inheriting this slot's interception evidence if end_parry_window fires
                // between the two passes (e.g. cue mutation / "attacker changed").
                Chr* partyForUi = _battleAdapter.GetPlayerCharacters();
                Chr* slotForUi = partyForUi != null ? partyForUi + param_3 : null;
                suppress_parried_damage_display(slotForUi, param_3, (byte)param_1, param_2, "p5=0");

                _internalInterceptedMask |= 1u << param_3;
                _internalInterceptedAttackerId[param_3] = (byte)param_1;
                write_session_hook_entry(
                    $"[MsSetDamageInternal] f={_debugFrameIndex} slot={param_3} p5=0 resolved=1 -> SKIP " +
                    $"attacker={param_1} cmd={param_2}");
                log_debug($"MsSetDamageInternal p5=0 commit skipped for {format_actor_slot((byte)param_3)} (resolved).");

                if (validation is not null) validation.Completed = true;

                return 0;
            }

            // An earlier unblocked apply is not a parry or proof of a duplicate payload.
            // Finalization can consume another payload and must retain native bookkeeping.
            if (param_5 == 1024 && latePreOpenCommitted && !alreadyResolved && !markerSet)
                clear_late_preopen_p5_zero_commit_slot(param_3);

            // p5=1024 is the authoritative HP/death commit for delayed-finalization attacks
            // (Anfunkeln, Blitzra, Hauch). Three skip paths:
            //   markerSet     — durable marker set at MsDamageSetMotion visual-impact time
            //   alreadyResolved — LastParriedTargetMask set by a prior resolution call
            //   internalBlocked — p5=0 was intercepted for this slot, same attacker:
            //                     the window may have been closed by "attacker changed" between
            //                     p5=0 and p5=1024, but the attacker id must still match to
            //                     prevent a different next-attacker's commit from being skipped.
            bool internalBlocked =
                (_internalInterceptedMask & (1u << param_3)) != 0
                && _internalInterceptedAttackerId[param_3] == (byte)param_1;

            // Dodge finalization: skip the p5=1024 commit for a dodge-resolved slot WITHOUT
            // setting LastParriedTargetMask (that would draw a second "PARRIED" text over "DODGE").
            // Marker is NOT consumed here: a multi-hit / AoE swing from the armed attacker must stay
            // fully evaded, and its later hits commit through this same pass. It is cleared in
            // clear_awaiting_turn_end, next to its parry twin. The attacker check is what stops a
            // surviving marker from swallowing an unrelated attacker's commit.
            if (param_5 == 1024
                && DodgeCommitGate.ShouldSkipCommit(
                    _optionDodgeEnabled, dodgeMarkerSet, _dodgeArmedAttackerId, (byte)param_1))
            {
                Chr* dodgeFinParty = _battleAdapter.GetPlayerCharacters();
                Chr* dodgeFinSlot = dodgeFinParty != null ? dodgeFinParty + param_3 : null;
                suppress_parried_damage_display(dodgeFinSlot, param_3, (byte)param_1, param_2, "dodge_p5=1024");
                log_debug($"MsSetDamageInternal p5=1024 skipped for {format_actor_slot((byte)param_3)} (dodge).");
                if (validation is not null) validation.Completed = true;
                return 0;
            }

            if (param_5 == 1024 && (markerSet || alreadyResolved || internalBlocked))
            {
                if (markerSet)
                    _parryResolvedAtImpactMask &= ~(1u << param_3);
                _internalInterceptedMask |= 1u << param_3;
                _runtime.LastParriedTargetMask |= 1u << param_3;
                clear_late_preopen_p5_zero_commit_slot(param_3);

                string skipReason = markerSet ? "marker=1"
                    : alreadyResolved ? "resolved=1"
                    : $"internal=1 attacker={param_1}";
                write_session_hook_entry(
                    $"[MsSetDamageInternal] f={_debugFrameIndex} slot={param_3} p5=1024 {skipReason} -> SKIP " +
                    $"attacker={param_1} cmd={param_2}");
                log_debug($"MsSetDamageInternal p5=1024 commit skipped for {format_actor_slot((byte)param_3)} ({(markerSet ? "impact marker" : alreadyResolved ? "resolved" : "internal intercepted")}).");

                Chr* party = _battleAdapter.GetPlayerCharacters();
                Chr* candidate = party != null ? party + param_3 : null;
                suppress_parried_damage_display(candidate, param_3, (byte)param_1, param_2, "p5=1024");

                // Hit lifecycle boundary: this slot's p5=1024 completion was handled,
                // so stale p5=0 evidence must not leak into the next hit in the same turn.
                clear_internal_intercepted_slot(param_3);

                if (validation is not null) validation.Completed = true;

                return 0;
            }
        }

        // Status-leak trace: every party-slot pass that reaches orig. _MsAfterDamageProcess writes
        // status_suffer in the same block as the HP subtract, so any pass logged here with a dodge
        // that looked valid to the player is a slot where Petrify/Confuse gets through. The dodge
        // gates are wall-clock (_dodgeWindowActive); the parry ones are durable markers — this line
        // shows which of them was still standing when the commit landed.
        if (_optionLogging && isPartySlot)
        {
            Chr* traceParty = _battleAdapter.GetPlayerCharacters();
            Chr* traceSlot = traceParty != null ? traceParty + param_3 : null;
            if (traceSlot != null && traceSlot->stat_exist_flag)
            {
                write_session_hook_entry(
                    $"[StatusTrace] f={_debugFrameIndex} slot={param_3} p5={param_5} attacker={param_1} cmd={param_2} -> PASS_ORIG "
                    + $"dodge_win={(_dodgeWindowActive ? 1 : 0)} dodge_valid={(is_dodge_window_valid() ? 1 : 0)} "
                    + $"dodge_marker={((_dodgeResolvedAtImpactMask & (1u << param_3)) != 0 ? 1 : 0)} "
                    + $"parry_marker={((_parryResolvedAtImpactMask & (1u << param_3)) != 0 ? 1 : 0)} "
                    + $"resolved={((_runtime.LastParriedTargetMask & (1u << param_3)) != 0 ? 1 : 0)} "
                    + $"stone={traceSlot->stat_stone} suffer={traceSlot->ram.status_suffer}");
            }
        }

        if (_optionLogging && _runtime.ParryWindowActive)
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
                            $"0x606=0x{*(ushort*)(slotB + ExternalMemoryOffsetMap.ChrStruct.OffsetStatusBits):X4} " +
                            $"0xDCC={slotB[ExternalMemoryOffsetMap.ChrStruct.OffsetDeathLatch]} " +
                            $"btl0_att={slotB[0x776]} btl0_cmd=0x{slotB[0x777]:X2} " +
                            $"btl1_att={slotB[0xA4E]} btl1_cmd=0x{slotB[0xA4F]:X2}");
                    }
                }
            }

            // Raw params — only logged during an active parry window.
            write_session_hook_entry(
                $"[MsSetDamageInternal] f={_debugFrameIndex} p1={param_1} p2={param_2} p3={param_3} p4={param_4} p5={param_5} " +
                $"attacker={_runtime.CurrentAttackerId} targetMask={_runtime.CurrentPartyTargetMask} windowActive={_runtime.ParryWindowActive}");
        }

        int result = orig_ms_set_damage_internal(param_1, param_2, param_3, param_4, param_5);

        if (validation is not null) validation.Completed = true;

        return result;
    }
}
