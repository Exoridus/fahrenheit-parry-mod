using System.Collections.Generic;
using Xunit;

namespace Fahrenheit.Mods.Parry.Tests;

/// <summary>
///     Unit coverage for the FIFO-with-overflow-drop ring used by the
///     frame-deferred native-probe logger. The ring's mechanics are
///     decoupled from the StreamWriter sink so the data-structure
///     behaviour can be validated directly.
/// </summary>
public sealed class NativeProbeRingTests
{
    [Fact]
    public void NewRing_IsEmpty()
    {
        var ring = new NativeProbeRing(8);
        Assert.Equal(0, ring.Count);
        Assert.Equal(0, ring.DroppedSinceDrain);
        Assert.Equal(8, ring.Capacity);
    }

    [Fact]
    public void Enqueue_IncrementsCountUntilCapacity()
    {
        var ring = new NativeProbeRing(4);

        ring.Enqueue("a");
        ring.Enqueue("b");
        ring.Enqueue("c");

        Assert.Equal(3, ring.Count);
        Assert.Equal(0, ring.DroppedSinceDrain);
    }

    [Fact]
    public void Enqueue_NullOrEmpty_IsIgnored()
    {
        var ring = new NativeProbeRing(4);
        ring.Enqueue("");
        ring.Enqueue(null!);
        Assert.Equal(0, ring.Count);
    }

    [Fact]
    public void Drain_EmitsInFifoOrder()
    {
        var ring = new NativeProbeRing(4);
        ring.Enqueue("first");
        ring.Enqueue("second");
        ring.Enqueue("third");

        var sink = new List<string>();
        int drained = ring.DrainTo(sink.Add);

        Assert.Equal(3, drained);
        Assert.Equal(new[] { "first", "second", "third" }, sink);
        Assert.Equal(0, ring.Count);
    }

    [Fact]
    public void Drain_OnEmptyRing_IsNoOp()
    {
        var ring = new NativeProbeRing(4);
        var sink = new List<string>();

        int drained = ring.DrainTo(sink.Add);

        Assert.Equal(0, drained);
        Assert.Empty(sink);
    }

    [Fact]
    public void Overflow_OverwritesOldestAndCountsDrops()
    {
        var ring = new NativeProbeRing(3);
        ring.Enqueue("a");
        ring.Enqueue("b");
        ring.Enqueue("c");
        // Ring is full at this point — next two pushes overwrite oldest.
        ring.Enqueue("d");
        ring.Enqueue("e");

        Assert.Equal(3, ring.Count);
        Assert.Equal(2, ring.DroppedSinceDrain);

        var sink = new List<string>();
        int drained = ring.DrainTo(sink.Add);

        Assert.Equal(3, drained);
        // Oldest two ("a", "b") were overwritten; FIFO order of survivors is c,d,e.
        Assert.Equal(new[] { "c", "d", "e" }, sink);
    }

    [Fact]
    public void Drain_ResetsDroppedCounter()
    {
        var ring = new NativeProbeRing(2);
        ring.Enqueue("a");
        ring.Enqueue("b");
        ring.Enqueue("c"); // drops "a"
        Assert.Equal(1, ring.DroppedSinceDrain);

        ring.DrainTo(_ => { });
        Assert.Equal(0, ring.DroppedSinceDrain);

        // Subsequent enqueue without overflow keeps drop counter at 0.
        ring.Enqueue("d");
        Assert.Equal(0, ring.DroppedSinceDrain);
    }

    [Fact]
    public void WrapAround_PreservesFifoAcrossDrainBoundary()
    {
        // Capacity 4; fill, drain, fill again, drain — second batch must come
        // out in its own arrival order regardless of where head landed.
        var ring = new NativeProbeRing(4);
        ring.Enqueue("a"); ring.Enqueue("b"); ring.Enqueue("c");
        var first = new List<string>();
        ring.DrainTo(first.Add);
        Assert.Equal(new[] { "a", "b", "c" }, first);

        ring.Enqueue("d"); ring.Enqueue("e"); ring.Enqueue("f"); ring.Enqueue("g");
        var second = new List<string>();
        ring.DrainTo(second.Add);
        Assert.Equal(new[] { "d", "e", "f", "g" }, second);
    }

    [Fact]
    public void Clear_DiscardsEntriesAndResetsState()
    {
        var ring = new NativeProbeRing(4);
        ring.Enqueue("a"); ring.Enqueue("b");
        ring.Clear();

        Assert.Equal(0, ring.Count);
        Assert.Equal(0, ring.DroppedSinceDrain);

        var sink = new List<string>();
        ring.DrainTo(sink.Add);
        Assert.Empty(sink);
    }

    [Fact]
    public void DrainTo_SinkException_DoesNotLeaveStaleEntries()
    {
        // If the sink throws partway, the ring must still be empty afterwards
        // (i.e., we do not retry the same entries forever) and the exception
        // must propagate so the caller can react.
        var ring = new NativeProbeRing(4);
        ring.Enqueue("a"); ring.Enqueue("b"); ring.Enqueue("c");

        Assert.Throws<System.IO.IOException>(() =>
            ring.DrainTo(_ => throw new System.IO.IOException("disk full")));

        // Ring is cleared regardless of the exception.
        Assert.Equal(0, ring.Count);
        var sink = new List<string>();
        ring.DrainTo(sink.Add);
        Assert.Empty(sink);
    }

    [Fact]
    public void DrainTo_NullSink_Throws()
    {
        var ring = new NativeProbeRing(4);
        Assert.Throws<System.ArgumentNullException>(() => ring.DrainTo(null!));
    }

    [Fact]
    public void Constructor_NonPositiveCapacity_Throws()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() => new NativeProbeRing(0));
        Assert.Throws<System.ArgumentOutOfRangeException>(() => new NativeProbeRing(-1));
    }
}
