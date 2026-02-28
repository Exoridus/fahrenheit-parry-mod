namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule {
    private void record_timing_hit(int slotIndex) {
        if (_runtime.ActiveTiming == null) return;

        _runtime.ActiveTiming.Events.Add(new ParryTimingEvent {
            Type = "hit",
            Slot = slotIndex,
            TimeSeconds = frames_to_seconds(_runtime.ParryWindowElapsedFrames)
        });
    }

    private void start_timing_session(AttackCue cue, byte cueIndex, uint partyMask, int leadFramesUsed) {
        uint[]? commandTargets = null;
        int commandCount = Math.Clamp((int)cue.command_count, 0, 4);
        if (commandCount > 0) {
            commandTargets = new uint[commandCount];
            for (int i = 0; i < commandCount; i++)
                commandTargets[i] = cue.command_list[i].targets;
        }

        _runtime.ActiveTiming = new ParryTimingTimeline {
            TimestampUtc = DateTime.UtcNow.ToString("O"),
            AttackerId = cue.attacker_id,
            CueIndex = cueIndex,
            TargetMask = partyMask,
            CommandCount = (byte)commandCount,
            CommandTargets = commandTargets,
            TimingMode = _optionTimingMode,
            LeadSeconds = frames_to_seconds(leadFramesUsed),
            LegacyWindowSeconds = Math.Clamp(_optionWindowSeconds, WindowMinSeconds, WindowMaxSeconds),
            ResolveWindowSeconds = Math.Clamp(_optionResolveWindowSeconds, ResolveWindowMinSeconds, ResolveWindowMaxSeconds)
        };
    }

    private void finalize_timing_capture(string reason, bool parrySucceeded = false) {
        if (_runtime.ActiveTiming == null) return;

        _runtime.ActiveTiming.EndSeconds = frames_to_seconds(_runtime.ParryWindowElapsedFrames);
        _runtime.ActiveTiming.EndReason = reason;
        _runtime.ActiveTiming.ParrySucceeded = parrySucceeded;

        if (!string.IsNullOrWhiteSpace(_timingLogPath)) {
            try {
                if (can_write_timing_sample()) {
                    rotate_timing_log_if_needed();
                    string json = JsonSerializer.Serialize(_runtime.ActiveTiming, _timingJsonOptions);
                    File.AppendAllText(_timingLogPath!, json + Environment.NewLine);
                }
            }
            catch (Exception ex) {
                _logger.Warning($"Failed to write parry timing sample: {ex.Message}");
            }
        }

        _runtime.ActiveTiming = null;
    }

    private bool can_write_timing_sample() {
        DateTime now = DateTime.UtcNow;
        if ((now - _runtime.TimingLogWindowStartUtc).TotalMinutes >= 1) {
            _runtime.TimingLogWindowStartUtc = now;
            _runtime.TimingLogSamplesInWindow = 0;
            _runtime.TimingLogDropNotified = false;
        }

        if (_runtime.TimingLogSamplesInWindow >= TimingLogMaxSamplesPerMinute) {
            if (!_runtime.TimingLogDropNotified) {
                _logger.Warning($"[Parry] Timing log throttled to {TimingLogMaxSamplesPerMinute} samples/minute.");
                _runtime.TimingLogDropNotified = true;
            }

            return false;
        }

        _runtime.TimingLogSamplesInWindow++;
        return true;
    }

    private void rotate_timing_log_if_needed() {
        if (string.IsNullOrWhiteSpace(_timingLogPath) || !File.Exists(_timingLogPath)) {
            return;
        }

        var info = new FileInfo(_timingLogPath);
        if (info.Length <= TimingLogMaxBytes) {
            return;
        }

        string rotated = _timingLogPath + ".prev";
        if (File.Exists(rotated)) {
            File.Delete(rotated);
        }

        // Keep at most one previous snapshot and start a fresh active log file.
        File.Move(_timingLogPath, rotated);
    }

    private sealed class ParryTimingTimeline {
        public string TimestampUtc { get; init; } = DateTime.UtcNow.ToString("O");
        public byte AttackerId { get; init; }
        public byte CueIndex { get; init; }
        public uint TargetMask { get; init; }
        public byte CommandCount { get; init; }
        public uint[]? CommandTargets { get; init; }
        public ParryTimingMode TimingMode { get; init; }
        public float LeadSeconds { get; init; }
        public float LegacyWindowSeconds { get; init; }
        public float ResolveWindowSeconds { get; init; }
        public List<ParryTimingEvent> Events { get; } = [];
        public float? EndSeconds { get; set; }
        public string? EndReason { get; set; }
        public bool ParrySucceeded { get; set; }
    }

    private sealed class ParryTimingEvent {
        public string Type { get; init; } = string.Empty;
        public int Slot { get; init; }
        public float TimeSeconds { get; init; }
    }
}
