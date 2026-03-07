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
                _turnTimeline.BeginBattle();
                append_debug_event("Battle session detected.");
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

    private void monitor_cue_transitions()
    {
        _debugCueScratch.Clear();
        collect_live_cues(_debugCueScratch, out _);

        if (_debugCueSnapshots.Count == 0 && _debugCueScratch.Count > 0)
        {
            _debugCueTurnId++;
        }

        int maxCount = Math.Max(_debugCueSnapshots.Count, _debugCueScratch.Count);
        for (int i = 0; i < maxCount; i++)
        {
            bool hasPrev = i < _debugCueSnapshots.Count;
            bool hasCur = i < _debugCueScratch.Count;

            if (!hasPrev && hasCur)
            {
                DebugCueSnapshot added = _debugCueScratch[i];
                log_debug($"Cue+ q{added.QueueIndex}: {format_cue_brief(added)}");
                append_cue_history("ADD", added);
                continue;
            }

            if (hasPrev && !hasCur)
            {
                DebugCueSnapshot removed = _debugCueSnapshots[i];
                log_debug($"Cue- q{removed.QueueIndex}: {format_cue_brief(removed)}");
                append_cue_history("DEL", removed, "Consumed", "-");
                continue;
            }

            DebugCueSnapshot previous = _debugCueSnapshots[i];
            DebugCueSnapshot current = _debugCueScratch[i];
            if (!current.EqualsSemantic(previous))
            {
                log_debug($"Cue~ q{current.QueueIndex}: {format_cue_brief(previous)} -> {format_cue_brief(current)}");
                append_cue_history("UPD", current);
            }
        }

        if (_debugCueSnapshots.Count > 0 && _debugCueScratch.Count == 0)
        {
            log_debug("Cue queue flushed.");
            append_cue_flush_history();
            _turnRuntimeEvents.EmitQueueFlushed(_debugCueTurnId, current_gameplay_timestamp(), _debugFrameIndex);
        }

        sync_turn_timeline_from_cues();

        _debugCueSnapshots.Clear();
        _debugCueSnapshots.AddRange(_debugCueScratch);
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

    private void process_turn_runtime_events()
    {
        _debugRuntimeSignalScratch.Clear();
        _turnRuntimeEvents.Drain(_debugRuntimeSignalScratch);

        for (int i = 0; i < _debugRuntimeSignalScratch.Count; i++)
        {
            TurnTimelineRuntimeSignal signal = _debugRuntimeSignalScratch[i];
            switch (signal.Kind)
            {
                case TurnTimelineRuntimeSignalKind.CueSnapshot:
                    _turnTimeline.UpdateCues(
                        cues: signal.Cues ?? Array.Empty<TurnTimelineCueObservation>(),
                        cueTurnId: signal.CueTurnId,
                        timestampLocal: signal.TimestampLocal,
                        frameIndex: signal.FrameIndex,
                        parryWindowActive: signal.ParryWindowActive);
                    break;
                case TurnTimelineRuntimeSignalKind.DispatchStarted:
                    _turnTimeline.CorrelateDispatchStarted(
                        attackerId: signal.AttackerId,
                        queueIndex: signal.QueueIndex < 0 ? 0 : signal.QueueIndex,
                        timestampLocal: signal.TimestampLocal,
                        frameIndex: signal.FrameIndex,
                        parryWindowActive: signal.ParryWindowActive);
                    break;
                case TurnTimelineRuntimeSignalKind.DispatchConsumed:
                    _turnTimeline.CorrelateDispatchConsumed(
                        attackerId: signal.AttackerId,
                        queueIndex: signal.QueueIndex,
                        timestampLocal: signal.TimestampLocal,
                        frameIndex: signal.FrameIndex,
                        reason: string.IsNullOrWhiteSpace(signal.Reason) ? "consumed" : signal.Reason);
                    break;
                case TurnTimelineRuntimeSignalKind.DamageResolved:
                    string targetLabel = signal.TargetSlot >= 0
                        ? format_actor_slot((byte)signal.TargetSlot)
                        : "Unknown target";
                    _turnTimeline.CorrelateDamageResolved(
                        targetSlot: signal.TargetSlot,
                        timestampLocal: signal.TimestampLocal,
                        frameIndex: signal.FrameIndex,
                        attackerId: signal.AttackerId,
                        queueIndex: signal.QueueIndex,
                        commandId: signal.CommandId,
                        commandLabel: signal.CommandLabel,
                        sourceStage: signal.SourceStage,
                        targetLabel: targetLabel);
                    break;
                case TurnTimelineRuntimeSignalKind.ParryWindowOpened:
                    _turnTimeline.MarkActiveParryOpen(signal.TimestampLocal, signal.FrameIndex);
                    break;
                case TurnTimelineRuntimeSignalKind.ParrySucceeded:
                    _turnTimeline.MarkActiveParried(signal.TimestampLocal, signal.FrameIndex);
                    break;
                case TurnTimelineRuntimeSignalKind.ParryMissed:
                    _turnTimeline.MarkActiveMissed(signal.Reason, signal.TimestampLocal, signal.FrameIndex);
                    break;
                case TurnTimelineRuntimeSignalKind.QueueFlushed:
                    _turnTimeline.AppendFlushMarker(signal.CueTurnId, signal.TimestampLocal, signal.FrameIndex);
                    break;
            }
        }

        flush_turn_timeline_events_to_log();
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
            return $"{baseAction}: {truncate_display(cue.CommandLabel, 28)}";
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

    private void render_debug_overlay()
    {
        if (!_optionDebugOverlay) return;
        if (!_debugGameSaveLoaded) return;
        if (!_debugGameplayReady) return;

        ImGui.SetNextWindowBgAlpha(0.55f);
        ImGui.SetNextWindowPos(new Vector2(20f, 20f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(1020f, 620f), ImGuiCond.FirstUseEver);

        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0f, 0f, 0f, 0.55f));
        const ImGuiWindowFlags overlayFlags =
            ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoBringToFrontOnFocus
            | ImGuiWindowFlags.NoNavInputs
            | ImGuiWindowFlags.NoNavFocus;
        if (ImGui.Begin("Parry Debug Overlay###fhparry.debug.overlay", overlayFlags))
        {
            render_debug_activity_panels(MathF.Max(0f, ImGui.GetContentRegionAvail().Y));
        }

        ImGui.End();
        ImGui.PopStyleColor();
    }

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
        bool hasNextThreat = try_get_next_enemy_party_cue(out DebugCueSnapshot nextCue, out string nextDecision, out string nextReason);
        string nextActor = hasNextThreat ? format_actor_slot(nextCue.AttackerId) : "None";
        string nextType = hasNextThreat ? format_cue_category(nextCue.Category) : "None";
        string nextTarget = hasNextThreat ? format_party_target_mask(nextCue.PartyMask) : "None";
        string nextQueue = hasNextThreat ? $"Queue {nextCue.QueueIndex + 1}" : "None";
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
                "Parried Text", _runtime.ParriedTextRemainingSeconds > 0f ? "Visible" : "Hidden");
            render_state_row_pair(
                "Impact Context", bool_to_yes_no(_runtime.AwaitingTurnEnd),
                "Parry Success", bool_to_yes_no(_runtime.ParryWindowSucceeded));
            render_state_row_pair(
                "Next Actor", nextActor,
                "Next Type", nextType);
            render_state_row_pair(
                "Next Target", nextTarget,
                "Next Queue", nextQueue);
            render_state_row_pair(
                "Decision", hasNextThreat ? nextDecision : "None",
                "Gate", hasNextThreat ? nextReason : "Ready");
            render_state_row_pair(
                "Timing", timingValue,
                "Frame", $"F{_debugFrameIndex:D7}");
            render_state_row_pair(
                "Difficulty", ParryDifficultyModel.FormatName(_optionDifficulty),
                "Spam Tier", format_spam_state());
            render_state_row_pair(
                "Spam Armed", bool_to_yes_no(_runtime.SpamReleaseArmed),
                "Spam Calm", _runtime.SpamTierResetRemainingSeconds > 0f ? "Active" : "Idle");
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
                "Last Parried", _runtime.LastParriedTargetSlot >= 0 ? format_actor_slot((byte)_runtime.LastParriedTargetSlot) : "-",
                "Parried Time", _runtime.ParriedTextRemainingSeconds > 0f ? $"{_runtime.ParriedTextRemainingSeconds:F2}s" : "0.00s");
            render_state_row_pair(
                "Since Flush", sinceFlush.ToString(CultureInfo.InvariantCulture),
                "Mode", "Input -> Active Window -> Impact Resolve");
            render_state_row_pair(
                "CSV Map", _dataMappings.HasAny ? "Loaded" : "Not loaded",
                "Coverage", $"cmd:{_dataMappings.CommandCount} auto:{_dataMappings.AutoAbilityCount} key:{_dataMappings.KeyItemCount} mon:{_dataMappings.MonsterCount} btl:{_dataMappings.BattleCount} evt:{_dataMappings.EventCount}");
            render_state_row_pair(
                "Map Source", truncate_display(_dataMappings.SourceSummary, 44),
                "Map Status", truncate_display(_dataMappingStatus, 44));

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
        const float minCueHeight = 140f;
        const float minLogHeight = 110f;

        float availableHeight = ImGui.GetContentRegionAvail().Y;
        if (availableHeight <= (minCueHeight + minLogHeight + splitterHeight))
        {
            render_debug_cue_preview_panel(Math.Max(minCueHeight, availableHeight * 0.6f));
            ImGui.Separator();
            render_debug_log_panel(Math.Max(minLogHeight, ImGui.GetContentRegionAvail().Y));
            ImGui.EndChild();
            return;
        }

        float movableHeight = availableHeight - splitterHeight;
        float minRatio = minCueHeight / movableHeight;
        float maxRatio = 1f - (minLogHeight / movableHeight);
        _debugCuePanelRatio = Math.Clamp(_debugCuePanelRatio, minRatio, maxRatio);

        float cueHeight = movableHeight * _debugCuePanelRatio;
        float logHeight = movableHeight - cueHeight;

        render_debug_cue_preview_panel(cueHeight);

        Vector2 splitterSize = new(ImGui.GetContentRegionAvail().X, splitterHeight);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.25f, 0.25f, 0.25f, 0.9f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.38f, 0.38f, 0.38f, 0.95f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.5f, 0.5f, 0.5f, 1f));
        ImGui.Button("###fhparry.debug.splitter", splitterSize);
        bool splitterActive = ImGui.IsItemActive();
        if (splitterActive)
        {
            float delta = ImGui.GetIO().MouseDelta.Y;
            _debugCuePanelRatio = Math.Clamp(_debugCuePanelRatio + (delta / movableHeight), minRatio, maxRatio);
        }

        ImGui.PopStyleColor(3);
        render_debug_log_panel(logHeight);
        ImGui.EndChild();
    }

    private void render_debug_cue_preview_panel(float panelHeight)
    {
        if (!ImGui.BeginChild("###fhparry.debug.cues", new Vector2(0f, panelHeight), ImGuiChildFlags.Borders, ImGuiWindowFlags.None))
        {
            ImGui.EndChild();
            return;
        }

        int liveCount = 0;
        int completedCount = 0;
        for (int i = 0; i < _turnTimeline.RowCount; i++)
        {
            TurnTimelineRow row = _turnTimeline.GetRowAt(i);
            if (row.IsFlushMarker) continue;
            if (row.Lifecycle == TurnTimelineLifecycleState.Completed) completedCount++;
            else liveCount++;
        }
        ImGui.TextUnformatted($"Turn Timeline: active/pending={liveCount}, completed={completedCount}, stored={_turnTimeline.RowCount}/{_turnTimeline.Capacity}");

        Vector2 tableSize = new(0f, ImGui.GetContentRegionAvail().Y - 2f);
        const ImGuiTableFlags tableFlags =
            ImGuiTableFlags.Borders
            | ImGuiTableFlags.RowBg
            | ImGuiTableFlags.ScrollY
            | ImGuiTableFlags.SizingStretchProp
            | ImGuiTableFlags.Resizable;

        if (ImGui.BeginTable("###fhparry.debug.cue.table", 8, tableFlags, tableSize))
        {
            float scrollY = ImGui.GetScrollY();
            float maxScrollY = ImGui.GetScrollMaxY();
            bool wasAtBottom = maxScrollY <= 0f || scrollY >= maxScrollY - 2f;

            ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("Turn", ImGuiTableColumnFlags.WidthFixed, 92f);
            ImGui.TableSetupColumn("Actor", ImGuiTableColumnFlags.WidthStretch, 1.0f);
            ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthStretch, 0.8f);
            ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthStretch, 1.6f);
            ImGui.TableSetupColumn("Parryable", ImGuiTableColumnFlags.WidthFixed, 92f);
            ImGui.TableSetupColumn("Parry", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("Lifecycle", ImGuiTableColumnFlags.WidthStretch, 1.0f);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            for (int i = 0; i < _turnTimeline.RowCount; i++)
            {
                TurnTimelineRow row = _turnTimeline.GetRowAt(i);
                ImGui.TableNextRow();
                bool isMarker = row.IsFlushMarker || row.IsDiagnosticMarker;
                if (row.IsFlushMarker)
                {
                    uint rowColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.35f, 0.3f, 0.15f, 0.2f));
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, rowColor);
                }
                else if (row.IsDiagnosticMarker)
                {
                    uint rowColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.4f, 0.12f, 0.12f, 0.22f));
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, rowColor);
                }

                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted($"{format_gameplay_timestamp(row.TimestampLocal)} F{row.FrameIndex:D6}");
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(isMarker ? "-" : format_turn_id(row));
                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(row.IsFlushMarker ? "Queue Flush" : (row.IsDiagnosticMarker ? "Warning" : row.Actor));
                if (!isMarker && ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted($"slot={row.AttackerId} rowId={row.RowId}");
                    ImGui.TextUnformatted($"queue={row.QueuePosition}/{row.QueueTotal}");
                    if (row.Command.CommandId != 0)
                    {
                        string commandKind = string.IsNullOrWhiteSpace(row.Command.Kind) ? "command" : row.Command.Kind;
                        string commandLabel = !string.IsNullOrWhiteSpace(row.Command.Label) ? row.Command.Label : "(unmapped)";
                        ImGui.TextUnformatted($"cmd=0x{row.Command.CommandId:X4} ({commandKind})");
                        ImGui.TextWrapped($"label={truncate_display(commandLabel, 180)}");
                        ImGui.TextUnformatted($"source={row.Command.Source}, confidence={row.Command.Confidence}");
                    }
                    if (row.AttackerId >= PartyActorCapacity)
                    {
                        Chr* enemy = try_get_chr(row.AttackerId);
                        if (enemy != null)
                        {
                            if (_dataMappings.TryResolveMonsterSensor(enemy->chr_id, out string sensor))
                            {
                                ImGui.Separator();
                                ImGui.TextWrapped($"Sensor: {truncate_display(sensor, 180)}");
                            }

                            if (_dataMappings.TryResolveMonsterScan(enemy->chr_id, out string scan))
                            {
                                ImGui.TextWrapped($"Scan: {truncate_display(scan, 180)}");
                            }
                        }
                    }
                    ImGui.EndTooltip();
                }
                ImGui.TableSetColumnIndex(3);
                ImGui.TextUnformatted(row.Action);
                ImGui.TableSetColumnIndex(4);
                ImGui.TextUnformatted(row.Targets);
                ImGui.TableSetColumnIndex(5);
                ImGui.TextUnformatted(isMarker ? "-" : format_parryability(row.Parryability));
                ImGui.TableSetColumnIndex(6);
                ImGui.TextUnformatted(isMarker ? "-" : format_parry_state(row.ParryState));
                ImGui.TableSetColumnIndex(7);
                ImGui.TextUnformatted(isMarker ? "Completed" : format_lifecycle(row.Lifecycle, row));
            }

            if (_debugCueAutoScroll && wasAtBottom && _turnTimeline.RowCount > 0)
            {
                ImGui.SetScrollHereY(1f);
            }

            ImGui.EndTable();
        }

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

    private string format_spam_state()
    {
        int tier = ParryDifficultyModel.ClampTierIndex(_runtime.SpamTierIndex) + 1;
        float resetMs = MathF.Max(0f, _runtime.SpamTierResetRemainingSeconds) * 1000f;
        if (_runtime.SpamTierResetRemainingSeconds <= 0f)
        {
            return $"T{tier} (idle)";
        }

        return $"T{tier} (reset {resetMs:F0}ms)";
    }

    private string format_window_status_summary()
    {
        if (!_runtime.ParryWindowActive) return "Closed";

        float remainingSeconds = Math.Max(_runtime.ParryWindowRemainingSeconds, 0f);
        float elapsedSeconds = Math.Max(_runtime.ParryWindowElapsedSeconds, 0f);
        return $"Open ({remainingSeconds:F2}s left, elapsed {elapsedSeconds:F2}s)";
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
        int totalCues = ParryDecisionPlanner.ClampCueCount(rawCueCount, MaxAttackCueScan);
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
        if (message.StartsWith("Cue+ ", StringComparison.Ordinal)) return new Vector4(0.35f, 0.95f, 0.35f, 1f);
        if (message.StartsWith("Cue~ ", StringComparison.Ordinal)) return new Vector4(0.35f, 0.8f, 1f, 1f);
        if (message.StartsWith("Cue- ", StringComparison.Ordinal)) return new Vector4(0.98f, 0.7f, 0.35f, 1f);
        if (message.StartsWith("Impact correlation matched", StringComparison.Ordinal)) return new Vector4(0.40f, 0.95f, 0.45f, 1f);
        if (message.StartsWith("Impact correlation rejected", StringComparison.Ordinal)) return new Vector4(0.98f, 0.55f, 0.35f, 1f);
        if (message.StartsWith("Impact correlation summary", StringComparison.Ordinal)) return new Vector4(0.75f, 0.85f, 1f, 1f);
        if (message.Contains("Parry input armed", StringComparison.Ordinal)) return new Vector4(0.35f, 0.95f, 0.35f, 1f);
        if (message.Contains("Parry resolved on impact", StringComparison.Ordinal)) return new Vector4(0.35f, 0.95f, 0.35f, 1f);
        if (message.Contains("Parry failed", StringComparison.Ordinal)) return new Vector4(0.98f, 0.4f, 0.4f, 1f);
        if (message.Contains("blocked", StringComparison.Ordinal)) return new Vector4(0.95f, 0.8f, 0.35f, 1f);
        if (message.Contains("window open", StringComparison.Ordinal)) return new Vector4(0.55f, 0.9f, 1f, 1f);
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
