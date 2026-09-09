namespace AN.Audio.Formats.Tests.Support;

/// <summary>
/// A <see cref="Stream"/> over a <c>byte[]</c> that behaves like a network read: <see cref="CanSeek"/> is false,
/// <see cref="Length"/>/<see cref="Position"/> throw, and each <see cref="Read(Span{byte})"/> returns a random 1..97 bytes
/// (seeded, so failures reproduce). Every format's tests run once seekable, once through this.
/// </summary>
public sealed class ForwardOnlyStream : Stream
{
    private readonly byte[] _data;
    private readonly Random _rng;
    private int _pos;
    public int ReadCalls { get; private set; }

    public ForwardOnlyStream(byte[] data, int seed = 12345)
    {
        _data = data;
        _rng = new Random(seed);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ReadCalls++;
        if (_pos >= _data.Length || buffer.Length == 0) return 0;
        int n = Math.Min(buffer.Length, Math.Min(_rng.Next(1, 98), _data.Length - _pos));
        _data.AsSpan(_pos, n).CopyTo(buffer);
        _pos += n;
        return n;
    }

    public override int ReadByte()
    {
        ReadCalls++;
        return _pos < _data.Length ? _data[_pos++] : -1;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}