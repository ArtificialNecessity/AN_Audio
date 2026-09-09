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
}

/// <summary>One entry of <see cref="Wav_Decoder.Chunks"/>: every chunk seen in the walk, known or not (§WAV).</summary>
/// <param name="Offset">Byte offset of the chunk HEADER (id) from the start of the stream.</param>
/// <param name="DeclaredLength">Length field as written.</param>
/// <param name="ClampedLength">Bytes actually available (== Declared unless the chunk overran the stream).</param>
/// <param name="PadBytePresent">For odd-length chunks: whether the RIFF pad byte was actually there (false for the clap-808 case).</param>
/// <param name="RawBytes">Body bytes for non-<c>data</c> chunks up to <see cref="Wav_Decoder.MaxRetainedChunkBytes"/>; null for <c>data</c> and oversize chunks.</param>
public sealed record Wav_ChunkInfo(Wav_ChunkId Id, long Offset, uint DeclaredLength, long ClampedLength, bool PadBytePresent, byte[]? RawBytes)
{
    public override string ToString() => $"{Id} @{Offset} len={DeclaredLength}{(ClampedLength != DeclaredLength ? $" (clamped {ClampedLength})" : "")}{(DeclaredLength % 2 == 1 && !PadBytePresent ? " no-pad" : "")}";
}