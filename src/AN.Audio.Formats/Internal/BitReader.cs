using System.Buffers.Binary;

namespace AN.Audio.Formats.Internal;

/// <summary>
/// Little/big-endian header field reads over a <see cref="PeekableStream"/> (WAV now, AIFF later). Every read is exact:
/// hitting EOF mid-field throws <see cref="EndOfStreamException"/>, which the format decoder turns into its own error.
/// FLAC keeps the reference decoder's own bit-level reader.
/// </summary>
internal sealed class BitReader
{
    private readonly PeekableStream _stream;
    private readonly byte[] _scratch = new byte[16];

    public BitReader(PeekableStream stream) => _stream = stream;

    public PeekableStream Stream => _stream;
    public long Position => _stream.Position;

    private ReadOnlySpan<byte> Exact(int count)
    {
        _stream.ReadExactlyOrThrow(_scratch.AsSpan(0, count));
        return _scratch.AsSpan(0, count);
    }

    public byte ReadByte() => Exact(1)[0];

    public ushort ReadU16LE() => BinaryPrimitives.ReadUInt16LittleEndian(Exact(2));
    public short ReadI16LE() => BinaryPrimitives.ReadInt16LittleEndian(Exact(2));
    public uint ReadU32LE() => BinaryPrimitives.ReadUInt32LittleEndian(Exact(4));
    public int ReadI32LE() => BinaryPrimitives.ReadInt32LittleEndian(Exact(4));
    public ulong ReadU64LE() => BinaryPrimitives.ReadUInt64LittleEndian(Exact(8));

    public ushort ReadU16BE() => BinaryPrimitives.ReadUInt16BigEndian(Exact(2));
    public uint ReadU32BE() => BinaryPrimitives.ReadUInt32BigEndian(Exact(4));
    public ulong ReadU64BE() => BinaryPrimitives.ReadUInt64BigEndian(Exact(8));

    /// <summary>Reads a 4-byte chunk id as its little-endian packed uint (the FourCC convention used by <c>Wav_ChunkId</c>).</summary>
    public uint ReadFourCC() => BinaryPrimitives.ReadUInt32LittleEndian(Exact(4));

    public Guid ReadGuid() => new(Exact(16));

    public void ReadExactly(Span<byte> destination) => _stream.ReadExactlyOrThrow(destination);

    /// <summary>Skips <paramref name="count"/> bytes; returns false if EOF arrived first.</summary>
    public bool TrySkip(long count) => _stream.Skip(count) == count;
}