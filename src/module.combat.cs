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
                if (is_resolve_mode()) {
                    on_damage_resolve_detected(i);
                }
            }
            else if (!hasDamage && _damageEventActive[i]) {
                _damageEventActive[i] = false;
            }
        }
    }

    private void on_damage_resolve_detected(int slotIndex) {
        record_timing_hit(slotIndex);

        bool shouldClose = ParryDecisionPlanner.ShouldCloseOnDamageResolve(
            parryWindowActive: _runtime.ParryWindowActive,
            resolveMode: is_resolve_mode(),
            currentPartyMask: _runtime.CurrentPartyTargetMask,
            slotIndex: slotIndex,
            fallbackPartyMask: PlayerTargetMask);

        if (!shouldClose) {
            return;
        }

        log_debug($"Detected incoming damage on {format_actor_slot((byte)slotIndex)}; closing parry window.");
        trigger_failure_feedback();
        end_parry_window("damage_resolve");
    }

    private bool monitor_attack_cues() {
        bool hasEnemyCue = try_get_enemy_attack_cue(out AttackCue cue, out byte cueIndex, out Chr* attacker);

        if (hasEnemyCue) {
            uint partyMask = extract_party_target_mask(cue);

            if (should_clear_awaiting_turn_end_for_new_cue(cue, cueIndex, partyMask)) {
                clear_awaiting_turn_end($"Detected next cue while awaiting turn end; switching from {format_actor_slot(_runtime.CurrentAttackerId)} to {format_actor_slot(cue.attacker_id)}.");
            }

            bool isMagic = is_magic_like_attack(attacker);

            var action = ParryDecisionPlanner.PlanStartAction(
                hasCue: true,
                attackerId: cue.attacker_id,
                cueIndex: cueIndex,
                partyMask: partyMask,
                isMagic: isMagic,
                parryWindowActive: _runtime.ParryWindowActive,
                leadPending: _runtime.LeadPending,
                awaitingTurnEnd: _runtime.AwaitingTurnEnd,
                debounceFrames: _runtime.ParryWindowDebounceFrames,
                leadPhysicalFrames: compute_lead_frames(false),
                leadMagicFrames: compute_lead_frames(true),
                initialWindowFrames: compute_initial_window_frames());

            switch (action.Kind) {
                case ParryStartActionKind.IgnoreCueNoPartyTargets:
                    log_debug($"{format_actor_slot(cue.attacker_id)} action ignored (no ally targets).");
                    break;
                case ParryStartActionKind.StartLead:
                    start_lead(action);
                    break;
                case ParryStartActionKind.OpenWindow:
                    _runtime.PendingLeadFramesApplied = 0;
                    begin_parry_window(cue, cueIndex, partyMask, 0);
                    break;
                default:
                    log_debug($"Cue blocked for {format_actor_slot(cue.attacker_id)} (q{cueIndex}): {get_gate_block_reason()}.");
                    break;
            }

            return true;
        }

        if (_runtime.ParryWindowActive) {
            log_debug("Enemy cue cleared without parry input; closing window.");
            end_parry_window("cue_cleared");
        }

        if (_runtime.LeadPending) {
            log_debug("Lead-in cancelled because cue disappeared.");
            _runtime.LeadPending = false;
            _runtime.PendingLeadFramesApplied = 0;
        }

        if (_runtime.AwaitingTurnEnd) {
            clear_awaiting_turn_end("Enemy action resolved; parry ready.");
        }

        return false;
    }

    private void start_lead(ParryStartAction action) {
        _runtime.LeadPending = true;
        _runtime.LeadFramesRemaining = action.LeadFrames;
        _runtime.PendingLeadFramesApplied = action.LeadFrames;
        _runtime.LeadAttackerId = action.AttackerId;
        _runtime.AwaitingTurnEnd = true;
        _runtime.CurrentAttackerId = action.AttackerId;
        _runtime.CurrentCueIndex = action.CueIndex;
        _runtime.CurrentPartyTargetMask = action.PartyMask;
        string damageType = action.IsMagic ? "Magic" : "Physical";
        log_debug($"Lead delay started for {format_actor_slot(_runtime.LeadAttackerId)}: {action.LeadFrames}f ({damageType}, targets: {format_party_target_mask(action.PartyMask)}).");
    }

    private void process_lead_pending() {
        if (!_runtime.LeadPending) return;

        if (!try_get_enemy_attack_cue(_runtime.LeadAttackerId, out AttackCue cue, out byte cueIndex, out Chr* _)) {
            _runtime.LeadPending = false;
            _runtime.AwaitingTurnEnd = false;
            _runtime.PendingLeadFramesApplied = 0;
            log_debug("Lead-in cancelled because attacker left the cue list.");
            return;
        }

        _runtime.LeadFramesRemaining--;
        if (_runtime.LeadFramesRemaining > 0) return;

        uint partyMask = extract_party_target_mask(cue);
        if (partyMask == 0) {
            _runtime.LeadPending = false;
            _runtime.AwaitingTurnEnd = false;
            _runtime.PendingLeadFramesApplied = 0;
            log_debug("Lead-in cancelled due to no remaining party targets.");
            return;
        }

        _runtime.LeadPending = false;
        begin_parry_window(cue, cueIndex, partyMask, _runtime.PendingLeadFramesApplied);
        _runtime.PendingLeadFramesApplied = 0;
    }

    private void begin_parry_window(AttackCue cue, byte cueIndex, uint partyMask, int leadFramesUsed) {
        _runtime.LeadPending = false;
        _runtime.ParryWindowActive = true;
        _runtime.AwaitingTurnEnd = true;
        _runtime.CurrentAttackerId = cue.attacker_id;
        _runtime.CurrentCueIndex = cueIndex;
        _runtime.CurrentPartyTargetMask = partyMask;
        _runtime.ParryWindowFrames = compute_initial_window_frames();
        _runtime.ParryWindowElapsedFrames = 0;
        _runtime.FailureFlashFrames = 0;
        _runtime.ParryWindowDebounceFrames = 0;
        _runtime.ParryInputDebounced = false;
        _runtime.ParryWindowSucceeded = false;
        _runtime.SuccessIndicatorActive = false;
        start_timing_session(cue, cueIndex, partyMask, leadFramesUsed);
        string damageType = is_magic_like_attack(try_get_chr(cue.attacker_id)) ? "Magic" : "Physical";
        log_debug($"{format_actor_slot(_runtime.CurrentAttackerId)} {damageType} command detected (q{cueIndex}) - parry window open for {_runtime.ParryWindowFrames}f, targets: {format_party_target_mask(partyMask)}.");
    }

    private bool try_get_enemy_attack_cue(out AttackCue cue, out byte cueIndex, out Chr* attacker) {
        return try_get_enemy_attack_cue_internal(null, out cue, out cueIndex, out attacker);
    }

    private bool try_get_enemy_attack_cue(byte attackerFilter, out AttackCue cue, out byte cueIndex, out Chr* attacker) {
        return try_get_enemy_attack_cue_internal(attackerFilter, out cue, out cueIndex, out attacker);
    }

    private bool try_get_enemy_attack_cue_internal(byte? attackerFilter, out AttackCue cue, out byte cueIndex, out Chr* attacker) {
        cueIndex = 0;
        cue = default;
        attacker = null;

        Btl* battle = _battleAdapter.GetBattle();
        if (battle == null) return false;

        int observedCues = battle->attack_cues_size;
        int totalCues = ParryDecisionPlanner.ClampCueCount(observedCues, MaxAttackCueScan);
        if (totalCues <= 0) return false;

        if (observedCues > totalCues && !_runtime.AttackCueClampWarned) {
            // Defensive clamp: corrupted cue counts could otherwise walk invalid memory.
            _logger.Warning($"[Parry] attack_cues_size was {observedCues}; clamping scan to {totalCues} for safety.");
            _runtime.AttackCueClampWarned = true;
        }

        for (int i = 0; i < totalCues; i++) {
            AttackCue candidate = battle->attack_cues[i];
            Chr* candidateChr = try_get_chr(candidate.attacker_id);
            if (!should_flag_as_enemy(candidate.attacker_id, candidateChr))
                continue;
            if (attackerFilter.HasValue && candidate.attacker_id != attackerFilter.Value)
                continue;

            cueIndex = (byte)i;
            cue = candidate;
            attacker = candidateChr;
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

    private int compute_initial_window_frames() {
        return is_resolve_mode() ? compute_resolve_window_frames() : compute_window_frames();
    }

    private int compute_negation_timeout_frames() {
        return compute_initial_window_frames();
    }

    private int compute_resolve_window_frames() {
        float clamped = Math.Clamp(_optionResolveWindowSeconds, ResolveWindowMinSeconds, ResolveWindowMaxSeconds);
        return seconds_to_frames(clamped);
    }

    private bool is_resolve_mode() {
        return _optionTimingMode == ParryTimingMode.ApplyDamageClamp;
    }

    private int compute_window_frames() {
        int minFrames = seconds_to_frames(WindowMinSeconds);
        int maxFrames = seconds_to_frames(WindowMaxSeconds);
        int target = seconds_to_frames(_optionWindowSeconds);
        return Math.Clamp(target, minFrames, maxFrames);
    }

    private int compute_lead_frames(bool isMagic) {
        float minSeconds = isMagic ? LeadMagicMinSeconds : LeadPhysicalMinSeconds;
        float maxSeconds = isMagic ? LeadMagicMaxSeconds : LeadPhysicalMaxSeconds;
        float option = isMagic ? _optionLeadMagicSeconds : _optionLeadPhysicalSeconds;
        float clamped = Math.Clamp(option, minSeconds, maxSeconds);
        return seconds_to_frames(clamped);
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
            // Game memory quirk: some non-active entries may keep stale data.
            // Treat non-existing or dead entries as enemies only by slot range fallback.
            if (chr->stat_group != 0) return true;
            if (!chr->stat_exist_flag || chr->ram.hp <= 0) return slotIndex >= PartyActorCapacity;
        }

        return slotIndex >= PartyActorCapacity;
    }

    private void end_parry_window(string reason) {
        if (_runtime.ParryWindowActive)
            log_debug($"Parry window closed for {format_actor_slot(_runtime.CurrentAttackerId)}.");

        finalize_timing_capture(reason);
        _runtime.ParryWindowActive = false;
        _runtime.ParryWindowFrames = 0;
        _runtime.ParryWindowElapsedFrames = 0;
        _runtime.ParryWindowDebounceFrames = 0;
        _runtime.ParryInputDebounced = false;
        _runtime.ParryWindowSucceeded = false;
        _runtime.SuccessIndicatorActive = false;
    }

    private bool should_clear_awaiting_turn_end_for_new_cue(AttackCue cue, byte cueIndex, uint partyMask) {
        if (!_runtime.AwaitingTurnEnd) return false;
        if (_runtime.ParryWindowActive || _runtime.LeadPending) return false;

        if (cue.attacker_id != _runtime.CurrentAttackerId) return true;
        if (cueIndex != _runtime.CurrentCueIndex) return true;
        return partyMask != _runtime.CurrentPartyTargetMask;
    }

    private void clear_awaiting_turn_end(string reason) {
        _runtime.AwaitingTurnEnd = false;
        _runtime.ParryInputDebounced = false;
        if (_runtime.SuccessIndicatorActive) {
            _runtime.SuccessIndicatorActive = false;
            _runtime.SuccessFlashFrames = Math.Max(_runtime.SuccessFlashFrames, IndicatorFlashFrames);
        }

        _runtime.ParryWindowSucceeded = false;
        log_debug(reason);
    }

    private void on_parry_success() {
        int framesRemaining = Math.Max(_runtime.ParryWindowFrames, 0);
        _runtime.ParryWindowActive = false;
        _runtime.ParryWindowFrames = 0;
        _runtime.AwaitingTurnEnd = true;
        _runtime.ParryWindowDebounceFrames = Math.Max(_runtime.ParryWindowDebounceFrames, Math.Max(framesRemaining, 1));

        _runtime.ParryWindowSucceeded = true;
        _runtime.SuccessIndicatorActive = true;
        _runtime.SuccessFlashFrames = Math.Max(_runtime.SuccessFlashFrames, IndicatorFlashFrames);
        _runtime.FailureFlashFrames = 0;

        log_debug($"Parry input detected against {format_actor_slot(_runtime.CurrentAttackerId)}.");
        finalize_timing_capture("parry_success", true);
        mark_pending_negation();
        apply_overdrive_boost(_runtime.CurrentPartyTargetMask);
        play_feedback_sound();
    }

    private void trigger_failure_feedback() {
        if (!_runtime.ParryWindowActive || _runtime.ParryWindowSucceeded) return;
        log_debug($"Parry failed against {format_actor_slot(_runtime.CurrentAttackerId)}.");
        if (_optionIndicator)
            _runtime.FailureFlashFrames = IndicatorFlashFrames;
        _runtime.SuccessFlashFrames = 0;
    }

    private void mark_pending_negation() {
        if (!_optionNegateDamage) return;

        uint mask = _runtime.CurrentPartyTargetMask;
        if (mask == 0) mask = PlayerTargetMask;

        _runtime.PendingNegateMask = mask;
        _runtime.PendingNegateTimeoutFrames = compute_negation_timeout_frames();
        log_debug($"Queued damage negation for {format_party_target_mask(mask)}.");
    }

    private void process_pending_negation() {
        if (_runtime.PendingNegateMask == 0 || !_optionNegateDamage) return;

        Chr* party = _battleAdapter.GetPlayerCharacters();
        if (party == null) {
            _runtime.PendingNegateMask = 0;
            return;
        }

        for (int i = 0; i < PartyActorCapacity; i++) {
            uint bit = 1u << i;
            if ((_runtime.PendingNegateMask & bit) == 0) continue;

            Chr* chr = party + i;
            if (!chr->stat_exist_flag || chr->ram.hp <= 0) {
                _runtime.PendingNegateMask &= ~bit;
                continue;
            }

            if (chr->damage_hp == 0 && chr->damage_mp == 0)
                continue;

            // Damage-negation hack: clear queued damage fields before the engine applies them.
            // This intentionally relies on battle-frame timing around attack resolve.
            chr->damage_hp = 0;
            chr->damage_mp = 0;
            chr->damage_ctb = 0;
            chr->stat_avoid_flag = true;
            _runtime.PendingNegateMask &= ~bit;
            log_debug($"Negated pending damage for {format_actor_slot((byte)i)}.");
        }

        if (_runtime.PendingNegateMask == 0) {
            _runtime.PendingNegateTimeoutFrames = 0;
            return;
        }

        if (_runtime.PendingNegateTimeoutFrames > 0) {
            _runtime.PendingNegateTimeoutFrames--;
            if (_runtime.PendingNegateTimeoutFrames == 0)
                _runtime.PendingNegateMask = 0;
        }
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
        _runtime.PendingSoundFrames = SoundResetFrames;
        log_debug("Queued confirm-style hit sound for local player.");
    }

    private void update_sound_flag() {
        if (_runtime.PendingSoundSlot < 0 || _runtime.PendingSoundFrames <= 0) return;

        _runtime.PendingSoundFrames--;
        if (_runtime.PendingSoundFrames == 0) {
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
        _runtime.PendingSoundFrames = 0;
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
