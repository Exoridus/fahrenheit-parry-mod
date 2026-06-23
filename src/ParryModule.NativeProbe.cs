namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule
{
    // ── Frame-deferred native-probe logger ───────────────────────────────────
    //
    // Stage-1 observe probes (per the FFX engine probe plan) need a
    // logging path that does NOT block the hot hook on synchronous I/O. The
    // existing `write_session_hook_entry` does a `WriteLine` against an
    // `AutoFlush=true` `StreamWriter` — fine for mid-frequency damage hooks,
    // but unsafe for a 90-caller-per-frame hub probe like
    // `MsBtlChrNumCheck`.
    //
    // This partial owns the gate + drain wiring; the ring mechanics live in
    // <see cref="NativeProbeRing"/> so they're unit-testable independently.
    //
    // The logger is intentionally INERT until `_optionNativeProbeLogging` is
    // flipped on. There is no production hook calling `enqueue_probe_event`
    // yet — the wiring exists ahead of Stage-1 probes so a future PR can add
    // hooks behind this gate without re-doing the I/O design.
    //
    // Existing logging paths (`log_debug`, `write_session_hook_entry`,
    // `write_session_timeline_event`) are unchanged. Only NEW probe events
    // route through the deferred channel.

    /// <summary>
    ///     Capacity of the ring buffer in entries. Sized to hold ~3 frames at
    ///     30 fps even if a hub probe is fully open. If overflow drops show
    ///     up in the session log repeatedly, increase this or reduce
    ///     per-frame probe volume.
    /// </summary>
    private const int NativeProbeRingCapacity = 4096;

    private readonly NativeProbeRing _probeRing = new(NativeProbeRingCapacity);

    /// <summary>
    ///     Push a pre-formatted event onto the deferred-probe ring. Safe to
    ///     call from inside any hook on the main game thread; performs no I/O.
    ///     Returns silently when probes are disabled or the session log is
    ///     unavailable; never throws.
    /// </summary>
    /// <remarks>
    ///     The caller owns the message format. To avoid allocation in the
    ///     hot path, prefer building the string only after the gate check:
    ///     <code>
    ///     if (_optionNativeProbeLogging) {
    ///         enqueue_probe_event($"[MsActionRequest] f={frame} ...");
    ///     }
    ///     </code>
    /// </remarks>
    private void enqueue_probe_event(string message)
    {
        if (!_optionNativeProbeLogging) return;
        if (_sessionLogDisabled) return;
        _probeRing.Enqueue(message);
    }

    /// <summary>
    ///     Drain the deferred-probe ring to the session debug log file.
    ///     Called once per <c>on_pre_update</c> tick on the main thread.
    ///     Drops since the last drain are surfaced as a single warning line.
    /// </summary>
    private void drain_probe_ring()
    {
        if (_probeRing.Count == 0 && _probeRing.DroppedSinceDrain == 0)
            return;

        if (_sessionLogDisabled || _sessionDebugLogWriter == null)
        {
            // Discard rather than buffer indefinitely — session logging may
            // be disabled mid-run for a real reason (write failure, rotation).
            _probeRing.Clear();
            return;
        }

        int dropsBeforeDrain = _probeRing.DroppedSinceDrain;
        StreamWriter writer = _sessionDebugLogWriter;
        try
        {
            _probeRing.DrainTo(writer.WriteLine);
            if (dropsBeforeDrain > 0)
            {
                writer.WriteLine(
                    $"[probe] dropped {dropsBeforeDrain} event(s) due to ring overflow at f={_debugFrameIndex}");
            }
        }
        catch (Exception ex)
        {
            disable_session_logging($"probe drain failed: {ex.Message}");
        }
    }
}
