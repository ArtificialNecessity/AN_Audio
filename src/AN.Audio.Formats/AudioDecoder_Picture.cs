namespace AN.Audio.Formats;

/// <summary>
/// Picture kind, numbered as ID3v2 <c>APIC</c> and FLAC <c>PICTURE</c> both define it (0–20) — one enum serves every container.
/// </summary>
public enum AudioDecoder_PictureType
{
    Other = 0,
    /// <summary>32×32 PNG file icon.</summary>
    FileIcon = 1,
    OtherFileIcon = 2,
    FrontCover = 3,
    BackCover = 4,
    LeafletPage = 5,
    Media = 6,
    LeadArtist = 7,
    Artist = 8,
    Conductor = 9,
    Band = 10,
    Composer = 11,
    Lyricist = 12,
    RecordingLocation = 13,
    DuringRecording = 14,
    DuringPerformance = 15,
    VideoScreenCapture = 16,
    BrightColouredFish = 17,
    Illustration = 18,
    BandLogo = 19,
    PublisherLogo = 20,
}

/// <summary>Everything known about an embedded picture besides its bytes.</summary>
public readonly record struct AudioDecoder_PictureInfo(
    AudioDecoder_PictureType Type,
    string MimeType,
    string Description,
    int ByteLength);

/// <summary>
/// Receives one embedded picture. <paramref name="imageBytes"/> is a window over a pooled buffer that is valid ONLY for the
/// duration of the call — copy it if you want to keep it. Pictures travel by callback, never by retention (spec 50 §MP3):
/// whether to hold megabytes of cover art is the client's memory decision, and streaming decoders only see the bytes once.
/// </summary>
public delegate void AudioDecoder_PictureCallback(in AudioDecoder_PictureInfo info, ReadOnlySpan<byte> imageBytes);