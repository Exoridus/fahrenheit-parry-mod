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

/// <summary>
///     Difficulty presets for the press-based parry window and its animation-backed
///     whiff recovery lockout.
///
///     The mod no longer uses a tiered anti-spam window; a single window duration is
///     applied on every fresh press, and a whiff that does not connect enters a
///     time-bounded recovery lockout that approximates the visible native
///     "return to guard stance" animation. See <c>FINAL_PARRY_SPEC.md</c> and
///     <c>TIERED_PENALTY_RATIONALE.md</c> (retired) for background.
/// </summary>
public static class ParryDifficultyModel
{
    // Window durations (milliseconds). A single value per difficulty — no tiers.
    private const float DebugWindowMs  = 500f;
    private const float EasyWindowMs   = 350f;
    private const float NormalWindowMs = 200f;
    private const float ExpertWindowMs = 150f;

    // Whiff recovery lockout durations (milliseconds). These approximate the visible
    // guard-stance recovery animation commitment; exact native-motion wiring is not
    // yet available, so the values are tuned to feel committed without being cheap.
    // Harder difficulties commit to a slightly longer recovery to match their tighter
    // windows; Easy feels looser.
    private const float DebugLockoutMs  = 300f;
    private const float EasyLockoutMs   = 450f;
    private const float NormalLockoutMs = 600f;
    private const float ExpertLockoutMs = 750f;

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

    /// <summary>
    ///     Returns the parry window duration in seconds for the given difficulty.
    ///     A single value — there are no tiers.
    /// </summary>
    public static float GetWindowSeconds(ParryDifficulty difficulty)
    {
        return get_window_ms(difficulty) / 1000f;
    }

    /// <summary>
    ///     Returns the whiff recovery lockout duration in seconds for the given
    ///     difficulty. This is the time a whiffed R1 press commits the player to
    ///     before another press is accepted — approximating the "return to normal
    ///     stance" animation.
    /// </summary>
    public static float GetWhiffLockoutSeconds(ParryDifficulty difficulty)
    {
        return get_lockout_ms(difficulty) / 1000f;
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

    private static float get_window_ms(ParryDifficulty difficulty) => difficulty switch
    {
#if DEBUG
        ParryDifficulty.Debug => DebugWindowMs,
#endif
        ParryDifficulty.Easy => EasyWindowMs,
        ParryDifficulty.Normal => NormalWindowMs,
        ParryDifficulty.Expert => ExpertWindowMs,
        _ => get_window_ms(DefaultDifficulty)
    };

    private static float get_lockout_ms(ParryDifficulty difficulty) => difficulty switch
    {
#if DEBUG
        ParryDifficulty.Debug => DebugLockoutMs,
#endif
        ParryDifficulty.Easy => EasyLockoutMs,
        ParryDifficulty.Normal => NormalLockoutMs,
        ParryDifficulty.Expert => ExpertLockoutMs,
        _ => get_lockout_ms(DefaultDifficulty)
    };
}
