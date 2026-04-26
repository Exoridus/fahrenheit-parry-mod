namespace Fahrenheit.Mods.Parry;

/// <summary>
///     Canonical press-based parry input state machine per FINAL_PARRY_SPEC.md.
///
///     Transitions:
///         Ready         -> Open         on fresh R1 press with a parryable cue
///         Open          -> Resolved     on successful parry resolution
///         Open          -> WhiffLockout on window expiry without a hit
///         Resolved      -> Ready        on turn end / cue clear
///         WhiffLockout  -> Ready        when the recovery timer elapses
///
///     There is no Held, PreArmed, or PersistentProtection state. Holding R1 never
///     opens, extends, or refreshes the window.
/// </summary>
public enum ParryInputState : byte
{
    /// <summary>Fresh R1 press is allowed.</summary>
    Ready = 0,

    /// <summary>A parry window is currently active; new presses are ignored.</summary>
    Open = 1,

    /// <summary>A hit landed during Open; waiting for the turn to finalize.</summary>
    Resolved = 2,

    /// <summary>The window expired without a hit; approximating native guard-stance recovery.</summary>
    WhiffLockout = 3
}
