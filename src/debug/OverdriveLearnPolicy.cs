namespace Fahrenheit.Mods.Parry;

/// <summary>
///     Pure decision policy for teaching the custom overdrive mode (index 0x11 /
///     bit 17) by parrying, mirroring how FFX teaches its own overdrive modes: a
///     per-character learn <b>countdown</b> in <c>limit_mode_counters[0x11]</c> that
///     the engine decrements per qualifying event and that grants the mode when it
///     reaches zero.
///
///     <para>
///         Kept separate from <see cref="OverdriveCounterFormatter"/> (which formats
///         and classifies raw counter values) and <see cref="OverdriveMaskFormatter"/>
///         (which does <c>limit_modes_obtained</c> bit arithmetic) because the learn
///         <i>policy</i> — combining a counter value, the mode's obtained-bit state,
///         and the calibrated threshold into a save-write decision — is a distinct
///         third responsibility. It <i>uses</i> the sentinel constants those helpers
///         define, so the two save fields stay authored in one place each.
///     </para>
///
///     <para>
///         Load-bearing invariant: <c>counter[0x11]</c> must never hold <c>0</c> while
///         bit 17 is unset. <c>MsLimitTypeProcess</c> iterates <c>i &lt; 0x14</c>, so
///         it visits index 0x11; if it finds the counter at <c>0</c> with the bit
///         unset it grants the mode itself and fires a spurious "learned" message.
///         The policy therefore never emits a bare decrement-to-zero: the final parry
///         is a <see cref="ParryAction.Grant"/> that sets the bit first and writes the
///         counter to <c>0</c> as one indivisible decision (the caller performs the
///         bit write before the counter write).
///     </para>
/// </summary>
internal static class OverdriveLearnPolicy
{
    /// <summary>
    ///     Number of successful parries a character must perform to learn the custom
    ///     overdrive mode.
    ///
    ///     <para>
    ///         100 is deliberate, not a round-number placeholder: it is Tidus's native
    ///         Slayer threshold (learned after 100 of his own kills) and sits in the
    ///         middle band of the game's own learn values (native range 35..1000,
    ///         median ~120). Do not "tidy" this to 50 or 128 — it is calibrated against
    ///         the engine's real distribution so the custom mode feels native.
    ///     </para>
    /// </summary>
    public const short LearnThreshold = 100;

    /// <summary>
    ///     What initialisation should do to <c>counter[0x11]</c> for one character at
    ///     the battle-begin edge, when learning is enabled.
    /// </summary>
    public enum InitAction
    {
        /// <summary>Bit 17 is already set: the mode is learned. Leave the counter alone.</summary>
        NothingToDo,

        /// <summary>Counter is a normal in-progress value (1..threshold). Leave it alone.</summary>
        LeaveInProgress,

        /// <summary>Write the threshold: an uninitialised (0xFFFF) or out-of-range counter.</summary>
        Initialise,

        /// <summary>
        ///     Write the threshold AND warn: counter is <c>0</c> with bit 17 unset, the
        ///     unsafe state the engine could grant incidentally at any moment.
        /// </summary>
        InitialiseWithWarning
    }

    /// <summary>What a single de-bounced successful parry should do to one character's counter.</summary>
    public enum ParryAction
    {
        /// <summary>Bit 17 already set: the mode is learned. No write.</summary>
        AlreadyLearned,

        /// <summary>Counter is the 0xFFFF "never learns" sentinel: not in a learning state. No write.</summary>
        NotLearnable,

        /// <summary>Decrement the counter by one (it stays &gt;= 1).</summary>
        Decrement,

        /// <summary>Final parry: set bit 17 first, then write the counter to 0.</summary>
        Grant
    }

    public readonly struct InitDecision
    {
        public readonly InitAction Action;

        /// <summary>Value to write when <see cref="Action"/> writes; otherwise ignored.</summary>
        public readonly short WriteValue;

        /// <summary>Human-readable reason for the log line.</summary>
        public readonly string Reason;

        public InitDecision(InitAction action, short writeValue, string reason)
        {
            Action = action;
            WriteValue = writeValue;
            Reason = reason;
        }
    }

    public readonly struct ParryDecision
    {
        public readonly ParryAction Action;

        /// <summary>
        ///     When <see cref="Action"/> writes the counter, the value to write. For
        ///     <see cref="ParryAction.Grant"/> this is <c>0</c>; the bit must be set
        ///     first (see <see cref="SetBit"/>).
        /// </summary>
        public readonly short WriteCounterValue;

        /// <summary>True only for <see cref="ParryAction.Grant"/>: set bit 17 before writing the counter.</summary>
        public readonly bool SetBit;

        /// <summary>Human-readable reason for the log line.</summary>
        public readonly string Reason;

        public ParryDecision(ParryAction action, short writeCounterValue, bool setBit, string reason)
        {
            Action = action;
            WriteCounterValue = writeCounterValue;
            SetBit = setBit;
            Reason = reason;
        }
    }

    /// <summary>
    ///     Decides how to initialise one character's <c>counter[0x11]</c> at the
    ///     battle-begin edge. Handles every observed case explicitly rather than
    ///     assuming a vanilla save reads 0xFFFF.
    /// </summary>
    public static InitDecision DecideInitialisation(short counter, bool modeBitSet)
    {
        if (modeBitSet)
        {
            // Bit 17 set = already learned. The counter is ours and may be 0; leave it.
            return new InitDecision(InitAction.NothingToDo, counter, "bit 17 set (already learned)");
        }

        if (counter == OverdriveCounterFormatter.NotApplicableValue)
        {
            // 0xFFFF: never initialised for learning. Enable it by writing the threshold.
            return new InitDecision(InitAction.Initialise, LearnThreshold, "0xFFFF (uninitialised) — enabling learning");
        }

        if (counter == OverdriveCounterFormatter.LearnedValue)
        {
            // 0 with bit unset: UNSAFE. MsLimitTypeProcess could grant the mode incidentally.
            return new InitDecision(InitAction.InitialiseWithWarning, LearnThreshold,
                "counter 0 with bit 17 unset — UNSAFE (engine could grant); writing threshold");
        }

        if (counter >= 1 && counter <= LearnThreshold)
        {
            return new InitDecision(InitAction.LeaveInProgress, counter, $"in progress ({counter} remaining)");
        }

        // Above the threshold, or a corrupt negative other than the 0xFFFF sentinel:
        // treat as uninitialised and reset to the threshold.
        return new InitDecision(InitAction.Initialise, LearnThreshold, $"out-of-range ({counter}) — reinitialising to threshold");
    }

    /// <summary>
    ///     Decides what one de-bounced successful parry does to a character's
    ///     <c>counter[0x11]</c>. Never returns a bare decrement to zero: at
    ///     <c>counter &lt;= 1</c> (including the unsafe <c>0</c>) it grants instead,
    ///     preserving the never-zero-while-unset invariant.
    /// </summary>
    public static ParryDecision DecideParry(short counter, bool modeBitSet)
    {
        if (modeBitSet)
        {
            return new ParryDecision(ParryAction.AlreadyLearned, counter, setBit: false, "bit 17 already set");
        }

        if (counter == OverdriveCounterFormatter.NotApplicableValue)
        {
            // 0xFFFF: this slot is not in a learning state (init did not arm it). Do not learn.
            return new ParryDecision(ParryAction.NotLearnable, counter, setBit: false, "0xFFFF (not in learning) — no change");
        }

        if (counter <= 1)
        {
            // Final parry (1), the unsafe 0, or a corrupt negative: grant now. Set bit first,
            // then write 0 — never leave a decrementable zero behind.
            return new ParryDecision(ParryAction.Grant, 0, setBit: true, "final parry — granting mode (bit 17 then counter 0)");
        }

        return new ParryDecision(ParryAction.Decrement, (short)(counter - 1), setBit: false, $"decrement to {counter - 1}");
    }
}
