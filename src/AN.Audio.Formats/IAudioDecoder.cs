namespace AN.Audio.Formats;

/// <summary>
/// The generic tier (spec 50 D3): a pull-based, frame-block-at-a-time decoder over a <see cref="Stream"/> that may be
/// forward-only, non-seekable and of unknown length (D2). Every per-format decoder implements this.
/// Decoders are called from worker threads, never from the audio callback; the read path allocates only at open (D13).
/// </summary>
public interface IAudioDecoder : IDisposable
{
    AudioDecoder_StreamInfo Info { get; }

    /// <summary>
    /// D16: the interleaved PCM layout this bitstream yields WITHOUT conversion (WAV PCM16 → Int16, WAV 24-bit → Int24,
    /// FLAC → Int32 with samples sign-extended in the LOW bits, MP3 → Float32). Rate/channels equal <see cref="Info"/>.
    /// </summary>
    AudioFormat NativeFormat { get; }

    /// <summary>Position in frames (frames delivered so far, or the seek target after <see cref="SeekToFrame"/>).</summary>
    long FramesRead { get; }

    /// <summary>D12: the stream ended before the declared length; the frames delivered are all there were.</summary>
    bool EndedEarly { get; }

    /// <summary>
    /// Simple path. Fills <paramref name="interleaved"/> (length must be a multiple of <c>Info.Channels</c>) with as many
    /// whole frames as available, converted to float per D4 (nominal [-1, 1], not clipped); returns frames written;
    /// 0 = end of stream. Blocks on the stream like <see cref="Stream.Read(Span{byte})"/> does.
    /// </summary>
    int ReadFrames(Span<float> interleaved);

    /// <summary>
    /// Zero-copy path (D16). Writes frames in <see cref="NativeFormat"/> directly into <paramref name="destination"/>
    /// (length must be a multiple of <c>NativeFormat.BytesPerFrame</c>); returns frames written; 0 = end of stream.
    /// </summary>
    int ReadFramesNative(Span<byte> destination);

    /// <summary>Convenience over <see cref="ReadFramesNative(Span{byte})"/>; throws if <c>view.Format != NativeFormat</c>.</summary>
    int ReadFramesNative(AudioBufferView view);

    /// <summary>D11. Throws <see cref="NotSupportedException"/> when <c>Info.CanSeek</c> is false.</summary>
    void SeekToFrame(long frame);
}