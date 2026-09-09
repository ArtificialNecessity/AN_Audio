using System.Buffers.Binary;
using System.Text;

namespace AN.Audio.Formats.Flac;

/// <summary>METADATA_BLOCK_HEADER type codes (FLAC format spec §9.1).</summary>
public enum Flac_MetadataBlockType : byte
{
    StreamInfo = 0,
    Padding = 1,
    Application = 2,
    SeekTable = 3,
    VorbisComment = 4,
    Cuesheet = 5,
    Picture = 6,
    /// <summary>127 is forbidden (would collide with the frame sync).</summary>
    Invalid = 127,
}

/// <summary>The STREAMINFO block (34 bytes): everything the bitstream promises about itself.</summary>
public sealed record Flac_StreamInfo(
    int MinBlockSize,
    int MaxBlockSize,
    int MinFrameSize,
    int MaxFrameSize,
    int SampleRate,
    int Channels,
    int BitsPerSample,
    long TotalSamples,
    byte[] Md5Signature)
{
    /// <summary>D10: STREAMINFO total = 0 means unknown.</summary>
    public long? TotalSamplesOrNull => TotalSamples != 0 ? TotalSamples : null;
    /// <summary>True when every frame (except possibly the last) has the same block size.</summary>
    public bool IsFixedBlockSize => MinBlockSize == MaxBlockSize;
    public bool HasMd5 => Md5Signature.Any(b => b != 0);

    internal static Flac_StreamInfo Parse(ReadOnlySpan<byte> b)
    {
        if (b.Length < 34) throw new AudioDecoder_FormatException($"STREAMINFO is {b.Length} bytes, need 34", AudioDecoder_Container.Flac);
        int minBlock = BinaryPrimitives.ReadUInt16BigEndian(b);
        int maxBlock = BinaryPrimitives.ReadUInt16BigEndian(b.Slice(2));
        int minFrame = (b[4] << 16) | (b[5] << 8) | b[6];
        int maxFrame = (b[7] << 16) | (b[8] << 8) | b[9];
        // 20 bits rate, 3 bits channels-1, 5 bits bps-1, 36 bits total samples
        ulong packed = BinaryPrimitives.ReadUInt64BigEndian(b.Slice(10));
        int rate = (int)(packed >> 44);
        int channels = (int)((packed >> 41) & 0x7) + 1;
        int bps = (int)((packed >> 36) & 0x1F) + 1;
        long total = (long)(packed & 0xF_FFFF_FFFFUL);
        if (rate == 0) throw new AudioDecoder_FormatException("STREAMINFO sample rate is 0", AudioDecoder_Container.Flac);
        return new Flac_StreamInfo(minBlock, maxBlock, minFrame, maxFrame, rate, channels, bps, total, b.Slice(18, 16).ToArray());
    }
}

/// <summary>One SEEKTABLE point. Offsets are relative to the first byte of the first frame.</summary>
public readonly record struct Flac_SeekPoint(long SampleNumber, long StreamOffset, int FrameSamples)
{
    public bool IsPlaceholder => SampleNumber == -1;   // 0xFFFFFFFFFFFFFFFF
}

/// <summary>The SEEKTABLE block: sorted, placeholders removed.</summary>
public sealed class Flac_SeekTable
{
    public IReadOnlyList<Flac_SeekPoint> Points { get; }
    private Flac_SeekTable(List<Flac_SeekPoint> points) => Points = points;

    /// <summary>The last point whose sample number is ≤ <paramref name="sample"/>, or null when none.</summary>
    public Flac_SeekPoint? FindBefore(long sample)
    {
        Flac_SeekPoint? best = null;
        foreach (var p in Points)
        {
            if (p.SampleNumber > sample) break;
            best = p;
        }
        return best;
    }

    internal static Flac_SeekTable Parse(ReadOnlySpan<byte> b)
    {
        var points = new List<Flac_SeekPoint>(b.Length / 18);
        for (int i = 0; i + 18 <= b.Length; i += 18)
        {
            long sample = (long)BinaryPrimitives.ReadUInt64BigEndian(b.Slice(i));
            if (sample == -1) continue;   // placeholder
            long offset = (long)BinaryPrimitives.ReadUInt64BigEndian(b.Slice(i + 8));
            int frameSamples = BinaryPrimitives.ReadUInt16BigEndian(b.Slice(i + 16));
            points.Add(new Flac_SeekPoint(sample, offset, frameSamples));
        }
        points.Sort((x, y) => x.SampleNumber.CompareTo(y.SampleNumber));
        return new Flac_SeekTable(points);
    }
}

/// <summary>
/// VORBIS_COMMENT tags: a case-insensitive multimap (<c>TITLE</c>, <c>ARTIST</c>, <c>ALBUM</c>, …; keys may repeat).
/// </summary>
public sealed class Flac_Tags
{
    private readonly Dictionary<string, List<string>> _tags = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<KeyValuePair<string, string>> _ordered = new();

    public string Vendor { get; private set; } = "";
    /// <summary>All entries in file order.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> All => _ordered;
    public int Count => _ordered.Count;

    /// <summary>First value for <paramref name="key"/> (case-insensitive), or null.</summary>
    public string? this[string key] => _tags.TryGetValue(key, out var v) ? v[0] : null;
    public IReadOnlyList<string> GetAll(string key) => _tags.TryGetValue(key, out var v) ? v : Array.Empty<string>();
    public bool Contains(string key) => _tags.ContainsKey(key);

    public string? Title => this["TITLE"];
    public string? Artist => this["ARTIST"];
    public string? Album => this["ALBUM"];
    public string? TrackNumber => this["TRACKNUMBER"];
    public string? Date => this["DATE"];
    public string? Genre => this["GENRE"];

    private void Add(string key, string value)
    {
        if (!_tags.TryGetValue(key, out var list)) _tags[key] = list = new List<string>();
        list.Add(value);
        _ordered.Add(new(key, value));
    }

    /// <summary>Parses a VORBIS_COMMENT body (lengths are LITTLE-endian, unlike the rest of FLAC).</summary>
    internal static Flac_Tags Parse(ReadOnlySpan<byte> b)
    {
        var tags = new Flac_Tags();
        if (b.Length < 8) return tags;
        int pos = 0;
        uint vendorLen = BinaryPrimitives.ReadUInt32LittleEndian(b); pos += 4;
        if (vendorLen > b.Length - pos) return tags;
        tags.Vendor = Encoding.UTF8.GetString(b.Slice(pos, (int)vendorLen)); pos += (int)vendorLen;
        if (pos + 4 > b.Length) return tags;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(pos)); pos += 4;
        for (uint i = 0; i < count && pos + 4 <= b.Length; i++)
        {
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(pos)); pos += 4;
            if (len > b.Length - pos) break;
            var entry = Encoding.UTF8.GetString(b.Slice(pos, (int)len)); pos += (int)len;
            int eq = entry.IndexOf('=');
            if (eq > 0) tags.Add(entry[..eq], entry[(eq + 1)..]);
        }
        return tags;
    }
}

/// <summary>Options for <see cref="Flac_Decoder"/>.</summary>
public sealed record Flac_DecoderOptions
{
    /// <summary>Compute MD5 over the decoded PCM and throw <see cref="AudioDecoder_FormatException"/> at end of stream if it differs from STREAMINFO. Off by default (costs a hash per frame).</summary>
    public bool VerifyMd5 { get; init; }

    public static readonly Flac_DecoderOptions Default = new();
}