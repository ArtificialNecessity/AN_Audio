using System.Runtime.InteropServices;

namespace AN.Audio;

/// <summary>
/// A typed window over interleaved PCM bytes: the ONE way a buffer and its <see cref="AudioFormat"/> travel
/// together (spec 50 D15). Construction validates that the byte length is a whole number of frames.
/// </summary>
public readonly ref struct AudioBufferView
{
    public Span<byte> Bytes { get; }
    public AudioFormat Format { get; }

    public AudioBufferView(Span<byte> bytes, AudioFormat format)
    {
        int bytesPerFrame = format.BytesPerFrame;   // throws for an unknown SampleFormat
        if (format.Channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(format), "Channels must be positive");
        if (bytes.Length % bytesPerFrame != 0)
            throw new ArgumentException($"Buffer length {bytes.Length} is not a multiple of BytesPerFrame {bytesPerFrame}", nameof(bytes));
        Bytes = bytes;
        Format = format;
    }

    /// <summary>Whole frames in the view.</summary>
    public int FrameCount => Bytes.Length / Format.BytesPerFrame;

    /// <summary>Interleaved samples as <c>float</c>; requires <see cref="SampleFormat.Float32"/>.</summary>
    public Span<float> AsFloat32() => MemoryMarshal.Cast<byte, float>(Require(SampleFormat.Float32));

    /// <summary>Interleaved samples as <c>short</c>; requires <see cref="SampleFormat.Int16"/>.</summary>
    public Span<short> AsInt16() => MemoryMarshal.Cast<byte, short>(Require(SampleFormat.Int16));

    /// <summary>Interleaved samples as <c>int</c>; requires <see cref="SampleFormat.Int32"/>.</summary>
    public Span<int> AsInt32() => MemoryMarshal.Cast<byte, int>(Require(SampleFormat.Int32));

    /// <summary>Interleaved samples as <c>double</c>; requires <see cref="SampleFormat.Float64"/>.</summary>
    public Span<double> AsFloat64() => MemoryMarshal.Cast<byte, double>(Require(SampleFormat.Float64));

    /// <summary>A view over the first <paramref name="frameCount"/> frames.</summary>
    public AudioBufferView SliceFrames(int frameCount)
        => new(Bytes.Slice(0, frameCount * Format.BytesPerFrame), Format);

    /// <summary>A view starting at frame <paramref name="frameStart"/>.</summary>
    public AudioBufferView SliceFrames(int frameStart, int frameCount)
        => new(Bytes.Slice(frameStart * Format.BytesPerFrame, frameCount * Format.BytesPerFrame), Format);

    private Span<byte> Require(SampleFormat expected)
    {
        if (Format.Format != expected)
            throw new InvalidOperationException($"AudioBufferView is {Format.Format}, not {expected}");
        return Bytes;
    }
}