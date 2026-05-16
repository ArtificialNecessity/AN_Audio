namespace AN.Audio;

/// <summary>
/// Describes the PCM audio format for a stream.
/// </summary>
public record struct AudioFormat(
    int SampleRate,
    int Channels,
    SampleFormat Format
)
{
    /// <summary>Bytes per single sample (one channel).</summary>
    public int BytesPerSample => Format switch
    {
        SampleFormat.Int16 => 2,
        SampleFormat.Float32 => 4,
        _ => throw new ArgumentOutOfRangeException()
    };

    /// <summary>Bytes per frame (all channels for one sample instant).</summary>
    public int BytesPerFrame => BytesPerSample * Channels;
}

/// <summary>
/// PCM sample format.
/// </summary>
public enum SampleFormat
{
    Int16,
    Float32
}