namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule
{
    private void initialize_session_logging(FhModContext modContext)
    {
        if (_sessionLogDisabled) return;

        try
        {
            string modDir = modContext.Paths.ModDir.FullName;
            DirectoryInfo? modsDir = Directory.GetParent(modDir);
            string fhRoot = (modsDir != null
                             && modsDir.Parent != null
                             && modsDir.Name.Equals("mods", StringComparison.OrdinalIgnoreCase))
                ? modsDir.Parent.FullName
                : modDir;

            _sessionLogsRoot = Path.Combine(fhRoot, "logs", "fhparry");
            string sessionId = $"session_{DateTime.Now:yyyyMMdd_HHmmss}";
            _sessionLogDirectory = create_unique_session_directory(_sessionLogsRoot, sessionId);
            Directory.CreateDirectory(_sessionLogDirectory);

            string debugPath = Path.Combine(_sessionLogDirectory, "debug-window.log");
            string timelinePath = Path.Combine(_sessionLogDirectory, "turn-timeline.tsv");

            _sessionDebugLogWriter = create_session_writer(debugPath);
            _sessionTimelineLogWriter = create_session_writer(timelinePath);
            if (_optionStartupProbeMode)
            {
                string startupProbePath = Path.Combine(_sessionLogDirectory, "startup-probe.tsv");
                _sessionStartupProbeWriter = create_session_writer(startupProbePath);
            }

            string startedLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            _sessionDebugLogWriter.WriteLine($"# fhparry session: {Path.GetFileName(_sessionLogDirectory)}");
            _sessionDebugLogWriter.WriteLine($"# started_local: {startedLocal}");
            _sessionDebugLogWriter.WriteLine($"# log_dir: {_sessionLogDirectory}");
            _sessionDebugLogWriter.WriteLine($"# process_id: {Environment.ProcessId}");
            _sessionDebugLogWriter.WriteLine("# format: [hh:mm:ss Fxxxxxxx] message");
            _sessionDebugLogWriter.WriteLine();

            _sessionTimelineLogWriter.WriteLine(
                "Time\tFrame\tEvent\tRowId\tTurn\tActor\tAction\tTargets\tParryable\tParry\tLifecycle\tQueue\tAttacker\tCommand\tCommandMeta\tMessage");

            _logger.Info($"[Parry] Session logging enabled. Dir: {_sessionLogDirectory}");
        }
        catch (Exception ex)
        {
            _sessionLogDisabled = true;
            _sessionDebugLogWriter = null;
            _sessionTimelineLogWriter = null;
            _sessionStartupProbeWriter = null;
            _logger.Warning($"[Parry] Session logging disabled: {ex.Message}");
        }
    }

    private static string create_unique_session_directory(string root, string preferredName)
    {
        Directory.CreateDirectory(root);
        string candidate = Path.Combine(root, preferredName);
        if (!Directory.Exists(candidate))
        {
            return candidate;
        }

        for (int i = 1; i <= 99; i++)
        {
            string withSuffix = Path.Combine(root, $"{preferredName}_{i:D2}");
            if (!Directory.Exists(withSuffix))
            {
                return withSuffix;
            }
        }

        return Path.Combine(root, $"{preferredName}_{DateTime.Now:fff}");
    }

    private static StreamWriter create_session_writer(string path)
    {
        return new StreamWriter(
            new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
    }

    private void write_session_debug_entry(in DebugLogEntry entry)
    {
        if (_sessionLogDisabled || _sessionDebugLogWriter == null) return;

        try
        {
            string prefix = format_log_prefix(entry);
            _sessionDebugLogWriter.WriteLine($"{prefix} {entry.Message}");
        }
        catch (Exception ex)
        {
            disable_session_logging($"debug write failed: {ex.Message}");
        }
    }

    private void write_session_timeline_event(in TurnTimelineEvent evt)
    {
        if (_sessionLogDisabled || _sessionTimelineLogWriter == null) return;

        try
        {
            TurnTimelineRow? row = null;
            if (evt.RowId > 0)
            {
                _turnTimeline.TryGetRowById(evt.RowId, out row);
            }

            string time = format_gameplay_timestamp(evt.TimestampLocal);
            string frame = $"F{evt.FrameIndex:D7}";
            string eventKind = evt.Kind.ToString();
            string rowId = evt.RowId > 0 ? evt.RowId.ToString(CultureInfo.InvariantCulture) : "-";
            string turn = row != null && !row.IsFlushMarker && !row.IsDiagnosticMarker ? format_turn_id(row) : "-";
            string actor = row?.Actor ?? "-";
            string action = row?.Action ?? "-";
            string targets = row?.Targets ?? "-";
            string parryable = row != null ? format_parryability(row.Parryability) : "-";
            string parry = row != null ? format_parry_state(row.ParryState) : "-";
            string lifecycle = row != null ? format_lifecycle(row.Lifecycle, row) : "-";
            string queue = row != null && row.QueueTotal > 0
                ? $"{row.QueuePosition}/{row.QueueTotal}"
                : "-";
            string attacker = row != null ? format_actor_slot(row.AttackerId) : "-";
            string command = row != null && row.Command.CommandId != 0
                ? $"0x{row.Command.CommandId:X4}"
                : "-";
            string commandMeta = row != null && row.Command.CommandId != 0
                ? $"{row.Command.Kind}|{row.Command.Source}|{row.Command.Confidence}|{truncate_display(row.Command.Label, 80)}"
                : "-";

            _sessionTimelineLogWriter.WriteLine(
                $"{tsv(time)}\t{tsv(frame)}\t{tsv(eventKind)}\t{tsv(rowId)}\t{tsv(turn)}\t{tsv(actor)}\t{tsv(action)}\t{tsv(targets)}\t{tsv(parryable)}\t{tsv(parry)}\t{tsv(lifecycle)}\t{tsv(queue)}\t{tsv(attacker)}\t{tsv(command)}\t{tsv(commandMeta)}\t{tsv(evt.Message)}");
        }
        catch (Exception ex)
        {
            disable_session_logging($"timeline write failed: {ex.Message}");
        }
    }

    private static string tsv(string value)
    {
        if (string.IsNullOrEmpty(value)) return "-";
        return value
            .Replace('\t', ' ')
            .Replace('\r', ' ')
            .Replace('\n', ' ');
    }

    private void disable_session_logging(string reason)
    {
        if (_sessionLogDisabled) return;

        _sessionLogDisabled = true;
        _logger.Warning($"[Parry] Session logging disabled: {reason}");

        try
        {
            _sessionDebugLogWriter?.Flush();
            _sessionDebugLogWriter?.Dispose();
        }
        catch
        {
            // ignored
        }

        try
        {
            _sessionTimelineLogWriter?.Flush();
            _sessionTimelineLogWriter?.Dispose();
        }
        catch
        {
            // ignored
        }

        try
        {
            _sessionStartupProbeWriter?.Flush();
            _sessionStartupProbeWriter?.Dispose();
        }
        catch
        {
            // ignored
        }

        _sessionDebugLogWriter = null;
        _sessionTimelineLogWriter = null;
        _sessionStartupProbeWriter = null;
    }

    private void write_session_startup_probe_header_if_needed()
    {
        if (_sessionLogDisabled || _sessionStartupProbeWriter == null || _startupProbeHeaderWritten) return;

        try
        {
            _sessionStartupProbeWriter.WriteLine(
                "Time\tFrame\tReason\tEventId\tEventName\tMenuState_0xF407E4\tNeedAutoSave\tMoviePlay_0xD2A008\tState_0xD36FA0\tState_0xD36FA4\tSaveData0C88\tGameplayReadyOverlay\tGameplayReadyStartup\tBattleActive");
            _startupProbeHeaderWritten = true;
        }
        catch (Exception ex)
        {
            disable_session_logging($"startup probe header write failed: {ex.Message}");
        }
    }

    private void write_session_startup_probe_entry(
        DateTime timestampLocal,
        ulong frameIndex,
        string reason,
        int eventId,
        string eventName,
        int menuState,
        int needAutoSave,
        int moviePlay,
        int stateD36FA0,
        int stateD36FA4,
        int saveData0C88,
        bool gameplayReadyOverlay,
        bool gameplayReadyStartup,
        bool battleActive)
    {
        if (_sessionLogDisabled || _sessionStartupProbeWriter == null) return;

        write_session_startup_probe_header_if_needed();
        if (_sessionLogDisabled || _sessionStartupProbeWriter == null) return;

        try
        {
            string time = format_gameplay_timestamp(timestampLocal);
            string frame = $"F{frameIndex:D7}";
            _sessionStartupProbeWriter.WriteLine(
                $"{tsv(time)}\t{tsv(frame)}\t{tsv(reason)}\t{eventId}\t{tsv(eventName)}\t{menuState}\t{needAutoSave}\t{moviePlay}\t{stateD36FA0}\t{stateD36FA4}\t{saveData0C88}\t{(gameplayReadyOverlay ? 1 : 0)}\t{(gameplayReadyStartup ? 1 : 0)}\t{(battleActive ? 1 : 0)}");
        }
        catch (Exception ex)
        {
            disable_session_logging($"startup probe write failed: {ex.Message}");
        }
    }

    private void prune_old_session_logs_if_needed()
    {
        if (_sessionRetentionPruned || _sessionLogDisabled) return;
        if (string.IsNullOrWhiteSpace(_sessionLogsRoot) || !Directory.Exists(_sessionLogsRoot))
        {
            _sessionRetentionPruned = true;
            return;
        }

        try
        {
            const int keepCount = 10;
            DirectoryInfo root = new(_sessionLogsRoot);
            List<DirectoryInfo> sessions = root
                .EnumerateDirectories("session_*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (sessions.Count > keepCount)
            {
                for (int i = keepCount; i < sessions.Count; i++)
                {
                    try
                    {
                        sessions[i].Delete(recursive: true);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning($"[Parry] Could not delete old session log '{sessions[i].FullName}': {ex.Message}");
                    }
                }
            }

            _sessionRetentionPruned = true;
        }
        catch (Exception ex)
        {
            _logger.Warning($"[Parry] Session log retention skipped: {ex.Message}");
            _sessionRetentionPruned = true;
        }
    }
}
