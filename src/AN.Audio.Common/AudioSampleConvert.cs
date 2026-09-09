using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace AN.Audio;

/// <summary>
/// Span-based conversion between any two <see cref="SampleFormat"/>s with the same rate and channel count.
/// This is the ONE place the PCM scaling rules live (spec 50 D4/D15):
/// <list type="bullet">
/// <item>integer → float: <c>value / 2^(bits-1)</c> (Int16 → /32768, Int24 → /8388608, Int32 → /2147483648); UInt8 is re-biased by 128 first</item>
/// <item>float → integer: <c>round(value * 2^(bits-1))</c> clamped to the integer range (so +1.0 saturates to max, never wraps)</item>
/// <item>integer → integer: shift (widen by zero-filling low bits; narrow by arithmetic shift with rounding)</item>
/// <item>float → float: cast; values are NOT clipped</item>
/// </list>
/// All integer layouts are little-endian. No allocation.
/// </summary>
public static class AudioSampleConvert
{
    /// <summary>Scale for integer formats: 2^(bits-1).</summary>
    public static readonly float Int16Scale = 32768f;
    public static readonly float Int24Scale = 8388608f;
    public static readonly double Int32Scale = 2147483648.0;

    /// <summary>
    /// Converts <paramref name="src"/> (whole frames in <paramref name="from"/>) into <paramref name="dst"/> in
    /// <paramref name="to"/>. Returns the number of frames converted (bounded by the smaller buffer).
    /// Throws when rate or channel count differ.
    /// </summary>
    public static int Convert(ReadOnlySpan<byte> src, AudioFormat from, Span<byte> dst, AudioFormat to)
    {
        if (from.SampleRate != to.SampleRate || from.Channels != to.Channels)
            throw new ArgumentException($"AudioSampleConvert only converts sample format; rate/channels differ ({from} → {to})");

        int frames = Math.Min(src.Length / from.BytesPerFrame, dst.Length / to.BytesPerFrame);
        int samples = frames * from.Channels;
        if (samples == 0) return frames;

        src = src.Slice(0, samples * from.BytesPerSample);
        dst = dst.Slice(0, samples * to.BytesPerSample);

        if (from.Format == to.Format)
        {
            src.CopyTo(dst);
            return frames;
        }

        switch (to.Format)
        {
            case SampleFormat.Float32: ToFloat32(src, from.Format, MemoryMarshal.Cast<byte, float>(dst)); break;
            case SampleFormat.Float64: ToFloat64(src, from.Format, MemoryMarshal.Cast<byte, double>(dst)); break;
            case SampleFormat.Int16:   ToInt16(src, from.Format, MemoryMarshal.Cast<byte, short>(dst)); break;
            case SampleFormat.Int32:   ToInt32(src, from.Format, MemoryMarshal.Cast<byte, int>(dst)); break;
            case SampleFormat.Int24:   ToInt24(src, from.Format, dst); break;
            case SampleFormat.UInt8:   ToUInt8(src, from.Format, dst); break;
            default: throw new ArgumentOutOfRangeException(nameof(to));
        }
        return frames;
    }

    /// <summary>Converts interleaved samples of any format to float32 (D4 rule). Returns samples written.</summary>
    public static int ToFloat32(ReadOnlySpan<byte> src, SampleFormat from, Span<float> dst)
    {
        int n = SampleCount(src, from, dst.Length);
        switch (from)
        {
            case SampleFormat.Float32: MemoryMarshal.Cast<byte, float>(src).Slice(0, n).CopyTo(dst); break;
            case SampleFormat.Float64: { var s = MemoryMarshal.Cast<byte, double>(src); for (int i = 0; i < n; i++) dst[i] = (float)s[i]; break; }
            case SampleFormat.Int16:   { var s = MemoryMarshal.Cast<byte, short>(src);  for (int i = 0; i < n; i++) dst[i] = s[i] * (1f / Int16Scale); break; }
            case SampleFormat.Int32:   { var s = MemoryMarshal.Cast<byte, int>(src);    for (int i = 0; i < n; i++) dst[i] = (float)(s[i] * (1.0 / Int32Scale)); break; }
            case SampleFormat.Int24:   for (int i = 0; i < n; i++) dst[i] = ReadInt24(src, i) * (1f / Int24Scale); break;
            case SampleFormat.UInt8:   for (int i = 0; i < n; i++) dst[i] = (src[i] - 128) * (1f / 128f); break;
            default: throw new ArgumentOutOfRangeException(nameof(from));
        }
        return n;
    }

    /// <summary>Converts interleaved samples of any format to float64. Returns samples written.</summary>
    public static int ToFloat64(ReadOnlySpan<byte> src, SampleFormat from, Span<double> dst)
    {
        int n = SampleCount(src, from, dst.Length);
        switch (from)
        {
            case SampleFormat.Float64: MemoryMarshal.Cast<byte, double>(src).Slice(0, n).CopyTo(dst); break;
            case SampleFormat.Float32: { var s = MemoryMarshal.Cast<byte, float>(src); for (int i = 0; i < n; i++) dst[i] = s[i]; break; }
            case SampleFormat.Int16:   { var s = MemoryMarshal.Cast<byte, short>(src); for (int i = 0; i < n; i++) dst[i] = s[i] / (double)Int16Scale; break; }
            case SampleFormat.Int32:   { var s = MemoryMarshal.Cast<byte, int>(src);   for (int i = 0; i < n; i++) dst[i] = s[i] / Int32Scale; break; }
            case SampleFormat.Int24:   for (int i = 0; i < n; i++) dst[i] = ReadInt24(src, i) / (double)Int24Scale; break;
            case SampleFormat.UInt8:   for (int i = 0; i < n; i++) dst[i] = (src[i] - 128) / 128.0; break;
            default: throw new ArgumentOutOfRangeException(nameof(from));
        }
        return n;
    }

    /// <summary>Converts interleaved samples of any format to Int16 (float clamped). Returns samples written.</summary>
    public static int ToInt16(ReadOnlySpan<byte> src, SampleFormat from, Span<short> dst)
    {
        int n = SampleCount(src, from, dst.Length);
        switch (from)
        {
            case SampleFormat.Int16:   MemoryMarshal.Cast<byte, short>(src).Slice(0, n).CopyTo(dst); break;
            case SampleFormat.Float32: { var s = MemoryMarshal.Cast<byte, float>(src);  for (int i = 0; i < n; i++) dst[i] = FloatToInt16(s[i]); break; }
            case SampleFormat.Float64: { var s = MemoryMarshal.Cast<byte, double>(src); for (int i = 0; i < n; i++) dst[i] = FloatToInt16((float)s[i]); break; }
            case SampleFormat.Int32:   { var s = MemoryMarshal.Cast<byte, int>(src);    for (int i = 0; i < n; i++) dst[i] = (short)(s[i] >> 16); break; }
            case SampleFormat.Int24:   for (int i = 0; i < n; i++) dst[i] = (short)(ReadInt24(src, i) >> 8); break;
            case SampleFormat.UInt8:   for (int i = 0; i < n; i++) dst[i] = (short)((src[i] - 128) << 8); break;
            default: throw new ArgumentOutOfRangeException(nameof(from));
        }
        return n;
    }

    /// <summary>Converts interleaved samples of any format to Int32 (float clamped). Returns samples written.</summary>
    public static int ToInt32(ReadOnlySpan<byte> src, SampleFormat from, Span<int> dst)
    {
        int n = SampleCount(src, from, dst.Length);
        switch (from)
        {
            case SampleFormat.Int32:   MemoryMarshal.Cast<byte, int>(src).Slice(0, n).CopyTo(dst); break;
            case SampleFormat.Float32: { var s = MemoryMarshal.Cast<byte, float>(src);  for (int i = 0; i < n; i++) dst[i] = FloatToInt32(s[i]); break; }
            case SampleFormat.Float64: { var s = MemoryMarshal.Cast<byte, double>(src); for (int i = 0; i < n; i++) dst[i] = FloatToInt32(s[i]); break; }
            case SampleFormat.Int16:   { var s = MemoryMarshal.Cast<byte, short>(src);  for (int i = 0; i < n; i++) dst[i] = s[i] << 16; break; }
            case SampleFormat.Int24:   for (int i = 0; i < n; i++) dst[i] = ReadInt24(src, i) << 8; break;
            case SampleFormat.UInt8:   for (int i = 0; i < n; i++) dst[i] = (src[i] - 128) << 24; break;
            default: throw new ArgumentOutOfRangeException(nameof(from));
        }
        return n;
    }

    private static void ToInt24(ReadOnlySpan<byte> src, SampleFormat from, Span<byte> dst)
    {
        int n = dst.Length / 3;
        switch (from)
        {
            case SampleFormat.Float32: { var s = MemoryMarshal.Cast<byte, float>(src);  for (int i = 0; i < n; i++) WriteInt24(dst, i, FloatToInt24(s[i])); break; }
            case SampleFormat.Float64: { var s = MemoryMarshal.Cast<byte, double>(src); for (int i = 0; i < n; i++) WriteInt24(dst, i, FloatToInt24((float)s[i])); break; }
            case SampleFormat.Int16:   { var s = MemoryMarshal.Cast<byte, short>(src);  for (int i = 0; i < n; i++) WriteInt24(dst, i, s[i] << 8); break; }
            case SampleFormat.Int32:   { var s = MemoryMarshal.Cast<byte, int>(src);    for (int i = 0; i < n; i++) WriteInt24(dst, i, s[i] >> 8); break; }
            case SampleFormat.UInt8:   for (int i = 0; i < n; i++) WriteInt24(dst, i, (src[i] - 128) << 16); break;
            default: throw new ArgumentOutOfRangeException(nameof(from));
        }
    }

    private static void ToUInt8(ReadOnlySpan<byte> src, SampleFormat from, Span<byte> dst)
    {
        int n = dst.Length;
        switch (from)
        {
            case SampleFormat.Float32: { var s = MemoryMarshal.Cast<byte, float>(src);  for (int i = 0; i < n; i++) dst[i] = FloatToUInt8(s[i]); break; }
            case SampleFormat.Float64: { var s = MemoryMarshal.Cast<byte, double>(src); for (int i = 0; i < n; i++) dst[i] = FloatToUInt8((float)s[i]); break; }
            case SampleFormat.Int16:   { var s = MemoryMarshal.Cast<byte, short>(src);  for (int i = 0; i < n; i++) dst[i] = (byte)((s[i] >> 8) + 128); break; }
            case SampleFormat.Int32:   { var s = MemoryMarshal.Cast<byte, int>(src);    for (int i = 0; i < n; i++) dst[i] = (byte)((s[i] >> 24) + 128); break; }
            case SampleFormat.Int24:   for (int i = 0; i < n; i++) dst[i] = (byte)((ReadInt24(src, i) >> 16) + 128); break;
            default: throw new ArgumentOutOfRangeException(nameof(from));
        }
    }

    // ── scalar helpers (public so per-sample callers such as AN.Audio's device converter share the rule) ──

    /// <summary>float → Int16 with the D4 scale and saturation.</summary>
    public static short FloatToInt16(float v)
    {
        float s = v * Int16Scale;
        if (s >= 32767f) return short.MaxValue;
        if (s <= -32768f) return short.MinValue;
        return (short)MathF.Round(s);
    }

    /// <summary>float → 24-bit signed integer (in an int) with the D4 scale and saturation.</summary>
    public static int FloatToInt24(float v)
    {
        float s = v * Int24Scale;
        if (s >= 8388607f) return 8388607;
        if (s <= -8388608f) return -8388608;
        return (int)MathF.Round(s);
    }

    /// <summary>float → Int32 with the D4 scale and saturation.</summary>
    public static int FloatToInt32(double v)
    {
        double s = v * Int32Scale;
        if (s >= int.MaxValue) return int.MaxValue;
        if (s <= int.MinValue) return int.MinValue;
        return (int)Math.Round(s);
    }

    /// <summary>float → UInt8 (bias 128) with saturation.</summary>
    public static byte FloatToUInt8(float v)
    {
        float s = v * 128f;
        if (s >= 127f) return 255;
        if (s <= -128f) return 0;
        return (byte)(MathF.Round(s) + 128f);
    }

    /// <summary>Int16 → float with the D4 scale.</summary>
    public static float Int16ToFloat(short v) => v * (1f / Int16Scale);

    /// <summary>Reads the little-endian 24-bit signed sample at index <paramref name="sampleIndex"/>, sign-extended.</summary>
    public static int ReadInt24(ReadOnlySpan<byte> src, int sampleIndex)
    {
        int o = sampleIndex * 3;
        return (src[o] | (src[o + 1] << 8) | (src[o + 2] << 16)) << 8 >> 8;
    }

    /// <summary>Writes the low 24 bits of <paramref name="value"/> little-endian at index <paramref name="sampleIndex"/>.</summary>
    public static void WriteInt24(Span<byte> dst, int sampleIndex, int value)
    {
        int o = sampleIndex * 3;
        dst[o] = (byte)value;
        dst[o + 1] = (byte)(value >> 8);
        dst[o + 2] = (byte)(value >> 16);
    }

    private static int SampleCount(ReadOnlySpan<byte> src, SampleFormat from, int dstSamples)
    {
        int bytesPerSample = new AudioFormat(1, 1, from).BytesPerSample;
        return Math.Min(src.Length / bytesPerSample, dstSamples);
    }
}