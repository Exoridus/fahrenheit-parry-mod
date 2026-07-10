namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule
{
    private enum DebugCueCategory
    {
        EnemyPhysicalParty,
        EnemyMagicParty,
        EnemyNonParty,
        PartyOrSystem,
        Unknown
    }

    private readonly struct DebugCueSnapshot
    {
        public readonly byte QueueIndex;
        public readonly byte AttackerId;
        public readonly int CommandCount;
        public readonly ushort CommandId;
        public readonly string CommandLabel;
        public readonly string CommandKind;
        public readonly string CommandDamageType;
        public readonly CommandIdSource CommandSource;
        public readonly CommandIdConfidence CommandConfidence;
        public readonly uint CommandSignature;
        public readonly uint PartyMask;
        public readonly uint NonPartyMask;
        public readonly bool IsEnemy;
        public readonly bool IsMagic;
        public readonly DebugCueCategory Category;
        public readonly int CurrentCtb;

        public DebugCueSnapshot(
            byte queueIndex,
            byte attackerId,
            int commandCount,
            ushort commandId,
            string commandLabel,
            string commandKind,
            string commandDamageType,
            CommandIdSource commandSource,
            CommandIdConfidence commandConfidence,
            uint commandSignature,
            uint partyMask,
            uint nonPartyMask,
            bool isEnemy,
            bool isMagic,
            DebugCueCategory category,
            int currentCtb)
        {
            QueueIndex = queueIndex;
            AttackerId = attackerId;
            CommandCount = commandCount;
            CommandId = commandId;
            CommandLabel = commandLabel ?? string.Empty;
            CommandKind = commandKind ?? string.Empty;
            CommandDamageType = commandDamageType ?? string.Empty;
            CommandSource = commandSource;
            CommandConfidence = commandConfidence;
            CommandSignature = commandSignature;
            PartyMask = partyMask;
            NonPartyMask = nonPartyMask;
            IsEnemy = isEnemy;
            IsMagic = isMagic;
            Category = category;
            CurrentCtb = currentCtb;
        }

        public bool EqualsSemantic(in DebugCueSnapshot other)
        {
            return AttackerId == other.AttackerId
                && CommandCount == other.CommandCount
                && CommandId == other.CommandId
                && CommandSignature == other.CommandSignature
                && PartyMask == other.PartyMask
                && NonPartyMask == other.NonPartyMask
                && IsEnemy == other.IsEnemy
                && IsMagic == other.IsMagic
                && Category == other.Category;
        }
    }

    private readonly struct DebugCueHistoryEntry
    {
        public readonly DateTime TimestampLocal;
        public readonly ulong FrameIndex;
        public readonly int TurnId;
        public readonly string Event;
        public readonly byte QueueIndex;
        public readonly string CueId;
        public readonly byte AttackerId;
        public readonly int CommandCount;
        public readonly int QueueDepth;
        public readonly int ActionableDepth;
        public readonly string Category;
        public readonly string Targets;
        public readonly string Decision;
        public readonly string Gate;

        public DebugCueHistoryEntry(
            DateTime timestampLocal,
            ulong frameIndex,
            int turnId,
            string @event,
            byte queueIndex,
            string cueId,
            byte attackerId,
            int commandCount,
            int queueDepth,
            int actionableDepth,
            string category,
            string targets,
            string decision,
            string gate)
        {
            TimestampLocal = timestampLocal;
            FrameIndex = frameIndex;
            TurnId = turnId;
            Event = @event;
            QueueIndex = queueIndex;
            CueId = cueId;
            AttackerId = attackerId;
            CommandCount = commandCount;
            QueueDepth = queueDepth;
            ActionableDepth = actionableDepth;
            Category = category;
            Targets = targets;
            Decision = decision;
            Gate = gate;
        }
    }

    private void update_debug_battle_session_state()
    {
        bool trackingEnabled = _debugGameSaveLoaded && _debugGameplayReady;
        bool active = trackingEnabled && _battleAdapter.GetBattle() != null;

        if (!trackingEnabled)
        {
            if (_debugBattleActive)
            {
                _debugBattleFrameIndex = 0;
                _debugCueTurnId = 0;
                _turnTimeline.EndBattle();
            }

            _debugBattleActive = false;
            return;
        }

        if (active)
        {
            if (!_debugBattleActive)
            {
                _debugBattleFrameIndex = 0;
                _debugCueTurnId = 0;
                _debugBattleSessionFirstCueSeen = false;
                _turnTimeline.BeginBattle();
                // "Battle session detected." log is deferred to monitor_attack_cues()
                // so that it fires only when the first actionable cue is observed,
                // not at the earlier gameplay-ready/battle-context transition.

                // This is the battle-begin edge. Gameplay that needs it hangs off
                // on_battle_session_begin() rather than off this debug tracker.
                on_battle_session_begin();
            }
            else
            {
                _debugBattleFrameIndex++;
            }
        }
        else if (_debugBattleActive)
        {
            append_debug_event("Battle session ended.");
            _debugBattleFrameIndex = 0;
            _debugCueTurnId = 0;
            _debugBattleSessionFirstCueSeen = false;
            _turnTimeline.EndBattle();
        }

        _debugBattleActive = active;
    }

    private void update_debug_save_loaded_state()
    {
        FhFfx.SaveData* save = FhFfx.Globals.save_data;
        bool loaded = is_game_save_loaded(save);
        bool gameplayReady = loaded && is_gameplay_ready_for_overlay(save);
        if (loaded && !_debugGameSaveLoaded)
        {
            append_debug_event("Game save detected.");
        }

        if (gameplayReady && !_debugGameplayReady)
        {
            append_debug_event("Gameplay ready. Debug overlay enabled.");
        }
        else if (!gameplayReady && _debugGameplayReady)
        {
            append_debug_event("Gameplay not ready. Debug overlay hidden.");
        }

        _debugGameSaveLoaded = loaded;
        _debugGameplayReady = gameplayReady;

        if (gameplayReady)
        {
            prune_old_session_logs_if_needed();
        }
    }

    private static bool is_game_save_loaded(FhFfx.SaveData* save)
    {
        if (save == null) return false;

        // Keep this check pragmatic: in title/boot these stay zeroed, while loaded saves quickly
        // populate at least one of these routing fields.
        if (save->saved_current_room_id != 0 || save->saved_now_eventjump_map_no != 0 || save->saved_now_eventjump_map_id != 0) return true;
        if (save->current_room_id != 0 || save->now_eventjump_map_no != 0 || save->now_eventjump_map_id != 0) return true;

        return false;
    }

    private bool is_gameplay_ready_for_overlay(FhFfx.SaveData* save)
    {
        if (save == null) return false;

        // Fahrenheit runtime identifies FFX main menu with event 0x17.
        if (*FhFfx.Globals.event_id == 0x17) return false;

        int eventId = *FhFfx.Globals.event_id;
        if (eventId > 0)
        {
            string eventName = get_current_event_name((uint)eventId);
            if (string.Equals(eventName, "test20", StringComparison.OrdinalIgnoreCase)
                || string.Equals(eventName, "memochek", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // During map fades/transitions the player is not controllable.
        if (save->fade_mode != 0 || save->fade_time != 0) return false;

        // If combat has initialized, keep overlay visible for parry debugging.
        if (_battleAdapter.GetBattle() != null) return true;

        // Field gameplay fallback.
        return save->current_room_id != 0 || save->saved_current_room_id != 0;
    }

    private static bool is_cue_ownership_change(in DebugCueSnapshot previous, in DebugCueSnapshot current)
    {
        return previous.AttackerId != current.AttackerId
            || previous.IsEnemy != current.IsEnemy;
    }

    private void sync_turn_timeline_from_cues()
    {
        _debugTimelineCueScratch.Clear();
        for (int i = 0; i < _debugCueScratch.Count; i++)
        {
            DebugCueSnapshot cue = _debugCueScratch[i];
            _debugTimelineCueScratch.Add(new TurnTimelineCueObservation(
                QueueIndex: cue.QueueIndex,
                AttackerId: cue.AttackerId,
                Actor: format_actor_slot(cue.AttackerId),
                Action: format_turn_action(cue),
                Targets: format_turn_targets(cue),
                Parryability: classify_turn_parryability(cue),
                Command: new TurnTimelineCommandInfo(
                    CommandId: cue.CommandId,
                    Label: cue.CommandLabel,
                    Kind: cue.CommandKind,
                    Source: format_command_source(cue.CommandSource),
                    Confidence: to_timeline_confidence(cue.CommandConfidence)),
                Fingerprint: new TurnTimelineCueFingerprint(
                    AttackerId: cue.AttackerId,
                    CommandCount: cue.CommandCount,
                    CommandSignature: cue.CommandSignature,
                    PartyMask: cue.PartyMask,
                    NonPartyMask: cue.NonPartyMask,
                    IsEnemy: cue.IsEnemy,
                    IsMagic: cue.IsMagic)));
        }

        _turnRuntimeEvents.EmitCueSnapshot(
            cues: _debugTimelineCueScratch,
            cueTurnId: _debugCueTurnId,
            timestampLocal: current_gameplay_timestamp(),
            frameIndex: _debugFrameIndex,
            parryWindowActive: _runtime.ParryWindowActive);
    }

    private void flush_turn_timeline_events_to_log()
    {
        _debugTimelineEventScratch.Clear();
        _turnTimeline.DrainEvents(_debugTimelineEventScratch);
        for (int i = 0; i < _debugTimelineEventScratch.Count; i++)
        {
            TurnTimelineEvent evt = _debugTimelineEventScratch[i];
            if (evt.Kind == TurnTimelineEventKind.CueSnapshot)
            {
                continue;
            }

            write_session_timeline_event(evt);
            log_debug(evt.Message);
        }
    }

    private void mark_active_turn_open()
    {
        _turnRuntimeEvents.EmitParryWindowOpened(current_gameplay_timestamp(), _debugFrameIndex);
    }

    private void mark_active_turn_parried()
    {
        _turnRuntimeEvents.EmitParrySucceeded(current_gameplay_timestamp(), _debugFrameIndex);
    }

    private void mark_active_turn_missed(string reason)
    {
        _turnRuntimeEvents.EmitParryMissed(current_gameplay_timestamp(), _debugFrameIndex, reason);
    }

    private static TurnTimelineParryability classify_turn_parryability(DebugCueSnapshot cue)
    {
        if (!cue.IsEnemy) return TurnTimelineParryability.NonParryable;
        if (cue.PartyMask != 0) return TurnTimelineParryability.Parryable;
        return TurnTimelineParryability.Unknown;
    }

    private string format_turn_action(DebugCueSnapshot cue)
    {
        string baseAction = !cue.IsEnemy
            ? "System"
            : cue.PartyMask != 0
                ? cue.IsMagic ? "Spell" : "Attack"
                : cue.NonPartyMask != 0
                    ? "Special"
                    : "System";

        if (cue.CommandId != 0 && !string.IsNullOrWhiteSpace(cue.CommandLabel))
        {
            string label = truncate_display(cue.CommandLabel, 28);
            bool hasDamageType = !string.IsNullOrWhiteSpace(cue.CommandDamageType)
                && !cue.CommandDamageType.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
            string suffix = hasDamageType ? $" [{cue.CommandDamageType}]" : string.Empty;
            return $"{baseAction}: {label}{suffix}";
        }

        return baseAction;
    }

    private string format_turn_targets(DebugCueSnapshot cue)
    {
        if (cue.PartyMask != 0) return format_party_target_mask(cue.PartyMask);
        if (cue.NonPartyMask != 0) return "Non-party";
        return "-";
    }

    private static string format_turn_id(TurnTimelineRow row)
    {
        return $"T{row.TurnId:D3}.{row.TurnOrdinal:D2}";
    }

    private static string format_parry_state(TurnTimelineParryState state)
    {
        return state switch
        {
            TurnTimelineParryState.Pending => "Pending",
            TurnTimelineParryState.Waiting => "Waiting",
            TurnTimelineParryState.Open => "Open",
            TurnTimelineParryState.Parried => "Parried",
            TurnTimelineParryState.Missed => "Missed",
            _ => "-"
        };
    }

    private static string format_parryability(TurnTimelineParryability parryability)
    {
        return parryability switch
        {
            TurnTimelineParryability.Parryable => "Yes",
            TurnTimelineParryability.Unknown => "Unknown",
            _ => "No"
        };
    }

    private static string format_lifecycle(TurnTimelineLifecycleState state, TurnTimelineRow row)
    {
        return state switch
        {
            TurnTimelineLifecycleState.Pending => "Pending",
            TurnTimelineLifecycleState.Active => row.QueueTotal > 0 ? $"Active ({row.QueuePosition}/{row.QueueTotal})" : "Active",
            _ => "Completed"
        };
    }

    private bool append_debug_event(string message)
    {
        if (!should_capture_debug_messages())
        {
            return false;
        }

        if (is_debug_message_throttled(message))
        {
            return false;
        }

        DateTime timestamp = current_gameplay_timestamp();
        double simulationSeconds = current_gameplay_seconds();

        if (_debugLog.Count > 0)
        {
            DebugLogEntry last = _debugLog[^1];
            if (string.Equals(last.Message, message, StringComparison.Ordinal))
            {
                last.RepeatCount++;
                last.TimestampLocal = timestamp;
                last.SimulationSeconds = simulationSeconds;
                last.FrameIndex = _debugFrameIndex;
                return false;
            }
        }

        if (_debugLog.Count >= DebugLogRingCapacity)
        {
            _debugLog.RemoveAt(0);
        }

        _debugLog.Add(new DebugLogEntry
        {
            TimestampLocal = timestamp,
            SimulationSeconds = simulationSeconds,
            FrameIndex = _debugFrameIndex,
            Message = message
        });
        write_session_debug_entry(_debugLog[^1]);
        return true;
    }

    private bool is_debug_message_throttled(string message)
    {
        ulong minFramesBetweenRepeats = message switch
        {
            "Parry input ignored (no parryable enemy cue)." => 120,
            "Parry release ignored (no active parryable enemy cue)." => 120,
            _ when message.StartsWith("Timeline integrity warning:", StringComparison.Ordinal) => 180,
            _ => 0
        };

        if (minFramesBetweenRepeats == 0)
        {
            return false;
        }

        if (_debugMessageLastEmitFrame.TryGetValue(message, out ulong lastFrame))
        {
            ulong delta = _debugFrameIndex >= lastFrame ? _debugFrameIndex - lastFrame : 0;
            if (delta <= minFramesBetweenRepeats)
            {
                return true;
            }
        }

        _debugMessageLastEmitFrame[message] = _debugFrameIndex;
        return false;
    }

    // ── FX / Motion Lab (debug, in-battle ID browser) ─────────────────────────
    // Step through hit-effect ids and motion ids live and watch them on a chosen
    // battler, so the right parry/guard/dodge visual is picked by eye. Effect =
    // MsBtlSetHitEffect (the same safe call the parry success visual uses). Motion =
    // MsSetMotion(slot, id, 0,0,1,0,0) — the exact crash-safe shape the engine's own
    // Defend code (MsDefenseStartProcess) uses.
    //
    // NOTE: there is intentionally NO effect "Stop"/auto-off. MsEtEffectStop frees the
    // effect batch WITHOUT re-initialising it (op_et_battle_effect_init / MsEtEffectSet),
    // after which the next MsBtlSetHitEffect operates on a freed batch and crashes the
    // game. Hit effects despawn on their own; a real stop would need the Free+Init pair.
    private int  _labTargetSlot;
    private int  _labEffectId = 0x4B;  // current parry-visual favourite
    private int  _labMotionId = 0x3C;  // 0x3C/0x3D = guard brace, 0x34 = covered (engine Defend poses)

    // Crash-safe motion browsing: before each MsSetMotion we persist the id and flush to disk.
    // If that call crashes the game, the pending file survives; on next launch the id is moved
    // to the persistent blocklist (_motionBlocklist) and skipped from then on.
    private readonly HashSet<int> _motionBlocklist = [];
    private string _motionPendingPath = string.Empty;
    private string _motionBlocklistPath = string.Empty;
    private bool   _motionBlocklistReady;

    // Evade probe (read-only): logs a party battler's move/avoid state transitions so a native
    // evade reveals the engine's real move-mode (back-hop vs. walk-back) — see tick_evade_probe.
    private bool _labEvadeProbe;
    // Party AND enemy slots: an attacker's post-attack "walk home" (move_mode 1) is what returns
    // it to its slot, so we must observe enemy move-modes too (e.g. a lunging dog left displaced
    // after a successful dodge).
    private readonly ulong[] _evadeProbePrev = new ulong[PartyActorCapacity + EnemyActorCapacity];

    // Quick-picks (confirmed-safe in testing). Stepping with -/+ fires as you go and can
    // still reach an unloaded effect id, which crashes natively — that risk is on the user.
    private static readonly int[] LabEffectQuickPicks = [0x4A, 0x4B];

    private void render_fx_motion_lab()
    {
        ImGui.Text($"Target: {lab_slot_label(_labTargetSlot)}");
        ImGui.SameLine(); if (ImGui.Button("<##labslot")) _labTargetSlot = lab_step_slot(-1);
        ImGui.SameLine(); if (ImGui.Button(">##labslot")) _labTargetSlot = lab_step_slot(+1);
        ImGui.SameLine(); if (ImGui.Button("Restore char##labslot")) lab_restore_char(); // re-show a model a status/death effect hid (experimental)
        ImGui.SameLine(); if (ImGui.Button("Clear FX##labslot")) lab_clear_char_fx();    // per-char effect reset, like the engine's own teardown (experimental)

        ImGui.Separator();
        ImGui.Text("Screen shake:");
        ImGui.SameLine();
        if (ImGui.Button($"Single ({ImpactShakeDuration / BattleFrameRate:F2}s)##labshake"))
            fire_screen_shake_ticks(ImpactShakeDuration, "lab single");
        ImGui.SameLine();
        if (ImGui.Button($"Whole-party ({ImpactShakeDurationWholeParty / BattleFrameRate:F2}s)##labshakewp"))
            fire_screen_shake_ticks(ImpactShakeDurationWholeParty, "lab whole-party");

        ImGui.Separator();
        render_freecam_panel();

        ImGui.Separator();
        ImGui.Text($"Hit effect: 0x{_labEffectId:X2}");
        ImGui.SameLine(); if (ImGui.Button("-##labfx")) { _labEffectId = Math.Max(0x00, _labEffectId - 1); lab_fire_effect(); }
        ImGui.SameLine(); if (ImGui.Button("+##labfx")) { _labEffectId = Math.Min(0xFF, _labEffectId + 1); lab_fire_effect(); }
        ImGui.SameLine(); if (ImGui.Button("Fire##labfx")) lab_fire_effect();
        foreach (int pick in LabEffectQuickPicks)
        {
            ImGui.SameLine();
            if (ImGui.Button($"0x{pick:X2}##labfx{pick}")) { _labEffectId = pick; lab_fire_effect(); }
        }
        ImGui.Text("quick-picks 0x4A 0x4B.  WARNING: -/+ fire as you step; an unloaded id crashes natively (uncatchable).");

        ImGui.Separator();
        bool motionBlocked = _motionBlocklist.Contains(_labMotionId);
        ImGui.Text($"Motion id: 0x{_labMotionId:X2}{(motionBlocked ? "  [BLOCKED]" : string.Empty)}");
        ImGui.SameLine(); if (ImGui.Button("-##labmot")) _labMotionId = Math.Max(0x00, _labMotionId - 1); // step only — Play to fire
        ImGui.SameLine(); if (ImGui.Button("+##labmot")) _labMotionId = Math.Min(0xFF, _labMotionId + 1);
        ImGui.SameLine(); if (ImGui.Button("Play##labmot")) lab_play_motion();
        ImGui.SameLine(); ImGui.Text($"blocklist: {_motionBlocklist.Count}");
        ImGui.SameLine(); if (ImGui.Button("Clear blocklist##labmot")) { _motionBlocklist.Clear(); save_motion_blocklist(); }
        ImGui.Text("known: 0x09 magic-hit  0x0C hit  0x1B flinch  0x30 heavy  0x34 covered  0x3C/0x3D guard  0x40 death  0x43 armored  0x4F stone");
        // Dodge step-back testing. Safe = a motion only (visual, no displacement — the native
        // evade is positional, so there is no real "dodge motion"; this just plays the chosen id).
        // The Evade probe (read-only) logs the engine's true move-mode/avoid during a native evade
        // — turn it on, then let a PC dodge an enemy hit — so the real move-engine back-step can be
        // built from confirmed values instead of inferred offsets.
        if (ImGui.Button("Dodge (motion, safe)##labdodge")) lab_play_motion();
        ImGui.SameLine(); ImGui.Checkbox("Evade probe##labdodge", ref _labEvadeProbe);
        ImGui.SameLine(); ImGui.Text("real move-engine dodge: deferred until the probe captures the move-mode");
        ImGui.Text("-/+ steps only; Play fires. A motion that crashes the game is auto-blocklisted on the next launch and then skipped.");
    }

    // Slot label: resolve the live battler at a slot to its character / monster name.
    private string lab_slot_label(int slot)
    {
        try
        {
            Chr* chr = try_get_chr((byte)slot);
            if (chr == null) return $"slot {slot} (empty)";
            if (slot < PartyActorCapacity)
                return try_map_party_chr_id_to_name(chr->chr_id, out string pn) ? $"{pn}  (slot {slot})" : $"party slot {slot}";
            return try_map_enemy_chr_id_to_name(chr->chr_id, out string en) ? $"{en}  (slot {slot})" : $"enemy slot {slot}";
        }
        catch { return $"slot {slot}"; }
    }

    // Step to the next slot that has a live actor (wraps 0..19); plain step as fallback.
    private int lab_step_slot(int dir)
    {
        for (int i = 1; i <= 20; i++)
        {
            int cand = (((_labTargetSlot + dir * i) % 20) + 20) % 20;
            try { if (try_get_chr((byte)cand) != null) return cand; } catch { /* ignore */ }
        }
        return (((_labTargetSlot + dir) % 20) + 20) % 20;
    }

    private void lab_fire_effect()
    {
        try
        {
            if (try_get_chr((byte)_labTargetSlot) == null) { log_debug($"[Lab] No live actor at slot {_labTargetSlot}."); return; }
            FhUtil.get_fptr<MsBtlSetHitEffectProbe>(
                ExternalMemoryOffsetMap.Functions.MsBtlSetHitEffect)((byte)_labTargetSlot, 0, _labEffectId, 1);
            log_debug($"[Lab] Fired hit effect 0x{_labEffectId:X2} on slot {_labTargetSlot}.");
        }
        catch (Exception ex) { log_debug($"[Lab] Fire effect failed: {ex.Message}"); }
    }

    // Experimental: re-show a battler model that a status/death effect (e.g. petrify-shatter)
    // hid, via the engine's own MsSetChrVisible(slot, 1). A focused visibility setter — unlike
    // the effect-batch free/init, which depends on battle-lifecycle state and crashed.
    private void lab_restore_char()
    {
        try
        {
            if (try_get_chr((byte)_labTargetSlot) == null) { log_debug($"[Lab] No live actor at slot {_labTargetSlot}."); return; }
            FhUtil.get_fptr<MsSetChrVisibleProbe>(
                ExternalMemoryOffsetMap.Functions.MsSetChrVisible)(_labTargetSlot, 1);
            log_debug($"[Lab] Restored visibility on slot {_labTargetSlot}.");
        }
        catch (Exception ex) { log_debug($"[Lab] Restore char failed: {ex.Message}"); }
    }

    // Experimental: clear the selected char's active effects via the engine's own per-character
    // teardown MsResetBindEffect(slot) (the same call MsBtlChrFree uses). Targeted (one char),
    // not the global op_et_battle_effect_free/init that crashed — worst case is a no-op.
    private void lab_clear_char_fx()
    {
        try
        {
            if (try_get_chr((byte)_labTargetSlot) == null) { log_debug($"[Lab] No live actor at slot {_labTargetSlot}."); return; }
            FhUtil.get_fptr<MsResetBindEffectProbe>(
                ExternalMemoryOffsetMap.Functions.MsResetBindEffect)((byte)_labTargetSlot);
            log_debug($"[Lab] Reset bind-effects on slot {_labTargetSlot}.");
        }
        catch (Exception ex) { log_debug($"[Lab] Clear FX failed: {ex.Message}"); }
    }

    private void lab_play_motion()
    {
        try
        {
            if (_motionBlocklist.Contains(_labMotionId)) { log_debug($"[Lab] Motion 0x{_labMotionId:X2} is blocklisted (crashed before) — skipped."); return; }
            if (try_get_chr((byte)_labTargetSlot) == null) { log_debug($"[Lab] No live actor at slot {_labTargetSlot}."); return; }

            // Persist + flush the id we're about to set BEFORE the native call. If MsSetMotion
            // crashes the process, the pending file survives and the id is blocklisted next launch.
            int attempted = _labMotionId;
            write_motion_pending(attempted);

            FhUtil.get_fptr<MsSetMotionProbe>(
                ExternalMemoryOffsetMap.Functions.MsSetMotion)(_labTargetSlot, attempted, 0, 0, 1, 0, 0);

            clear_motion_pending(); // returned without crashing → this id is fine
            if (_labTargetSlot < PartyActorCapacity) _motionPlayFrame[_labTargetSlot] = _debugFrameIndex; // MsEffectEndMotion duration probe
            log_debug($"[Lab] Played motion 0x{attempted:X2} on slot {_labTargetSlot}.");
        }
        catch (Exception ex) { clear_motion_pending(); log_debug($"[Lab] Play motion failed: {ex.Message}"); }
    }

    // ── motion crash-recovery blocklist ───────────────────────────────────────
    // The crash itself is the signal: if MsSetMotion takes down the process, the flushed
    // pending file is still on disk next launch, so that id is moved to the blocklist.
    private void initialize_motion_blocklist()
    {
        try
        {
            string? dir = string.IsNullOrWhiteSpace(_settingsFilePath) ? null : Path.GetDirectoryName(_settingsFilePath);
            if (string.IsNullOrWhiteSpace(dir)) { _logger.Warning("[Lab] Motion blocklist disabled (no settings dir)."); return; }

            _motionBlocklistPath = Path.Combine(dir, "fhparry-motion-blocklist.txt");
            _motionPendingPath   = Path.Combine(dir, "fhparry-motion-pending.txt");
            _motionBlocklistReady = true;

            if (File.Exists(_motionBlocklistPath))
            {
                foreach (string line in File.ReadAllLines(_motionBlocklistPath))
                    if (try_parse_motion_id(line, out int id)) _motionBlocklist.Add(id);
            }

            // A leftover pending file means the previous MsSetMotion call never returned.
            if (File.Exists(_motionPendingPath))
            {
                string pending = File.ReadAllText(_motionPendingPath).Trim();
                File.Delete(_motionPendingPath);
                if (try_parse_motion_id(pending, out int crashedId) && _motionBlocklist.Add(crashedId))
                {
                    save_motion_blocklist();
                    _logger.Info($"[Lab] Motion 0x{crashedId:X2} crashed the game last session — added to the blocklist.");
                }
            }

            _logger.Info($"[Lab] Motion blocklist ready ({_motionBlocklist.Count} ids).");
        }
        catch (Exception ex) { _logger.Warning($"[Lab] Motion blocklist init failed: {ex.Message}"); }
    }

    private void save_motion_blocklist()
    {
        if (!_motionBlocklistReady) return;
        try
        {
            List<int> ids = [.. _motionBlocklist];
            ids.Sort();
            File.WriteAllLines(_motionBlocklistPath, ids.Select(id => $"0x{id:X2}"));
        }
        catch (Exception ex) { log_debug($"[Lab] Save motion blocklist failed: {ex.Message}"); }
    }

    private void write_motion_pending(int id)
    {
        if (!_motionBlocklistReady) return;
        try
        {
            using FileStream fs = new(_motionPendingPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            byte[] bytes = Encoding.ASCII.GetBytes($"0x{id:X2}");
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(true); // flush to disk so the record survives a hard native crash
        }
        catch { /* best effort */ }
    }

    private void clear_motion_pending()
    {
        if (!_motionBlocklistReady) return;
        try { if (File.Exists(_motionPendingPath)) File.Delete(_motionPendingPath); }
        catch { /* best effort */ }
    }

    // Observe-only: while the Evade probe is on, poll each party battler's move/avoid state
    // (Chr+0x4AC motion_type, +0x415 move-mode, +0x425 avoid flag, offsets from the evade-
    // choreography RE) and log transitions. Captured during a native evade, this reveals the
    // real move-mode of the back-hop vs. the walk-back (mode 1) — the data needed to build a
    // true move-engine dodge from confirmed values. Reads only; never writes.
    private void tick_evade_probe()
    {
        if (!_labEvadeProbe) return;
        for (byte slot = 0; slot < PartyActorCapacity + EnemyActorCapacity; slot++)
        {
            Chr* chr = try_get_chr(slot);
            if (chr == null) { _evadeProbePrev[slot] = 0; continue; }

            uint motionType = *(uint*)((byte*)chr + 0x4AC);
            byte moveMode = (byte)(*((byte*)chr + 0x415) & 0x7F);
            byte avoid = *((byte*)chr + 0x425);

            ulong key = ((ulong)moveMode << 8) | avoid;
            if (key == _evadeProbePrev[slot]) continue;
            _evadeProbePrev[slot] = key;

            if (moveMode != 0 || avoid != 0)
                log_debug($"[EvadeProbe] {format_actor_slot(slot)} move_mode=0x{moveMode:X2} avoid=0x{avoid:X2} motion_type=0x{motionType:X4}");
        }
    }

    private static bool try_parse_motion_id(string s, out int id)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id);
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
    }

    // Impact-shake duration sweep. Only the duration varies; amplitude and both frequencies are
    // fixed, so each parry differs in exactly one dimension. Stages come from a shuffled bag, so the
    // order is unpredictable and no stage repeats back-to-back — a rising or falling sequence would
    // invite judging each shake against its neighbour instead of on its own.
    /// <summary>
    ///     The mod's own window. It exists because alpha11 removes FhSettingCustomRenderer
    ///     and offers no boolean or combo setting type, so there is nowhere in Fahrenheit's
    ///     settings panel left to draw our 17 controls.
    ///
    ///     Settings render unconditionally — you must be able to change the difficulty from
    ///     the main menu, before any save is loaded. The debug tabs carry the old gates
    ///     (save loaded, gameplay ready); they show a placeholder rather than vanishing, so
    ///     the tab bar does not reflow under the cursor. That matters most for the Lab tab,
    ///     whose widgets fire MsSetMotion and MsBtlSetHitEffect natively and crash
    ///     uncatchably on an unloaded id — with no gameplay it draws no widgets at all.
    /// </summary>
    private void render_debug_overlay()
    {
        update_overlay_proximity_opacity();
        drive_freecam();

        if (_overlayCollapsed)
        {
            render_overlay_collapsed_caret();
            return;
        }

        ImGui.SetNextWindowPos(_overlayWindowPos, ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(_overlayWindowSize, ImGuiCond.Appearing);
        ImGui.SetNextWindowBgAlpha(_overlayBgAlpha);

        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0f, 0f, 0f, _overlayBgAlpha));
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, _overlayContentAlpha);
        const ImGuiWindowFlags overlayFlags =
            ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoBringToFrontOnFocus
            | ImGuiWindowFlags.NoNavInputs
            | ImGuiWindowFlags.NoNavFocus;
        if (ImGui.Begin("###fhparry.window", overlayFlags))
        {
            capture_overlay_rect();

            if (ImGui.BeginTabBar("###fhparry.tabs"))
            {
                // Collapse caret, pinned to the top-right of the shared tab-bar header.
                if (ImGui.TabItemButton("v###fhparry.collapse", ImGuiTabItemFlags.Trailing | ImGuiTabItemFlags.NoTooltip))
                {
                    _overlayCollapsed = true;
                }

                if (ImGui.BeginTabItem("Settings"))
                {
                    render_settings_tab();
                    ImGui.EndTabItem();
                }

#if DEBUG
                bool liveReady = _optionDebugOverlay && _debugGameSaveLoaded && _debugGameplayReady;

                if (ImGui.BeginTabItem("Live"))
                {
                    if (liveReady) render_debug_activity_panels(MathF.Max(0f, ImGui.GetContentRegionAvail().Y));
                    else           render_debug_tab_placeholder();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Lab"))
                {
                    if (liveReady) render_fx_motion_lab();
                    else           render_debug_tab_placeholder();
                    ImGui.EndTabItem();
                }
#endif

                ImGui.EndTabBar();
            }
        }

        ImGui.End();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
    }

    // Collapsed state: a small square caret pinned to the top-right corner where the window's
    // header was, derived from the last captured window pos+size. Clicking it reopens the window
    // in place, at its previous size.
    private void render_overlay_collapsed_caret()
    {
        Vector2 caretPos = new(
            _overlayWindowPos.X + _overlayWindowSize.X - OverlayCaretSize - 4f,
            _overlayWindowPos.Y);
        ImGui.SetNextWindowPos(caretPos, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(_overlayBgAlpha);

        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0f, 0f, 0f, _overlayBgAlpha));
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, _overlayContentAlpha);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(2f, 2f));
        const ImGuiWindowFlags caretFlags =
            ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.AlwaysAutoResize
            | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoBringToFrontOnFocus
            | ImGuiWindowFlags.NoNavInputs
            | ImGuiWindowFlags.NoNavFocus;
        if (ImGui.Begin("###fhparry.caret", caretFlags))
        {
            // Proximity fade follows the caret's own rect while collapsed.
            _overlayPrevRectMin = ImGui.GetWindowPos();
            _overlayPrevRectMax = _overlayPrevRectMin + ImGui.GetWindowSize();
            if (ImGui.Button("<###fhparry.expand", new Vector2(OverlayCaretSize, OverlayCaretSize)))
            {
                _overlayCollapsed = false;
            }
        }
        ImGui.End();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();
    }

    // Eases the window's background and content opacity toward opaque while the mouse is within a
    // 40 px margin of last frame's window rect, and toward faint when it is away. Exponential,
    // frame-rate independent, ~150 ms time constant.
    private void update_overlay_proximity_opacity()
    {
        Vector2 mouse = ImGui.GetIO().MousePos;
        const float margin = 40f;
        bool near =
            mouse.X >= _overlayPrevRectMin.X - margin && mouse.X <= _overlayPrevRectMax.X + margin &&
            mouse.Y >= _overlayPrevRectMin.Y - margin && mouse.Y <= _overlayPrevRectMax.Y + margin;

        float targetBg      = near ? 0.55f : 0.15f;
        float targetContent = near ? 1.0f  : 0.75f;

        float dt = ImGui.GetIO().DeltaTime;
        float k = dt > 0f ? 1f - MathF.Exp(-dt / 0.15f) : 1f;
        _overlayBgAlpha      += (targetBg - _overlayBgAlpha) * k;
        _overlayContentAlpha += (targetContent - _overlayContentAlpha) * k;
    }

    private void capture_overlay_rect()
    {
        _overlayWindowPos = ImGui.GetWindowPos();
        _overlayWindowSize = ImGui.GetWindowSize();
        _overlayPrevRectMin = _overlayWindowPos;
        _overlayPrevRectMax = _overlayWindowPos + _overlayWindowSize;
    }

#if DEBUG
    private void render_debug_tab_placeholder()
    {
        if (!_optionDebugOverlay)      ImGui.TextDisabled("Debug overlay is off (Settings -> Diagnostics).");
        else if (!_debugGameSaveLoaded) ImGui.TextDisabled("Waiting for a save to load.");
        else                            ImGui.TextDisabled("Waiting for gameplay.");
    }
#endif

    private void render_debug_state_panel(float panelHeight)
    {
        float stateHeight = MathF.Max(120f, panelHeight);
        const ImGuiWindowFlags stateFlags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        if (!ImGui.BeginChild("###fhparry.debug.state", new Vector2(0f, stateHeight), ImGuiChildFlags.Borders, stateFlags))
        {
            ImGui.EndChild();
            return;
        }

        Btl* battle = _battleAdapter.GetBattle();
        int attackCueSize = battle != null ? battle->attack_cues_size : 0;
        string battleTime = format_battle_time(_debugBattleFrameIndex);
        int flushIndex = find_last_flush_index();
        int sinceFlush = Math.Max(0, _debugCueHistory.Count - (flushIndex + 1));
        bool hasNextThreat = try_get_next_enemy_party_cue(out _, out string nextDecision, out string nextReason);
        string timingValue = format_window_status_summary();
        string battleSummary = format_current_battle_summary();
        string lastCommandSummary = format_last_command_summary();

        const ImGuiTableFlags tableFlags = ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("###fhparry.debug.state.table", 4, tableFlags))
        {
            ImGui.TableSetupColumn("label1", ImGuiTableColumnFlags.WidthFixed, 170f);
            ImGui.TableSetupColumn("value1", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("label2", ImGuiTableColumnFlags.WidthFixed, 170f);
            ImGui.TableSetupColumn("value2", ImGuiTableColumnFlags.WidthStretch, 1f);

            render_state_row_pair(
                "Window", bool_to_on_off(_runtime.ParryWindowActive),
                "Input State", format_input_state());
            render_state_row_pair(
                "Impact Context", bool_to_yes_no(_runtime.AwaitingTurnEnd),
                "Parry Success", bool_to_yes_no(_runtime.ParryWindowSucceeded));
            render_state_row_pair(
                "Decision", hasNextThreat ? nextDecision : "None",
                "Gate", hasNextThreat ? nextReason : "Ready");
            render_state_row_pair(
                "Timing", timingValue,
                "Frame", $"F{_debugFrameIndex:D7}");
            render_state_row_pair(
                "Lockout", format_whiff_lockout_state(),
                "Recovery", "Enabled");
            render_state_row_pair(
                "Streak", format_parry_streak_state(),
                "Threshold", $"≥ {ParryStreakObserveThreshold}");
            render_state_row_pair(
                "Battle Time", battleTime,
                "Queue", $"Engine {attackCueSize} / Tracked {_debugCueSnapshots.Count}");
            render_state_row_pair(
                "Battle", battleSummary,
                "Last Cmd", lastCommandSummary);
            render_state_row_pair(
                "Impact Corr", truncate_display(format_correlation_stats(), 44),
                "Reject Top", truncate_display(format_top_correlation_reject(), 44));
            render_state_row_pair(
                "Last Parried", _runtime.LastParriedTargetMask != 0 ? format_party_target_mask(_runtime.LastParriedTargetMask) : "-",
                "Parried Time", _runtime.ParriedTextRemainingSeconds > 0f ? $"{_runtime.ParriedTextRemainingSeconds:F2}s" : "0.00s");
            render_state_row_pair(
                "Since Flush", sinceFlush.ToString(CultureInfo.InvariantCulture),
                "Mode", "Input -> Active Window -> Impact Resolve");

            ImGui.EndTable();
        }

        ImGui.EndChild();
    }

    private static void render_state_row_pair(string label1, string value1, string label2, string value2)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted(label1);
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(value1);
        ImGui.TableSetColumnIndex(2);
        ImGui.TextUnformatted(label2);
        ImGui.TableSetColumnIndex(3);
        ImGui.TextUnformatted(value2);
    }

    private void render_debug_activity_panels(float panelHeight)
    {
        if (!ImGui.BeginChild("###fhparry.debug.activity", new Vector2(0f, panelHeight), ImGuiChildFlags.None, ImGuiWindowFlags.None))
        {
            ImGui.EndChild();
            return;
        }

        const float splitterHeight = 6f;
        const float minStateHeight = 140f;
        const float minLogHeight = 110f;

        float availableHeight = ImGui.GetContentRegionAvail().Y;
        if (availableHeight <= (minStateHeight + minLogHeight + splitterHeight))
        {
            render_debug_state_panel(Math.Max(minStateHeight, availableHeight * 0.5f));
            ImGui.Separator();
            render_debug_log_panel(Math.Max(minLogHeight, ImGui.GetContentRegionAvail().Y));
            ImGui.EndChild();
            return;
        }

        float movableHeight = availableHeight - splitterHeight;
        float minRatio = minStateHeight / movableHeight;
        float maxRatio = 1f - (minLogHeight / movableHeight);
        _debugStatePanelRatio = Math.Clamp(_debugStatePanelRatio, minRatio, maxRatio);

        float stateHeight = movableHeight * _debugStatePanelRatio;
        float logHeight = movableHeight - stateHeight;

        render_debug_state_panel(stateHeight);

        Vector2 splitterSize = new(ImGui.GetContentRegionAvail().X, splitterHeight);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.25f, 0.25f, 0.25f, 0.9f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.38f, 0.38f, 0.38f, 0.95f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.5f, 0.5f, 0.5f, 1f));
        ImGui.Button("###fhparry.debug.splitter", splitterSize);
        if (ImGui.IsItemActive())
        {
            float delta = ImGui.GetIO().MouseDelta.Y;
            _debugStatePanelRatio = Math.Clamp(_debugStatePanelRatio + (delta / movableHeight), minRatio, maxRatio);
        }

        ImGui.PopStyleColor(3);
        render_debug_log_panel(logHeight);
        ImGui.EndChild();
    }

    private void render_debug_log_panel(float panelHeight)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0f, 0f, 0f, 0.45f));
        if (ImGui.BeginChild("###fhparry.debug.log", new Vector2(0f, panelHeight), ImGuiChildFlags.Borders, ImGuiWindowFlags.AlwaysVerticalScrollbar))
        {
            float scrollY = ImGui.GetScrollY();
            float maxScrollY = ImGui.GetScrollMaxY();
            bool wasAtBottom = maxScrollY <= 0f || scrollY >= maxScrollY - 2f;

            for (int i = 0; i < _debugLog.Count; i++)
            {
                DebugLogEntry entry = _debugLog[i];
                bool isCueFlush = entry.Message.StartsWith("Cue queue flushed.", StringComparison.Ordinal);
                string prefix = format_log_prefix(entry);
                string suffix = entry.RepeatCount > 1 ? $" (x{entry.RepeatCount})" : string.Empty;

                if (isCueFlush)
                {
                    ImGui.SeparatorText("Cue Flush");
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.95f, 0.85f, 0.35f, 1f));
                }

                Vector4? logColor = get_log_color(entry.Message);
                if (logColor.HasValue)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, logColor.Value);
                }

                ImGui.TextUnformatted(prefix);
                ImGui.SameLine();
                float wrapPos = ImGui.GetCursorPosX();
                ImGui.PushTextWrapPos();
                ImGui.SetCursorPosX(wrapPos);
                ImGui.TextWrapped(entry.Message + suffix);
                ImGui.PopTextWrapPos();

                if (logColor.HasValue)
                {
                    ImGui.PopStyleColor();
                }

                if (isCueFlush)
                {
                    ImGui.PopStyleColor();
                }
            }

            if (_debugAutoScroll && wasAtBottom)
            {
                ImGui.SetScrollHereY(1f);
            }
        }

        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    private string format_log_prefix(DebugLogEntry entry)
    {
        string time = format_simulation_clock(entry.SimulationSeconds);
        return $"[{time} F{entry.FrameIndex:D7}]";
    }

    private static string format_gameplay_timestamp(DateTime timestamp)
    {
        double seconds = (timestamp - DateTime.UnixEpoch).TotalSeconds;
        return format_simulation_clock(seconds);
    }

    private static string format_simulation_clock(double seconds)
    {
        double safe = Math.Max(0d, seconds);
        TimeSpan span = TimeSpan.FromSeconds(safe);
        return $"{(int)span.TotalHours:D2}:{span.Minutes:D2}:{span.Seconds:D2}";
    }

    private static string format_battle_time(ulong battleFrames)
    {
        double totalSeconds = battleFrames * FrameDurationSeconds;
        int minutes = (int)(totalSeconds / 60d);
        int seconds = (int)(totalSeconds % 60d);
        int milliseconds = (int)((totalSeconds - Math.Floor(totalSeconds)) * 1000d);
        return $"{minutes:D2}:{seconds:D2}.{milliseconds:D3}";
    }

    private static string bool_to_on_off(bool value) => value ? "On" : "Off";
    private static string bool_to_yes_no(bool value) => value ? "Yes" : "No";

    private string format_input_state() => _runtime.InputState switch
    {
        ParryInputState.Ready        => "Ready",
        ParryInputState.Open         => "Open (guard)",
        ParryInputState.Resolved     => "Resolved",
        ParryInputState.WhiffLockout => "WhiffLockout",
        _                            => "Unknown"
    };

    private string format_whiff_lockout_state()
    {
        if (_runtime.InputState != ParryInputState.WhiffLockout)
        {
            return "Idle";
        }
        float remainingMs = ParryDifficultyModel.TicksToMs(_runtime.WhiffLockoutRemainingTicks);
        float totalMs = ParryDifficultyModel.TicksToMs(_runtime.WhiffLockoutTotalTicks);
        return $"{remainingMs:F0}/{totalMs:F0}ms";
    }

    private string format_parry_streak_state()
    {
        // Compact per-slot list, omitting zero-streak slots so the row stays
        // readable. Empty result when no slot has a live streak.
        StringBuilder sb = new();
        for (int i = 0; i < PartyActorCapacity; i++)
        {
            byte streak = _consecutiveParriesPerSlot[i];
            if (streak == 0) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(format_actor_slot((byte)i));
            sb.Append(':');
            sb.Append(streak);
            if (streak >= ParryStreakObserveThreshold) sb.Append('!');
        }
        return sb.Length == 0 ? "-" : sb.ToString();
    }

    private string format_window_status_summary()
    {
        if (!_runtime.ParryWindowActive) return "Closed";

        float elapsedSeconds = ParryDifficultyModel.TicksToSeconds(Math.Max(_runtime.ParryWindowElapsedTicks, 0));
        return $"Open (lifecycle, elapsed {elapsedSeconds:F2}s)";
    }

    private static string format_window_type(BtlWindowType type)
    {
        return type switch
        {
            BtlWindowType.Main => "Main Command",
            BtlWindowType.BlackMagic => "Black Magic",
            BtlWindowType.WhiteMagic => "White Magic",
            BtlWindowType.Skill => "Skill",
            BtlWindowType.Overdrive => "Overdrive",
            BtlWindowType.Summon => "Summon",
            BtlWindowType.Item => "Item",
            BtlWindowType.Weapon => "Weapon",
            BtlWindowType.Change => "Party Change",
            BtlWindowType.Left => "Left Menu",
            BtlWindowType.Right => "Right Menu",
            BtlWindowType.Special => "Special",
            BtlWindowType.Armor => "Armor",
            BtlWindowType.Use => "Use",
            BtlWindowType.Mix => "Mix",
            BtlWindowType.SpareChange => "Spare Change",
            BtlWindowType.YojimboPay => "Yojimbo Pay",
            _ => $"Window {(ushort)type}"
        };
    }

    private void collect_live_cues(List<DebugCueSnapshot> output, out int rawCueCount)
    {
        rawCueCount = 0;
        if (!try_get_live_battle_context(out Btl* battle)) return;

        rawCueCount = battle->attack_cues_size;
        int totalCues = Math.Clamp(rawCueCount, 0, MaxAttackCueScan);
        for (int i = 0; i < totalCues; i++)
        {
            AttackCue cue = battle->attack_cues[i];
            output.Add(create_cue_snapshot(battle, (byte)i, cue));
        }
    }

    private DebugCueSnapshot create_cue_snapshot(Btl* battle, byte queueIndex, AttackCue cue)
    {
        int commandCount = Math.Clamp((int)cue.command_count, 0, 4);
        ResolvedCommandInfo resolvedCommand = resolve_command_for_cue(battle, queueIndex, cue);
        uint commandSignature = compute_command_signature(cue, commandCount);
        uint partyMask = extract_party_target_mask(cue);
        uint nonPartyMask = extract_non_party_target_mask(cue);

        Chr* attacker = try_get_chr(cue.attacker_id);
        bool isEnemy = should_flag_as_enemy(cue.attacker_id, attacker);
        bool isMagic = isEnemy && is_magic_like_attack(attacker);
        int ctb = attacker != null ? attacker->ram.current_ctb : -1;

        DebugCueCategory category = classify_cue_category(isEnemy, isMagic, partyMask);
        return new DebugCueSnapshot(
            queueIndex,
            cue.attacker_id,
            commandCount,
            resolvedCommand.CommandId,
            resolvedCommand.Label,
            resolvedCommand.Kind,
            resolvedCommand.DamageType,
            resolvedCommand.Source,
            resolvedCommand.Confidence,
            commandSignature,
            partyMask,
            nonPartyMask,
            isEnemy,
            isMagic,
            category,
            ctb);
    }

    private static uint compute_command_signature(AttackCue cue, int commandCount)
    {
        unchecked
        {
            uint hash = 2166136261u; // FNV-1a seed
            for (int i = 0; i < commandCount; i++)
            {
                uint targets = cue.command_list[i].targets;
                hash ^= targets;
                hash *= 16777619u;
                hash ^= (uint)i + 1u;
                hash *= 16777619u;
            }

            hash ^= (uint)commandCount;
            hash *= 16777619u;
            return hash;
        }
    }

    private static DebugCueCategory classify_cue_category(bool isEnemy, bool isMagic, uint partyMask)
    {
        if (!isEnemy) return DebugCueCategory.PartyOrSystem;
        if (partyMask == 0) return DebugCueCategory.EnemyNonParty;
        return isMagic ? DebugCueCategory.EnemyMagicParty : DebugCueCategory.EnemyPhysicalParty;
    }

    private string format_cue_brief(DebugCueSnapshot cue)
    {
        return $"{format_actor_slot(cue.AttackerId)} {format_turn_action(cue)} | cmds={cue.CommandCount} | targets={format_cue_targets(cue)}";
    }

    private static string format_cue_category(DebugCueCategory category)
    {
        return category switch
        {
            DebugCueCategory.EnemyPhysicalParty => "Physical",
            DebugCueCategory.EnemyMagicParty => "Magic",
            DebugCueCategory.EnemyNonParty => "Non-party",
            DebugCueCategory.PartyOrSystem => "Ally/System",
            _ => "Unknown"
        };
    }

    private string format_cue_targets(DebugCueSnapshot cue)
    {
        if (cue.PartyMask == 0 && cue.NonPartyMask == 0) return "None";
        if (cue.NonPartyMask == 0) return format_party_target_mask(cue.PartyMask);
        if (cue.PartyMask == 0) return format_non_party_target_mask(cue.NonPartyMask);
        return $"{format_party_target_mask(cue.PartyMask)} + {format_non_party_target_mask(cue.NonPartyMask)}";
    }

    private static string format_non_party_target_mask(uint mask)
    {
        int bitCount = 0;
        uint cursor = mask;
        while (cursor != 0)
        {
            bitCount += (int)(cursor & 1u);
            cursor >>= 1;
        }

        return bitCount > 0 ? $"Other targets ({bitCount})" : "Other targets";
    }

    private string describe_cue_decision(DebugCueSnapshot cue, out string gateReason)
    {
        if (!cue.IsEnemy)
        {
            gateReason = "Not an enemy action";
            return "Ignore";
        }

        if (cue.PartyMask == 0)
        {
            gateReason = "No ally targets";
            return "Ignore";
        }

        if (_runtime.ParryWindowActive)
        {
            gateReason = "Window currently active";
            return "Active";
        }

        gateReason = get_gate_block_reason();
        return "Ready";
    }

    private string get_gate_block_reason()
    {
        if (_runtime.ParryWindowActive) return "Parry window already open";
        if (!_runtime.AwaitingTurnEnd) return "No active enemy impact context";
        return "Ready";
    }

    private void append_cue_history(string eventTag, DebugCueSnapshot cue, string? decisionOverride = null, string? gateOverride = null)
    {
        string decision;
        string gate;
        if (decisionOverride != null && gateOverride != null)
        {
            decision = decisionOverride;
            gate = gateOverride;
        }
        else
        {
            decision = describe_cue_decision(cue, out gate);
        }

        append_cue_history(new DebugCueHistoryEntry(
            timestampLocal: current_gameplay_timestamp(),
            frameIndex: _debugFrameIndex,
            turnId: _debugCueTurnId,
            @event: eventTag,
            queueIndex: cue.QueueIndex,
            cueId: compute_cue_id(cue),
            attackerId: cue.AttackerId,
            commandCount: cue.CommandCount,
            queueDepth: _debugCueScratch.Count,
            actionableDepth: count_actionable_cues(_debugCueScratch),
            category: format_cue_category(cue.Category),
            targets: format_cue_targets(cue),
            decision: decision,
            gate: gate));
    }

    private void append_cue_flush_history()
    {
        append_cue_history(new DebugCueHistoryEntry(
            timestampLocal: current_gameplay_timestamp(),
            frameIndex: _debugFrameIndex,
            turnId: _debugCueTurnId,
            @event: "FLUSH",
            queueIndex: 0,
            cueId: "-",
            attackerId: 0,
            commandCount: 0,
            queueDepth: 0,
            actionableDepth: 0,
            category: "-",
            targets: "-",
            decision: "Flush",
            gate: "Queue empty"));
    }

    private static string compute_cue_id(DebugCueSnapshot cue)
    {
        return $"{cue.QueueIndex + 1:D2}-{cue.AttackerId:D2}-{cue.CommandCount:D1}";
    }

    private void append_cue_history(DebugCueHistoryEntry entry)
    {
        if (_debugCueHistory.Count >= CueHistoryRingCapacity)
        {
            _debugCueHistory.RemoveAt(0);
        }

        _debugCueHistory.Add(entry);
    }

    private int find_last_flush_index()
    {
        for (int i = _debugCueHistory.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_debugCueHistory[i].Event, "FLUSH", StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static void render_colored_event_tag(string eventTag)
    {
        Vector4 color = eventTag switch
        {
            "ADD" => new Vector4(0.35f, 0.95f, 0.35f, 1f),
            "UPD" => new Vector4(0.35f, 0.8f, 1f, 1f),
            "DEL" => new Vector4(0.98f, 0.7f, 0.35f, 1f),
            "FLUSH" => new Vector4(0.95f, 0.85f, 0.35f, 1f),
            _ => new Vector4(0.85f, 0.85f, 0.85f, 1f)
        };
        string label = eventTag switch
        {
            "ADD" => "Added",
            "UPD" => "Changed",
            "DEL" => "Consumed",
            "FLUSH" => "Flushed",
            _ => eventTag
        };

        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(label);
        ImGui.PopStyleColor();
    }

    private static void render_colored_decision(string decision)
    {
        Vector4 color = decision switch
        {
            "Open" => new Vector4(0.35f, 0.95f, 0.35f, 1f),
            "Blocked" => new Vector4(0.98f, 0.7f, 0.35f, 1f),
            "Ignore" => new Vector4(0.75f, 0.75f, 0.75f, 1f),
            "Consumed" => new Vector4(0.98f, 0.7f, 0.35f, 1f),
            "Flush" => new Vector4(0.95f, 0.85f, 0.35f, 1f),
            _ => new Vector4(0.85f, 0.85f, 0.85f, 1f)
        };

        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(decision);
        ImGui.PopStyleColor();
    }

    private static Vector4? get_log_color(string message)
    {
        // Green: parry success / HP negated / parry window open
        if (message.Contains("Parry resolved", StringComparison.Ordinal)) return new Vector4(0.28f, 0.95f, 0.42f, 1f);
        if (message.Contains("Turn complete —", StringComparison.Ordinal)) return new Vector4(0.28f, 0.95f, 0.42f, 1f);
        if (message.Contains("HP negated on finalization", StringComparison.Ordinal)) return new Vector4(0.28f, 0.95f, 0.42f, 1f);
        if (message.Contains("resolving parry at impact", StringComparison.Ordinal)) return new Vector4(0.28f, 0.95f, 0.42f, 1f);
        if (message.Contains("Parry window active at impact", StringComparison.Ordinal)) return new Vector4(0.28f, 0.95f, 0.42f, 1f);

        // Orange: window expired without hit
        if (message.Contains("Parry window expired", StringComparison.Ordinal)) return new Vector4(1.0f, 0.63f, 0.25f, 1f);

        // Red: impact missed / damage received / target KO'd
        if (message.Contains("Hit taken:", StringComparison.Ordinal)) return new Vector4(0.98f, 0.4f, 0.4f, 1f);
        if (message.Contains("Impact hit", StringComparison.Ordinal)) return new Vector4(0.98f, 0.4f, 0.4f, 1f);
        if (message.Contains("outside parry window", StringComparison.Ordinal)) return new Vector4(0.98f, 0.4f, 0.4f, 1f);
        if (message.Contains("Parry failed", StringComparison.Ordinal)) return new Vector4(0.98f, 0.4f, 0.4f, 1f);
        if (message.Contains("expired without", StringComparison.Ordinal)) return new Vector4(0.98f, 0.4f, 0.4f, 1f);

        // Yellow/amber: status block / non-parryable target / window armed but missed / finalization skipped
        if (message.Contains("Magic/special finalization skipped", StringComparison.Ordinal)) return new Vector4(0.95f, 0.75f, 0.30f, 1f);
        if (message.Contains("status block", StringComparison.Ordinal)) return new Vector4(0.95f, 0.75f, 0.30f, 1f);
        if (message.Contains("non-parryable", StringComparison.Ordinal)) return new Vector4(0.95f, 0.75f, 0.30f, 1f);
        if (message.Contains("Berserk", StringComparison.Ordinal) && message.Contains("parry skipped", StringComparison.Ordinal)) return new Vector4(0.95f, 0.75f, 0.30f, 1f);
        if (message.Contains("Blind", StringComparison.Ordinal) && message.Contains("parry skipped", StringComparison.Ordinal)) return new Vector4(0.95f, 0.75f, 0.30f, 1f);

        // Cyan: parry window opened (armed) / cue appeared
        if (message.Contains("Parry input armed", StringComparison.Ordinal)) return new Vector4(0.55f, 0.9f, 1f, 1f);
        if (message.StartsWith("Cue+ ", StringComparison.Ordinal)) return new Vector4(0.55f, 0.9f, 1f, 1f);
        if (message.StartsWith("Cue~ ", StringComparison.Ordinal)) return new Vector4(0.55f, 0.9f, 1f, 1f);

        // Correlation
        if (message.StartsWith("Impact correlation matched", StringComparison.Ordinal)) return new Vector4(0.40f, 0.95f, 0.45f, 1f);
        if (message.StartsWith("Impact correlation rejected", StringComparison.Ordinal)) return new Vector4(0.98f, 0.55f, 0.35f, 1f);
        if (message.StartsWith("Impact correlation summary", StringComparison.Ordinal)) return new Vector4(0.75f, 0.85f, 1f, 1f);

        // Hook impact detection
        if (message.Contains("Hook impact:", StringComparison.Ordinal)) return new Vector4(0.28f, 0.95f, 0.42f, 1f);

        // Gray/dim: routine state transitions (cue cleared, awaiting cleared, window closed)
        if (message.StartsWith("Cue- ", StringComparison.Ordinal)) return new Vector4(0.70f, 0.70f, 0.70f, 0.9f);
        if (message.Contains("Parry window closed", StringComparison.Ordinal)) return new Vector4(0.70f, 0.70f, 0.70f, 0.9f);
        if (message.Contains("parry context cleared", StringComparison.Ordinal)) return new Vector4(0.70f, 0.70f, 0.70f, 0.9f);
        if (message.Contains("Awaiting turn end cleared", StringComparison.Ordinal)) return new Vector4(0.70f, 0.70f, 0.70f, 0.9f);
        if (message.Contains("Cue queue flushed", StringComparison.Ordinal)) return new Vector4(0.70f, 0.70f, 0.70f, 0.9f);

        return null;
    }

    private string format_next_cue_summary()
    {
        if (_debugCueSnapshots.Count == 0)
        {
            return "None";
        }

        for (int i = 0; i < _debugCueSnapshots.Count; i++)
        {
            DebugCueSnapshot cue = _debugCueSnapshots[i];
            if (!cue.IsEnemy || cue.PartyMask == 0) continue;

            string decision = describe_cue_decision(cue, out string gateReason);
            return $"q{cue.QueueIndex} {format_actor_slot(cue.AttackerId)} | {format_cue_category(cue.Category)} | Targets: {format_party_target_mask(cue.PartyMask)} | Decision: {decision} ({gateReason})";
        }

        DebugCueSnapshot first = _debugCueSnapshots[0];
        return $"q{first.QueueIndex} {format_actor_slot(first.AttackerId)} | {format_cue_category(first.Category)} | Targets: {format_cue_targets(first)}";
    }

    private bool try_get_next_enemy_party_cue(out DebugCueSnapshot cue, out string decision, out string reason)
    {
        for (int i = 0; i < _debugCueSnapshots.Count; i++)
        {
            var candidate = _debugCueSnapshots[i];
            if (!candidate.IsEnemy || candidate.PartyMask == 0) continue;

            cue = candidate;
            decision = describe_cue_decision(candidate, out reason);
            return true;
        }

        cue = default;
        decision = "None";
        reason = "Ready";
        return false;
    }

    private static int count_actionable_cues(List<DebugCueSnapshot> cues)
    {
        int count = 0;
        for (int i = 0; i < cues.Count; i++)
        {
            if (cues[i].IsEnemy && cues[i].PartyMask != 0)
            {
                count++;
            }
        }

        return count;
    }

    private string format_party_target_mask(uint mask)
    {
        if (mask == 0) return "None";
        if ((mask & PlayerTargetMask) == PlayerTargetMask) return "All allies";

        var labels = new List<string>(PartyActorCapacity);
        for (int i = 0; i < PartyActorCapacity; i++)
        {
            uint bit = 1u << i;
            if ((mask & bit) == 0) continue;

            labels.Add(format_party_slot_label(i));
        }

        return labels.Count == 0 ? "None" : string.Join(", ", labels);
    }

    private string format_actor_slot(byte slot)
    {
        if (slot < PartyActorCapacity)
        {
            return format_party_slot_label(slot);
        }

        Chr* enemy = try_get_chr(slot);
        if (enemy != null && try_map_enemy_chr_id_to_name(enemy->chr_id, out string enemyName))
        {
            return enemyName;
        }

        int enemySlot = slot - PartyActorCapacity + 1;
        return $"E{enemySlot}";
    }

    private string format_party_slot_label(int slot)
    {
        Chr* chr = try_get_chr((byte)slot);
        if (chr != null && try_map_party_chr_id_to_name(chr->chr_id, out string name))
        {
            return name;
        }

        return $"P{slot + 1}";
    }

    private static bool try_map_party_chr_id_to_name(int chrId, out string name)
    {
        name = chrId switch
        {
            0 => "Tidus",
            1 => "Yuna",
            2 => "Auron",
            3 => "Kimahri",
            4 => "Wakka",
            5 => "Lulu",
            6 => "Rikku",
            7 => "Seymour",
            8 => "Valefor",
            9 => "Ifrit",
            10 => "Ixion",
            11 => "Shiva",
            12 => "Bahamut",
            13 => "Anima",
            14 => "Yojimbo",
            15 => "Cindy",
            16 => "Sandy",
            17 => "Mindy",
            _ => string.Empty
        };

        return !string.IsNullOrWhiteSpace(name);
    }


    private static uint extract_non_party_target_mask(AttackCue cue)
    {
        uint mask = 0;
        int commandCount = Math.Clamp((int)cue.command_count, 0, 4);
        for (int i = 0; i < commandCount; i++)
        {
            mask |= cue.command_list[i].targets;
        }

        return mask & ~PlayerTargetMask;
    }
}
