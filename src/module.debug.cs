namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule {
    private enum DebugCueCategory {
        EnemyPhysicalParty,
        EnemyMagicParty,
        EnemyNonParty,
        PartyOrSystem,
        Unknown
    }

    private readonly struct DebugCueSnapshot {
        public readonly byte QueueIndex;
        public readonly byte AttackerId;
        public readonly int CommandCount;
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
            uint partyMask,
            uint nonPartyMask,
            bool isEnemy,
            bool isMagic,
            DebugCueCategory category,
            int currentCtb) {
            QueueIndex = queueIndex;
            AttackerId = attackerId;
            CommandCount = commandCount;
            PartyMask = partyMask;
            NonPartyMask = nonPartyMask;
            IsEnemy = isEnemy;
            IsMagic = isMagic;
            Category = category;
            CurrentCtb = currentCtb;
        }

        public bool EqualsSemantic(in DebugCueSnapshot other) {
            return AttackerId == other.AttackerId
                && CommandCount == other.CommandCount
                && PartyMask == other.PartyMask
                && NonPartyMask == other.NonPartyMask
                && IsEnemy == other.IsEnemy
                && IsMagic == other.IsMagic
                && Category == other.Category;
        }
    }

    private readonly struct DebugCueHistoryEntry {
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
            string gate) {
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

    private void update_debug_battle_session_state() {
        bool active = _battleAdapter.GetBattle() != null;
        if (active) {
            if (!_debugBattleActive) {
                _debugBattleFrameIndex = 0;
                _debugCueTurnId = 0;
                append_debug_event("Battle session detected.");
            }
            else {
                _debugBattleFrameIndex++;
            }
        }
        else if (_debugBattleActive) {
            append_debug_event("Battle session ended.");
            _debugBattleFrameIndex = 0;
            _debugCueTurnId = 0;
        }

        _debugBattleActive = active;
    }

    private void update_debug_save_loaded_state() {
        FhFfx.SaveData* save = FhFfx.Globals.save_data;
        bool loaded = is_game_save_loaded(save);
        if (loaded && !_debugGameSaveLoaded) {
            append_debug_event("Game save detected. Debug overlay enabled.");
        }

        _debugGameSaveLoaded = loaded;
    }

    private static bool is_game_save_loaded(FhFfx.SaveData* save) {
        if (save == null) return false;

        // Keep this check pragmatic: in title/boot these stay zeroed, while loaded saves quickly
        // populate at least one of these routing/progression fields.
        if (save->saved_current_room_id != 0 || save->saved_now_eventjump_map_no != 0 || save->saved_now_eventjump_map_id != 0) return true;
        if (save->current_room_id != 0 || save->now_eventjump_map_no != 0 || save->now_eventjump_map_id != 0) return true;
        if (save->story_progress != 0 || save->time != 0) return true;

        return false;
    }

    private void monitor_cue_transitions() {
        _debugCueScratch.Clear();
        collect_live_cues(_debugCueScratch, out _);

        if (_debugCueSnapshots.Count == 0 && _debugCueScratch.Count > 0) {
            _debugCueTurnId++;
        }

        int maxCount = Math.Max(_debugCueSnapshots.Count, _debugCueScratch.Count);
        for (int i = 0; i < maxCount; i++) {
            bool hasPrev = i < _debugCueSnapshots.Count;
            bool hasCur = i < _debugCueScratch.Count;

            if (!hasPrev && hasCur) {
                DebugCueSnapshot added = _debugCueScratch[i];
                log_debug($"Cue+ q{added.QueueIndex}: {format_cue_brief(added)}");
                append_cue_history("ADD", added);
                continue;
            }

            if (hasPrev && !hasCur) {
                DebugCueSnapshot removed = _debugCueSnapshots[i];
                log_debug($"Cue- q{removed.QueueIndex}: {format_cue_brief(removed)}");
                append_cue_history("DEL", removed, "Consumed", "-");
                continue;
            }

            DebugCueSnapshot previous = _debugCueSnapshots[i];
            DebugCueSnapshot current = _debugCueScratch[i];
            if (!current.EqualsSemantic(previous)) {
                log_debug($"Cue~ q{current.QueueIndex}: {format_cue_brief(previous)} -> {format_cue_brief(current)}");
                append_cue_history("UPD", current);
            }
        }

        if (_debugCueSnapshots.Count > 0 && _debugCueScratch.Count == 0) {
            log_debug("Cue queue flushed.");
            append_cue_flush_history();
        }

        _debugCueSnapshots.Clear();
        _debugCueSnapshots.AddRange(_debugCueScratch);
    }

    private bool append_debug_event(string message) {
        DateTime timestamp = DateTime.Now;

        if (_debugLog.Count > 0) {
            DebugLogEntry last = _debugLog[^1];
            if (string.Equals(last.Message, message, StringComparison.Ordinal)) {
                last.RepeatCount++;
                last.TimestampLocal = timestamp;
                last.FrameIndex = _debugFrameIndex;
                return false;
            }
        }

        if (_debugLog.Count >= DebugLogRingCapacity) {
            _debugLog.RemoveAt(0);
        }

        _debugLog.Add(new DebugLogEntry {
            TimestampLocal = timestamp,
            FrameIndex = _debugFrameIndex,
            Message = message
        });
        return true;
    }

    private void render_debug_overlay() {
        if (!_optionDebugOverlay) return;
        if (!_debugGameSaveLoaded) return;

        ImGui.SetNextWindowBgAlpha(0.55f);
        ImGui.SetNextWindowPos(new Vector2(20f, 20f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(1020f, 620f), ImGuiCond.FirstUseEver);

        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0f, 0f, 0f, 0.55f));
        const ImGuiWindowFlags overlayFlags = ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNav;
        if (ImGui.Begin("Parry Debug Overlay###fhparry.debug.overlay", overlayFlags)) {
            const float splitterHeight = 6f;
            const float minStateHeight = 140f;
            const float minActivityHeight = 220f;

            float availableHeight = ImGui.GetContentRegionAvail().Y;
            if (availableHeight <= (minStateHeight + minActivityHeight + splitterHeight)) {
                float compactStateHeight = MathF.Max(minStateHeight, availableHeight * 0.32f);
                render_debug_state_panel(compactStateHeight);
                ImGui.Separator();
                render_debug_activity_panels(MathF.Max(0f, ImGui.GetContentRegionAvail().Y));
            }
            else {
                float movableHeight = availableHeight - splitterHeight;
                float minRatio = minStateHeight / movableHeight;
                float maxRatio = 1f - (minActivityHeight / movableHeight);
                _debugStatePanelRatio = Math.Clamp(_debugStatePanelRatio, minRatio, maxRatio);

                float stateHeight = movableHeight * _debugStatePanelRatio;
                float activityHeight = movableHeight - stateHeight;

                render_debug_state_panel(stateHeight);

                Vector2 splitterSize = new(ImGui.GetContentRegionAvail().X, splitterHeight);
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.25f, 0.25f, 0.25f, 0.9f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.38f, 0.38f, 0.38f, 0.95f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.5f, 0.5f, 0.5f, 1f));
                ImGui.Button("###fhparry.debug.state.splitter", splitterSize);
                if (ImGui.IsItemActive()) {
                    float delta = ImGui.GetIO().MouseDelta.Y;
                    _debugStatePanelRatio = Math.Clamp(_debugStatePanelRatio + (delta / movableHeight), minRatio, maxRatio);
                }
                ImGui.PopStyleColor(3);

                render_debug_activity_panels(activityHeight);
            }
        }

        ImGui.End();
        ImGui.PopStyleColor();
    }

    private void render_debug_state_panel(float panelHeight) {
        float stateHeight = MathF.Max(120f, panelHeight);
        const ImGuiWindowFlags stateFlags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        if (!ImGui.BeginChild("###fhparry.debug.state", new Vector2(0f, stateHeight), ImGuiChildFlags.Borders, stateFlags)) {
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

        const ImGuiTableFlags tableFlags = ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("###fhparry.debug.state.table", 4, tableFlags)) {
            ImGui.TableSetupColumn("label1", ImGuiTableColumnFlags.WidthFixed, 170f);
            ImGui.TableSetupColumn("value1", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("label2", ImGuiTableColumnFlags.WidthFixed, 170f);
            ImGui.TableSetupColumn("value2", ImGuiTableColumnFlags.WidthStretch, 1f);

            render_state_row_pair(
                "Window", bool_to_on_off(_runtime.ParryWindowActive),
                "Overlay", format_overlay_state(_runtime.OverlayState));
            render_state_row_pair(
                "Lead", bool_to_on_off(_runtime.LeadPending),
                "Await Resolve", bool_to_yes_no(_runtime.AwaitingTurnEnd));
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
                "Battle Time", battleTime,
                "Queue", $"Engine {attackCueSize} / Tracked {_debugCueSnapshots.Count}");
            render_state_row_pair(
                "Since Flush", sinceFlush.ToString(CultureInfo.InvariantCulture),
                "Damage Guard", format_negation_summary());

            ImGui.EndTable();
        }

        ImGui.EndChild();
    }

    private static void render_state_row_pair(string label1, string value1, string label2, string value2) {
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

    private void render_debug_activity_panels(float panelHeight) {
        if (!ImGui.BeginChild("###fhparry.debug.activity", new Vector2(0f, panelHeight), ImGuiChildFlags.None, ImGuiWindowFlags.None)) {
            ImGui.EndChild();
            return;
        }

        const float splitterHeight = 6f;
        const float minCueHeight = 140f;
        const float minLogHeight = 110f;

        float availableHeight = ImGui.GetContentRegionAvail().Y;
        if (availableHeight <= (minCueHeight + minLogHeight + splitterHeight)) {
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
        if (splitterActive) {
            float delta = ImGui.GetIO().MouseDelta.Y;
            _debugCuePanelRatio = Math.Clamp(_debugCuePanelRatio + (delta / movableHeight), minRatio, maxRatio);
        }

        ImGui.PopStyleColor(3);
        render_debug_log_panel(logHeight);
        ImGui.EndChild();
    }

    private void render_debug_cue_preview_panel(float panelHeight) {
        if (!ImGui.BeginChild("###fhparry.debug.cues", new Vector2(0f, panelHeight), ImGuiChildFlags.Borders, ImGuiWindowFlags.None)) {
            ImGui.EndChild();
            return;
        }

        _debugCueScratch.Clear();
        collect_live_cues(_debugCueScratch, out int rawCueCount);
        const int visibleHistoryLimit = 32;
        int rowCount = Math.Min(visibleHistoryLimit, _debugCueHistory.Count);
        int rowStart = Math.Max(0, _debugCueHistory.Count - rowCount);
        int currentActionable = count_actionable_cues(_debugCueScratch);
        ImGui.TextUnformatted($"Cue History (last {visibleHistoryLimit}): turn=T{_debugCueTurnId:D3}, queue={currentActionable}/{_debugCueScratch.Count} actionable/total, shown={rowCount}, stored={_debugCueHistory.Count}/{CueHistoryRingCapacity}");

        Vector2 tableSize = new(0f, ImGui.GetContentRegionAvail().Y - 2f);
        const ImGuiTableFlags tableFlags =
            ImGuiTableFlags.Borders
            | ImGuiTableFlags.RowBg
            | ImGuiTableFlags.ScrollY
            | ImGuiTableFlags.ScrollX
            | ImGuiTableFlags.SizingStretchProp
            | ImGuiTableFlags.Resizable;

        if (ImGui.BeginTable("###fhparry.debug.cue.table", 10, tableFlags, tableSize)) {
            float scrollY = ImGui.GetScrollY();
            float maxScrollY = ImGui.GetScrollMaxY();
            bool wasAtBottom = maxScrollY <= 0f || scrollY >= maxScrollY - 2f;

            ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.WidthStretch, 1.1f);
            ImGui.TableSetupColumn("Turn", ImGuiTableColumnFlags.WidthFixed, 68f);
            ImGui.TableSetupColumn("Stage", ImGuiTableColumnFlags.WidthFixed, 88f);
            ImGui.TableSetupColumn("Cue", ImGuiTableColumnFlags.WidthStretch, 1.1f);
            ImGui.TableSetupColumn("Actor", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthStretch, 0.8f);
            ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthStretch, 1.8f);
            ImGui.TableSetupColumn("Decision", ImGuiTableColumnFlags.WidthStretch, 0.9f);
            ImGui.TableSetupColumn("Queue", ImGuiTableColumnFlags.WidthFixed, 78f);
            ImGui.TableSetupColumn("Reason", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            for (int i = rowStart; i < _debugCueHistory.Count; i++) {
                DebugCueHistoryEntry row = _debugCueHistory[i];
                bool isFlush = string.Equals(row.Event, "FLUSH", StringComparison.Ordinal);

                ImGui.TableNextRow();
                if (isFlush) {
                    uint rowColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.35f, 0.3f, 0.15f, 0.2f));
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, rowColor);
                }
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted($"{row.TimestampLocal:HH:mm:ss} F{row.FrameIndex:D7}");
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted($"T{row.TurnId:D3}");
                ImGui.TableSetColumnIndex(2);
                render_colored_event_tag(row.Event);
                ImGui.TableSetColumnIndex(3);
                ImGui.TextUnformatted(isFlush ? "-" : $"Cue {row.CueId}");
                ImGui.TableSetColumnIndex(4);
                ImGui.TextUnformatted(isFlush ? "-" : format_actor_slot(row.AttackerId));
                ImGui.TableSetColumnIndex(5);
                ImGui.TextUnformatted(row.Category);
                ImGui.TableSetColumnIndex(6);
                ImGui.TextUnformatted(row.Targets);
                ImGui.TableSetColumnIndex(7);
                render_colored_decision(row.Decision);
                ImGui.TableSetColumnIndex(8);
                ImGui.TextUnformatted(isFlush ? "0/0" : $"{row.ActionableDepth}/{row.QueueDepth}");
                ImGui.TableSetColumnIndex(9);
                ImGui.TextUnformatted(row.Gate);
            }

            if (_debugCueAutoScroll && wasAtBottom && rowCount > 0) {
                ImGui.SetScrollHereY(1f);
            }

            ImGui.EndTable();
        }

        ImGui.EndChild();
    }

    private void render_debug_log_panel(float panelHeight) {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0f, 0f, 0f, 0.45f));
        if (ImGui.BeginChild("###fhparry.debug.log", new Vector2(0f, panelHeight), ImGuiChildFlags.Borders, ImGuiWindowFlags.AlwaysVerticalScrollbar)) {
            float scrollY = ImGui.GetScrollY();
            float maxScrollY = ImGui.GetScrollMaxY();
            bool wasAtBottom = maxScrollY <= 0f || scrollY >= maxScrollY - 2f;

            for (int i = 0; i < _debugLog.Count; i++) {
                DebugLogEntry entry = _debugLog[i];
                bool isCueFlush = entry.Message.StartsWith("Cue queue flushed.", StringComparison.Ordinal);
                string prefix = format_log_prefix(entry);
                string suffix = entry.RepeatCount > 1 ? $" (x{entry.RepeatCount})" : string.Empty;

                if (isCueFlush) {
                    ImGui.SeparatorText("Cue Flush");
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.95f, 0.85f, 0.35f, 1f));
                }

                Vector4? logColor = get_log_color(entry.Message);
                if (logColor.HasValue) {
                    ImGui.PushStyleColor(ImGuiCol.Text, logColor.Value);
                }

                ImGui.TextUnformatted(prefix);
                ImGui.SameLine();
                float wrapPos = ImGui.GetCursorPosX();
                ImGui.PushTextWrapPos();
                ImGui.SetCursorPosX(wrapPos);
                ImGui.TextWrapped(entry.Message + suffix);
                ImGui.PopTextWrapPos();

                if (logColor.HasValue) {
                    ImGui.PopStyleColor();
                }

                if (isCueFlush) {
                    ImGui.PopStyleColor();
                }
            }

            if (_debugAutoScroll && wasAtBottom) {
                ImGui.SetScrollHereY(1f);
            }
        }

        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    private string format_log_prefix(DebugLogEntry entry) {
        string time = entry.TimestampLocal.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        return $"[{time} F{entry.FrameIndex:D7}]";
    }

    private static string format_battle_time(ulong battleFrames) {
        double totalSeconds = battleFrames * FrameDurationSeconds;
        int minutes = (int)(totalSeconds / 60d);
        int seconds = (int)(totalSeconds % 60d);
        int milliseconds = (int)((totalSeconds - Math.Floor(totalSeconds)) * 1000d);
        return $"{minutes:D2}:{seconds:D2}.{milliseconds:D3}";
    }

    private static string bool_to_on_off(bool value) => value ? "On" : "Off";
    private static string bool_to_yes_no(bool value) => value ? "Yes" : "No";

    private string format_window_status_summary() {
        if (!_runtime.ParryWindowActive) return "Closed";

        float remainingSeconds = frames_to_seconds(Math.Max(_runtime.ParryWindowFrames, 0));
        float elapsedSeconds = frames_to_seconds(Math.Max(_runtime.ParryWindowElapsedFrames, 0));
        return $"Open ({remainingSeconds:F2}s left, elapsed {elapsedSeconds:F2}s)";
    }

    private string format_negation_summary() {
        if (_runtime.PendingNegateMask == 0) return "None";
        return $"{format_party_target_mask(_runtime.PendingNegateMask)} ({_runtime.PendingNegateTimeoutFrames}f left)";
    }

    private static string format_overlay_state(ParryOverlayState state) {
        return state switch {
            ParryOverlayState.Hidden => "Hidden",
            ParryOverlayState.Parry => "Parry Prompt",
            ParryOverlayState.Success => "Success Flash",
            ParryOverlayState.Failure => "Failure Flash",
            _ => "Unknown"
        };
    }

    private static string format_window_type(BtlWindowType type) {
        return type switch {
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

    private void collect_live_cues(List<DebugCueSnapshot> output, out int rawCueCount) {
        rawCueCount = 0;
        Btl* battle = _battleAdapter.GetBattle();
        if (battle == null) return;

        rawCueCount = battle->attack_cues_size;
        int totalCues = ParryDecisionPlanner.ClampCueCount(rawCueCount, MaxAttackCueScan);
        for (int i = 0; i < totalCues; i++) {
            AttackCue cue = battle->attack_cues[i];
            output.Add(create_cue_snapshot((byte)i, cue));
        }
    }

    private DebugCueSnapshot create_cue_snapshot(byte queueIndex, AttackCue cue) {
        int commandCount = Math.Clamp((int)cue.command_count, 0, 4);
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
            partyMask,
            nonPartyMask,
            isEnemy,
            isMagic,
            category,
            ctb);
    }

    private static DebugCueCategory classify_cue_category(bool isEnemy, bool isMagic, uint partyMask) {
        if (!isEnemy) return DebugCueCategory.PartyOrSystem;
        if (partyMask == 0) return DebugCueCategory.EnemyNonParty;
        return isMagic ? DebugCueCategory.EnemyMagicParty : DebugCueCategory.EnemyPhysicalParty;
    }

    private string format_cue_brief(DebugCueSnapshot cue) {
        return $"{format_actor_slot(cue.AttackerId)} {format_cue_category(cue.Category)} | cmds={cue.CommandCount} | targets={format_cue_targets(cue)}";
    }

    private static string format_cue_category(DebugCueCategory category) {
        return category switch {
            DebugCueCategory.EnemyPhysicalParty => "Physical",
            DebugCueCategory.EnemyMagicParty => "Magic",
            DebugCueCategory.EnemyNonParty => "Non-party",
            DebugCueCategory.PartyOrSystem => "Ally/System",
            _ => "Unknown"
        };
    }

    private string format_cue_targets(DebugCueSnapshot cue) {
        if (cue.PartyMask == 0 && cue.NonPartyMask == 0) return "None";
        if (cue.NonPartyMask == 0) return format_party_target_mask(cue.PartyMask);
        if (cue.PartyMask == 0) return format_non_party_target_mask(cue.NonPartyMask);
        return $"{format_party_target_mask(cue.PartyMask)} + {format_non_party_target_mask(cue.NonPartyMask)}";
    }

    private static string format_non_party_target_mask(uint mask) {
        int bitCount = 0;
        uint cursor = mask;
        while (cursor != 0) {
            bitCount += (int)(cursor & 1u);
            cursor >>= 1;
        }

        return bitCount > 0 ? $"Other targets ({bitCount})" : "Other targets";
    }

    private string describe_cue_decision(DebugCueSnapshot cue, out string gateReason) {
        if (!cue.IsEnemy) {
            gateReason = "Not an enemy action";
            return "Ignore";
        }

        if (cue.PartyMask == 0) {
            gateReason = "No ally targets";
            return "Ignore";
        }

        ParryStartAction action = ParryDecisionPlanner.PlanStartAction(
            hasCue: true,
            attackerId: cue.AttackerId,
            cueIndex: cue.QueueIndex,
            partyMask: cue.PartyMask,
            isMagic: cue.IsMagic,
            parryWindowActive: _runtime.ParryWindowActive,
            leadPending: _runtime.LeadPending,
            awaitingTurnEnd: _runtime.AwaitingTurnEnd,
            debounceFrames: _runtime.ParryWindowDebounceFrames,
            leadPhysicalFrames: compute_lead_frames(false),
            leadMagicFrames: compute_lead_frames(true),
            initialWindowFrames: compute_initial_window_frames());

        switch (action.Kind) {
            case ParryStartActionKind.OpenWindow:
                gateReason = "-";
                return "Open";
            case ParryStartActionKind.StartLead:
                gateReason = $"{action.LeadFrames}f";
                return "Lead";
            case ParryStartActionKind.IgnoreCueNoPartyTargets:
                gateReason = "No ally targets";
                return "Ignore";
            default:
                gateReason = get_gate_block_reason();
                return "Blocked";
        }
    }

    private string get_gate_block_reason() {
        if (_runtime.ParryWindowActive) return "Parry window already open";
        if (_runtime.LeadPending) return $"Lead delay active ({_runtime.LeadFramesRemaining}f left)";
        if (_runtime.AwaitingTurnEnd) return "Waiting for previous action to resolve";
        if (_runtime.ParryWindowDebounceFrames > 0) return $"Input cooldown ({_runtime.ParryWindowDebounceFrames}f)";
        return "Ready";
    }

    private void append_cue_history(string eventTag, DebugCueSnapshot cue, string? decisionOverride = null, string? gateOverride = null) {
        string decision;
        string gate;
        if (decisionOverride != null && gateOverride != null) {
            decision = decisionOverride;
            gate = gateOverride;
        }
        else {
            decision = describe_cue_decision(cue, out gate);
        }

        append_cue_history(new DebugCueHistoryEntry(
            timestampLocal: DateTime.Now,
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

    private void append_cue_flush_history() {
        append_cue_history(new DebugCueHistoryEntry(
            timestampLocal: DateTime.Now,
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

    private static string compute_cue_id(DebugCueSnapshot cue) {
        return $"{cue.QueueIndex + 1:D2}-{cue.AttackerId:D2}-{cue.CommandCount:D1}";
    }

    private void append_cue_history(DebugCueHistoryEntry entry) {
        if (_debugCueHistory.Count >= CueHistoryRingCapacity) {
            _debugCueHistory.RemoveAt(0);
        }

        _debugCueHistory.Add(entry);
    }

    private int find_last_flush_index() {
        for (int i = _debugCueHistory.Count - 1; i >= 0; i--) {
            if (string.Equals(_debugCueHistory[i].Event, "FLUSH", StringComparison.Ordinal)) {
                return i;
            }
        }

        return -1;
    }

    private static void render_colored_event_tag(string eventTag) {
        Vector4 color = eventTag switch {
            "ADD" => new Vector4(0.35f, 0.95f, 0.35f, 1f),
            "UPD" => new Vector4(0.35f, 0.8f, 1f, 1f),
            "DEL" => new Vector4(0.98f, 0.7f, 0.35f, 1f),
            "FLUSH" => new Vector4(0.95f, 0.85f, 0.35f, 1f),
            _ => new Vector4(0.85f, 0.85f, 0.85f, 1f)
        };
        string label = eventTag switch {
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

    private static void render_colored_decision(string decision) {
        Vector4 color = decision switch {
            "Open" => new Vector4(0.35f, 0.95f, 0.35f, 1f),
            "Lead" => new Vector4(0.95f, 0.85f, 0.35f, 1f),
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

    private static Vector4? get_log_color(string message) {
        if (message.StartsWith("Cue+ ", StringComparison.Ordinal)) return new Vector4(0.35f, 0.95f, 0.35f, 1f);
        if (message.StartsWith("Cue~ ", StringComparison.Ordinal)) return new Vector4(0.35f, 0.8f, 1f, 1f);
        if (message.StartsWith("Cue- ", StringComparison.Ordinal)) return new Vector4(0.98f, 0.7f, 0.35f, 1f);
        if (message.Contains("Parry input detected", StringComparison.Ordinal)) return new Vector4(0.35f, 0.95f, 0.35f, 1f);
        if (message.Contains("Parry failed", StringComparison.Ordinal)) return new Vector4(0.98f, 0.4f, 0.4f, 1f);
        if (message.Contains("blocked", StringComparison.Ordinal)) return new Vector4(0.95f, 0.8f, 0.35f, 1f);
        if (message.Contains("window open", StringComparison.Ordinal)) return new Vector4(0.55f, 0.9f, 1f, 1f);
        return null;
    }

    private string format_next_cue_summary() {
        if (_debugCueSnapshots.Count == 0) {
            return "None";
        }

        for (int i = 0; i < _debugCueSnapshots.Count; i++) {
            DebugCueSnapshot cue = _debugCueSnapshots[i];
            if (!cue.IsEnemy || cue.PartyMask == 0) continue;

            string decision = describe_cue_decision(cue, out string gateReason);
            return $"q{cue.QueueIndex} {format_actor_slot(cue.AttackerId)} | {format_cue_category(cue.Category)} | Targets: {format_party_target_mask(cue.PartyMask)} | Decision: {decision} ({gateReason})";
        }

        DebugCueSnapshot first = _debugCueSnapshots[0];
        return $"q{first.QueueIndex} {format_actor_slot(first.AttackerId)} | {format_cue_category(first.Category)} | Targets: {format_cue_targets(first)}";
    }

    private bool try_get_next_enemy_party_cue(out DebugCueSnapshot cue, out string decision, out string reason) {
        for (int i = 0; i < _debugCueSnapshots.Count; i++) {
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

    private static int count_actionable_cues(List<DebugCueSnapshot> cues) {
        int count = 0;
        for (int i = 0; i < cues.Count; i++) {
            if (cues[i].IsEnemy && cues[i].PartyMask != 0) {
                count++;
            }
        }

        return count;
    }

    private string format_party_target_mask(uint mask) {
        if (mask == 0) return "None";
        if ((mask & PlayerTargetMask) == PlayerTargetMask) return "All allies";

        var labels = new List<string>(PartyActorCapacity);
        for (int i = 0; i < PartyActorCapacity; i++) {
            uint bit = 1u << i;
            if ((mask & bit) == 0) continue;

            labels.Add(format_party_slot_label(i));
        }

        return labels.Count == 0 ? "None" : string.Join(", ", labels);
    }

    private string format_actor_slot(byte slot) {
        if (slot < PartyActorCapacity) {
            return format_party_slot_label(slot);
        }

        int enemySlot = slot - PartyActorCapacity + 1;
        return $"Enemy {enemySlot}";
    }

    private string format_party_slot_label(int slot) {
        Chr* chr = try_get_chr((byte)slot);
        if (chr != null && try_map_party_chr_id_to_name(chr->chr_id, out string name)) {
            return name;
        }

        return $"Ally {slot + 1}";
    }

    private static bool try_map_party_chr_id_to_name(int chrId, out string name) {
        name = chrId switch {
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

    private static uint extract_non_party_target_mask(AttackCue cue) {
        uint mask = 0;
        int commandCount = Math.Clamp((int)cue.command_count, 0, 4);
        for (int i = 0; i < commandCount; i++) {
            mask |= cue.command_list[i].targets;
        }

        return mask & ~PlayerTargetMask;
    }
}
