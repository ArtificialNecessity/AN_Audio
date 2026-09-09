namespace AN.Audio.Formats.Internal;

/// <summary>
/// Wraps any <see cref="Stream"/> and adds bounded look-ahead so <see cref="AudioDecoder.Sniff"/> and header parsing
/// can inspect leading bytes without consuming them, then replays them. THE reason non-seekable sources work (D2).
/// <list type="bullet">
/// <item><see cref="Peek"/> fills an internal buffer from the inner stream and returns what it has (up to the requested count).</item>
/// <item><see cref="Read(Span{byte})"/> drains the buffer first, then passes through to the inner stream (no extra copies once drained).</item>
/// <item><see cref="CanSeek"/> mirrors the inner stream; <see cref="Position"/> is correct in both modes (logical position, buffer-aware).</item>
/// <item>Seeking discards the look-ahead buffer.</item>
/// </list>
/// </summary>
internal sealed class PeekableStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private byte[] _buffer;
    private int _bufferStart;   // first unread byte in _buffer
    private int _bufferEnd;     // one past the last valid byte in _buffer
    private long _position;     // logical position = bytes handed out to the caller so far (or seek target)

    public PeekableStream(Stream inner, bool leaveOpen = false, int initialCapacity = 4096)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _leaveOpen = leaveOpen;
        _buffer = new byte[Math.Max(16, initialCapacity)];
        _position = inner.CanSeek ? inner.Position : 0;
    }

    public Stream Inner => _inner;

    /// <summary>Bytes currently buffered ahead of the logical position.</summary>
    public int BufferedCount => _bufferEnd - _bufferStart;

    /// <summary>
    /// Ensures at least <paramref name="count"/> bytes are buffered (reading from the inner stream as needed) and returns
    /// a span of the buffered bytes starting at the logical position. The span may be SHORTER than requested at EOF.
    /// </summary>
    public ReadOnlySpan<byte> Peek(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        EnsureBuffered(count);
        return _buffer.AsSpan(_bufferStart, Math.Min(count, BufferedCount));
    }

    /// <summary>Peeks exactly <paramref name="count"/> bytes or returns false at EOF.</summary>
    public bool TryPeekExact(int count, out ReadOnlySpan<byte> bytes)
    {
        bytes = Peek(count);
        return bytes.Length == count;
    }

    /// <summary>Discards <paramref name="count"/> bytes (from the buffer first, then the inner stream). Returns bytes actually skipped.</summary>
    public long Skip(long count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        long skipped = 0;
        int fromBuffer = (int)Math.Min(count, BufferedCount);
        _bufferStart += fromBuffer;
        skipped += fromBuffer;
        long remaining = count - fromBuffer;
        if (remaining > 0)
        {
            if (_inner.CanSeek)
            {
                long before = _inner.Position;
                long target = Math.Min(before + remaining, _inner.Length);
                _inner.Position = target;
                skipped += target - before;
            }
            else
            {
                // Forward-only: read and discard through our own buffer
                while (remaining > 0)
                {
                    int chunk = (int)Math.Min(remaining, _buffer.Length);
                    int n = _inner.Read(_buffer, 0, chunk);
                    if (n <= 0) break;
                    remaining -= n;
                    skipped += n;
                }
                _bufferStart = _bufferEnd = 0;
            }
        }
        _position += skipped;
        if (_bufferStart == _bufferEnd) _bufferStart = _bufferEnd = 0;
        return skipped;
    }

    /// <summary>Reads exactly <paramref name="buffer"/>.Length bytes or throws <see cref="EndOfStreamException"/>.</summary>
    public void ReadExactlyOrThrow(Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = Read(buffer.Slice(total));
            if (n <= 0) throw new EndOfStreamException();
            total += n;
        }
    }

    /// <summary>Reads up to <paramref name="buffer"/>.Length bytes, looping over short reads; returns bytes read (&lt; length only at EOF).</summary>
    public int ReadFully(Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = Read(buffer.Slice(total));
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    private void EnsureBuffered(int count)
    {
        if (BufferedCount >= count) return;
        // compact
        if (_bufferStart > 0)
        {
            Array.Copy(_buffer, _bufferStart, _buffer, 0, BufferedCount);
            _bufferEnd -= _bufferStart;
            _bufferStart = 0;
        }
        if (_buffer.Length < count)
        {
            int newSize = Math.Max(count, _buffer.Length * 2);
            Array.Resize(ref _buffer, newSize);
        }
        while (BufferedCount < count)
        {
            int n = _inner.Read(_buffer, _bufferEnd, _buffer.Length - _bufferEnd);
            if (n <= 0) break;
            _bufferEnd += n;
        }
    }

    // ── Stream ──

    public override bool CanRead => true;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0) return 0;
        int buffered = BufferedCount;
        if (buffered > 0)
        {
            int n = Math.Min(buffered, buffer.Length);
            _buffer.AsSpan(_bufferStart, n).CopyTo(buffer);
            _bufferStart += n;
            if (_bufferStart == _bufferEnd) _bufferStart = _bufferEnd = 0;
            _position += n;
            return n;
        }
        int read = _inner.Read(buffer);
        if (read > 0) _position += read;
        return read;
    }

    public override int ReadByte()
    {
        if (BufferedCount > 0)
        {
            byte b = _buffer[_bufferStart++];
            if (_bufferStart == _bufferEnd) _bufferStart = _bufferEnd = 0;
            _position++;
            return b;
        }
        int v = _inner.ReadByte();
        if (v >= 0) _position++;
        return v;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        if (!_inner.CanSeek) throw new NotSupportedException("Underlying stream does not support seeking");
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _inner.Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        // Fast path: target lies inside the look-ahead buffer
        long bufferLogicalStart = _position;
        if (target >= bufferLogicalStart && target <= bufferLogicalStart + BufferedCount)
        {
            _bufferStart += (int)(target - bufferLogicalStart);
            if (_bufferStart == _bufferEnd) _bufferStart = _bufferEnd = 0;
        }
        else
        {
            _bufferStart = _bufferEnd = 0;
            _inner.Position = target;
        }
        _position = target;
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen) _inner.Dispose();
        base.Dispose(disposing);
    }
}