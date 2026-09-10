namespace AN.Audio.Midi.Internal;

/// <summary>
/// Per-port SysEx reassembly (SPEC-30 D7/D20). Drivers return SysEx in fixed-size buffers; a message longer
/// than one buffer arrives as consecutive fragments, the last ending in F7. Not thread-safe: one instance per
/// port, fed from that port's callback only.
/// </summary>
internal sealed class Midi_SysExReassembler
{
    private readonly int _maxBytes;
    private byte[] _buffer;
    private int _length;
    private bool _inMessage;
    private bool _overflowed;
    private long _discardedCount;

    public Midi_SysExReassembler(int initialBytes, int maxBytes)
    {
        _maxBytes = maxBytes;
        _buffer = new byte[Math.Max(16, Math.Min(initialBytes, maxBytes))];
    }

    /// <summary>Messages discarded because they exceeded the cap or were aborted (D20).</summary>
    public long DiscardedCount => _discardedCount;

    /// <summary>True between an F0 and its F7: the next data bytes belong to a SysEx in progress (CoreMIDI packet walker uses this to route data bytes).</summary>
    public bool InMessage => _inMessage;

    /// <summary>
    /// Feed one fragment. Returns true when a complete F0..F7 message is available in <paramref name="complete"/>
    /// (valid until the next call). Fragments of an over-cap message are swallowed until its F7.
    /// </summary>
    public bool Append(ReadOnlySpan<byte> fragment, out ReadOnlySpan<byte> complete)
    {
        complete = default;
        if (fragment.IsEmpty) return false;

        // A new F0 while mid-message means the previous one was aborted (no F7 ever came).
        if (fragment[0] == (byte)Midi_Status.SysExStart)
        {
            if (_inMessage && !_overflowed) _discardedCount++;
            _length = 0;
            _inMessage = true;
            _overflowed = false;
        }
        else if (!_inMessage)
        {
            // Continuation without a start we saw (e.g. buffer posted mid-stream). Nothing to attach it to.
            return false;
        }

        bool endsMessage = fragment[^1] == (byte)Midi_Status.SysExEnd;

        if (!_overflowed)
        {
            if (_length + fragment.Length > _maxBytes)
            {
                _overflowed = true;
                _discardedCount++;
            }
            else
            {
                EnsureCapacity(_length + fragment.Length);
                fragment.CopyTo(_buffer.AsSpan(_length));
                _length += fragment.Length;
            }
        }

        if (!endsMessage) return false;

        _inMessage = false;
        if (_overflowed) { _overflowed = false; _length = 0; return false; }

        complete = _buffer.AsSpan(0, _length);
        _length = 0;
        return true;
    }

    /// <summary>Forget any partial message (port reset/close).</summary>
    public void Reset()
    {
        if (_inMessage && !_overflowed) _discardedCount++;
        _inMessage = false;
        _overflowed = false;
        _length = 0;
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _buffer.Length) return;
        int size = _buffer.Length;
        while (size < needed) size *= 2;
        Array.Resize(ref _buffer, Math.Min(size, _maxBytes));
    }
}