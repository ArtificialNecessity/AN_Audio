using AN.Audio.Midi;
using Xunit;

namespace AN.Audio.Midi.Tests;

public class MidiInput_MessageRingTests
{
    private static MidiInput_Message Msg(int n) => MidiInput_Message.FromMidi1(n, n, 0x90, (byte)(n & 0x7F), (byte)((n >> 7) & 0x7F), new MidiInput_PortIndex((byte)(n & 0xFF)));

    [Fact]
    public void Empty_ring_dequeues_nothing()
    {
        var ring = new MidiInput_MessageRing(8, 8);
        Assert.False(ring.TryDequeue(out _));
        Assert.Equal(0, ring.DroppedCount);
    }

    [Fact]
    public void Preserves_order_and_wraps_within_a_segment()
    {
        var ring = new MidiInput_MessageRing(4, 4);
        int next = 0, expect = 0;
        for (int round = 0; round < 25; round++)
        {
            for (int i = 0; i < 3; i++) Assert.True(ring.TryEnqueue(Msg(next++)));
            for (int i = 0; i < 3; i++) { Assert.True(ring.TryDequeue(out var m)); Assert.Equal(expect++, m.ArrivalTicks); }
        }
        Assert.False(ring.TryDequeue(out _));
        Assert.Equal(0, ring.DroppedCount);
        Assert.Equal(0, ring.GrowCount);
    }

    [Fact]
    public void Fixed_size_drops_newest_when_full()
    {
        var ring = new MidiInput_MessageRing(4, 4);
        for (int i = 0; i < 4; i++) Assert.True(ring.TryEnqueue(Msg(i)));
        Assert.False(ring.TryEnqueue(Msg(99)));
        Assert.False(ring.TryEnqueue(Msg(100)));
        Assert.Equal(2, ring.DroppedCount);
        Assert.Equal(4, ring.CurrentCapacity);
        for (int i = 0; i < 4; i++) { Assert.True(ring.TryDequeue(out var m)); Assert.Equal(i, m.ArrivalTicks); }
        Assert.False(ring.TryDequeue(out _));
        // Space is available again after draining.
        Assert.True(ring.TryEnqueue(Msg(5)));
        Assert.True(ring.TryDequeue(out var last));
        Assert.Equal(5, last.ArrivalTicks);
    }

    [Fact]
    public void Grows_instead_of_dropping_until_max()
    {
        var ring = new MidiInput_MessageRing(4, 12);
        for (int i = 0; i < 12; i++) Assert.True(ring.TryEnqueue(Msg(i)));
        Assert.Equal(12, ring.CurrentCapacity);
        Assert.Equal(2, ring.GrowCount);
        Assert.Equal(0, ring.DroppedCount);

        Assert.False(ring.TryEnqueue(Msg(999)));
        Assert.Equal(1, ring.DroppedCount);

        for (int i = 0; i < 12; i++) { Assert.True(ring.TryDequeue(out var m)); Assert.Equal(i, m.ArrivalTicks); }
        Assert.False(ring.TryDequeue(out _));
    }

    [Fact]
    public void Order_survives_segment_boundaries_under_interleaving()
    {
        var ring = new MidiInput_MessageRing(3, 30);
        var rng = new Random(1234);
        int next = 0, expect = 0;
        for (int step = 0; step < 5000; step++)
        {
            int produce = rng.Next(0, 7);
            for (int i = 0; i < produce; i++)
            {
                if (next - expect >= 30 - 3) break;                // guaranteed backlog = Max - SegmentSize; drops are tested elsewhere
                Assert.True(ring.TryEnqueue(Msg(next)), $"step={step} backlog={next - expect} cap={ring.CurrentCapacity} grew={ring.GrowCount} dropped={ring.DroppedCount}");
                next++;
            }
            int consume = rng.Next(0, 6);
            for (int i = 0; i < consume; i++)
                if (ring.TryDequeue(out var m)) Assert.Equal(expect++, m.ArrivalTicks);
        }
        while (ring.TryDequeue(out var m)) Assert.Equal(expect++, m.ArrivalTicks);
        Assert.Equal(next, expect);
        Assert.Equal(0, ring.DroppedCount);
        Assert.True(ring.CurrentCapacity <= 30);
    }

    [Fact]
    public void Grown_ring_reuses_segments_without_further_growth()
    {
        var ring = new MidiInput_MessageRing(4, 16);
        for (int i = 0; i < 16; i++) ring.TryEnqueue(Msg(i));
        while (ring.TryDequeue(out _)) { }
        long grew = ring.GrowCount;
        Assert.Equal(16, ring.CurrentCapacity);

        int next = 100;
        for (int round = 0; round < 1000; round++)
        {
            for (int i = 0; i < 10; i++) Assert.True(ring.TryEnqueue(Msg(next++)));
            int got = 0; while (ring.TryDequeue(out _)) got++;
            Assert.Equal(10, got);
        }
        Assert.Equal(grew, ring.GrowCount);
        Assert.Equal(0, ring.DroppedCount);
    }

    [Fact]
    public void Steady_state_is_allocation_free()
    {
        var ring = new MidiInput_MessageRing(64, 256);
        // warm-up: force growth to the working size
        for (int i = 0; i < 256; i++) ring.TryEnqueue(Msg(i));
        while (ring.TryDequeue(out _)) { }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int round = 0; round < 20_000; round++)
        {
            for (int i = 0; i < 50; i++) ring.TryEnqueue(Msg(i));
            while (ring.TryDequeue(out _)) { }
        }
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
    }

    [Fact]
    public void Concurrent_producer_consumer_delivers_everything_in_order()
    {
        var ring = new MidiInput_MessageRing(16, 1024);
        const int total = 500_000;
        long dropped = 0;
        var producer = new Thread(() =>
        {
            for (int i = 0; i < total; i++)
            {
                while (!ring.TryEnqueue(Msg(i))) Thread.SpinWait(20);   // never lose a message in this test
            }
        });
        long expect = 0;
        var consumer = new Thread(() =>
        {
            while (expect < total)
            {
                if (ring.TryDequeue(out var m)) { Assert.Equal(expect, m.ArrivalTicks); expect++; }
                else Thread.SpinWait(20);
            }
        });
        producer.Start(); consumer.Start();
        Assert.True(producer.Join(30_000));
        Assert.True(consumer.Join(30_000));
        Assert.Equal(total, expect);
        _ = dropped;
    }

    [Fact]
    public void LagCount_is_independent_of_DroppedCount()
    {
        var ring = new MidiInput_MessageRing(4, 4);
        ring.NoteDriverLag(); ring.NoteDriverLag();
        Assert.Equal(2, ring.LagCount);
        Assert.Equal(0, ring.DroppedCount);
    }

    [Fact]
    public void Invalid_capacities_throw()
    {
        Assert.Throws<ArgumentException>(() => new MidiInput_MessageRing(0, 4));
        Assert.Throws<ArgumentException>(() => new MidiInput_MessageRing(8, 4));
    }
}