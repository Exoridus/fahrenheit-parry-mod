namespace Fahrenheit.Mods.Parry;

/// <summary>
///     Pure helpers for the per-character <c>limit_mode_counters</c> array read by
///     the read-only overdrive save probe.
///
///     <para>
///         Each counter is a per-mode learn <b>countdown</b>, not a threshold table:
///         its start value in the new-game save template <i>was</i> the learn
///         threshold, and the engine decrements it by 1 per qualifying event
///         (<c>FUN_007b10d0</c> indexes it as <c>*(short*)(base + mode*2)</c>). A
///         value of <c>0</c> therefore means the mode is <b>already learned</b>, and
///         <c>0xFFFF</c> (i.e. <c>-1</c> as a signed short) means the character can
///         never learn that mode. Any other value is the number of remaining events.
///     </para>
///
///     <para>
///         Kept separate from <see cref="OverdriveMaskFormatter"/> on purpose: that
///         type formats the 32-bit <c>limit_modes_obtained</c> bitmask, whereas this
///         one classifies the distinct <c>limit_mode_counters</c> array. They are two
///         different save fields with different semantics, so folding them together
///         would blur that boundary. Extracted here so the classification and the
///         min/median/max used for calibration are unit-tested without a live read.
///     </para>
/// </summary>
internal static class OverdriveCounterFormatter
{
    /// <summary>Raw value of a counter slot that the character can never learn (0xFFFF as a signed short).</summary>
    public const short NotApplicableValue = unchecked((short)0xFFFF);

    /// <summary>Raw value of a counter slot whose mode is already learned (the countdown reached zero).</summary>
    public const short LearnedValue = 0;

    /// <summary>Human-readable mode names by counter index, verified against the decompilation.</summary>
    private static readonly string[] ModeNames =
    {
        "Warrior",  "Comrade",  "Stoic",    "Healer",   "Tactician",
        "Victim",   "Dancer",   "Avenger",  "Slayer",   "Hero",
        "Rook",     "Victor",   "Coward",   "Ally",     "Sufferer",
        "Daredevil","Loner",    "unused1",  "unused2",  "Aeons"
    };

    /// <summary>Number of counter slots (modes 0x00..0x13).</summary>
    public static int ModeCount => ModeNames.Length;

    public enum CounterClass
    {
        /// <summary>Countdown reached zero: the mode is already learned.</summary>
        Learned,

        /// <summary>0xFFFF sentinel: this character can never learn this mode.</summary>
        NotApplicable,

        /// <summary>An ordinary value: the number of qualifying events still remaining.</summary>
        Remaining
    }

    /// <summary>
    ///     Classifies one raw counter value. Only <c>0</c> and <c>0xFFFF</c> (<c>-1</c>)
    ///     are special; everything else is a remaining-event count.
    /// </summary>
    public static CounterClass Classify(short value)
    {
        if (value == LearnedValue) return CounterClass.Learned;
        if (value == NotApplicableValue) return CounterClass.NotApplicable;
        return CounterClass.Remaining;
    }

    /// <summary>
    ///     Renders one counter for the log: <c>"learned"</c> for <c>0</c>, <c>"n/a"</c>
    ///     for <c>0xFFFF</c>, otherwise the raw remaining-event count.
    /// </summary>
    public static string FormatValue(short value) => Classify(value) switch
    {
        CounterClass.Learned => "learned",
        CounterClass.NotApplicable => "n/a",
        _ => value.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };

    /// <summary>
    ///     Returns the mode name for a counter index, or <c>"mode{index}"</c> if the
    ///     index is outside the known table.
    /// </summary>
    public static string ModeName(int index) =>
        index >= 0 && index < ModeNames.Length
            ? ModeNames[index]
            : $"mode{index}";

    /// <summary>
    ///     Computes min/median/max over only the <see cref="CounterClass.Remaining"/>
    ///     counters in <paramref name="counters"/>, ignoring every <c>learned</c> and
    ///     <c>n/a</c> entry. Returns <c>false</c> (and zeros the outputs) when there is
    ///     no remaining counter, so an all-special input yields "no statistics" rather
    ///     than a misleading zero. Median of an even count is the average of the two
    ///     middle values.
    /// </summary>
    public static bool TryComputeStats(
        System.Collections.Generic.IReadOnlyList<short> counters,
        out int min,
        out double median,
        out int max)
    {
        min = 0;
        median = 0d;
        max = 0;

        if (counters == null) return false;

        var remaining = new System.Collections.Generic.List<int>(counters.Count);
        for (int i = 0; i < counters.Count; i++)
        {
            if (Classify(counters[i]) == CounterClass.Remaining)
            {
                remaining.Add(counters[i]);
            }
        }

        if (remaining.Count == 0) return false;

        remaining.Sort();
        min = remaining[0];
        max = remaining[^1];

        int mid = remaining.Count / 2;
        median = (remaining.Count % 2 == 1)
            ? remaining[mid]
            : (remaining[mid - 1] + remaining[mid]) / 2d;

        return true;
    }
}
