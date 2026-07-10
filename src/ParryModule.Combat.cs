namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule
{
    private void monitor_damage_resolves()
    {
        if (!try_get_live_battle_context(out _))
        {
            Array.Clear(_damageEventActive);
            return;
        }

        Chr* party = _battleAdapter.GetPlayerCharacters();
        if (party == null)
        {
            Array.Clear(_damageEventActive);
            return;
        }

        for (int i = 0; i < PartyActorCapacity; i++)
        {
            Chr* chr = party + i;
            bool hasDamage = chr->damage_hp != 0 || chr->damage_mp != 0;
            if (hasDamage && !_damageEventActive[i])
            {
                _damageEventActive[i] = true;
                write_session_hook_entry($"[PollDetect] slot={i} damage_hp staged, hooks are authoritative");
            }
            else if (!hasDamage && _damageEventActive[i])
            {
                _damageEventActive[i] = false;
            }
        }
    }

    /// <summary>
    ///     Returns true if the target's current battle status prevents them from being parried.
    ///     KO, Petrification, Sleep, and Confusion are non-parryable: the target cannot react,
    ///     and the player should not be penalised for failing to parry on their behalf.
    /// </summary>
    private static bool has_confuse_status(Chr* target)
    {
        if (target == null || !target->stat_exist_flag) return false;
        if (target->ram.status_suffer.HasFlag(StatusPermanentFlags.CONFUSE)) return true;

        // Runtime fallback: battle traces show Confuse can be staged in the
        // status-bits half-word (chr+OffsetStatusBits, bit ConfuseStatusBitMask)
        // before status_suffer flags are fully reflected.
        ushort statusBits = *(ushort*)((byte*)target + ExternalMemoryOffsetMap.ChrStruct.OffsetStatusBits);
        return (statusBits & ExternalMemoryOffsetMap.ChrStruct.ConfuseStatusBitMask) != 0;
    }

    private static bool is_target_non_parryable(Chr* target)
    {
        if (target == null || !target->stat_exist_flag) return true;
        if (target->stat_death != 0) return true;                        // KO'd
        if (target->stat_stone != 0) return true;                        // Petrified
        if (target->ram.status_suffer_turns_left.sleep > 0) return true; // Sleeping
        if (has_confuse_status(target)) return true;                      // Confused
        if (target->ram.status_suffer.HasFlag(StatusPermanentFlags.BERSERK)) return true; // Berserk
        return false;
    }

    private static string get_non_parryable_label(Chr* target)
    {
        if (target->stat_death != 0) return "KO";
        if (target->stat_stone != 0) return "Petrified";
        if (target->ram.status_suffer_turns_left.sleep > 0) return "Sleeping";
        if (has_confuse_status(target)) return "Confused";
        if (target->ram.status_suffer.HasFlag(StatusPermanentFlags.BERSERK)) return "Berserk";
        return "Status";
    }

    private void on_impact_detected(int slotIndex, Chr* target, string source = "impact_poll")
    {
        if (!is_relevant_impact_slot(slotIndex))
        {
            return;
        }

        if (is_target_non_parryable(target))
        {
            string statusLabel = get_non_parryable_label(target);
            _runtime.StatusBlockTextRemainingSeconds = ParriedTextSeconds;
            _runtime.StatusBlockLabel = statusLabel;
            log_debug($"Impact on {format_actor_slot((byte)slotIndex)} — {statusLabel} (status block, parry skipped).");
            return;
        }

        if (!is_impact_correlated_to_active_action(out string correlationReason))
        {
            on_correlation_rejected((byte)slotIndex, source, correlationReason);
            return;
        }

        try_capture_current_impact_command_context(out byte attackerId, out int queueIndex, out ResolvedCommandInfo command);
        on_correlation_matched((byte)slotIndex, source, command);
        _turnRuntimeEvents.EmitDamageResolved(
            targetSlot: slotIndex,
            timestampLocal: current_gameplay_timestamp(),
            frameIndex: _debugFrameIndex,
            attackerId: attackerId,
            queueIndex: queueIndex,
            commandId: command.CommandId,
            commandLabel: command.Label,
            sourceStage: source);

        long cueToImpactFrames = _runtime.CueFirstSeenFrame > 0
            ? (long)(_debugFrameIndex - _runtime.CueFirstSeenFrame)
            : -1;
        long windowOpenToImpactFrames = _runtime.WindowOpenFrame > 0
            ? (long)(_debugFrameIndex - _runtime.WindowOpenFrame)
            : -1;
        float windowRemainingAtImpactMs = -1f;
        if (_runtime.WindowOpenFrame > 0 && _runtime.WindowDurationSecondsAtOpen > 0f)
        {
            float impactTimestampSeconds = (float)_simulationClockSeconds;
            float windowCloseTimestampSeconds = _runtime.WindowOpenTimestampSeconds + _runtime.WindowDurationSecondsAtOpen;
            windowRemainingAtImpactMs = (windowCloseTimestampSeconds - impactTimestampSeconds) * 1000f;
        }
        else if (_runtime.ParryWindowActive)
        {
            windowRemainingAtImpactMs = ParryDifficultyModel.TicksToMs(_runtime.ParryWindowRemainingTicks);
        }
        string timingTag = $"[cue+{cueToImpactFrames}F win+{windowOpenToImpactFrames}F rem={windowRemainingAtImpactMs:F0}ms]";

        if (_runtime.ParryWindowActive)
        {
            log_debug($"Parry window active at impact for {format_actor_slot((byte)slotIndex)}. {timingTag}");
            resolve_successful_parry(slotIndex, target, source);
            return;
        }

        // A valid dodge window handles this hit via the native evade (MsDamageSetMotion hook) —
        // it is not a parry miss, so suppress the failure feedback / missed-turn marking.
        if (is_dodge_window_valid())
        {
            // Durable resolution, mirroring the parry's LastParriedTargetMask: from here on this
            // slot counts as evaded for the rest of the cue, even after the wall-clock window
            // expires. Delayed-finalization attacks (Anfunkeln, Blitzra) commit HP *and status*
            // long after 350ms, and a wall-clock-only gate lets Petrify/Confuse through.
            mark_dodge_resolved(slotIndex);
            log_debug($"Dodge active at impact for {format_actor_slot((byte)slotIndex)} — evaded, not a parry miss. {timingTag}");
            return;
        }

        mark_active_turn_missed("impact outside active parry window");
        trigger_failure_feedback();
        _runtime.TurnImpactMissedSeen = true;
        _runtime.TurnImpactMissedAttackerId = _runtime.CurrentAttackerId;

        // Streak reset is performed per-cue at clear_awaiting_turn_end via
        // resolve_streak_at_cue_clear(); per-slot reset on hit was removed
        // because user-case "multi-target attack hits at least one char →
        // failed streak for ALL targeted slots" needs the cue-wide context
        // that only the cue-clear boundary has.

        log_debug($"Impact hit {format_actor_slot((byte)slotIndex)} outside parry window. {timingTag}");
    }

    private static void negate_damage_on_impact(Chr* chr)
    {
        // Clear staged display damage values. Actual HP protection is handled upstream
        // by h_ms_calc_damage returning 0 when a party slot has an active parry expiry
        // timestamp, preventing damage from entering the native pipeline.
        chr->damage_hp = 0;
        chr->damage_mp = 0;
        chr->damage_ctb = 0;
    }

    private bool is_relevant_impact_slot(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= PartyActorCapacity) return false;
        if (!_runtime.AwaitingTurnEnd) return false;

        uint mask = _runtime.CurrentPartyTargetMask;
        if (mask == 0) return false;
        uint bit = 1u << slotIndex;
        return (mask & bit) != 0;
    }

    private bool monitor_attack_cues()
    {
        bool hasCue = try_get_enemy_attack_cue(out AttackCue cue, out byte cueIndex, out Chr* attacker);
        if (!hasCue)
        {
            if (_runtime.AwaitingTurnEnd && should_emit_poll_consumed_signal())
            {
                _turnRuntimeEvents.EmitDispatchConsumed(
                    attackerId: _runtime.CurrentAttackerId,
                    queueIndex: _runtime.CurrentCueIndex,
                    timestampLocal: current_gameplay_timestamp(),
                    frameIndex: _debugFrameIndex,
                    reason: "cue list cleared");
                clear_awaiting_turn_end("Enemy action resolved; parry context cleared.");
            }

            return false;
        }

        uint partyMask = extract_party_target_mask(cue);
        bool actionable = partyMask != 0;
        if (!actionable)
        {
            return true;
        }

        bool changed =
            !_runtime.AwaitingTurnEnd
            || cue.attacker_id != _runtime.CurrentAttackerId
            || cueIndex != _runtime.CurrentCueIndex
            || partyMask != _runtime.CurrentPartyTargetMask;

        if (changed)
        {
            bool cueIdentityChanged =
                !_runtime.AwaitingTurnEnd
                || cue.attacker_id != _runtime.CurrentAttackerId
                || cueIndex != _runtime.CurrentCueIndex;

            // If the window was open for a previous attacker and a different attacker's cue
            // is now active, close the window before setting up the new context. A window
            // opened for E12's lingering post-hit cue must not carry over to E11's attack.
            if (_runtime.ParryWindowActive
                && _runtime.AwaitingTurnEnd
                && cue.attacker_id != _runtime.CurrentAttackerId)
            {
                end_parry_window("attacker changed");
            }

            // A different enemy action begins here while the turn context is still open (chained
            // cues never empty the list, so clear_awaiting_turn_end will not run between them).
            // Close out the outgoing action before the new cue overwrites CurrentPartyTargetMask,
            // which resolve_streak_at_cue_clear reads.
            if (cueIdentityChanged && _runtime.AwaitingTurnEnd)
            {
                end_enemy_action($"attacker {_runtime.CurrentAttackerId} -> {cue.attacker_id}");
            }

            if (_debugBattleActive && !_debugBattleSessionFirstCueSeen)
            {
                _debugBattleSessionFirstCueSeen = true;
                append_debug_event("Battle session detected.");
            }

            int commandCount = Math.Clamp((int)cue.command_count, 0, 4);
            _runtime.CurrentAttackerId = cue.attacker_id;
            _runtime.CurrentCueIndex = cueIndex;
            _runtime.CurrentPartyTargetMask = partyMask;
            _runtime.CurrentCueSignature = compute_command_signature(cue, commandCount);
            if (cueIdentityChanged || _runtime.CueFirstSeenFrame == 0)
            {
                _runtime.CueFirstSeenFrame = _debugFrameIndex;
            }
            _runtime.AwaitingTurnEnd = true;
            _runtime.ParryWindowSucceeded = false;
            _runtime.SuccessIndicatorActive = false;

            _turnRuntimeEvents.EmitDispatchStarted(
                attackerId: cue.attacker_id,
                queueIndex: cueIndex,
                timestampLocal: current_gameplay_timestamp(),
                frameIndex: _debugFrameIndex,
                parryWindowActive: _runtime.ParryWindowActive);

            string damageType = is_magic_like_attack(attacker) ? "Magic" : "Physical";
            string commandHint = format_command_hint(resolve_command_for_cue(_battleAdapter.GetBattle(), cueIndex, cue), maxLabelLength: 24);
            log_debug($"{format_actor_slot(cue.attacker_id)} {damageType} command{commandHint} active (q{cueIndex}), targets: {format_party_target_mask(partyMask)}.");

        }

        return true;
    }

    private bool try_get_enemy_attack_cue(out AttackCue cue, out byte cueIndex, out Chr* attacker)
    {
        cueIndex = 0;
        cue = default;
        attacker = null;

        if (!try_get_live_battle_context(out Btl* battle)) return false;

        int observedCues = battle->attack_cues_size;
        int totalCues = Math.Clamp(observedCues, 0, MaxAttackCueScan);
        if (totalCues <= 0) return false;

        if (observedCues > totalCues && !_runtime.AttackCueClampWarned)
        {
            _logger.Warning($"[Parry] attack_cues_size was {observedCues}; clamping scan to {totalCues} for safety.");
            _runtime.AttackCueClampWarned = true;
        }

        int fallbackIndex = -1;
        AttackCue fallbackCue = default;
        Chr* fallbackChr = null;

        for (int i = 0; i < totalCues; i++)
        {
            AttackCue candidate = battle->attack_cues[i];
            Chr* candidateChr = try_get_chr(candidate.attacker_id);
            if (!should_flag_as_enemy(candidate.attacker_id, candidateChr))
                continue;

            if (fallbackIndex < 0)
            {
                fallbackIndex = i;
                fallbackCue = candidate;
                fallbackChr = candidateChr;
            }

            if (extract_party_target_mask(candidate) != 0)
            {
                cueIndex = (byte)i;
                cue = candidate;
                attacker = candidateChr;
                return true;
            }
        }

        if (fallbackIndex >= 0)
        {
            cueIndex = (byte)fallbackIndex;
            cue = fallbackCue;
            attacker = fallbackChr;
            return true;
        }

        return false;
    }

    private static uint extract_party_target_mask(AttackCue cue)
    {
        uint mask = 0;
        int commandCount = Math.Clamp((int)cue.command_count, 0, 4);
        for (int i = 0; i < commandCount; i++)
        {
            ref AttackCommandInfo info = ref cue.command_list[i];
            mask |= info.targets & PlayerTargetMask;
        }

        return mask;
    }

    private uint filter_parryable_party_target_mask(uint partyMask)
    {
        if (partyMask == 0) return 0;

        Chr* party = _battleAdapter.GetPlayerCharacters();
        if (party == null) return 0;

        uint filtered = 0;
        uint mask = partyMask;
        while (mask != 0)
        {
            int slot = BitOperations.TrailingZeroCount(mask);
            mask &= mask - 1;

            Chr* candidate = party + slot;
            if (candidate->stat_exist_flag && !is_target_non_parryable(candidate))
                filtered |= 1u << slot;
        }

        return filtered;
    }

    private ParryInputContext capture_parry_input_context()
    {
        if (!try_get_parryable_enemy_cue(out AttackCue cue, out byte cueIndex, out _, out uint partyMask))
        {
            return ParryInputContext.None;
        }

        return new ParryInputContext(
            hasParryableCue: true,
            cue: cue,
            cueIndex: cueIndex,
            partyMask: partyMask);
    }

    private void handle_parry_input_press(ParryInputContext context)
    {
        // Decision is pure (FINAL_PARRY_SPEC.md, see ParryInputStateTransitions).
        // The corresponding tests live in tests/Parry.Tests/ParryInputStateTransitionsTests.cs.
        ParryInputStateTransitions.PressDecision decision =
            ParryInputStateTransitions.DecidePress(_runtime.InputState, context.HasParryableCue);

        if (!decision.Accepted)
        {
            log_press_rejection(decision.RejectReason);
            return;
        }

        AttackCue cue = context.Cue;
        byte cueIndex = context.CueIndex;
        uint partyMask = context.PartyMask;

        transition_to_open(cue, cueIndex, partyMask);
    }

    // Dodge (L1): arm an evade window for the current incoming attack. Unlike parry, this does
    // not drive the parry state machine and never feeds the streak/counter path — so a dodge
    // triggers NO counterattack.
    //
    // The hit is negated in h_ms_set_damage_internal, which skips the whole native commit
    // (HP + status + death). h_ms_dmg_calc_check_hit is NOT involved: it forces MISS->HIT to
    // disable native PC evasion, and there is no force-miss path anywhere in this mod.
    // The step-out (move-mode 0x425 + motion 0xC) is driven by us, on press, right below.
    private void handle_dodge_input_press(ParryInputContext context)
    {
        if (!context.HasParryableCue)
        {
            if (_optionLogging)
            {
                log_debug("[Dodge] Ignored — no parryable incoming attack.");
            }
            return;
        }

        // Move-state whiffout (primary pacing): don't start a new step-out while a targeted char is
        // still in the evade move (Chr 0x415 != 0: 0x09 = stepping out, 0x01 = walking back). Waits
        // until the char has returned home — this paces multi-press to the actual move duration and
        // prevents stacking ever further backward on repeated presses (whiff OR success).
        Chr* moveStateParty = _battleAdapter.GetPlayerCharacters();
        for (int slot = 0; slot < PartyActorCapacity; slot++)
        {
            if ((context.PartyMask & (1u << slot)) == 0) continue;
            Chr* c = moveStateParty != null ? moveStateParty + slot : null;
            if (c != null && c->stat_exist_flag && ((byte*)c)[ChrEvadeMovePhaseOffset] != 0)
            {
                if (_optionLogging)
                {
                    log_debug($"[Dodge] Ignored — {format_actor_slot((byte)slot)} still mid-evade (0x415={((byte*)c)[ChrEvadeMovePhaseOffset]:X2}).");
                }
                return;
            }
        }

        // (Re)arm the negation window so a valid press keeps the dodge live until the hit lands.
        _dodgeWindowActive = true;
        _dodgeArmedAttackerId = context.Cue.attacker_id;
        _dodgeArmedCueFrame = _runtime.CueFirstSeenFrame;
        // Snapshot exactly the slots that receive the step-out below (context.PartyMask, the cue's
        // filtered parryable target mask). A fresh press re-arms this alongside the other
        // _dodgeArmed* fields, so it always tracks the current cue's targets — never an untargeted
        // slot. (_runtime.CurrentPartyTargetMask is the raw, unfiltered mask and is maintained by
        // the parry-path cue tracking, not this dodge path; context.PartyMask is the authoritative
        // in-hand set here and matches the step-out loop precisely.)
        _dodgeArmedTargetMask = context.PartyMask;
        _dodgeWindowRemainingTicks = ParryDifficultyModel.GetDodgeWindowTicks(_optionDifficulty);

        // Step-out for each targeted PC — WITHOUT MsDamageSetMotion: set only the avoid move-mode
        // (Chr+0x425, what FUN_0078f090 sets) + play the evade animation (motion 0xC). This skips
        // the hit-terminate flag field_0x433 that MsDamageSetMotion's case 1 also sets, which is
        // read by battle-update logic and, poked out-of-band during a multi-phase cast chargeup,
        // desyncs the enemy action → soft-lock. Minimal + re-pressable: each press steps further.
        Chr* party = _battleAdapter.GetPlayerCharacters();
        for (int slot = 0; slot < PartyActorCapacity; slot++)
        {
            if ((context.PartyMask & (1u << slot)) == 0) continue;
            Chr* chr = party != null ? party + slot : null;
            if (chr == null || !chr->stat_exist_flag) continue;
            // Motion 0xC drives BOTH the evade animation AND the displacement — the move-mode-only
            // experiment produced no animation and no move, so we keep the motion. (Async via
            // motion-suppression is a dead end.)
            // Orientation: motion 0xC steps out away from Chr+0xdef, not from anything MsSetMotion
            // is passed. MsDamageSetMotion case 1 refreshes it from the attacker right before the
            // motion call; without this the step-out aims at whoever attacked last.
            ((byte*)chr)[ChrLastAttackerIdOffset] = context.Cue.attacker_id;
            // A press must react NOW, not wait for the previous animation. But re-issuing MsSetMotion
            // on a live script only restarts it and leaves the flags half-torn, which is what let the
            // old dodge-spam keep an actor permanently "animating". So end the previous motion through
            // the engine's own path first, then start a fresh one.
            try_end_battle_motion(slot, "press_restart");

            ((byte*)chr)[ChrEvadeMoveModeOffset] = 1;
            // 3rd arg 0 = non-blocking: do not hold Chr+0x432 ourselves. The ATEL worker sets it while
            // the motion actually plays, and we now terminate deterministically, so holding it would
            // only widen the window in which other actors wait on us.
            FhUtil.get_fptr<MsSetMotionProbe>(
                ExternalMemoryOffsetMap.Functions.MsSetMotion)(slot, EvadeMotionId, 0, 0, 1, 0, 0);
            _dodgeProbeSlotsMask |= 1u << slot;
            if (_optionLogging)
            {
                log_debug($"[Dodge] Step-out (attacker 0x{context.Cue.attacker_id:X2} → 0xdef, move-mode 0x425 + motion 0x{EvadeMotionId:X}) for {format_actor_slot((byte)slot)}.");
            }
        }

        _dodgeProbeFramesLeft = 40;
    }

    // After a step-out, log Chr+0x415/0x425/0x4AC (move-mode / avoid / motion-type) + world
    // position each frame for a short window — captures how the move-mode evolves and how far the
    // char actually travels, to find a move distance/speed knob for a bigger jump. Debug-only.
    private void tick_dodge_field_probe()
    {
        if (_dodgeProbeFramesLeft <= 0 || _dodgeProbeSlotsMask == 0) return;
        _dodgeProbeFramesLeft--;

        if (_optionLogging)
        {
            uint mask = _dodgeProbeSlotsMask;
            while (mask != 0)
            {
                int slot = BitOperations.TrailingZeroCount(mask);
                mask &= mask - 1;
                Chr* chr = try_get_chr((byte)slot);
                if (chr == null) continue;
                byte* b = (byte*)chr;
                byte f415 = b[0x415];
                byte f425 = b[0x425];
                uint f4AC = *(uint*)(b + 0x4AC);

                // The motion-system flags, so we can stop guessing which one our non-blocking
                // MsSetMotion actually sets. 0x432 = motion-active, 0x433 = hit-reaction pending
                // (the global barrier), 0x3f3 = motion-disable (gates MsEffectResetMotionDisable,
                // and is 0 in every sample so far), 0xdf2 = the motion request the *blocking*
                // MsSetMotion variant writes.
                byte f432 = b[0x432];
                byte f433 = b[0x433];
                byte f3F3 = b[0x3f3];
                byte fDF2 = b[0xdf2];

                float px = chr->actor != null ? chr->actor->chr_pos_vec.X : 0f;
                float pz = chr->actor != null ? chr->actor->chr_pos_vec.Z : 0f;
                log_debug($"[EvadeFields] {format_actor_slot((byte)slot)} 0x415={f415:X2} 0x425={f425:X2} 0x4AC={f4AC:X8} " +
                          $"0x432={f432:X2} 0x433={f433:X2} 0x3f3={f3F3:X2} 0xdf2={fDF2:X2} pos=({px:F2},{pz:F2})");
            }
        }

        if (_dodgeProbeFramesLeft <= 0) _dodgeProbeSlotsMask = 0;
    }

    // True while a dodge window armed by L1 is live AND the current incoming attack is from the
    // armed attacker. Checked at impact (MsDamageSetMotion hook) and in on_impact_detected to
    // route the hit to the native evade instead of a parry/miss. Not consumed on use — the
    // window expires by time — so a multi-hit / AoE swing from that attacker is fully evaded.
    private bool is_dodge_window_valid()
        => _optionDodgeEnabled
        && _dodgeWindowActive
        && _dodgeArmedAttackerId == _runtime.CurrentAttackerId
        && _runtime.CueFirstSeenFrame == _dodgeArmedCueFrame;

    private void log_press_rejection(string reason)
    {
        // Map the pure reason identifier from ParryInputStateTransitions to the
        // user-facing log message. Centralised so the message text is the only
        // localisation/UX concern; the underlying decision is testable.
        switch (reason)
        {
            case "no_parryable_cue":
                log_debug("Parry input ignored (no parryable enemy cue).");
                return;
            case "window_already_open":
                log_debug("Parry input ignored — window already open.");
                return;
            case "current_attack_already_parried":
                log_debug("Parry input ignored — current attack already parried.");
                return;
            case "in_guard_recovery":
                float lockoutRemainingMs = ParryDifficultyModel.TicksToMs(_runtime.WhiffLockoutRemainingTicks);
                log_debug($"Parry input rejected — in guard recovery ({lockoutRemainingMs:F0}ms remaining).");
                return;
            default:
                log_debug($"Parry input rejected ({reason}).");
                return;
        }
    }

    private int parry_window_ticks()
    {
        return ParryDifficultyModel.GetParryWindowTicks(_optionDifficulty);
    }

    // Both windows open on the press, and the parry window is the tighter one. A dodge is
    // "perfect" when the press-to-hit time is at most one parry window long — pressed late
    // and precisely, not early and hopefully. When the dodge window itself is narrower than
    // the parry window, every hit inside it is necessarily within one parry window of the
    // press, so grading it perfect is correct, not a bug: there is no shorter interval left
    // for a "non-perfect" dodge to land in.
    private bool is_perfect_dodge()
    {
        if (!_dodgeWindowActive) return false;

        int pressToHitTicks = ParryDifficultyModel.GetDodgeWindowTicks(_optionDifficulty) - _dodgeWindowRemainingTicks;
        return pressToHitTicks <= parry_window_ticks();
    }

    // Single entry point for "this slot has evaded". Idempotent per cue: the durable marker gates
    // the commit passes, and the perfect grade is awarded exactly once, at the first impact —
    // while _dodgeWindowRemainingTicks still carries the press-to-hit timing.
    //
    // A perfect dodge grades PERFECT and takes the gold label, but grants no overdrive charge and no
    // counter. Overdrive is the parry's reward alone: evading a hit removes it from the fight, while
    // parrying answers it. Only the parry feeds the custom overdrive mode's learn counter.
    private void mark_dodge_resolved(int slotIndex)
    {
        uint bit = 1u << slotIndex;
        bool firstImpact = (_dodgeResolvedAtImpactMask & bit) == 0;
        _dodgeResolvedAtImpactMask |= bit;
        if (!firstImpact) return;

        // The hit is evaded: end the evade animation now instead of waiting for it to run out. The
        // move machine (Chr+0x415) keeps running, so the character still slides home while whatever
        // was waiting on this actor's motion can proceed.
        try_end_battle_motion(slotIndex, "dodge_hit");

        if (is_perfect_dodge())
        {
            // A perfect dodge is still a DODGE: the hit is avoided, not met. All it shares with a
            // parry is the *window* — is_perfect_dodge() tests the same difficulty-scaled parry
            // timing — so the player can see that a parry press would have landed, and ramp up from
            // the looser dodge window. It gets no impact feedback (no screen shake) and no parry
            // consequences: the overdrive charge and the counter both bind to a real parry.
            _dodgeTextPerfectMask |= bit;
            log_debug($"Perfect dodge for {format_actor_slot((byte)slotIndex)} (inside the parry window).");
        }
    }

    private int whiff_lockout_ticks()
    {
        if (!_optionWhiffLockout) return 0;
        return ParryDifficultyModel.GetWhiffLockoutTicks(_optionDifficulty);
    }

    private void transition_to_open(AttackCue cue, byte cueIndex, uint partyMask)
    {
        int windowDurationTicks = parry_window_ticks();
        // Seconds are derived for the wall-clock per-slot expiry (read by the damage hooks, which
        // fire outside PreUpdate) and for display/telemetry only — the window itself counts ticks.
        float windowDurationSeconds = ParryDifficultyModel.TicksToSeconds(windowDurationTicks);

        _runtime.InputState = ParryInputState.Open;
        _runtime.AwaitingTurnEnd = true;
        _runtime.CurrentAttackerId = cue.attacker_id;
        _runtime.CurrentCueIndex = cueIndex;
        _runtime.CurrentPartyTargetMask = partyMask;
        _runtime.ParryWindowActive = true;
        _runtime.ParryWindowRemainingTicks = windowDurationTicks;
        _runtime.ParryWindowElapsedTicks = 0;
        _runtime.ParryWindowSucceeded = false;
        _runtime.SuccessIndicatorActive = false;
        if (_runtime.TurnImpactMissedSeen && _runtime.TurnImpactMissedAttackerId != _runtime.CurrentAttackerId)
            _runtime.TurnImpactMissedSeen = false;
        _runtime.ParriedTextRemainingSeconds = 0f;
        _runtime.WindowOpenFrame = _debugFrameIndex;
        _runtime.WindowOpenTimestampSeconds = (float)_simulationClockSeconds;
        _runtime.WindowDurationSecondsAtOpen = windowDurationSeconds;

        mark_active_turn_open();

        long expiry = DateTime.UtcNow.Ticks + (long)(windowDurationSeconds * TimeSpan.TicksPerSecond);
        for (int i = 0; i < PartyActorCapacity; i++)
        {
            if ((partyMask & (1u << i)) != 0)
            {
                _parryExpiry[i] = expiry;
                _parryArmedAttackerId[i] = _runtime.CurrentAttackerId;
            }
        }

        // Guard-stance feedback marker: the player has committed to the parry
        // stance for this window. The "enter guard stance" visual is approximated
        // here via the overlay HUD state ("Open") and log entry; real native
        // motion wiring (MsSetMotion for party guard) is a future enhancement.
        float windowMs = windowDurationSeconds * 1000f;
        log_debug($"Parry input armed for {format_actor_slot(cue.attacker_id)} (q{cueIndex}) — guard stance, {windowMs:F0}ms window [{ParryDifficultyModel.FormatName(_optionDifficulty)}].");
    }

    private void transition_to_whiff_lockout()
    {
        string attackerLabel = format_actor_slot(_runtime.CurrentAttackerId);
        int lockoutTicks = whiff_lockout_ticks();

        // Streak reset is performed per-cue at clear_awaiting_turn_end via
        // resolve_streak_at_cue_clear(). A whiff is a cue-wide failure for
        // every slot in the target mask; that handler covers it.

        // Clear the open-window arrays (per-slot expiry, telemetry, feedback pending)
        // so hooks no longer treat this window as live. The InputState flip that
        // follows is what gates further R1 presses — not the array values.
        end_parry_window("whiff_lockout", transitionToReady: false);

        // Pure decision — see ParryInputStateTransitions tests. A positive tick count means a
        // lockout is armed (the same > 0 semantics the float overload uses).
        ParryInputState nextState = ParryInputStateTransitions.DecideWindowExpiry(lockoutTicks);

        if (nextState == ParryInputState.WhiffLockout)
        {
            _runtime.InputState = ParryInputState.WhiffLockout;
            _runtime.WhiffLockoutRemainingTicks = lockoutTicks;
            _runtime.WhiffLockoutTotalTicks = lockoutTicks;
            mark_active_turn_missed("parry window expired without a hit");
            trigger_failure_feedback();
            log_debug($"Parry whiff ({attackerLabel}) — returning to normal stance, {ParryDifficultyModel.TicksToMs(lockoutTicks):F0}ms recovery.");
        }
        else
        {
            // Lockout disabled — transition straight back to Ready with no recovery.
            _runtime.InputState = ParryInputState.Ready;
            _runtime.WhiffLockoutRemainingTicks = 0;
            _runtime.WhiffLockoutTotalTicks = 0;
            mark_active_turn_missed("parry window expired without a hit");
            trigger_failure_feedback();
            log_debug($"Parry whiff ({attackerLabel}) — lockout disabled, immediately ready.");
        }
    }

    private void transition_whiff_lockout_to_ready()
    {
        _runtime.WhiffLockoutRemainingTicks = 0;
        _runtime.WhiffLockoutTotalTicks = 0;
        _runtime.InputState = ParryInputState.Ready;
        log_debug("Guard recovery complete — ready for next parry.");
    }

    private bool try_get_parryable_enemy_cue(out AttackCue cue, out byte cueIndex, out Chr* attacker, out uint partyMask)
    {
        partyMask = 0;
        if (!try_get_enemy_attack_cue(out cue, out cueIndex, out attacker))
        {
            return false;
        }

        partyMask = filter_parryable_party_target_mask(extract_party_target_mask(cue));
        return partyMask != 0;
    }

    private static bool is_magic_like_attack(Chr* attacker)
    {
        if (attacker == null) return false;
        byte commandType = attacker->stat_command_type;
        return commandType >= 1;
    }

    private Chr* try_get_chr(byte slotIndex)
    {
        Chr* party = _battleAdapter.GetPlayerCharacters();
        Chr* enemies = _battleAdapter.GetMonsterCharacters();

        if (party != null && slotIndex < PartyActorCapacity)
        {
            Chr* chr = party + slotIndex;
            return chr->stat_exist_flag ? chr : null;
        }

        int enemyIdx = slotIndex - PartyActorCapacity;
        if (enemies != null && enemyIdx >= 0 && enemyIdx < EnemyActorCapacity)
        {
            Chr* chr = enemies + enemyIdx;
            return chr->stat_exist_flag ? chr : null;
        }

        return null;
    }

    private static bool should_flag_as_enemy(byte slotIndex, Chr* chr)
    {
        // Party-member attackers (slot < PartyActorCapacity, stat_group == 0) must never be
        // flagged as enemies, even when confused. Confused party attacks are friendly fire
        // and must not arm parry windows, resolve parries, restore HP, or grant overdrive.
        if (chr != null)
        {
            if (chr->stat_group != 0) return true;
            if (!chr->stat_exist_flag || chr->ram.hp <= 0) return slotIndex >= PartyActorCapacity;
        }

        return slotIndex >= PartyActorCapacity;
    }

    private void flush_attack_telemetry(string reason)
    {
        for (int i = 0; i < PartyActorCapacity; i++)
        {
            ref AttackTelemetry t = ref _attackTelemetry[i];
            bool anyActivity = t.CalcDamageFired || t.SetMotionFired || t.SetDamageTargetFired
                || t.HpBeforeFinalization != 0 || t.HpAfterFinalization != 0;
            if (!anyActivity) continue;

            int hpDelta = (int)t.HpAfterFinalization - (int)t.HpBeforeFinalization;

            // Emit a user-visible "Hit taken" event when HP dropped and the attack was not parried.
            if (hpDelta < 0 && !t.CalcDamageIntercepted && t.HpBeforeFinalization != 0)
            {
                string targetSlotLabel = format_actor_slot((byte)i);
                log_debug($"Hit taken: {targetSlotLabel} | HP {t.HpBeforeFinalization} → {t.HpAfterFinalization} (−{-hpDelta})");
            }

            if (!_optionLogging) continue;

            bool hpChanged = t.HpAfterFinalization < t.HpBeforeFinalization;
            string leakTag;
            if (t.CalcDamageIntercepted)
                leakTag = hpChanged ? "LEAK=YES" : "LEAK=NO";
            else if (!t.CalcDamageFired && hpChanged)
                leakTag = "BYPASSED";
            else
                leakTag = "-";

            string cmdStr = t.CommandId != 0 ? $"0x{t.CommandId:X4}" : "-";
            write_session_hook_entry(
                $"[AttackTelemetry] reason={reason} slot={i} cmd={cmdStr}" +
                $" calc={(t.CalcDamageFired ? "Y" : "N")}" +
                $" intercepted={(t.CalcDamageIntercepted ? "Y" : "N")}" +
                $" setmotion={(t.SetMotionFired ? "Y" : "N")}" +
                $" settarget={(t.SetDamageTargetFired ? "Y" : "N")}" +
                $" hp_before={t.HpBeforeFinalization}" +
                $" hp_after={t.HpAfterFinalization}" +
                $" hp_delta={hpDelta:+#;-#;0}" +
                $" {leakTag}");
        }

        Array.Clear(_attackTelemetry);
    }

    /// <summary>
    ///     Tears down the open parry window's per-slot state (expiry, feedback pending,
    ///     telemetry, snapshots). Does NOT own the input-state machine transition:
    ///     callers decide whether to flip to <see cref="ParryInputState.Ready"/> or
    ///     stay in a different state (e.g. <see cref="ParryInputState.WhiffLockout"/>).
    /// </summary>
    private void end_parry_window(string reason, bool transitionToReady = true)
    {
        if (_runtime.ParryWindowActive)
            log_debug($"Parry window closed for {format_actor_slot(_runtime.CurrentAttackerId)} ({reason}).");

        flush_attack_telemetry(reason);
        Array.Clear(_parryExpiry);
        Array.Clear(_parryArmedAttackerId);
        Array.Clear(_parryFeedbackPending);
        // _internalInterceptedMask intentionally NOT cleared globally here.
        // Slot evidence is cleared at the per-hit p5=1024 boundary in h_ms_set_damage_internal.
        // Keeping the global mask untouched here preserves any other slot that still has an
        // in-flight delayed finalization.
        _latePreOpenP5ZeroCommitMask = 0;
        Array.Clear(_latePreOpenP5ZeroCommitAttackerId);
        _parryResolvedAtImpactMask = 0;
        Array.Clear(_preHitHpSnapshot);
        _runtime.ParryWindowActive = false;
        _runtime.ParryWindowRemainingTicks = 0;
        _runtime.ParryWindowElapsedTicks = 0;
        _runtime.ParryWindowSucceeded = false;
        _runtime.SuccessIndicatorActive = false;

        // LastParriedTargetMask is deliberately NOT cleared here. It is the durable, per-action-window
        // record of which slots parried, and it outlives the window: clear_awaiting_turn_end reads it
        // to run the overdrive learn countdown, and the PARRIED overlay reads it to know whom to label.
        // resolve_successful_parry sets the bit and then calls us with closeWindow: true — so clearing
        // it here wiped the very evidence the parry had just produced. That is why 8 resolved parries
        // in a battle produced 0 overdrive decrements. It is cleared at cue-clear (after the read) and
        // by reset_runtime_state.

        if (transitionToReady && _runtime.InputState != ParryInputState.WhiffLockout)
        {
            _runtime.InputState = ParryInputState.Ready;
        }
    }

    /// <summary>
    ///     Ends one enemy ACTION (cue) without ending the turn context.
    ///
    ///     clear_awaiting_turn_end only runs when the cue LIST empties. When several enemies
    ///     act back-to-back their cues chain inside one AwaitingTurnEnd span, so nothing reset
    ///     the per-action parry state between them: LastParriedTargetMask kept a slot's bit from
    ///     an earlier attacker, and every later hit on that slot was silently skipped as
    ///     "already resolved" — damage negated, no PARRIED text, no sound, no effect. The same
    ///     stale bit made resolve_streak_at_cue_clear see failedMask == 0 for a cue the slot
    ///     never parried, inflating the streak until the counter-attack fired.
    ///
    ///     Order matters: resolve streak and overdrive learning FIRST (both read the outgoing
    ///     cue's CurrentPartyTargetMask and LastParriedTargetMask), only then clear. This is the
    ///     same read-before-clear invariant clear_awaiting_turn_end relies on.
    /// </summary>
    private void end_enemy_action(string reason)
    {
        resolve_streak_at_cue_clear();
        resolve_overdrive_learning_at_cue_clear(_runtime.LastParriedTargetMask);

        _runtime.LastParriedTargetMask = 0;
        _parryResolvedAtImpactMask = 0;
        _dodgeResolvedAtImpactMask = 0;
        Array.Clear(_preHitHpSnapshot);

        log_debug($"Enemy action ended ({reason}) — per-action parry state cleared.");
    }

    private void clear_awaiting_turn_end(string reason)
    {
        // Resolve streak BEFORE clearing the per-cue masks below — needs both
        // CurrentPartyTargetMask (slots targeted this cue) and LastParriedTargetMask
        // (slots that successfully parried at least one hit).
        resolve_streak_at_cue_clear();

        // Count successful parries toward the custom-overdrive learn countdown, also
        // BEFORE the masks are cleared. Reads LastParriedTargetMask (the slots that
        // parried at least one hit this action window), so a multi-hit attack counts
        // exactly once per character.
        resolve_overdrive_learning_at_cue_clear(_runtime.LastParriedTargetMask);

        flush_attack_telemetry(reason);
        _runtime.AwaitingTurnEnd = false;
        Array.Clear(_parryExpiry);
        Array.Clear(_parryArmedAttackerId);
        Array.Clear(_parryFeedbackPending);
        _internalInterceptedMask = 0;
        Array.Clear(_internalInterceptedAttackerId);
        _latePreOpenP5ZeroCommitMask = 0;
        Array.Clear(_latePreOpenP5ZeroCommitAttackerId);
        _parryResolvedAtImpactMask = 0;
        _dodgeResolvedAtImpactMask = 0;
        _dodgeArmedTargetMask = 0;
        Array.Clear(_preHitHpSnapshot);
        if (_runtime.ParryWindowActive)
        {
            // Turn context ended; silently cancel any lingering open window.
            _runtime.ParryWindowActive = false;
            _runtime.ParryWindowRemainingTicks = 0;
            _runtime.ParryWindowElapsedTicks = 0;
        }

        _runtime.ParryWindowSucceeded = false;
        _runtime.SuccessIndicatorActive = false;
        _runtime.CurrentAttackerId = 0;
        _runtime.CurrentPartyTargetMask = 0;
        _runtime.LastParriedTargetMask = 0;
        _runtime.CurrentCueSignature = 0;
        _runtime.CueFirstSeenFrame = 0;
        _runtime.WindowOpenFrame = 0;
        _runtime.WindowOpenTimestampSeconds = 0f;
        _runtime.WindowDurationSecondsAtOpen = 0f;
        _runtime.TurnImpactMissedSeen = false;
        _runtime.TurnImpactMissedAttackerId = 0;

        // Turn boundaries do not break an active guard recovery: the player committed
        // to the stance and the approximated recovery animation still has to finish.
        // Only Open / Resolved states collapse back to Ready here.
        if (_runtime.InputState == ParryInputState.Open || _runtime.InputState == ParryInputState.Resolved)
        {
            _runtime.InputState = ParryInputState.Ready;
        }

        log_debug(reason);
    }

    private void trigger_failure_feedback()
    {
        _runtime.ParriedTextRemainingSeconds = 0f;
        _runtime.ParryMissedTextRemainingSeconds = ParryMissedTextSeconds;
    }

    /// <summary>
    ///     Per-cue streak resolution. Called once at cue-clear time (i.e. when the
    ///     enemy's turn fully resolves), BEFORE the per-cue masks are cleared.
    ///
    ///     Implements the case-handling spec from the parry roadmap:
    ///       - Multi-target attack hits at least one targeted slot
    ///         → streak failure for ALL targeted slots in this cue
    ///         (a partial-defense doesn't reward anyone — the team failed).
    ///       - All targeted slots successfully parried at least once
    ///         → streak +1 for each targeted slot.
    ///       - Random-target attack collapses to a single slot (others not in mask)
    ///         → only the single targeted slot is considered.
    ///       - Random-target attack hits 2 of 3 chars
    ///         → both targeted slots resolve via the multi-target rule above.
    ///
    ///     When a slot's streak crosses <see cref="ParryStreakObserveThreshold"/>,
    ///     the counter-attack queue marker fires (currently log-only; native
    ///     command insertion path is the next implementation step). The
    ///     consuming slot's streak resets to 0 after the counter is queued so
    ///     the chain restarts from zero.
    /// </summary>
    private void resolve_streak_at_cue_clear()
    {
        uint targetedMask = _runtime.CurrentPartyTargetMask;
        if (targetedMask == 0)
        {
            // No party slots were involved in this cue — nothing to resolve.
            return;
        }

        uint parriedMask = _runtime.LastParriedTargetMask;
        uint failedMask  = targetedMask & ~parriedMask;
        bool anyFailure  = failedMask != 0;

        if (anyFailure)
        {
            // Multi-target rule: at least one targeted slot failed (took a hit
            // or was never parried). Streak resets for every slot in the cue —
            // even slots that DID parry don't get credit because the team's
            // overall defensive pass failed.
            for (int i = 0; i < PartyActorCapacity; i++)
            {
                if ((targetedMask & (1u << i)) == 0) continue;
                if (_consecutiveParriesPerSlot[i] == 0) continue;
                log_debug($"Streak reset (cue failure): {format_actor_slot((byte)i)} (was {_consecutiveParriesPerSlot[i]}× — cue had {BitOperations.PopCount(failedMask)} failed slot(s)).");
                _consecutiveParriesPerSlot[i] = 0;
            }
            return;
        }

        // Full success: every targeted slot parried at least once. Increment
        // each slot's streak, and check whether any crossed the threshold so
        // the counter-attack queue marker fires.
        for (int i = 0; i < PartyActorCapacity; i++)
        {
            if ((targetedMask & (1u << i)) == 0) continue;

            byte before = _consecutiveParriesPerSlot[i];
            byte after  = before == byte.MaxValue ? before : (byte)(before + 1);
            _consecutiveParriesPerSlot[i] = after;

            if (before < ParryStreakObserveThreshold && after >= ParryStreakObserveThreshold)
            {
                log_debug($"Streak ready: {format_actor_slot((byte)i)} parried {after}× consecutively (threshold {ParryStreakObserveThreshold}).");
            }

            // Counter-attack trigger gate: fire once per crossing-or-beyond.
            // Currently log-only — native command insertion path (the actual
            // queue-an-Attack-command call into the engine) is the next
            // implementation step. When the wiring lands, replace the log
            // with the native call (build an AttackCue targeting
            // _runtime.CurrentAttackerId from slot i and inject) and KEEP
            // the streak reset so the chain restarts.
            if (after >= ParryStreakObserveThreshold)
            {
                queue_streak_counter_attack(slotIndex: i, targetEnemySlot: _runtime.CurrentAttackerId);
                _consecutiveParriesPerSlot[i] = 0;
            }
        }
    }

    /// <summary>
    ///     Streak-completion counter-attack trigger. Queues a basic Attack
    ///     (command id 0x4000 — no MP cost, universally available) from the
    ///     parrying party slot onto the original attacker via the engine's
    ///     <c>MsInsertBtlCommand</c> path.
    ///     <br/><br/>
    ///     Cue layout follows the engine's own auto-counter pattern (see
    ///     <c>MsAutoRelifeProcess</c> at FFX.exe+0x38C780 in the decompile
    ///     snapshot): zeroed AttackCue, attacker_id = parrier slot,
    ///     command_count = 1, command_list[0].command_ids = {0x4000, 0xFF},
    ///     command_list[0].targets = bitmask of the original enemy slot.
    ///     The 4th param to MsInsertBtlCommand is the chr_id "context" — the
    ///     engine's auto-counter passes the original incoming attacker, so we
    ///     do the same.
    ///     <br/><br/>
    ///     When <see cref="_optionStreakCounter"/> is off, this falls through
    ///     to log-only mode (the streak observation still runs).
    /// </summary>
    private void queue_streak_counter_attack(int slotIndex, byte targetEnemySlot)
    {
        // Always log + emit telemetry — observation is independent of the actual
        // queue insertion (so users can monitor the streak feature even when the
        // native counter is disabled).
        log_debug(
            $"[StreakCounter] Slot {format_actor_slot((byte)slotIndex)} streak threshold reached — "
            + $"countering {format_actor_slot(targetEnemySlot)} "
            + (_optionStreakCounter ? "(firing native Attack)." : "(log-only — counter disabled)."));

        _turnRuntimeEvents.EmitDispatchConsumed(
            attackerId: targetEnemySlot,
            queueIndex: 0xFE, // sentinel — "streak counter pseudo-event"
            timestampLocal: current_gameplay_timestamp(),
            frameIndex: _debugFrameIndex,
            reason: $"streak counter ready (slot {slotIndex})");

        if (!_optionStreakCounter)
        {
            return;
        }

        try
        {
            // Build the AttackCue on the stack. `default` zero-initialises every
            // byte of the struct so unused fields stay at the default the engine
            // expects (mirroring MsStructClear(&local_50, 0x48) in the engine's
            // auto-counter code).
            AttackCue cue = default;

            cue.attacker_id   = (byte)slotIndex;
            cue.command_count = 1;

            // command_list[0] sits at offset 0x8 in AttackCue. Inside each
            // AttackCommandInfo (size 0x10), the engine writes:
            //   offset 0x0: command_ids[0] (u16) — primary command id
            //   offset 0x2: command_ids[1] (u16) — sentinel 0xFF
            //   offset 0x8: targets (u32)        — bitmask of slot ids
            // The Fahrenheit AttackCue struct only formally exposes `targets`
            // at the AttackCommandInfo offset, so we write the command_ids via
            // direct byte-pointer arithmetic at the documented offsets.
            const int CommandListBaseOffset = 0x8;
            const ushort AttackCommandId = 0x4000; // basic Attack — no MP, universal
            const ushort CommandIdSentinel = 0xFF;

            byte* commandInfo0 = (byte*)&cue + CommandListBaseOffset;
            *(ushort*)(commandInfo0 + 0x0) = AttackCommandId;
            *(ushort*)(commandInfo0 + 0x2) = CommandIdSentinel;
            *(uint*)(commandInfo0 + 0x8)   = 1u << targetEnemySlot;

            int result = FhUtil.get_fptr<MsInsertBtlCommandProbe>(
                ExternalMemoryOffsetMap.Functions.MsInsertBtlCommand)(&cue, 0, 1, targetEnemySlot);

            if (result != 0)
            {
                log_debug($"[StreakCounter] MsInsertBtlCommand returned {result} (validation failed) — counter not queued.");
            }
            else if (_optionLogging)
            {
                log_debug($"[StreakCounter] Queued: attacker={format_actor_slot((byte)slotIndex)} target={format_actor_slot(targetEnemySlot)} cmd=0x{AttackCommandId:X4}.");
            }
        }
        catch (Exception ex)
        {
            // Defensive: never let a counter-queue failure interrupt the
            // current parry-resolution / cue-clear path.
            log_debug($"[StreakCounter] Exception during MsInsertBtlCommand: {ex.Message}");
        }
    }

    private void update_parried_text_timer(float deltaSeconds)
    {
        if (_runtime.ParriedTextRemainingSeconds > 0f)
        {
            _runtime.ParriedTextRemainingSeconds = MathF.Max(0f, _runtime.ParriedTextRemainingSeconds - deltaSeconds);
            // Do NOT clear LastParriedTargetMask here — it must survive until the
            // finalization call (p3=0x400) restores HP. Some attacks delay finalization
            // beyond the 1s display window (e.g. Niedermähen: ~1.27s). The mask is
            // cleared by end_parry_window (on finalization) or clear_awaiting_turn_end.
        }
        else
        {
            _runtime.ParriedTextRemainingSeconds = 0f;
        }

        if (_runtime.ParryMissedTextRemainingSeconds > 0f)
        {
            _runtime.ParryMissedTextRemainingSeconds = MathF.Max(0f, _runtime.ParryMissedTextRemainingSeconds - deltaSeconds);
        }
        else
        {
            _runtime.ParryMissedTextRemainingSeconds = 0f;
        }

        if (_runtime.StatusBlockTextRemainingSeconds > 0f)
        {
            _runtime.StatusBlockTextRemainingSeconds = MathF.Max(0f, _runtime.StatusBlockTextRemainingSeconds - deltaSeconds);
            if (_runtime.StatusBlockTextRemainingSeconds <= 0f)
            {
                _runtime.StatusBlockLabel = string.Empty;
            }
        }
    }

    private void resolve_successful_parry(int slotIndex, Chr* target, string source, bool closeWindow = true)
    {
        if (is_target_non_parryable(target))
        {
            string statusLabel = get_non_parryable_label(target);
            _runtime.StatusBlockTextRemainingSeconds = ParriedTextSeconds;
            _runtime.StatusBlockLabel = statusLabel;
            log_debug($"Parry resolution blocked for {format_actor_slot((byte)slotIndex)} — {statusLabel} (status block).");
            return;
        }

        if (_optionNegateDamage)
        {
            negate_damage_on_impact(target);
        }

        _runtime.InputState = ParryInputState.Resolved;
        _runtime.ParryWindowSucceeded = true;
        _runtime.SuccessIndicatorActive = true;
        _runtime.ParriedTextRemainingSeconds = ParriedTextSeconds;
        _parriedTextSeed = next_label_seed();
        _runtime.ParryMissedTextRemainingSeconds = 0f;
        _runtime.LastParriedTargetMask |= 1u << slotIndex;

        mark_active_turn_parried();
        string sourceLabel = source switch
        {
            "physical"    => "physical impact",
            "visual"      => "visual impact",
            "deferred"    => "deferred impact",
            "magic_impact" => "magic/special impact",
            "fallback"    => "fallback",
            "impact_poll" => "poll detection",
            _             => source
        };
        log_debug($"Parry resolved ({sourceLabel}) for {format_actor_slot((byte)slotIndex)}.");
        apply_overdrive_boost(1u << slotIndex);

        // Streak increment is per-cue, not per-hit — performed at cue-clear time
        // in resolve_streak_at_cue_clear() so multi-hit attacks count as one
        // defensive success per slot. Per-hit increment here overcounted those
        // and was removed.

        if (BitOperations.PopCount(_runtime.LastParriedTargetMask) == 1)
        {
            play_feedback_sound();
        }

        // Visual feedback: fire the Sentinel barrier visual on the parrying
        // character via MsBtlSetHitEffect (global-handle path, PC-safe).
        // Default-on; toggleable via the "Parry Effect Visual" setting.
        fire_parry_visual_effect((byte)slotIndex);
        fire_impact_screen_shake("parry");
        play_parry_block_motion((byte)slotIndex);

        if (closeWindow)
        {
            // Close the window but stay in Resolved until the cue clears; clear_awaiting_turn_end
            // will promote us back to Ready so a fresh press can immediately begin the next parry.
            end_parry_window("impact_parried", transitionToReady: false);
        }
    }

    // Ends a slot's running battle motion through the engine's own completion path.
    //
    // MsEffectEndMotion -> MsEffectResetMotionDisable -> MsTerminateMotion clears Chr+0x432
    // (motion-active) and +0x433 in one u16, clears the motion request +0xdf2, and re-issues the
    // idle motion — which, because MsSetMotion resets the target ATEL script, also cancels the
    // running motion script. It is the engine's own "this motion is over" transition, reached early.
    //
    // MsEffectResetMotionDisable is gated on Chr+0x3f3, which the ATEL worker sets only once it has
    // actually started the motion. Calling before that is a silent no-op, so we check it ourselves
    // and say so in the log rather than pretending the call did something.
    private bool try_end_battle_motion(int slotIndex, string reason)
    {
        if (!_optionDodgeMotionCancel) return false;

        Chr* party = _battleAdapter.GetPlayerCharacters();
        Chr* chr = party != null ? party + slotIndex : null;
        if (chr == null || !chr->stat_exist_flag) return false;

        if (((byte*)chr)[ChrMotionDisableOffset] == 0)
        {
            if (_optionLogging)
            {
                log_debug($"[DodgeMotion] {format_actor_slot((byte)slotIndex)}: motion not started yet (0x3f3=0), nothing to end ({reason}).");
            }
            return false;
        }

        try
        {
            FhUtil.get_fptr<MsEffectEndMotionProbe>(
                ExternalMemoryOffsetMap.Functions.MsEffectEndMotion)((uint)slotIndex, DodgeEndMotionMode);

            if (_optionLogging)
            {
                log_debug($"[DodgeMotion] Ended motion for {format_actor_slot((byte)slotIndex)} via MsEffectEndMotion(mode={DodgeEndMotionMode}) ({reason}).");
            }
            return true;
        }
        catch (Exception ex)
        {
            log_debug($"[DodgeMotion] MsEffectEndMotion failed for slot {slotIndex}: {ex.Message}");
            return false;
        }
    }

    // Draws the next duration preset from a shuffled bag: every stage appears equally often, the
    // order is unpredictable, and a stage never repeats back-to-back. A plain random draw would
    // cluster; walking the stages in order would let you judge each shake against its predecessor
    // rather than on its own.
    private int next_shake_preset()
    {
        if (!_optionImpactShakeSweep) return ImpactShakeDefaultPreset;

        if (_shakeBag.Length == 0 || _shakeBagPos >= _shakeBag.Length)
        {
            int n = ImpactShakeDurationPresets.Length;
            if (_shakeBag.Length != n) _shakeBag = new int[n];
            for (int i = 0; i < n; i++) _shakeBag[i] = i;

            // Fisher-Yates.
            for (int i = n - 1; i > 0; i--)
            {
                int j = _labelRng.Next(i + 1);
                (_shakeBag[i], _shakeBag[j]) = (_shakeBag[j], _shakeBag[i]);
            }

            // Never repeat across the bag seam.
            if (n > 1 && _shakeBag[0] == _lastShakePreset)
            {
                (_shakeBag[0], _shakeBag[n - 1]) = (_shakeBag[n - 1], _shakeBag[0]);
            }

            _shakeBagPos = 0;
        }

        int preset = _shakeBag[_shakeBagPos++];
        _lastShakePreset = preset;
        _shakePresetCounts[preset]++;
        return preset;
    }

    // Fires the engine's own screen shake when a hit is *met* — i.e. on a successful parry, and
    // only there. A dodge (perfect or not) avoids the hit, so nothing lands and nothing shakes.
    // MsScreenSetShake stores a decaying envelope (mode 1) and the engine's per-frame applier
    // (FUN_007bc090) runs it down to zero and stops by itself — we fire once and never clean up.
    // ATEL drives this same setter for scripted quakes, so a battle context is a valid caller.
    private void fire_impact_screen_shake(string source)
    {
        if (!_optionImpactShake) return;
        if (!try_get_live_battle_context(out _)) return;

        int preset = next_shake_preset();
        uint duration = ImpactShakeDurationPresets[preset];

        try
        {
            var shake = FhUtil.get_fptr<MsScreenSetShakeProbe>(ExternalMemoryOffsetMap.Functions.MsScreenSetShake);

            // Two calls, one per axis. A single axis_mask = 3 call would give both axes the same
            // phase and frequency, collapsing the shake onto a diagonal line.
            shake(ImpactShakeScreenId, ImpactShakeAxisA, ImpactShakeModeDecay,
                  ImpactShakeFreqA, duration, ImpactShakeAmpA, ImpactShakeRandomness);
            shake(ImpactShakeScreenId, ImpactShakeAxisB, ImpactShakeModeDecay,
                  ImpactShakeFreqB, duration, ImpactShakeAmpB, ImpactShakeRandomness);

            if (_optionLogging)
            {
                string tag = _optionImpactShakeSweep ? $"preset={ImpactShakeDurationLabels[preset]} " : "";
                log_debug($"[ImpactShake] {tag}dur={duration} ({duration / BattleFrameRate:F2}s) A(amp={ImpactShakeAmpA} freq={ImpactShakeFreqA}) B(amp={ImpactShakeAmpB} freq={ImpactShakeFreqB}) ({source}).");
            }
        }
        catch (Exception ex)
        {
            // Defensive, as with the visual effect: cosmetic feedback must never break the
            // resolution path it is attached to.
            log_debug($"[ImpactShake] Failed to fire screen shake: {ex.Message}");
        }
    }

    private void fire_parry_visual_effect(byte slotIndex)
    {
        if (!_optionParryEffect)
        {
            return;
        }

        try
        {
            // Effect id chosen by eye in the in-battle FX lab (defensive-barrier family,
            // forensic ref: FUN_0079E530 / op_et_eff). 0x4B reads cleanest as a parry.
            // Neighbours: 0x4A Sentinel barrier, 0x48 Shield, 0x49 (the other family members).
            const int ParrySuccessEffectId = 0x4B;

            FhUtil.get_fptr<MsBtlSetHitEffectProbe>(
                ExternalMemoryOffsetMap.Functions.MsBtlSetHitEffect)(slotIndex, 0, ParrySuccessEffectId, 1);

            if (_optionLogging)
            {
                log_debug($"[ParryEffect] Fired hit effect 0x{ParrySuccessEffectId:X2} on {format_actor_slot(slotIndex)}.");
            }
        }
        catch (Exception ex)
        {
            // Defensive: never let a visual-effect failure interrupt the parry
            // resolution path. Log and move on.
            log_debug($"[ParryEffect] Failed to fire visual effect: {ex.Message}");
        }
    }

    // Animated parry feedback: play the short "block" reaction (motion 0x43) on the parrying
    // character. 0x43 is a brief one-shot that returns to idle on its own — unlike the 0x3C/0x3D
    // guard brace, which holds until another motion is set — so it reads cleanly as a parry beat.
    // Gated by the same "Parry Effect Visual" setting as the barrier visual.
    private void play_parry_block_motion(byte slotIndex)
    {
        if (!_optionParryEffect)
        {
            return;
        }

        // The native block path (guard flag + orig MsDamageSetMotion) only plays 0x43 when
        // MsDamageSetMotion actually runs. A parry resolved at MsSetDamageInternal returns early
        // and MsDamageSetMotion — which the engine calls from inside it — never fires, so nothing
        // plays. Stand down only if the native path already played the block for this slot within
        // the last few frames; otherwise poke it manually. Exactly one driver per hit, so the old
        // double-drive "twitch" cannot come back.
        if (parry_block_recently_played(slotIndex))
        {
            return;
        }

        try
        {
            const int ParryBlockMotionId = 0x43; // chosen by eye in the FX/Motion lab
            FhUtil.get_fptr<MsSetMotionProbe>(
                ExternalMemoryOffsetMap.Functions.MsSetMotion)(slotIndex, ParryBlockMotionId, 0, 0, 1, 0, 0);

            if (slotIndex < PartyActorCapacity)
            {
                _motionPlayFrame[slotIndex] = _debugFrameIndex;       // MsEffectEndMotion duration probe
                _parryBlockPlayedFrame[slotIndex] = _debugFrameIndex; // stand-down marker for the native path
            }

            if (_optionLogging)
            {
                log_debug($"[ParryEffect] Played block motion 0x{ParryBlockMotionId:X2} on {format_actor_slot(slotIndex)}.");
            }
        }
        catch (Exception ex)
        {
            log_debug($"[ParryEffect] Failed to play block motion: {ex.Message}");
        }
    }

    private bool try_get_live_battle_context(out Btl* battle)
    {
        battle = _battleAdapter.GetBattle();
        if (battle == null) return false;

        if (battle->battle_state == 0) return false;
        if (battle->ptr_pos_def == null) return false;
        return true;
    }

    private bool should_emit_poll_consumed_signal()
    {
        if (_runtime.LastDispatchConsumedFrame != _debugFrameIndex) return true;
        if (_runtime.LastDispatchConsumedAttackerId != _runtime.CurrentAttackerId) return true;
        return _runtime.LastDispatchConsumedQueueIndex != _runtime.CurrentCueIndex;
    }

    private bool is_impact_correlated_to_active_action(out string reason)
    {
        if (!_runtime.AwaitingTurnEnd)
        {
            reason = "No active turn context";
            return false;
        }

        if (try_get_enemy_attack_cue(out AttackCue cue, out byte cueIndex, out Chr* attacker))
        {
            if (cue.attacker_id != _runtime.CurrentAttackerId)
            {
                reason = $"Attacker changed ({cue.attacker_id} != {_runtime.CurrentAttackerId})";
                return false;
            }

            if (cueIndex != _runtime.CurrentCueIndex)
            {
                reason = $"Queue index changed ({cueIndex} != {_runtime.CurrentCueIndex})";
                return false;
            }

            uint partyMask = extract_party_target_mask(cue);
            if ((partyMask & _runtime.CurrentPartyTargetMask) == 0)
            {
                reason = "Target mask mismatch";
                return false;
            }

            int commandCount = Math.Clamp((int)cue.command_count, 0, 4);
            uint signature = compute_command_signature(cue, commandCount);
            if (_runtime.CurrentCueSignature != 0 && signature != 0 && signature != _runtime.CurrentCueSignature)
            {
                reason = "Cue signature mismatch";
                return false;
            }

            reason = "Matched live cue";
            return true;
        }

        bool consumedSameFrame =
            _runtime.LastDispatchConsumedFrame == _debugFrameIndex
            && _runtime.LastDispatchConsumedAttackerId == _runtime.CurrentAttackerId
            && _runtime.LastDispatchConsumedQueueIndex == _runtime.CurrentCueIndex;

        if (consumedSameFrame)
        {
            reason = "Matched same-frame dispatch consume";
            return true;
        }

        reason = "No matching live cue";
        return false;
    }

    private void on_correlation_matched(byte targetSlot, string source, in ResolvedCommandInfo command)
    {
        _impactCorrelationMatchedCount++;
        maybe_emit_correlation_summary();

        string commandHint = format_command_hint(command, maxLabelLength: 22);
        string target = format_actor_slot(targetSlot);
        string attacker = format_actor_slot(_runtime.CurrentAttackerId);
        log_debug($"Impact correlation matched [{source}]: {attacker} -> {target}{commandHint}.");
    }

    private void on_correlation_rejected(byte targetSlot, string source, string reason)
    {
        _impactCorrelationRejectedCount++;
        _impactCorrelationLastRejectReason = reason;
        _impactCorrelationRejectCounts.TryGetValue(reason, out int count);
        count++;
        _impactCorrelationRejectCounts[reason] = count;
        maybe_emit_correlation_summary();

        bool shouldLog =
            _runtime.LastCorrelationSkipFrame != _debugFrameIndex
            || count == 1
            || count == 5
            || count % 20 == 0;
        if (!shouldLog) return;

        _runtime.LastCorrelationSkipFrame = _debugFrameIndex;
        string target = format_actor_slot(targetSlot);
        string attacker = format_actor_slot(_runtime.CurrentAttackerId);
        log_debug($"Impact correlation rejected [{source}]: {attacker} -> {target} ({reason}) [count={count}].");
    }

    private void maybe_emit_correlation_summary()
    {
        const ulong summaryIntervalFrames = 180; // 6s at 30 FPS
        if (_debugFrameIndex - _impactCorrelationLastSummaryFrame < summaryIntervalFrames) return;
        _impactCorrelationLastSummaryFrame = _debugFrameIndex;

        if (_impactCorrelationMatchedCount == 0 && _impactCorrelationRejectedCount == 0) return;
        log_debug($"Impact correlation summary: {format_correlation_stats()} | top reject: {format_top_correlation_reject()}.");
    }

    private string format_correlation_stats()
    {
        int total = _impactCorrelationMatchedCount + _impactCorrelationRejectedCount;
        if (total <= 0) return "0 matched / 0 rejected";

        double matchPct = (double)_impactCorrelationMatchedCount / total * 100.0;
        return $"{_impactCorrelationMatchedCount} matched / {_impactCorrelationRejectedCount} rejected ({matchPct:F1}% match)";
    }

    private string format_top_correlation_reject()
    {
        if (_impactCorrelationRejectCounts.Count == 0) return "none";

        string bestReason = "none";
        int bestCount = -1;
        foreach (var pair in _impactCorrelationRejectCounts)
        {
            if (pair.Value <= bestCount) continue;
            bestReason = pair.Key;
            bestCount = pair.Value;
        }

        return $"{bestReason} x{bestCount}";
    }

    private void try_capture_current_impact_command_context(out byte attackerId, out int queueIndex, out ResolvedCommandInfo command)
    {
        attackerId = _runtime.CurrentAttackerId;
        queueIndex = _runtime.CurrentCueIndex;
        command = ResolvedCommandInfo.None;

        if (try_get_enemy_attack_cue(out AttackCue cue, out byte cueIndex, out _)
            && cue.attacker_id == _runtime.CurrentAttackerId
            && cueIndex == _runtime.CurrentCueIndex)
        {
            Btl* liveBattle = _battleAdapter.GetBattle();
            command = resolve_command_for_cue(liveBattle, cueIndex, cue);
            attackerId = cue.attacker_id;
            queueIndex = cueIndex;
            if (command.HasCommandId) return;
        }

        Btl* battle = _battleAdapter.GetBattle();
        if (battle != null)
        {
            ushort lastCom = (ushort)(battle->last_com & 0xFFFFu);
            if (is_plausible_command_id(lastCom))
            {
                command = create_resolved_command_info(lastCom, CommandIdSource.LastComFallback, CommandIdConfidence.Low);
            }
        }
    }
}
