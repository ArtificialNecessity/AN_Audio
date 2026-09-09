namespace AN.Audio.Formats;

/// <summary>
/// Everything a consumer needs to size buffers and label the source. Known after the header, before any audio.
/// </summary>
/// <param name="SourceBitDepth">Significant bits per sample in the source: 8/16/20/24/32 for PCM and FLAC; 0 when meaningless (MP3).</param>
/// <param name="TotalFrames">Total frames when the header declares them; <c>null</c> = unknown (D10).</param>
/// <param name="CanSeek">True only when the underlying stream seeks AND the format supports it (D11).</param>
public readonly record struct AudioDecoder_StreamInfo(
    AudioDecoder_Container Container,
    AudioDecoder_SourceEncoding Encoding,
    int SampleRate,
    int Channels,
    int SourceBitDepth,
    long? TotalFrames,
    bool CanSeek)
{
    /// <summary>Duration when <see cref="TotalFrames"/> is known.</summary>
    public TimeSpan? Duration => TotalFrames is { } f && SampleRate > 0 ? TimeSpan.FromSeconds((double)f / SampleRate) : null;
}