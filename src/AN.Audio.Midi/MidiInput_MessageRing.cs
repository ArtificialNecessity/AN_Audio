namespace AN.Audio.Midi;

/// <summary>
/// Lock-free single-producer / single-consumer queue of <see cref="MidiInput_Message"/> (SPEC-30 D5/D23).
/// Producer = the driver callback. Consumer = exactly ONE thread of yours (audio callback or UI tick).
/// Fully encapsulated: you only call TryDequeue/DequeueAll and read counters; storage is invisible.
///
/// Storage is a circular linked list of fixed-size segments, each an SPSC circular buffer. When the tail
/// segment is full and the next segment is still being read by the consumer, the producer inserts a fresh
/// segment (until <see cref="MaxCapacity"/>) instead of dropping. Nothing is ever copied or freed; once the
/// working size is reached the steady state allocates nothing. At max capacity the newest message is dropped
/// and <see cref="DroppedCount"/> increments. The producer may only reuse a segment the consumer has LEFT (writing
/// into the consumer's current segment would reorder messages), and the consumer leaves a drained segment on its
/// next dequeue, so the guaranteed undropped backlog is MaxCapacity - InitialCapacity (one segment of slack).
/// With the defaults that is 16384 - 1024 = 15360 messages.
/// </summary>
public sealed class MidiInput_MessageRing
{
    private sealed class Segment
    {
        public readonly MidiInput_Message[] Slots;
        public long Head;   // consumer-owned, monotonic
        public long Tail;   // producer-owned, monotonic
        public Segment Next;

        public Segment(int capacity)
        {
            Slots = new MidiInput_Message[capacity];
            Next = this;
        }
    }

    private readonly int _segmentCapacity;
    private readonly int _maxCapacity;
    private int _currentCapacity;
    private Segment _producerSegment;
    private Segment _consumerSegment;
    private long _droppedCount;
    private long _lagCount;
    private long _growCount;

    public MidiInput_MessageRing(int initialCapacity, int maxCapacity)
    {
        if (initialCapacity <= 0) throw new ArgumentException("initialCapacity must be positive", nameof(initialCapacity));
        if (maxCapacity < initialCapacity) throw new ArgumentException("maxCapacity must be >= initialCapacity", nameof(maxCapacity));
        _segmentCapacity = initialCapacity;
        _maxCapacity = maxCapacity;
        _currentCapacity = initialCapacity;
        var first = new Segment(initialCapacity);
        _producerSegment = first;
        _consumerSegment = first;
    }

    /// <summary>Messages the queue can hold right now. Starts at RingInitialCapacity, grows up to <see cref="MaxCapacity"/>, never shrinks.</summary>
    public int CurrentCapacity => Volatile.Read(ref _currentCapacity);

    public int MaxCapacity => _maxCapacity;

    /// <summary>Messages this library discarded because the queue was full at max capacity (D5). Nothing else ever increments it.</summary>
    public long DroppedCount => Volatile.Read(ref _droppedCount);

    /// <summary>Driver-reported lag events (WinMM MIM_MOREDATA, D22). Those messages WERE delivered; this only says we were slow.</summary>
    public long LagCount => Volatile.Read(ref _lagCount);

    /// <summary>How many times the queue grew. A stable system shows this stop increasing after warm-up.</summary>
    public long GrowCount => Volatile.Read(ref _growCount);

    // ── consumer side ────────────────────────────────────────────────────────────────────

    /// <summary>Consumer thread only. Returns false when empty.</summary>
    public bool TryDequeue(out MidiInput_Message message)
    {
        while (true)
        {
            Segment seg = _consumerSegment;
            long head = seg.Head;                      // our own write; plain read is fine
            long tail = Volatile.Read(ref seg.Tail);
            if (head != tail)
            {
                message = seg.Slots[(int)(head % seg.Slots.Length)];
                Volatile.Write(ref seg.Head, head + 1);
                return true;
            }

            // Segment drained. If the producer is still on it, the queue is empty.
            if (ReferenceEquals(seg, Volatile.Read(ref _producerSegment)))
            {
                message = default;
                return false;
            }

            // Producer moved on (it only does so when this segment was full), so follow it.
            // Publishing _consumerSegment is the signal to the producer that this segment is free.
            Volatile.Write(ref _consumerSegment, Volatile.Read(ref seg.Next));
        }
    }

    /// <summary>Consumer thread only. Drains up to <paramref name="into"/>.Length messages; returns the count.</summary>
    public int DequeueAll(Span<MidiInput_Message> into)
    {
        int n = 0;
        while (n < into.Length && TryDequeue(out into[n])) n++;
        return n;
    }

    // ── producer side (library-internal, driver thread) ───────────────────────────────────────────

    /// <summary>Producer thread only. Returns false if the message was dropped (queue full at max capacity).</summary>
    internal bool TryEnqueue(in MidiInput_Message message)
    {
        Segment seg = _producerSegment;
        long tail = seg.Tail;                          // our own write
        long head = Volatile.Read(ref seg.Head);
        if (tail - head < seg.Slots.Length)
        {
            seg.Slots[(int)(tail % seg.Slots.Length)] = message;
            Volatile.Write(ref seg.Tail, tail + 1);
            return true;
        }

        // Current segment full. Can we move to the next one?
        Segment next = seg.Next;
        if (!ReferenceEquals(next, Volatile.Read(ref _consumerSegment)))
        {
            // Consumer has already left 'next' (it only leaves a drained segment), so it is empty and reusable.
            return WriteFirstInto(next, message);
        }

        // Next segment is the one the consumer is reading. Grow if allowed (D23), else drop newest (D5).
        int capacity = _currentCapacity;
        if (capacity < _maxCapacity)
        {
            int newSize = Math.Min(_segmentCapacity, _maxCapacity - capacity);
            var fresh = new Segment(newSize) { Next = next };
            Volatile.Write(ref seg.Next, fresh);
            Volatile.Write(ref _currentCapacity, capacity + newSize);
            _growCount++;
            return WriteFirstInto(fresh, message);
        }

        Volatile.Write(ref _droppedCount, _droppedCount + 1);
        return false;
    }

    private bool WriteFirstInto(Segment target, in MidiInput_Message message)
    {
        long tail = target.Tail;
        target.Slots[(int)(tail % target.Slots.Length)] = message;
        Volatile.Write(ref target.Tail, tail + 1);
        Volatile.Write(ref _producerSegment, target);
        return true;
    }

    /// <summary>Producer thread only (D22).</summary>
    internal void NoteDriverLag() => Volatile.Write(ref _lagCount, _lagCount + 1);
}