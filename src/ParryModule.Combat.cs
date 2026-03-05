namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule {
    private void monitor_damage_resolves() {
        Chr* party = _battleAdapter.GetPlayerCharacters();
        if (party == null) {
            Array.Clear(_damageEventActive);
            return;
        }

        for (int i = 0; i < PartyActorCapacity; i++) {
            Chr* chr = party + i;
            bool hasDamage = chr->damage_hp != 0 || chr->damage_mp != 0;
            if (hasDamage && !_damageEventActive[i]) {
                _damageEventActive[i] = true;
                on_impact_detected(i, chr);
            }
            else if (!hasDamage && _damageEventActive[i]) {
                _damageEventActive[i] = false;
            }
        }
    }

    private void on_impact_detected(int slotIndex, Chr* target) {
        _turnRuntimeEvents.EmitDamageResolved(slotIndex, current_gameplay_timestamp(), _debugFrameIndex);

        if (!is_relevant_impact_slot(slotIndex)) {
            return;
        }

        if (_runtime.ParryWindowActive) {
            if (_optionNegateDamage) {
                negate_damage_on_impact(target);
            }

            _runtime.ParryWindowSucceeded = true;
            _runtime.SuccessIndicatorActive = true;
            _runtime.SuccessFlashSeconds = MathF.Max(_runtime.SuccessFlashSeconds, IndicatorFlashSeconds);
            _runtime.FailureFlashSeconds = 0f;

            mark_active_turn_parried();
            log_debug($"Parry resolved on impact for {format_actor_slot((byte)slotIndex)}.");
            apply_overdrive_boost(1u << slotIndex);
            play_feedback_sound();
            reset_spam_tier("success", logTransition: true);
            end_parry_window("impact_parried");
            return;
        }

        mark_active_turn_missed("impact outside active parry window");
        trigger_failure_feedback();
        log_debug($"Impact hit {format_actor_slot((byte)slotIndex)} outside parry window.");
    }

    private static void negate_damage_on_impact(Chr* chr) {
        // Resolve-at-impact behavior: neutralize queued damage exactly when impact arrives.
        chr->damage_hp = 0;
        chr->damage_mp = 0;
        chr->damage_ctb = 0;
        chr->stat_avoid_flag = true;
    }

    private bool is_relevant_impact_slot(int slotIndex) {
        if (slotIndex < 0 || slotIndex >= PartyActorCapacity) return false;
        if (!_runtime.AwaitingTurnEnd) return false;

        uint mask = _runtime.CurrentPartyTargetMask;
        if (mask == 0) return false;
        uint bit = 1u << slotIndex;
        return (mask & bit) != 0;
    }

    private bool monitor_attack_cues() {
        bool hasCue = try_get_enemy_attack_cue(out AttackCue cue, out byte cueIndex, out Chr* attacker);
        if (!hasCue) {
            if (_runtime.AwaitingTurnEnd) {
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
        if (!actionable) {
            return true;
        }

        bool changed =
            !_runtime.AwaitingTurnEnd
            || cue.attacker_id != _runtime.CurrentAttackerId
            || cueIndex != _runtime.CurrentCueIndex
            || partyMask != _runtime.CurrentPartyTargetMask;

        if (changed) {
            _runtime.CurrentAttackerId = cue.attacker_id;
            _runtime.CurrentCueIndex = cueIndex;
            _runtime.CurrentPartyTargetMask = partyMask;
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

    private bool try_get_enemy_attack_cue(out AttackCue cue, out byte cueIndex, out Chr* attacker) {
        cueIndex = 0;
        cue = default;
        attacker = null;

        Btl* battle = _battleAdapter.GetBattle();
        if (battle == null) return false;

        int observedCues = battle->attack_cues_size;
        int totalCues = ParryDecisionPlanner.ClampCueCount(observedCues, MaxAttackCueScan);
        if (totalCues <= 0) return false;

        if (observedCues > totalCues && !_runtime.AttackCueClampWarned) {
            _logger.Warning($"[Parry] attack_cues_size was {observedCues}; clamping scan to {totalCues} for safety.");
            _runtime.AttackCueClampWarned = true;
        }

        int fallbackIndex = -1;
        AttackCue fallbackCue = default;
        Chr* fallbackChr = null;

        for (int i = 0; i < totalCues; i++) {
            AttackCue candidate = battle->attack_cues[i];
            Chr* candidateChr = try_get_chr(candidate.attacker_id);
            if (!should_flag_as_enemy(candidate.attacker_id, candidateChr))
                continue;

            if (fallbackIndex < 0) {
                fallbackIndex = i;
                fallbackCue = candidate;
                fallbackChr = candidateChr;
            }

            if (extract_party_target_mask(candidate) != 0) {
                cueIndex = (byte)i;
                cue = candidate;
                attacker = candidateChr;
                return true;
            }
        }

        if (fallbackIndex >= 0) {
            cueIndex = (byte)fallbackIndex;
            cue = fallbackCue;
            attacker = fallbackChr;
            return true;
        }

        return false;
    }

    private static uint extract_party_target_mask(AttackCue cue) {
        uint mask = 0;
        int commandCount = Math.Clamp((int)cue.command_count, 0, 4);
        for (int i = 0; i < commandCount; i++) {
            ref AttackCommandInfo info = ref cue.command_list[i];
            mask |= info.targets & PlayerTargetMask;
        }

        return mask;
    }

    private ParryInputContext capture_parry_input_context() {
        if (!try_get_parryable_enemy_cue(out AttackCue cue, out byte cueIndex, out _, out uint partyMask)) {
            return ParryInputContext.None;
        }

        return new ParryInputContext(
            hasParryableCue: true,
            cue: cue,
            cueIndex: cueIndex,
            partyMask: partyMask);
    }

    private void handle_parry_input_release(ParryInputContext context) {
        if (!context.HasParryableCue) {
            log_debug("Parry release ignored (no active parryable enemy cue).");
            return;
        }

        _spamController.ArmOnQualifyingRelease();
    }

    private void handle_parry_input_press(ParryInputContext context) {
        if (!context.HasParryableCue) {
            log_debug("Parry input ignored (no parryable enemy cue).");
            return;
        }

        AttackCue cue = context.Cue;
        byte cueIndex = context.CueIndex;
        uint partyMask = context.PartyMask;

        ParrySpamTransition spamTransition = _spamController.OnQualifyingPress();
        if (spamTransition.TierChanged) {
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
        _runtime.FailureFlashSeconds = 0f;

        mark_active_turn_open();
        float windowMs = _runtime.ParryWindowRemainingSeconds * 1000f;
        log_debug($"Parry input armed for {format_actor_slot(cue.attacker_id)} (q{cueIndex}) for {windowMs:F0}ms [{ParryDifficultyModel.FormatName(_optionDifficulty)} T{spamTier + 1}].");
    }

    private float compute_window_seconds_for_tier(int tierIndex) {
        return ParryDifficultyModel.GetWindowSeconds(_optionDifficulty, tierIndex);
    }

    private void advance_spam_penalty_timers(float deltaSeconds) {
        ParrySpamTransition transition = _spamController.Tick(deltaSeconds);
        if (transition.Reset && string.Equals(transition.Reason, "calm", StringComparison.Ordinal)) {
            reset_spam_tier("calm", logTransition: true, alreadyReset: true);
        }
    }

    private void reset_spam_tier(string reason, bool logTransition, bool alreadyReset = false) {
        ParrySpamTransition transition = alreadyReset ? default : _spamController.Reset(reason);
        bool changed = alreadyReset ? true : transition.Reset;
        if (logTransition && changed) {
            log_debug($"Anti-spam tier reset ({reason}).");
        }
    }

    private bool try_get_parryable_enemy_cue(out AttackCue cue, out byte cueIndex, out Chr* attacker, out uint partyMask) {
        partyMask = 0;
        if (!try_get_enemy_attack_cue(out cue, out cueIndex, out attacker)) {
            return false;
        }

        partyMask = extract_party_target_mask(cue);
        return partyMask != 0;
    }

    private static bool is_magic_like_attack(Chr* attacker) {
        if (attacker == null) return false;
        byte commandType = attacker->stat_command_type;
        return commandType >= 1;
    }

    private Chr* try_get_chr(byte slotIndex) {
        Chr* party = _battleAdapter.GetPlayerCharacters();
        Chr* enemies = _battleAdapter.GetMonsterCharacters();

        if (party != null && slotIndex < PartyActorCapacity)
            return party + slotIndex;

        int enemyIdx = slotIndex - PartyActorCapacity;
        if (enemies != null && enemyIdx >= 0 && enemyIdx < EnemyActorCapacity)
            return enemies + enemyIdx;

        return null;
    }

    private static bool should_flag_as_enemy(byte slotIndex, Chr* chr) {
        if (chr != null) {
            if (chr->stat_group != 0) return true;
            if (!chr->stat_exist_flag || chr->ram.hp <= 0) return slotIndex >= PartyActorCapacity;
        }

        return slotIndex >= PartyActorCapacity;
    }

    private void end_parry_window(string reason) {
        if (_runtime.ParryWindowActive)
            log_debug($"Parry window closed for {format_actor_slot(_runtime.CurrentAttackerId)} ({reason}).");

        _runtime.ParryWindowActive = false;
        _runtime.ParryWindowRemainingSeconds = 0f;
        _runtime.ParryWindowElapsedSeconds = 0f;
        _runtime.ParryWindowSucceeded = false;
        _runtime.SuccessIndicatorActive = false;
    }

    private void clear_awaiting_turn_end(string reason) {
        _runtime.AwaitingTurnEnd = false;
        _runtime.ParryWindowSucceeded = false;
        _runtime.SuccessIndicatorActive = false;
        _runtime.CurrentPartyTargetMask = 0;
        log_debug(reason);
    }

    private void trigger_failure_feedback() {
        if (_optionIndicator)
            _runtime.FailureFlashSeconds = IndicatorFlashSeconds;
        _runtime.SuccessFlashSeconds = 0f;
    }

    private void apply_overdrive_boost(uint mask) {
        if (!_optionOverdriveBoost) return;

        Chr* party = _battleAdapter.GetPlayerCharacters();
        if (party == null) return;

        uint effectiveMask = mask == 0 ? PlayerTargetMask : mask;

        for (int i = 0; i < PartyActorCapacity; i++) {
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

    private void play_feedback_sound() {
        if (!_optionSound) return;

        if (!try_find_first_active_player(out byte slotIndex, out Chr* player)) return;

        // Audio feedback hack: this flag asks the engine to play a hit confirmation sound once.
        player->stat_sound_hit_num = 3;
        _runtime.PendingSoundSlot = slotIndex;
        _runtime.PendingSoundSeconds = SoundResetSeconds;
        log_debug("Queued confirm-style hit sound for local player.");
    }

    private void update_sound_flag(float deltaSeconds) {
        if (_runtime.PendingSoundSlot < 0 || _runtime.PendingSoundSeconds <= 0f) return;

        _runtime.PendingSoundSeconds = MathF.Max(0f, _runtime.PendingSoundSeconds - deltaSeconds);
        if (_runtime.PendingSoundSeconds <= 0f) {
            end_pending_sound_feedback(forceResetSound: true);
            log_debug("Reset temporary hit sound flag.");
        }
    }

    private void end_pending_sound_feedback(bool forceResetSound) {
        if (_runtime.PendingSoundSlot >= 0 && forceResetSound) {
            Chr* chr = try_get_chr((byte)_runtime.PendingSoundSlot);
            if (chr != null && _runtime.PendingSoundSlot < PartyActorCapacity) {
                chr->stat_sound_hit_num = 0;
            }
        }

        _runtime.PendingSoundSlot = -1;
        _runtime.PendingSoundSeconds = 0f;
    }

    private bool try_find_first_active_player(out byte slotIndex, out Chr* player) {
        slotIndex = 0;
        player = null;

        Chr* party = _battleAdapter.GetPlayerCharacters();
        if (party == null) return false;

        for (byte i = 0; i < PartyActorCapacity; i++) {
            Chr* chr = party + i;
            if (chr->stat_exist_flag && chr->ram.hp > 0) {
                slotIndex = i;
                player = chr;
                return true;
            }
        }

        return false;
    }
}
