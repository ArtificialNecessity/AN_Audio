using AN.Audio.Formats.Internal;
using AN.Audio.Formats.Tests.Support;
using Xunit;

namespace AN.Audio.Formats.Tests;

public class PeekableStreamTests
{
    private static byte[] Seq(int n) => Enumerable.Range(0, n).Select(i => (byte)i).ToArray();

    public static IEnumerable<object[]> Modes() { yield return [true]; yield return [false]; }

    private static PeekableStream Wrap(byte[] data, bool seekable)
    {
        Stream inner = seekable ? new MemoryStream(data) : new ForwardOnlyStream(data);
        return new PeekableStream(inner, leaveOpen: false, initialCapacity: 16);
    }

    [Theory, MemberData(nameof(Modes))]
    public void Peek_Then_Read_Replays(bool seekable)
    {
        var data = Seq(200);
        using var s = Wrap(data, seekable);
        var p = s.Peek(12);
        Assert.Equal(12, p.Length);
        Assert.Equal(data[..12], p.ToArray());
        Assert.Equal(0, s.Position);

        // peek larger than the initial capacity forces growth + compaction
        p = s.Peek(50);
        Assert.Equal(50, p.Length);
        Assert.Equal(data[..50], p.ToArray());

        var buf = new byte[200];
        int total = 0;
        while (total < 200)
        {
            int n = s.Read(buf.AsSpan(total, Math.Min(7, 200 - total)));
            if (n == 0) break;
            total += n;
        }
        Assert.Equal(200, total);
        Assert.Equal(data, buf);
        Assert.Equal(200, s.Position);
        Assert.Equal(0, s.Read(buf));
    }

    [Theory, MemberData(nameof(Modes))]
    public void Read_AcrossBufferBoundary(bool seekable)
    {
        var data = Seq(100);
        using var s = Wrap(data, seekable);
        s.Peek(10);
        var buf = new byte[30];
        int got = s.ReadFully(buf);   // 10 from buffer + 20 from inner
        Assert.Equal(30, got);
        Assert.Equal(data[..30], buf);
        Assert.Equal(30, s.Position);
        Assert.Equal(0, s.BufferedCount);
    }

    [Theory, MemberData(nameof(Modes))]
    public void Skip_FromBufferAndInner(bool seekable)
    {
        var data = Seq(100);
        using var s = Wrap(data, seekable);
        s.Peek(8);
        Assert.Equal(20, s.Skip(20));
        Assert.Equal(20, s.Position);
        Assert.Equal(20, s.ReadByte());
        Assert.Equal(79, s.Skip(1000));   // clamps at EOF
        Assert.Equal(100, s.Position);
        Assert.Equal(-1, s.ReadByte());
    }

    [Theory, MemberData(nameof(Modes))]
    public void Peek_AtEof_IsShort(bool seekable)
    {
        using var s = Wrap(Seq(5), seekable);
        Assert.Equal(5, s.Peek(12).Length);
        Assert.False(s.TryPeekExact(6, out _));
        Assert.True(s.TryPeekExact(5, out var five));
        Assert.Equal(5, five.Length);
    }

    [Fact]
    public void CanSeek_Mirrors_And_SeekDiscardsBuffer()
    {
        var data = Seq(100);
        using var s = Wrap(data, seekable: true);
        Assert.True(s.CanSeek);
        s.Peek(20);
        s.Position = 50;
        Assert.Equal(50, s.ReadByte());
        Assert.Equal(51, s.Position);
        // seek within the buffered window
        s.Position = 0; s.Peek(20); s.Position = 5;
        Assert.Equal(5, s.ReadByte());
        Assert.Equal(100, s.Length);
    }

    [Fact]
    public void ForwardOnly_SeekThrows()
    {
        using var s = Wrap(Seq(10), seekable: false);
        Assert.False(s.CanSeek);
        Assert.Throws<NotSupportedException>(() => s.Position = 3);
        Assert.Throws<NotSupportedException>(() => s.Length);
    }

    [Fact]
    public void LeaveOpen_Honoured()
    {
        var inner = new MemoryStream(Seq(10));
        var s = new PeekableStream(inner, leaveOpen: true);
        s.Dispose();
        Assert.True(inner.CanRead);
        inner = new MemoryStream(Seq(10));
        s = new PeekableStream(inner, leaveOpen: false);
        s.Dispose();
        Assert.False(inner.CanRead);
    }

    [Fact]
    public void ReadExactlyOrThrow_Throws_AtEof()
    {
        using var s = Wrap(Seq(3), seekable: false);
        Assert.Throws<EndOfStreamException>(() => s.ReadExactlyOrThrow(new byte[4]));
    }
}