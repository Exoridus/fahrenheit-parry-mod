namespace Fahrenheit.Mods.Parry;

/// <summary>
///     Lightweight per-probe per-frame fire-rate cap.
///
///     Hot Stage-1 probes (e.g. <c>MsSetMotion</c>) can fire many times per
///     frame; without a cap a misbehaving probe could exhaust the deferred
///     ring inside one frame and starve other probes. This struct gates
///     emission to at most <c>maxPerFrame</c> events per frame index, with
///     bookkeeping reset whenever the frame index advances.
///
///     Single-thread game-loop assumption (same as <see cref="NativeProbeRing"/>):
///     all calls happen on the FFX update tick, so no locking.
///
///     The struct is mutated in place; callers must hold it as a field, not
///     pass it by value.
/// </summary>
internal struct PerFrameProbeThrottle
{
    private ulong _lastFrame;
    private int _firedThisFrame;
    private int _droppedThisFrame;

    /// <summary>Frame index seen on the most recent <see cref="ShouldEmit"/> call.</summary>
    public ulong LastFrame => _lastFrame;

    /// <summary>Events admitted on the current frame so far.</summary>
    public int FiredThisFrame => _firedThisFrame;

    /// <summary>Events dropped on the current frame because the cap was hit.</summary>
    public int DroppedThisFrame => _droppedThisFrame;

    /// <summary>
    ///     Returns <c>true</c> if the caller may emit a probe event for
    ///     <paramref name="currentFrame"/>. Returns <c>false</c> once
    ///     <paramref name="maxPerFrame"/> events have already been admitted
    ///     for this frame.
    /// </summary>
    public bool ShouldEmit(ulong currentFrame, int maxPerFrame)
    {
        if (currentFrame != _lastFrame)
        {
            _lastFrame = currentFrame;
            _firedThisFrame = 0;
            _droppedThisFrame = 0;
        }

        if (_firedThisFrame >= maxPerFrame)
        {
            _droppedThisFrame++;
            return false;
        }

        _firedThisFrame++;
        return true;
    }
}
