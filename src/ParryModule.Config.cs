namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule
{
    private sealed class PersistedSettings
    {
        public bool? Enabled { get; set; }
        public bool? Sound { get; set; }
        public float? AudioVolume { get; set; }
        public bool? ParryStateHud { get; set; }
        public bool? Logging { get; set; }
        public bool? OverdriveBoost { get; set; }
        public bool? NegateDamage { get; set; }
        public bool? StartupSkipForceTitle { get; set; }
        public bool? DebugOverlay { get; set; }
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
            if (persisted == null)
            {
                return;
            }

            if (persisted.Enabled.HasValue) _optionEnabled = persisted.Enabled.Value;
            if (persisted.Sound.HasValue) _optionSound = persisted.Sound.Value;
            if (persisted.AudioVolume.HasValue) _optionAudioVolume = Math.Clamp(persisted.AudioVolume.Value, 0f, 1f);
            if (persisted.ParryStateHud.HasValue) _optionParryStateHud = persisted.ParryStateHud.Value;
            if (persisted.Logging.HasValue) _optionLogging = persisted.Logging.Value;
            if (persisted.OverdriveBoost.HasValue) _optionOverdriveBoost = persisted.OverdriveBoost.Value;
            if (persisted.NegateDamage.HasValue) _optionNegateDamage = persisted.NegateDamage.Value;
            if (persisted.StartupSkipForceTitle.HasValue) _optionStartupSkipForceTitle = persisted.StartupSkipForceTitle.Value;
            if (persisted.DebugOverlay.HasValue) _optionDebugOverlay = persisted.DebugOverlay.Value;

            if (!string.IsNullOrWhiteSpace(persisted.Difficulty)
                && Enum.TryParse(persisted.Difficulty, ignoreCase: true, out ParryDifficulty difficulty))
            {
                _optionDifficulty = difficulty;
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
                AudioVolume = Math.Clamp(_optionAudioVolume, 0f, 1f),
                ParryStateHud = _optionParryStateHud,
                Logging = _optionLogging,
                OverdriveBoost = _optionOverdriveBoost,
                NegateDamage = _optionNegateDamage,
                StartupSkipForceTitle = _optionStartupSkipForceTitle,
                DebugOverlay = _optionDebugOverlay,
                Difficulty = _optionDifficulty.ToString()
            };

            string json = JsonSerializer.Serialize(payload, PersistedSettingsJsonOptions);
            string tempPath = _settingsFilePath + ".tmp";
            File.WriteAllText(tempPath, json + Environment.NewLine, Encoding.UTF8);
            File.Copy(tempPath, _settingsFilePath, overwrite: true);
            File.Delete(tempPath);
        }
        catch (Exception ex)
        {
            _logger.Warning($"[Parry] Failed to persist settings to '{_settingsFilePath}': {ex.Message}");
        }
    }
}
