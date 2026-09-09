using System.Buffers.Binary;
using System.Text;

namespace AN.Audio.Formats.Wav;

/// <summary>
/// A RIFF FourCC, branded (overview rule 3). Stored as the little-endian packed <c>uint</c> of the 4 ASCII bytes, so
/// <c>Wav_ChunkId.Data.Value == 0x61746164</c> ('d','a','t','a'). Compare with the well-known statics or by string.
/// </summary>
public readonly record struct Wav_ChunkId(uint Value)
{
    public static Wav_ChunkId FromString(string fourcc)
    {
        if (fourcc.Length != 4) throw new ArgumentException("FourCC must be 4 characters", nameof(fourcc));
        Span<byte> b = stackalloc byte[4];
        for (int i = 0; i < 4; i++) b[i] = (byte)fourcc[i];
        return new(BinaryPrimitives.ReadUInt32LittleEndian(b));
    }

    public static Wav_ChunkId FromBytes(ReadOnlySpan<byte> fourBytes) => new(BinaryPrimitives.ReadUInt32LittleEndian(fourBytes));

    public void WriteTo(Span<byte> destination) => BinaryPrimitives.WriteUInt32LittleEndian(destination, Value);

    public override string ToString()
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, Value);
        var sb = new StringBuilder(4);
        foreach (byte c in b) sb.Append(c is >= 0x20 and < 0x7F ? (char)c : '?');
        return sb.ToString();
    }

    // ── well-known ids ──
    public static readonly Wav_ChunkId Riff = FromString("RIFF");
    public static readonly Wav_ChunkId Rf64 = FromString("RF64");
    public static readonly Wav_ChunkId Wave = FromString("WAVE");
    public static readonly Wav_ChunkId Fmt  = FromString("fmt ");
    public static readonly Wav_ChunkId Data = FromString("data");
    public static readonly Wav_ChunkId Fact = FromString("fact");
    public static readonly Wav_ChunkId Smpl = FromString("smpl");
    public static readonly Wav_ChunkId Cue  = FromString("cue ");
    public static readonly Wav_ChunkId List = FromString("LIST");
    public static readonly Wav_ChunkId Info = FromString("INFO");
    public static readonly Wav_ChunkId Adtl = FromString("adtl");
    public static readonly Wav_ChunkId Inst = FromString("inst");
    public static readonly Wav_ChunkId Bext = FromString("bext");
    public static readonly Wav_ChunkId Id3  = FromString("id3 ");
    public static readonly Wav_ChunkId Ds64 = FromString("ds64");
    public static readonly Wav_ChunkId Bw64 = FromString("BW64");
    /// <summary>Sony Wave64: the first four bytes of the <c>riff</c> GUID (lower-case, unlike RIFF).</summary>
    public static readonly Wav_ChunkId W64Riff = FromString("riff");
    public static readonly Wav_ChunkId W64Wave = FromString("wave");

    /// <summary>Wave64 GUID tail shared by every chunk GUID except <c>riff</c> (ffmpeg <c>w64.c</c>): bytes 4..15.</summary>
    public static ReadOnlySpan<byte> W64ChunkGuidTail => [0xF3, 0xAC, 0xD3, 0x11, 0x8C, 0xD1, 0x00, 0xC0, 0x4F, 0x8E, 0xDB, 0x8A];
    /// <summary>Wave64 <c>riff</c> GUID tail: bytes 4..15.</summary>
    public static ReadOnlySpan<byte> W64RiffGuidTail => [0x2E, 0x91, 0xCF, 0x11, 0xA5, 0xD6, 0x28, 0xDB, 0x04, 0xC1, 0x00, 0x00];
}

/// <summary>Which of the three WAVE container layouts the stream uses (spec 50 Phase 4c).</summary>
public enum Wav_ContainerLayout
{
    /// <summary>Classic RIFF: 32-bit sizes, 2-byte alignment.</summary>
    Riff,
    /// <summary>EBU Tech 3306 RF64 (or ITU-R BS.2088 <c>BW64</c>): RIFF layout plus a <c>ds64</c> chunk carrying 64-bit sizes.</summary>
    Rf64,
    /// <summary>Sony Wave64: 16-byte GUID chunk ids, 64-bit sizes that include the 24-byte header, 8-byte alignment.</summary>
    Wave64,
}

/// <summary>The RF64 <c>ds64</c> chunk: 64-bit sizes that replace any 32-bit size field written as <c>0xFFFFFFFF</c>.</summary>
/// <param name="RiffSize">Size of the RIFF body (from byte 8), 64-bit.</param>
/// <param name="DataSize">Size of the <c>data</c> body, 64-bit (authoritative; the <c>data</c> chunk's own field is usually -1).</param>
/// <param name="SampleCount">Sample frames per channel as written by the encoder (may be 0 = not filled in).</param>
/// <param name="Table">64-bit sizes for any OTHER chunk whose 32-bit field is -1.</param>
public sealed record Wav_DataSize64Chunk(ulong RiffSize, ulong DataSize, ulong SampleCount, IReadOnlyList<Wav_DataSize64Chunk.Entry> Table)
{
    public readonly record struct Entry(Wav_ChunkId Id, ulong Size);

    public ulong? SizeFor(Wav_ChunkId id)
    {
        if (id == Wav_ChunkId.Data) return DataSize;
        foreach (var e in Table) if (e.Id == id) return e.Size;
        return null;
    }
}

/// <summary>One entry of <see cref="Wav_Decoder.Chunks"/>: every chunk seen in the walk, known or not (§WAV).</summary>
/// <param name="Offset">Byte offset of the chunk HEADER (id) from the start of the stream.</param>
/// <param name="DeclaredLength">Body length as declared (RIFF: the 32-bit field; RF64: resolved through <c>ds64</c> when the field is -1; Wave64: the 64-bit size minus the 24-byte header).</param>
/// <param name="ClampedLength">Bytes actually available (== Declared unless the chunk overran the stream).</param>
/// <param name="PadBytePresent">For chunks needing alignment padding (RIFF/RF64: odd length → 1 byte; Wave64: to 8 bytes): whether the padding was actually there (false for the clap-808 case).</param>
/// <param name="RawBytes">Body bytes for non-<c>data</c> chunks up to <see cref="Wav_Decoder.MaxRetainedChunkBytes"/>; null for <c>data</c> and oversize chunks.</param>
public sealed record Wav_ChunkInfo(Wav_ChunkId Id, long Offset, long DeclaredLength, long ClampedLength, bool PadBytePresent, byte[]? RawBytes)
{
    public override string ToString() => $"{Id} @{Offset} len={DeclaredLength}{(ClampedLength != DeclaredLength ? $" (clamped {ClampedLength})" : "")}{(!PadBytePresent ? " no-pad" : "")}";
}