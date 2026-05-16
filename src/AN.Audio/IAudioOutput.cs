namespace AN.Audio;

/// <summary>
/// Callback invoked on the audio thread when the backend needs more samples.
/// Write interleaved PCM data into the buffer. Return the number of frames written.
/// If fewer frames are written than requested, remaining samples are filled with silence.
/// </summary>
public delegate int AudioCallback(Span<byte> buffer, int frameCount, AudioFormat format);

/// <summary>
/// Platform-independent audio output interface.
/// Implementations are event-driven: the OS signals when it needs more data.
/// </summary>
public interface IAudioOutput : IDisposable
{
    /// <summary>The PCM format this output was initialized with.</summary>
    AudioFormat Format { get; }

    /// <summary>
    /// Start playback. The AudioCallback will be invoked on a dedicated audio thread
    /// whenever the OS needs more samples. This is NOT the calling thread.
    /// </summary>
    void Start(AudioCallback callback);

    /// <summary>
    /// Stop playback. Blocks until the audio thread has drained or stopped.
    /// After Stop(), Start() can be called again.
    /// </summary>
    void Stop();

    /// <summary>
    /// Estimated latency from buffer submission to DAC output, in milliseconds.
    /// </summary>
    double LatencyMs { get; }
}