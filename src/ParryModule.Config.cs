namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule
{
    private sealed class PersistedSettings
    {
        public bool? Enabled { get; set; }
        public bool? Sound { get; set; }
        public bool? Logging { get; set; }
        public bool? DebugOverlay { get; set; }
        // Native-engine probe channel (separate from general Logging). Default-off.
        // When true, probe-tagged events are queued into the frame-deferred ring
        // and flushed once per pre-update tick. Future Stage-1 observe probes
        // will route through this; the existing logging path is unchanged.
        public bool? NativeProbeLogging { get; set; }
        public string? BattleCameraLockMode { get; set; }  // canonical — human-readable enum name
        public bool? EnemyCameraLock { get; set; }          // legacy — migrated on load, never written
        public bool? ParryEffect { get; set; }
        public bool? ImpactShake { get; set; }
        public bool? ImpactShakeSweep { get; set; }
        public bool? StreakCounter { get; set; }
        public bool? DodgeEnabled { get; set; }
        public bool? ParryNativeBlock { get; set; }
        public bool? CameraProbe { get; set; }
        public int? CheckHitHitValue { get; set; }
        public string? Difficulty { get; set; }
    }

    private static readonly JsonSerializerOptions PersistedSettingsJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    // Through alpha10, mod_context.Paths.SettingsPath is empty or points at the DEPLOYED MOD FOLDER
    // (alpha10 resolves it to Path.Join(mod_dir, "<mod_name>.config.json")), which
    // `build.cmd deploy` mirrors — anything written there is deleted on the next deploy. We instead
    // use the mod's global-state directory (state/global/<mod>/): the framework hands us a FileStream
    // into it at init, so its directory is a stable home for our files.
    //
    // A full deploy mirrors the ENTIRE Fahrenheit tree, state/ included, and deletes whatever the
    // source lacks. That directory therefore only survives because "state" is listed in
    // DeployPreservePaths (build/Build.cs). Remove it from that list and every deploy silently
    // resets the user's settings — which is exactly what happened before 2026-07-10.
    //
    // The old locations remain as fallbacks for hosts that don't supply a global state file.
    private static string resolve_settings_path(FhModContext mod_context, FileStream? global_state_file)
    {
        string? stateDir = null;
        try { stateDir = Path.GetDirectoryName(global_state_file?.Name); } catch { /* not path-backed */ }
        if (!string.IsNullOrWhiteSpace(stateDir) && Directory.Exists(stateDir))
        {
            return Path.Combine(stateDir, "fhparry.config.json");
        }

        string p = mod_context.Paths.SettingsPath ?? string.Empty;
        if (Directory.Exists(p))
        {
            return Path.Combine(p, "fhparry.config.json");
        }
        if (string.IsNullOrWhiteSpace(p))
        {
            string baseDir = mod_context.Paths.ResourcesDir?.FullName ?? AppContext.BaseDirectory;
            return Path.Combine(baseDir, "fhparry.config.json");
        }
        return p;
    }

    private void load_persistent_settings()
    {
        if (string.IsNullOrWhiteSpace(_settingsFilePath))
        {
            return;
        }

        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                return;
            }

            string json = File.ReadAllText(_settingsFilePath);
            PersistedSettings? persisted = JsonSerializer.Deserialize<PersistedSettings>(json, PersistedSettingsJsonOptions);
            // Legacy fields (for example prior audioVolume slider state) are ignored on load.
            if (persisted == null)
            {
                return;
            }

            if (persisted.Enabled.HasValue) _optionEnabled = persisted.Enabled.Value;
            if (persisted.Sound.HasValue) _optionSound = persisted.Sound.Value;
            if (persisted.Logging.HasValue) _optionLogging = persisted.Logging.Value;
            if (persisted.DebugOverlay.HasValue) _optionDebugOverlay = persisted.DebugOverlay.Value;
            if (persisted.NativeProbeLogging.HasValue) _optionNativeProbeLogging = persisted.NativeProbeLogging.Value;
            if (persisted.BattleCameraLockMode != null
                && Enum.TryParse(persisted.BattleCameraLockMode, ignoreCase: true, out BattleCameraLockMode parsedMode))
            {
                _optionBattleCameraLockMode = parsedMode;
            }
            else if (persisted.EnemyCameraLock.HasValue)
            {
                // Legacy migration: old bool → new enum. true → EnemyTurnsOnly (preserves prior
                // behaviour); false → Off. New installs without either field default to
                // EnemyTurnsOnly via the field initializer.
                _optionBattleCameraLockMode = persisted.EnemyCameraLock.Value
                    ? BattleCameraLockMode.EnemyTurnsOnly
                    : BattleCameraLockMode.Off;
                _logger.Info($"[Parry] Migrated legacy EnemyCameraLock={persisted.EnemyCameraLock.Value} → BattleCameraLockMode={_optionBattleCameraLockMode}.");
            }
            if (persisted.ParryEffect.HasValue) _optionParryEffect = persisted.ParryEffect.Value;
            if (persisted.ImpactShake.HasValue) _optionImpactShake = persisted.ImpactShake.Value;
            if (persisted.ImpactShakeSweep.HasValue) _optionImpactShakeSweep = persisted.ImpactShakeSweep.Value;
            if (persisted.StreakCounter.HasValue) _optionStreakCounter = persisted.StreakCounter.Value;
            if (persisted.DodgeEnabled.HasValue) _optionDodgeEnabled = persisted.DodgeEnabled.Value;
            if (persisted.ParryNativeBlock.HasValue) _optionParryNativeBlock = persisted.ParryNativeBlock.Value;
            if (persisted.CameraProbe.HasValue) _optionCameraProbe = persisted.CameraProbe.Value;
            if (persisted.CheckHitHitValue.HasValue) _checkHitHitValue = persisted.CheckHitHitValue.Value;

            if (ParryDifficultyModel.TryParsePersistedDifficulty(persisted.Difficulty, out ParryDifficulty difficulty))
            {
                _optionDifficulty = ParryDifficultyModel.NormalizeDifficulty(difficulty);
            }
            else
            {
                _optionDifficulty = ParryDifficultyModel.NormalizeDifficulty(_optionDifficulty);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"[Parry] Failed to load settings from '{_settingsFilePath}': {ex.Message}");
        }
    }

    private void persist_settings()
    {
        if (string.IsNullOrWhiteSpace(_settingsFilePath))
        {
            _logger.Warning("[Parry] Cannot persist settings — resolved settings path is empty.");
            return;
        }

        try
        {
            string? directory = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            PersistedSettings payload = new()
            {
                Enabled = _optionEnabled,
                Sound = _optionSound,
                Logging = _optionLogging,
                DebugOverlay = _optionDebugOverlay,
                NativeProbeLogging = _optionNativeProbeLogging,
                BattleCameraLockMode = _optionBattleCameraLockMode.ToString(),
                ParryEffect = _optionParryEffect,
                ImpactShake = _optionImpactShake,
                ImpactShakeSweep = _optionImpactShakeSweep,
                StreakCounter = _optionStreakCounter,
                DodgeEnabled = _optionDodgeEnabled,
                ParryNativeBlock = _optionParryNativeBlock,
                CameraProbe = _optionCameraProbe,
                CheckHitHitValue = _checkHitHitValue,
                Difficulty = _optionDifficulty.ToString()
            };

            string json = JsonSerializer.Serialize(payload, PersistedSettingsJsonOptions);
            string tempPath = _settingsFilePath + ".tmp";
            File.WriteAllText(tempPath, json + Environment.NewLine, Encoding.UTF8);
            File.Move(tempPath, _settingsFilePath, overwrite: true);
            if (_optionLogging)
            {
                log_debug($"[Parry] Settings persisted to '{_settingsFilePath}'.");
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"[Parry] Failed to persist settings to '{_settingsFilePath}': {ex.Message}");
        }
    }
}
