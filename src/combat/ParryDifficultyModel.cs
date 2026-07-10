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
///     "return to guard stance" animation.
///
///     <para>
///         Everything is counted in <b>battle ticks</b>; milliseconds are derived, never stored.
///         Both endpoints of a parry are tick-locked. The press is read from the game's own input
///         word, which the game refreshes once per tick, so a press at 0.6 of a frame becomes
///         visible on the next one — the 0.6 never exists. The impact arrives from
///         MsSetDamageInternal, also on a tick. The only continuous quantity in the old model was
///         the real elapsed time subtracted from the window each frame, and that injected
///         wall-clock drift into a tick-based simulation: one dropped frame burns two ticks of
///         window while the enemy animation and the impact stall with it.
///     </para>
///     <para>
///         A window of W ms admits exactly <c>floor(W / 33.33) + 1</c> impacts, and the fraction
///         beyond that does nothing — except when W lands exactly on a tick, where float residue
///         and frame pacing decide. Normal's old 200 ms was exactly 6.0 ticks. Counting ticks
///         removes the coin flip.
///     </para>
///     <para>
///         <b>Difficulty no longer moves the window.</b> At 30 Hz a tighter window punishes
///         perception and hardware rather than skill — Expert's old 150 ms was literally five
///         sampling chances — and it invalidates the timing a player learned on Easy. The three
///         real difficulties share one set of thresholds and differ only in how much a hit that
///         *could have been parried* costs. Debug is not a difficulty; it is a testing aid, and
///         keeps its deliberately generous windows.
///     </para>
///     <para>
///         Total commitment is the design number; the lockout is derived from it. In the old model
///         it was hidden: three of the four presets happened to satisfy <c>lockout = 800 − window</c>,
///         and Expert broke the pattern at 900, paying twice for its tighter window.
///     </para>
/// </summary>
public static class ParryDifficultyModel
{
    /// <summary>The battle logic tick rate. Both the input word and the impact hook live on it.</summary>
    public const int TicksPerSecond = 30;

    private const float TickSeconds = 1f / TicksPerSecond;

    // The three real difficulties share these. See the class remarks.
    private const int PlayParryTicks      = 6;   // 200 ms nominal — 7 sampling chances
    private const int PlayDodgeTicks      = 10;  // 333 ms — safer, but no counter and no overdrive
    private const int PlayCommitmentTicks = 15;  // 500 ms total, measured from the press

    // Debug is a testing aid, not a difficulty: generous windows, same commitment shape.
    private const int DebugParryTicks      = 15; // 500 ms
    private const int DebugDodgeTicks      = 24; // 800 ms
    private const int DebugCommitmentTicks = 24; // 800 ms

    // Difficulty scales the cost of a hit you could have parried, and nothing else. Applied to
    // the damage MsCalcDamage returns, and ONLY when the cue was parryable: FFX answers raw
    // lethality with Auto-Phoenix, Auto-Life, Protect and 9999 HP — a build, not a skill — and
    // the engine, not the player, chooses who gets targeted.
    private const float EasyDamageScale   = 0.75f;
    private const float NormalDamageScale = 1.00f;
    private const float ExpertDamageScale = 1.75f;
    private const float DebugDamageScale  = 1.00f;

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

    /// <summary>Parry window, in battle ticks. The window admits impacts on ticks 0..N-1 after the press.</summary>
    public static int GetParryWindowTicks(ParryDifficulty difficulty) => difficulty switch
    {
#if DEBUG
        ParryDifficulty.Debug => DebugParryTicks,
#endif
        ParryDifficulty.Easy or ParryDifficulty.Normal or ParryDifficulty.Expert => PlayParryTicks,
        _ => GetParryWindowTicks(DefaultDifficulty)
    };

    /// <summary>Dodge window, in battle ticks. Wider than the parry window: safer, but it grants neither counter nor overdrive.</summary>
    public static int GetDodgeWindowTicks(ParryDifficulty difficulty) => difficulty switch
    {
#if DEBUG
        ParryDifficulty.Debug => DebugDodgeTicks,
#endif
        ParryDifficulty.Easy or ParryDifficulty.Normal or ParryDifficulty.Expert => PlayDodgeTicks,
        _ => GetDodgeWindowTicks(DefaultDifficulty)
    };

    /// <summary>
    ///     Total commitment from the press, in battle ticks: the parry window plus the whiff
    ///     recovery that follows it. This is the design number; the lockout is derived from it.
    /// </summary>
    public static int GetTotalCommitmentTicks(ParryDifficulty difficulty) => difficulty switch
    {
#if DEBUG
        ParryDifficulty.Debug => DebugCommitmentTicks,
#endif
        ParryDifficulty.Easy or ParryDifficulty.Normal or ParryDifficulty.Expert => PlayCommitmentTicks,
        _ => GetTotalCommitmentTicks(DefaultDifficulty)
    };

    /// <summary>
    ///     Whiff recovery lockout, in battle ticks — the time a whiffed press commits the player
    ///     to before another is accepted. Derived: total commitment minus the window it follows.
    /// </summary>
    public static int GetWhiffLockoutTicks(ParryDifficulty difficulty)
        => GetTotalCommitmentTicks(difficulty) - GetParryWindowTicks(difficulty);

    /// <summary>
    ///     Multiplier applied to incoming damage — but only for a hit the player could have
    ///     parried. This is the whole of what difficulty does.
    /// </summary>
    public static float GetParryableDamageScale(ParryDifficulty difficulty) => difficulty switch
    {
#if DEBUG
        ParryDifficulty.Debug => DebugDamageScale,
#endif
        ParryDifficulty.Easy => EasyDamageScale,
        ParryDifficulty.Normal => NormalDamageScale,
        ParryDifficulty.Expert => ExpertDamageScale,
        _ => GetParryableDamageScale(DefaultDifficulty)
    };

    /// <summary>Nominal wall-clock duration of a tick count. For display only — never for a gameplay decision.</summary>
    public static float TicksToSeconds(int ticks) => ticks * TickSeconds;

    /// <summary>Nominal wall-clock duration of a tick count, in milliseconds. Display only.</summary>
    public static float TicksToMs(int ticks) => ticks * (1000f / TicksPerSecond);

    public static float GetWindowSeconds(ParryDifficulty difficulty)
        => TicksToSeconds(GetParryWindowTicks(difficulty));

    public static float GetDodgeWindowSeconds(ParryDifficulty difficulty)
        => TicksToSeconds(GetDodgeWindowTicks(difficulty));

    public static float GetWhiffLockoutSeconds(ParryDifficulty difficulty)
        => TicksToSeconds(GetWhiffLockoutTicks(difficulty));

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

}
