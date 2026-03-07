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
                on_impact_detected(i, chr);
            }
            else if (!hasDamage && _damageEventActive[i])
            {
                _damageEventActive[i] = false;
            }
        }
    }

    private void on_impact_detected(int slotIndex, Chr* target)
    {
        if (!is_relevant_impact_slot(slotIndex))
        {
            return;
        }

        if (!is_impact_correlated_to_active_action(out string correlationReason))
        {
            on_correlation_rejected((byte)slotIndex, "impact_poll", correlationReason);
            return;
        }

        try_capture_current_impact_command_context(out byte attackerId, out int queueIndex, out ResolvedCommandInfo command);
        on_correlation_matched((byte)slotIndex, "impact_poll", command);
        _turnRuntimeEvents.EmitDamageResolved(
            targetSlot: slotIndex,
            timestampLocal: current_gameplay_timestamp(),
            frameIndex: _debugFrameIndex,
            attackerId: attackerId,
            queueIndex: queueIndex,
            commandId: command.CommandId,
            commandLabel: command.Label,
            sourceStage: "impact_poll");

        if (_runtime.ParryWindowActive)
        {
            resolve_successful_parry(slotIndex, target, "impact_poll");
            return;
        }

        mark_active_turn_missed("impact outside active parry window");
        trigger_failure_feedback();
        log_debug($"Impact hit {format_actor_slot((byte)slotIndex)} outside parry window.");
    }

    private static void negate_damage_on_impact(Chr* chr)
    {
        // Resolve-at-impact behavior: neutralize queued damage exactly when impact arrives.
        chr->damage_hp = 0;
        chr->damage_mp = 0;
        chr->damage_ctb = 0;
        chr->stat_avoid_flag = true;
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
            int commandCount = Math.Clamp((int)cue.command_count, 0, 4);
            _runtime.CurrentAttackerId = cue.attacker_id;
            _runtime.CurrentCueIndex = cueIndex;
            _runtime.CurrentPartyTargetMask = partyMask;
            _runtime.CurrentCueSignature = compute_command_signature(cue, commandCount);
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
        int totalCues = ParryDecisionPlanner.ClampCueCount(observedCues, MaxAttackCueScan);
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

    private void handle_parry_input_release(ParryInputContext context)
    {
        if (!context.HasParryableCue)
        {
            log_debug("Parry release ignored (no active parryable enemy cue).");
            return;
        }

        _spamController.ArmOnQualifyingRelease();
    }

    private void handle_parry_input_press(ParryInputContext context)
    {
        if (!context.HasParryableCue)
        {
            log_debug("Parry input ignored (no parryable enemy cue).");
            return;
        }

        AttackCue cue = context.Cue;
        byte cueIndex = context.CueIndex;
        uint partyMask = context.PartyMask;

        ParrySpamTransition spamTransition = _spamController.OnQualifyingPress();
        if (spamTransition.TierChanged)
        {
            int fromTier = spamTransition.PreviousTier + 1;
            int toTier = spamTransition.CurrentTier + 1;
            log_debug($"Anti-spam tier {fromTier} -> {toTier} (tap/re-engage).");
        }

        _runtime.AwaitingTurnEnd = true;
        _runtime.CurrentAttackerId = cue.attacker_id;
        _runtime.CurrentCueIndex = cueIndex;
        _runtime.CurrentPartyTargetMask = partyMask;
        _runtime.ParryWindowActive = true;
        int spamTier = ParryDifficultyModel.ClampTierIndex(_spamController.TierIndex);
        _runtime.ParryWindowRemainingSeconds = compute_window_seconds_for_tier(spamTier);
        _runtime.ParryWindowElapsedSeconds = 0f;
        _runtime.ParryWindowSucceeded = false;
        _runtime.SuccessIndicatorActive = false;
        _runtime.ParriedTextRemainingSeconds = 0f;

        mark_active_turn_open();
        float windowMs = _runtime.ParryWindowRemainingSeconds * 1000f;
        log_debug($"Parry input armed for {format_actor_slot(cue.attacker_id)} (q{cueIndex}) for {windowMs:F0}ms [{ParryDifficultyModel.FormatName(_optionDifficulty)} T{spamTier + 1}].");
    }

    private float compute_window_seconds_for_tier(int tierIndex)
    {
        return ParryDifficultyModel.GetWindowSeconds(_optionDifficulty, tierIndex);
    }

    private void advance_spam_penalty_timers(float deltaSeconds)
    {
        ParrySpamTransition transition = _spamController.Tick(deltaSeconds);
        if (transition.Reset && string.Equals(transition.Reason, "calm", StringComparison.Ordinal))
        {
            reset_spam_tier("calm", logTransition: true, alreadyReset: true);
        }
    }

    private void reset_spam_tier(string reason, bool logTransition, bool alreadyReset = false)
    {
        ParrySpamTransition transition = alreadyReset ? default : _spamController.Reset(reason);
        bool changed = alreadyReset ? true : transition.Reset;
        if (logTransition && changed)
        {
            log_debug($"Anti-spam tier reset ({reason}).");
        }
    }

    private bool try_get_parryable_enemy_cue(out AttackCue cue, out byte cueIndex, out Chr* attacker, out uint partyMask)
    {
        partyMask = 0;
        if (!try_get_enemy_attack_cue(out cue, out cueIndex, out attacker))
        {
            return false;
        }

        partyMask = extract_party_target_mask(cue);
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
            return party + slotIndex;

        int enemyIdx = slotIndex - PartyActorCapacity;
        if (enemies != null && enemyIdx >= 0 && enemyIdx < EnemyActorCapacity)
            return enemies + enemyIdx;

        return null;
    }

    private static bool should_flag_as_enemy(byte slotIndex, Chr* chr)
    {
        if (chr != null)
        {
            if (chr->stat_group != 0) return true;
            if (!chr->stat_exist_flag || chr->ram.hp <= 0) return slotIndex >= PartyActorCapacity;
        }

        return slotIndex >= PartyActorCapacity;
    }

    private void end_parry_window(string reason)
    {
        if (_runtime.ParryWindowActive)
            log_debug($"Parry window closed for {format_actor_slot(_runtime.CurrentAttackerId)} ({reason}).");

        _runtime.ParryWindowActive = false;
        _runtime.ParryWindowRemainingSeconds = 0f;
        _runtime.ParryWindowElapsedSeconds = 0f;
        _runtime.ParryWindowSucceeded = false;
        _runtime.SuccessIndicatorActive = false;
    }

    private void clear_awaiting_turn_end(string reason)
    {
        _runtime.AwaitingTurnEnd = false;
        _runtime.ParryWindowSucceeded = false;
        _runtime.SuccessIndicatorActive = false;
        _runtime.CurrentPartyTargetMask = 0;
        _runtime.CurrentCueSignature = 0;
        log_debug(reason);
    }

    private void trigger_failure_feedback()
    {
        _runtime.ParriedTextRemainingSeconds = 0f;
    }

    private void apply_overdrive_boost(uint mask)
    {
        if (!_optionOverdriveBoost) return;

        Chr* party = _battleAdapter.GetPlayerCharacters();
        if (party == null) return;

        uint effectiveMask = mask == 0 ? PlayerTargetMask : mask;

        for (int i = 0; i < PartyActorCapacity; i++)
        {
            uint bit = 1u << i;
            if ((effectiveMask & bit) == 0) continue;

            Chr* chr = party + i;
            if (!chr->stat_exist_flag || chr->ram.hp <= 0) continue;

            byte maxCharge = chr->ram.limit_charge_max;
            if (maxCharge == 0) continue;

            int before = chr->ram.limit_charge;
            int delta = Math.Max(1, (int)MathF.Round(maxCharge * OverdriveBoostPercent));
            int after = Math.Clamp(before + delta, 0, maxCharge);
            if (after == before) continue;

            chr->ram.limit_charge = (byte)after;
            log_debug($"Increased overdrive for {format_actor_slot((byte)i)} from {before} to {after}.");
        }
    }

    private void update_parried_text_timer(float deltaSeconds)
    {
        if (_runtime.ParriedTextRemainingSeconds <= 0f)
        {
            _runtime.ParriedTextRemainingSeconds = 0f;
            return;
        }

        _runtime.ParriedTextRemainingSeconds = MathF.Max(0f, _runtime.ParriedTextRemainingSeconds - deltaSeconds);
        if (_runtime.ParriedTextRemainingSeconds <= 0f)
        {
            _runtime.LastParriedTargetSlot = -1;
        }
    }

    private void resolve_successful_parry(int slotIndex, Chr* target, string source)
    {
        if (_optionNegateDamage)
        {
            negate_damage_on_impact(target);
        }

        _runtime.ParryWindowSucceeded = true;
        _runtime.SuccessIndicatorActive = true;
        _runtime.ParriedTextRemainingSeconds = ParriedTextSeconds;
        _runtime.LastParriedTargetSlot = slotIndex;

        mark_active_turn_parried();
        log_debug($"Parry resolved on {source} for {format_actor_slot((byte)slotIndex)}.");
        apply_overdrive_boost(1u << slotIndex);
        play_feedback_sound();
        reset_spam_tier("success", logTransition: true);
        end_parry_window("impact_parried");
    }

    private bool try_apply_phase_b_negation_for_pending_damage(string stage, bool resolveOnFirstHit)
    {
        if (!_optionEnabled) return false;
        if (!_runtime.AwaitingTurnEnd) return false;

        uint mask = _runtime.CurrentPartyTargetMask & PlayerTargetMask;
        if (mask == 0) return false;

        bool touched = false;
        int resolvedSlot = -1;
        Chr* resolvedChr = null;

        for (int slot = 0; slot < PartyActorCapacity; slot++)
        {
            uint bit = 1u << slot;
            if ((mask & bit) == 0) continue;

            Chr* chr = try_get_chr((byte)slot);
            if (chr == null) continue;

            bool hadPendingDamage = chr->damage_hp != 0 || chr->damage_mp != 0 || chr->damage_ctb != 0;
            if (!hadPendingDamage) continue;

            touched = true;
            if (resolvedSlot < 0)
            {
                resolvedSlot = slot;
                resolvedChr = chr;
            }
        }

        if (!touched || resolvedSlot < 0 || resolvedChr == null)
        {
            return touched;
        }

        if (_runtime.LastHookImpactFrame == _debugFrameIndex && _runtime.LastHookImpactSlot == resolvedSlot)
        {
            return touched;
        }

        _runtime.LastHookImpactFrame = _debugFrameIndex;
        _runtime.LastHookImpactSlot = resolvedSlot;

        if (!is_impact_correlated_to_active_action(out string correlationReason))
        {
            on_correlation_rejected((byte)resolvedSlot, $"hook_{stage}", correlationReason);
            return touched;
        }

        try_capture_current_impact_command_context(out byte attackerId, out int queueIndex, out ResolvedCommandInfo command);
        on_correlation_matched((byte)resolvedSlot, $"hook_{stage}", command);

        if (_optionNegateDamage && _runtime.ParryWindowActive)
        {
            for (int slot = 0; slot < PartyActorCapacity; slot++)
            {
                uint bit = 1u << slot;
                if ((mask & bit) == 0) continue;

                Chr* chr = try_get_chr((byte)slot);
                if (chr == null) continue;
                if (chr->damage_hp == 0 && chr->damage_mp == 0 && chr->damage_ctb == 0) continue;
                negate_damage_on_impact(chr);
            }
        }

        _turnRuntimeEvents.EmitDamageResolved(
            targetSlot: resolvedSlot,
            timestampLocal: current_gameplay_timestamp(),
            frameIndex: _debugFrameIndex,
            attackerId: attackerId,
            queueIndex: queueIndex,
            commandId: command.CommandId,
            commandLabel: command.Label,
            sourceStage: $"hook_{stage}");

        if (resolveOnFirstHit && _runtime.ParryWindowActive)
        {
            resolve_successful_parry(resolvedSlot, resolvedChr, $"hook_{stage}");
        }
        else if (!_runtime.ParryWindowActive)
        {
            mark_active_turn_missed($"hook impact ({stage}) outside active parry window");
            trigger_failure_feedback();
            log_debug($"Hook impact hit {format_actor_slot((byte)resolvedSlot)} outside parry window ({stage}).");
        }

        return touched;
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

        if (try_get_enemy_attack_cue(out AttackCue cue, out byte cueIndex, out _))
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
