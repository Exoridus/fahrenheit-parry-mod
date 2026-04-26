namespace Fahrenheit.Mods.Parry;

/// <summary>
///     Fixed-capacity FIFO ring of pre-formatted probe-event strings.
///
///     Used by the frame-deferred native-probe logger so hot hooks can enqueue
///     events without performing synchronous I/O on the game thread.
///     <c>ParryModule.NativeProbe.cs</c> wraps the ring with the
///     <c>_optionNativeProbeLogging</c> gate and the <c>StreamWriter</c> drain
///     target; this struct owns only the ring mechanics (enqueue / drain /
///     overflow accounting) so the behaviour can be unit-tested.
///
///     Single-thread-game-loop assumption: writes and drains happen on the
///     same thread (the FFX update tick). No locking.
/// </summary>
internal sealed class NativeProbeRing
{
    private readonly string?[] _slots;
    private int _head;          // next slot the producer will write
    private int _count;         // live entries currently in the ring (0..capacity)
    private int _dropped;       // entries overwritten on overflow since last drain

    public NativeProbeRing(int capacity)
    {
        if (capacity <= 0) throw new System.ArgumentOutOfRangeException(nameof(capacity));
        _slots = new string?[capacity];
    }

    /// <summary>Capacity provided at construction.</summary>
    public int Capacity => _slots.Length;

    /// <summary>Live entries currently in the ring.</summary>
    public int Count => _count;

    /// <summary>
    ///     Number of entries dropped due to overflow since the last
    ///     <see cref="DrainTo"/> call. Reset on every drain.
    /// </summary>
    public int DroppedSinceDrain => _dropped;

    /// <summary>
    ///     Push a message onto the ring. On overflow, overwrites the oldest
    ///     entry and increments <see cref="DroppedSinceDrain"/>; never blocks.
    /// </summary>
    public void Enqueue(string message)
    {
        if (string.IsNullOrEmpty(message)) return;

        if (_count == _slots.Length)
        {
            // Overwrite oldest. Head currently points at the slot that holds
            // the oldest entry — we replace it in place; count stays at capacity.
            _slots[_head] = message;
            _head = (_head + 1) % _slots.Length;
            _dropped++;
            return;
        }

        _slots[_head] = message;
        _head = (_head + 1) % _slots.Length;
        _count++;
    }

    /// <summary>
    ///     Drain all entries to <paramref name="sink"/> in FIFO order, clearing
    ///     them from the ring. Resets <see cref="DroppedSinceDrain"/>.
    ///
    ///     <paramref name="sink"/> is invoked synchronously per entry on the
    ///     calling thread. Exceptions thrown by <paramref name="sink"/>
    ///     propagate back to the caller AFTER the ring has been cleared,
    ///     preventing a sticky failure from blocking subsequent drains.
    /// </summary>
    /// <returns>The number of entries drained.</returns>
    public int DrainTo(System.Action<string> sink)
    {
        if (sink is null) throw new System.ArgumentNullException(nameof(sink));
        if (_count == 0)
        {
            _dropped = 0;
            return 0;
        }

        int drained = _count;
        int tail = (_head - _count + _slots.Length) % _slots.Length;
        System.Exception? capturedFailure = null;

        for (int i = 0; i < drained; i++)
        {
            int slot = (tail + i) % _slots.Length;
            string? message = _slots[slot];
            _slots[slot] = null;
            if (message != null && capturedFailure is null)
            {
                try { sink(message); }
                catch (System.Exception ex) { capturedFailure = ex; }
            }
        }

        _head = 0;
        _count = 0;
        _dropped = 0;

        if (capturedFailure != null)
            throw capturedFailure;

        return drained;
    }

    /// <summary>
    ///     Discard all queued entries without invoking the sink. Resets
    ///     <see cref="DroppedSinceDrain"/>.
    /// </summary>
    public void Clear()
    {
        for (int i = 0; i < _slots.Length; i++) _slots[i] = null;
        _head = 0;
        _count = 0;
        _dropped = 0;
    }
}
