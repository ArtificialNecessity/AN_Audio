namespace AN.Audio;

/// <summary>
/// Describes an interleaved PCM audio layout: sample rate, channel count and per-sample encoding.
/// Shared vocabulary (spec 50 D15): lives in ArtificialNecessity.Audio.Common so that the device layer
/// (AN.Audio) and the decoders (AN.Audio.Formats) speak ONE type.
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
        SampleFormat.UInt8 => 1,
        SampleFormat.Int16 => 2,
        SampleFormat.Int24 => 3,
        SampleFormat.Int32 => 4,
        SampleFormat.Float32 => 4,
        SampleFormat.Float64 => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(Format), Format, "Unknown SampleFormat")
    };

    /// <summary>Bytes per frame (all channels for one sample instant).</summary>
    public int BytesPerFrame => BytesPerSample * Channels;

    /// <summary>Significant bits per sample for this encoding (8/16/24/32/32/64).</summary>
    public int BitsPerSample => BytesPerSample * 8;
}

/// <summary>
/// PCM sample encoding. Integer formats are little-endian, signed and right-justified except
/// <see cref="UInt8"/> which is unsigned with a bias of 128 (the WAV 8-bit convention).
/// Device backends in AN.Audio accept only <see cref="Int16"/> and <see cref="Float32"/>; the
/// remaining members exist so every WAV/FLAC/AIFF layout is nameable (spec 50 D16).
/// </summary>
public enum SampleFormat
{
    /// <summary>Unsigned 8-bit, 128 = silence.</summary>
    UInt8,
    /// <summary>Signed 16-bit little-endian.</summary>
    Int16,
    /// <summary>Signed 24-bit little-endian, packed in 3 bytes.</summary>
    Int24,
    /// <summary>Signed 32-bit little-endian.</summary>
    Int32,
    /// <summary>IEEE 754 single, nominal range [-1, 1].</summary>
    Float32,
    /// <summary>IEEE 754 double, nominal range [-1, 1].</summary>
    Float64
}