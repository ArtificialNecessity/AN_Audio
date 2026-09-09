using System.Buffers.Binary;
using System.Text;

namespace AN.Audio.Formats.Wav;

/// <summary><c>smpl</c> loop type (dwType).</summary>
public enum Wav_SampleLoopType : uint
{
    Forward = 0,
    PingPong = 1,
    Backward = 2,
}

/// <summary>One loop of the <c>smpl</c> chunk. <see cref="Start"/>/<see cref="End"/> are sample-frame offsets (End inclusive per the spec).</summary>
public readonly record struct Wav_SampleLoop(uint CuePointId, Wav_SampleLoopType Type, uint Start, uint End, uint Fraction, uint PlayCount)
{
    /// <summary>0 = loop forever.</summary>
    public bool IsInfinite => PlayCount == 0;
}

/// <summary>The <c>smpl</c> chunk: the sampler's root note, fine tune and loop points.</summary>
public sealed record Wav_SamplerChunk(
    uint Manufacturer,
    uint Product,
    uint SamplePeriodNanoseconds,
    int MidiUnityNote,
    uint MidiPitchFraction,
    uint SmpteFormat,
    uint SmpteOffset,
    Wav_SampleLoop[] Loops,
    byte[] SamplerSpecificData)
{
    /// <summary>Pitch fraction as semitone cents (0..100), from the 0x80000000 = ½-semitone convention.</summary>
    public double MidiPitchFractionCents => MidiPitchFraction / 4294967296.0 * 100.0;

    internal static Wav_SamplerChunk? TryParse(ReadOnlySpan<byte> b)
    {
        if (b.Length < 36) return null;
        uint numLoops = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(28));
        uint dataBytes = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(32));
        int loopsAvailable = (int)Math.Min(numLoops, (uint)((b.Length - 36) / 24));
        var loops = new Wav_SampleLoop[loopsAvailable];
        for (int i = 0; i < loopsAvailable; i++)
        {
            var l = b.Slice(36 + i * 24, 24);
            loops[i] = new Wav_SampleLoop(
                BinaryPrimitives.ReadUInt32LittleEndian(l),
                (Wav_SampleLoopType)BinaryPrimitives.ReadUInt32LittleEndian(l.Slice(4)),
                BinaryPrimitives.ReadUInt32LittleEndian(l.Slice(8)),
                BinaryPrimitives.ReadUInt32LittleEndian(l.Slice(12)),
                BinaryPrimitives.ReadUInt32LittleEndian(l.Slice(16)),
                BinaryPrimitives.ReadUInt32LittleEndian(l.Slice(20)));
        }
        int dataStart = 36 + loopsAvailable * 24;
        int dataLen = (int)Math.Min(dataBytes, (uint)Math.Max(0, b.Length - dataStart));
        return new Wav_SamplerChunk(
            BinaryPrimitives.ReadUInt32LittleEndian(b),
            BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(4)),
            BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(8)),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(12)),
            BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(16)),
            BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(20)),
            BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(24)),
            loops,
            b.Slice(dataStart, dataLen).ToArray());
    }
}

/// <summary>One entry of the <c>cue </c> chunk.</summary>
public readonly record struct Wav_CuePoint(uint Id, uint Position, Wav_ChunkId DataChunkId, uint ChunkStart, uint BlockStart, uint SampleOffset)
{
    internal static Wav_CuePoint[]? TryParse(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4) return null;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(b);
        int available = (int)Math.Min(count, (uint)((b.Length - 4) / 24));
        var cues = new Wav_CuePoint[available];
        for (int i = 0; i < available; i++)
        {
            var c = b.Slice(4 + i * 24, 24);
            cues[i] = new Wav_CuePoint(
                BinaryPrimitives.ReadUInt32LittleEndian(c),
                BinaryPrimitives.ReadUInt32LittleEndian(c.Slice(4)),
                Wav_ChunkId.FromBytes(c.Slice(8)),
                BinaryPrimitives.ReadUInt32LittleEndian(c.Slice(12)),
                BinaryPrimitives.ReadUInt32LittleEndian(c.Slice(16)),
                BinaryPrimitives.ReadUInt32LittleEndian(c.Slice(20)));
        }
        return cues;
    }
}

/// <summary>The <c>inst</c> chunk (7 bytes).</summary>
public readonly record struct Wav_InstrumentChunk(byte UnshiftedNote, sbyte FineTuneCents, sbyte GainDb, byte LowNote, byte HighNote, byte LowVelocity, byte HighVelocity)
{
    internal static Wav_InstrumentChunk? TryParse(ReadOnlySpan<byte> b)
        => b.Length < 7 ? null : new Wav_InstrumentChunk(b[0], (sbyte)b[1], (sbyte)b[2], b[3], b[4], b[5], b[6]);
}

/// <summary>
/// <c>LIST/INFO</c> tags. Keys are the INFO FourCCs (<c>INAM</c>, <c>IART</c>, …) as <see cref="Wav_ChunkId"/>; values are the
/// null-terminated strings decoded as UTF-8 (falls back to Latin-1 on invalid UTF-8, which older tools write).
/// </summary>
public sealed class Wav_InfoTags
{
    public static readonly Wav_ChunkId Name = Wav_ChunkId.FromString("INAM");
    public static readonly Wav_ChunkId Artist = Wav_ChunkId.FromString("IART");
    public static readonly Wav_ChunkId Comment = Wav_ChunkId.FromString("ICMT");
    public static readonly Wav_ChunkId Software = Wav_ChunkId.FromString("ISFT");
    public static readonly Wav_ChunkId Copyright = Wav_ChunkId.FromString("ICOP");
    public static readonly Wav_ChunkId CreationDate = Wav_ChunkId.FromString("ICRD");
    public static readonly Wav_ChunkId Genre = Wav_ChunkId.FromString("IGNR");
    public static readonly Wav_ChunkId Product = Wav_ChunkId.FromString("IPRD");
    public static readonly Wav_ChunkId Track = Wav_ChunkId.FromString("ITRK");
    public static readonly Wav_ChunkId Keywords = Wav_ChunkId.FromString("IKEY");
    public static readonly Wav_ChunkId Subject = Wav_ChunkId.FromString("ISBJ");
    public static readonly Wav_ChunkId Engineer = Wav_ChunkId.FromString("IENG");

    private readonly Dictionary<Wav_ChunkId, string> _tags = new();

    public IReadOnlyDictionary<Wav_ChunkId, string> All => _tags;
    public int Count => _tags.Count;

    public string? this[Wav_ChunkId id] => _tags.TryGetValue(id, out var v) ? v : null;
    public string? this[string fourcc] => this[Wav_ChunkId.FromString(fourcc)];

    public string? Title => this[Name];
    public string? ArtistName => this[Artist];
    public string? CommentText => this[Comment];
    public string? SoftwareName => this[Software];

    private static readonly Encoding Latin1 = Encoding.Latin1;
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    /// <summary>Parses the body of a <c>LIST</c> chunk whose list type is <c>INFO</c> (body starts after the type id).</summary>
    internal static Wav_InfoTags Parse(ReadOnlySpan<byte> body)
    {
        var tags = new Wav_InfoTags();
        int pos = 0;
        while (pos + 8 <= body.Length)
        {
            var id = Wav_ChunkId.FromBytes(body.Slice(pos));
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos + 4));
            pos += 8;
            int avail = (int)Math.Min(len, (uint)(body.Length - pos));
            var text = body.Slice(pos, avail);
            int nul = text.IndexOf((byte)0);
            if (nul >= 0) text = text.Slice(0, nul);
            tags._tags[id] = Decode(text);
            pos += avail + (avail % 2);   // sub-chunks are word aligned
        }
        return tags;
    }

    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return Latin1.GetString(bytes); }
    }
}