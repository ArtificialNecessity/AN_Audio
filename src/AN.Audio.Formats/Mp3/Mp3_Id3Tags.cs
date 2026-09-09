namespace AN.Audio.Formats.Mp3;

/// <summary>Four-character ID3v2 frame id (v2.2 three-character ids are mapped to their v2.3 names, e.g. <c>TT2</c> → <c>TIT2</c>).</summary>
public readonly record struct Mp3_Id3FrameId(string Value)
{
    public static readonly Mp3_Id3FrameId Title = new("TIT2");
    public static readonly Mp3_Id3FrameId Artist = new("TPE1");
    public static readonly Mp3_Id3FrameId AlbumArtist = new("TPE2");
    public static readonly Mp3_Id3FrameId Album = new("TALB");
    public static readonly Mp3_Id3FrameId Track = new("TRCK");
    public static readonly Mp3_Id3FrameId Year = new("TYER");
    public static readonly Mp3_Id3FrameId RecordingTime = new("TDRC");   // v2.4 replacement for TYER
    public static readonly Mp3_Id3FrameId Genre = new("TCON");
    public static readonly Mp3_Id3FrameId Comment = new("COMM");
    public static readonly Mp3_Id3FrameId UserText = new("TXXX");
    public static readonly Mp3_Id3FrameId Picture = new("APIC");
    public override string ToString() => Value;
}

/// <summary>Which tag(s) the stream carried.</summary>
public enum Mp3_Id3Source { None, Id3v22, Id3v23, Id3v24, Id3v1 }

/// <summary>
/// Metadata read from an ID3v2 tag (or, on a seekable stream without one, ID3v1). Text frames are kept in a multimap keyed
/// by frame id (<c>TXXX</c> frames as <c>TXXX:&lt;description&gt;</c>); the named properties are the common ones.
/// Pictures are NOT retained — see <see cref="Mp3_DecoderOptions.OnPicture"/>; <see cref="PictureCount"/> says how many there were.
/// </summary>
public sealed class Mp3_Id3Tags
{
    private readonly List<KeyValuePair<Mp3_Id3FrameId, string>> _frames = new();

    public Mp3_Id3Source Source { get; internal set; }
    /// <summary>Total bytes of the ID3v2 tag at the head of the stream (header + body + footer); 0 for ID3v1/none.</summary>
    public int TagLength { get; internal set; }
    /// <summary>Number of <c>APIC</c>/<c>PIC</c> frames seen (delivered to the callback when one was registered).</summary>
    public int PictureCount { get; internal set; }

    public IReadOnlyList<KeyValuePair<Mp3_Id3FrameId, string>> Frames => _frames;

    public string? Title => this[Mp3_Id3FrameId.Title];
    public string? Artist => this[Mp3_Id3FrameId.Artist];
    public string? AlbumArtist => this[Mp3_Id3FrameId.AlbumArtist];
    public string? Album => this[Mp3_Id3FrameId.Album];
    public string? Track => this[Mp3_Id3FrameId.Track];
    /// <summary><c>TYER</c> (v2.3) or <c>TDRC</c> (v2.4), whichever is present.</summary>
    public string? Year => this[Mp3_Id3FrameId.Year] ?? this[Mp3_Id3FrameId.RecordingTime];
    public string? Genre => this[Mp3_Id3FrameId.Genre];
    public string? Comment => this[Mp3_Id3FrameId.Comment];

    /// <summary>First value for a frame id (case-sensitive, ids are upper-case by spec), or null.</summary>
    public string? this[Mp3_Id3FrameId id]
    {
        get { foreach (var kv in _frames) if (kv.Key == id) return kv.Value; return null; }
    }
    public string? this[string id] => this[new Mp3_Id3FrameId(id)];

    public IEnumerable<string> All(Mp3_Id3FrameId id)
    {
        foreach (var kv in _frames) if (kv.Key == id) yield return kv.Value;
    }

    internal void Add(Mp3_Id3FrameId id, string value)
    {
        if (value.Length > 0) _frames.Add(new(id, value));
    }
}