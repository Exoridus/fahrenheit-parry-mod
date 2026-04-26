namespace Fahrenheit.Mods.Parry;

/// <summary>
///     Formats Stage-1 native-observe probe events into a single compact line
///     for the deferred probe ring.
///
///     Extracted from <c>ParryModule.Stage1Probes.cs</c> so the format
///     contract is unit-testable without spinning up the full module.
/// </summary>
internal static class Stage1ProbeFormatter
{
    /// <summary>
    ///     Build a probe event line.
    ///     Layout: <c>[stage1.&lt;probe&gt;] f=&lt;frame&gt; &lt;args&gt; state=&lt;input&gt; atk=&lt;attacker&gt; pwa=&lt;0|1&gt;</c>
    /// </summary>
    /// <param name="probeName">Short native function name (no <c>stage1.</c> prefix).</param>
    /// <param name="args">Pre-formatted key=value tokens. May be empty when the
    ///     probe has no inferred args; the prefix/suffix still emit.</param>
    /// <param name="frameIndex">Game-loop frame index at probe entry.</param>
    /// <param name="inputState">Current parry input state.</param>
    /// <param name="currentAttackerId">Current battle attacker slot, or 0 when none.</param>
    /// <param name="parryWindowActive">Whether the parry window is open.</param>
    public static string Format(
        string probeName,
        string args,
        ulong frameIndex,
        ParryInputState inputState,
        byte currentAttackerId,
        bool parryWindowActive)
    {
        string body = string.IsNullOrEmpty(args) ? string.Empty : (args + " ");
        int pwa = parryWindowActive ? 1 : 0;
        return $"[stage1.{probeName}] f={frameIndex} {body}state={inputState} atk={currentAttackerId} pwa={pwa}";
    }

    /// <summary>
    ///     Build a probe-failure event. Used by hook bodies that catch
    ///     exceptions while preparing the args string. The orig call is
    ///     made unconditionally; this only describes the formatting fault.
    /// </summary>
    public static string FormatFailure(string probeName, ulong frameIndex, string reason)
    {
        return $"[stage1.{probeName}] f={frameIndex} probe_fault reason=\"{reason}\"";
    }
}
