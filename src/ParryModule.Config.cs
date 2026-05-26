namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule
{
    private sealed class PersistedSettings
    {
        public bool? Enabled { get; set; }
        public bool? Sound { get; set; }
        public bool? ParryStateHud { get; set; }
        public bool? Logging { get; set; }
        public bool? OverdriveBoost { get; set; }
        public bool? NegateDamage { get; set; }
        public bool? Penalty { get; set; }
        public bool? DebugOverlay { get; set; }
        // Native-engine probe channel (separate from general Logging). Default-off.
        // When true, probe-tagged events are queued into the frame-deferred ring
        // and flushed once per pre-update tick. Future Stage-1 observe probes
        // will route through this; the existing logging path is unchanged.
        public bool? NativeProbeLogging { get; set; }
        public string? BattleCameraLockMode { get; set; }  // canonical — human-readable enum name
        public bool? EnemyCameraLock { get; set; }          // legacy — migrated on load, never written
        public bool? ParryEffect { get; set; }
        public bool? StreakCounter { get; set; }
        public bool? DisableNativeEvasion { get; set; }
        public int? CheckHitHitValue { get; set; }
        public int? CheckHitMissValue { get; set; }
        public string? Difficulty { get; set; }
    }

    private static readonly JsonSerializerOptions PersistedSettingsJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

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
            if (persisted.ParryStateHud.HasValue) _optionParryStateHud = persisted.ParryStateHud.Value;
            if (persisted.Logging.HasValue) _optionLogging = persisted.Logging.Value;
            if (persisted.OverdriveBoost.HasValue) _optionOverdriveBoost = persisted.OverdriveBoost.Value;
            if (persisted.NegateDamage.HasValue) _optionNegateDamage = persisted.NegateDamage.Value;
            // Persisted as "penalty" for backward compatibility with earlier settings files.
            // The semantic is now "whiff recovery lockout enabled" (see FINAL_PARRY_SPEC.md).
            if (persisted.Penalty.HasValue) _optionWhiffLockout = persisted.Penalty.Value;
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
            if (persisted.StreakCounter.HasValue) _optionStreakCounter = persisted.StreakCounter.Value;
            if (persisted.DisableNativeEvasion.HasValue) _optionDisableNativeEvasion = persisted.DisableNativeEvasion.Value;
            if (persisted.CheckHitHitValue.HasValue) _checkHitHitValue = persisted.CheckHitHitValue.Value;
            if (persisted.CheckHitMissValue.HasValue) _checkHitMissValue = persisted.CheckHitMissValue.Value;

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
                ParryStateHud = _optionParryStateHud,
                Logging = _optionLogging,
                OverdriveBoost = _optionOverdriveBoost,
                NegateDamage = _optionNegateDamage,
                Penalty = _optionWhiffLockout,
                DebugOverlay = _optionDebugOverlay,
                NativeProbeLogging = _optionNativeProbeLogging,
                BattleCameraLockMode = _optionBattleCameraLockMode.ToString(),
                ParryEffect = _optionParryEffect,
                StreakCounter = _optionStreakCounter,
                DisableNativeEvasion = _optionDisableNativeEvasion,
                CheckHitHitValue = _checkHitHitValue,
                CheckHitMissValue = _checkHitMissValue,
                Difficulty = _optionDifficulty.ToString()
            };

            string json = JsonSerializer.Serialize(payload, PersistedSettingsJsonOptions);
            string tempPath = _settingsFilePath + ".tmp";
            File.WriteAllText(tempPath, json + Environment.NewLine, Encoding.UTF8);
            File.Move(tempPath, _settingsFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.Warning($"[Parry] Failed to persist settings to '{_settingsFilePath}': {ex.Message}");
        }
    }
}
