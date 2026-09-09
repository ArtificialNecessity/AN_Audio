namespace AN.Audio.Formats;

/// <summary>The container (file/stream format) a decoder recognised by content (spec 50 D8).</summary>
public enum AudioDecoder_Container
{
    Wav,
    Flac,
    Mp3,
    /// <summary>Phase 4.</summary>
    Aiff,
    /// <summary>Phase 4.</summary>
    Ogg,
}

/// <summary>How the audio inside the container is encoded.</summary>
public enum AudioDecoder_SourceEncoding
{
    PcmInt,
    PcmFloat,
    Flac,
    MpegLayer1,
    MpegLayer2,
    MpegLayer3,
    Vorbis,
    Opus,
    Alaw,
    Mulaw,
    Adpcm,
}

/// <summary>
/// Optional tiebreaker for <see cref="AudioDecoder.Sniff"/> (from a file extension or MIME type). Content always
/// wins; the hint only decides for bare MPEG streams whose sync word is a weak signature (D8).
/// </summary>
public enum AudioDecoder_FormatHint
{
    None,
    Wav,
    Flac,
    Mp3,
    Aiff,
    Ogg,
}