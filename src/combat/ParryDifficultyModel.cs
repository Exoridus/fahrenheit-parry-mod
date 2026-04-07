namespace Fahrenheit.Mods.Parry;

public enum ParryDifficulty
{
#if DEBUG
    Debug = 0,
    Easy = 1,
    Normal = 2,
    Expert = 3
#else
    Easy = 0,
    Normal = 1,
    Expert = 2
#endif
}

public static class ParryDifficultyModel
{
    public const int MaxSpamTierIndex = 3;
    public const float SpamTierResetCooldownSeconds = 0.50f;

    private static readonly float[] DebugTierDurationsMs = [500f, 200f, 100f, 0f];
    private static readonly float[] EasyTierDurationsMs = [350f, 200f, 100f, 0f];
    private static readonly float[] NormalTierDurationsMs = [200f, 100f, 67f, 0f];
    private static readonly float[] ExpertTierDurationsMs = [150f, 75f, 33f, 0f];

#if DEBUG
    private static readonly ParryDifficulty[] SelectableDifficulties =
    [
        ParryDifficulty.Debug,
        ParryDifficulty.Easy,
        ParryDifficulty.Normal,
        ParryDifficulty.Expert
    ];
    private const string DifficultyComboItems = "Debug\0Easy\0Normal\0Expert\0";
#else
    private static readonly ParryDifficulty[] SelectableDifficulties =
    [
        ParryDifficulty.Easy,
        ParryDifficulty.Normal,
        ParryDifficulty.Expert
    ];
    private const string DifficultyComboItems = "Easy\0Normal\0Expert\0";
#endif

    public static ParryDifficulty DefaultDifficulty =>
#if DEBUG
        ParryDifficulty.Debug;
#else
        ParryDifficulty.Normal;
#endif

    public static string FormatName(ParryDifficulty difficulty) => difficulty switch
    {
#if DEBUG
        ParryDifficulty.Debug => "Debug",
#endif
        ParryDifficulty.Easy => "Easy",
        ParryDifficulty.Normal => "Normal",
        ParryDifficulty.Expert => "Expert",
        _ => FormatName(DefaultDifficulty)
    };

    public static int ClampTierIndex(int tierIndex)
    {
        return Math.Clamp(tierIndex, 0, MaxSpamTierIndex);
    }

    public static float GetWindowSeconds(ParryDifficulty difficulty, int tierIndex)
    {
        ReadOnlySpan<float> tiers = get_tiers_ms(difficulty);
        int idx = ClampTierIndex(tierIndex);
        return tiers[idx] / 1000f;
    }

    public static float GetBaseWindowSeconds(ParryDifficulty difficulty)
    {
        return GetWindowSeconds(difficulty, tierIndex: 0);
    }

    public static int IncreaseSpamTier(int currentTierIndex)
    {
        return Math.Min(ClampTierIndex(currentTierIndex) + 1, MaxSpamTierIndex);
    }

    public static ReadOnlySpan<ParryDifficulty> GetSelectableDifficulties()
    {
        return SelectableDifficulties;
    }

    public static string GetComboItems()
    {
        return DifficultyComboItems;
    }

    public static int GetComboIndex(ParryDifficulty difficulty)
    {
        ParryDifficulty normalized = NormalizeDifficulty(difficulty);
        for (int i = 0; i < SelectableDifficulties.Length; i++)
        {
            if (SelectableDifficulties[i] == normalized)
            {
                return i;
            }
        }

        return 0;
    }

    public static ParryDifficulty DifficultyFromComboIndex(int index)
    {
        if (index < 0 || index >= SelectableDifficulties.Length)
        {
            return DefaultDifficulty;
        }

        return SelectableDifficulties[index];
    }

    public static ParryDifficulty NormalizeDifficulty(ParryDifficulty difficulty)
    {
        return difficulty switch
        {
#if DEBUG
            ParryDifficulty.Debug => ParryDifficulty.Debug,
#endif
            ParryDifficulty.Easy => ParryDifficulty.Easy,
            ParryDifficulty.Normal => ParryDifficulty.Normal,
            ParryDifficulty.Expert => ParryDifficulty.Expert,
            _ => DefaultDifficulty
        };
    }

    public static bool TryParsePersistedDifficulty(string? persistedValue, out ParryDifficulty difficulty)
    {
        difficulty = DefaultDifficulty;
        if (string.IsNullOrWhiteSpace(persistedValue))
        {
            return false;
        }

#if !DEBUG
        if (persistedValue.Equals("Debug", StringComparison.OrdinalIgnoreCase))
        {
            difficulty = ParryDifficulty.Normal;
            return true;
        }
#endif

        if (!Enum.TryParse(persistedValue, ignoreCase: true, out ParryDifficulty parsed))
        {
            return false;
        }

        difficulty = NormalizeDifficulty(parsed);
        return true;
    }

    private static ReadOnlySpan<float> get_tiers_ms(ParryDifficulty difficulty) => difficulty switch
    {
#if DEBUG
        ParryDifficulty.Debug => DebugTierDurationsMs,
#endif
        ParryDifficulty.Easy => EasyTierDurationsMs,
        ParryDifficulty.Normal => NormalTierDurationsMs,
        ParryDifficulty.Expert => ExpertTierDurationsMs,
        _ => get_tiers_ms(DefaultDifficulty)
    };
}
