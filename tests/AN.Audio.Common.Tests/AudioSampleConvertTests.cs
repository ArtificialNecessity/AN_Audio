using System.Runtime.InteropServices;
using Xunit;

namespace AN.Audio.Common.Tests;

public class AudioSampleConvertTests
{
    private static AudioFormat F(SampleFormat f, int ch = 1) => new(48000, ch, f);

    private static byte[] Convert(byte[] src, SampleFormat from, SampleFormat to, int ch = 1)
    {
        var fFrom = F(from, ch);
        var fTo = F(to, ch);
        int frames = src.Length / fFrom.BytesPerFrame;
        var dst = new byte[frames * fTo.BytesPerFrame];
        int n = AudioSampleConvert.Convert(src, fFrom, dst, fTo);
        Assert.Equal(frames, n);
        return dst;
    }

    [Fact]
    public void Int16_To_Float32_To_Int16_IsExact()
    {
        var shorts = new short[65536];
        for (int i = 0; i < shorts.Length; i++) shorts[i] = (short)(i - 32768);
        var src = MemoryMarshal.AsBytes<short>(shorts).ToArray();

        var f = Convert(src, SampleFormat.Int16, SampleFormat.Float32);
        var floats = MemoryMarshal.Cast<byte, float>(f);
        Assert.Equal(-1f, floats[0]);
        Assert.Equal(0f, floats[32768]);
        Assert.Equal(32767f / 32768f, floats[65535]);

        var back = Convert(f, SampleFormat.Float32, SampleFormat.Int16);
        Assert.Equal(src, back);
    }

    [Fact]
    public void Float_To_Int16_Saturates_NeverWraps()
    {
        var floats = new float[] { 1.0f, 1.5f, -1.0f, -2f, 0.5f };
        var dst = Convert(MemoryMarshal.AsBytes<float>(floats).ToArray(), SampleFormat.Float32, SampleFormat.Int16);
        var s = MemoryMarshal.Cast<byte, short>(dst);
        Assert.Equal(short.MaxValue, s[0]);
        Assert.Equal(short.MaxValue, s[1]);
        Assert.Equal(short.MinValue, s[2]);
        Assert.Equal(short.MinValue, s[3]);
        Assert.Equal(16384, s[4]);
    }

    [Fact]
    public void Int24_SignExtension()
    {
        // samples: 0x7FFFFF (max), 0x800000 (min), 0xFFFFFF (-1), 0x000001 (+1)
        var src = new byte[] { 0xFF, 0xFF, 0x7F,  0x00, 0x00, 0x80,  0xFF, 0xFF, 0xFF,  0x01, 0x00, 0x00 };
        Assert.Equal(8388607, AudioSampleConvert.ReadInt24(src, 0));
        Assert.Equal(-8388608, AudioSampleConvert.ReadInt24(src, 1));
        Assert.Equal(-1, AudioSampleConvert.ReadInt24(src, 2));
        Assert.Equal(1, AudioSampleConvert.ReadInt24(src, 3));

        var f = MemoryMarshal.Cast<byte, float>(Convert(src, SampleFormat.Int24, SampleFormat.Float32));
        Assert.Equal(8388607f / 8388608f, f[0]);
        Assert.Equal(-1f, f[1]);
        Assert.Equal(-1f / 8388608f, f[2]);

        var i32 = MemoryMarshal.Cast<byte, int>(Convert(src, SampleFormat.Int24, SampleFormat.Int32));
        Assert.Equal(8388607 << 8, i32[0]);
        Assert.Equal(int.MinValue, i32[1]);
        Assert.Equal(-256, i32[2]);

        // round trip via Int32 and via Float32 is exact
        Assert.Equal(src, Convert(Convert(src, SampleFormat.Int24, SampleFormat.Int32), SampleFormat.Int32, SampleFormat.Int24));
        Assert.Equal(src, Convert(Convert(src, SampleFormat.Int24, SampleFormat.Float32), SampleFormat.Float32, SampleFormat.Int24));
    }

    [Fact]
    public void UInt8_Bias()
    {
        var src = new byte[] { 128, 0, 255, 192 };
        var f = MemoryMarshal.Cast<byte, float>(Convert(src, SampleFormat.UInt8, SampleFormat.Float32));
        Assert.Equal(0f, f[0]);
        Assert.Equal(-1f, f[1]);
        Assert.Equal(127f / 128f, f[2]);
        Assert.Equal(0.5f, f[3]);

        var s = MemoryMarshal.Cast<byte, short>(Convert(src, SampleFormat.UInt8, SampleFormat.Int16));
        Assert.Equal(0, s[0]);
        Assert.Equal(-32768, s[1]);
        Assert.Equal(127 << 8, s[2]);

        Assert.Equal(src, Convert(Convert(src, SampleFormat.UInt8, SampleFormat.Float32), SampleFormat.Float32, SampleFormat.UInt8));
        Assert.Equal(src, Convert(Convert(src, SampleFormat.UInt8, SampleFormat.Int16), SampleFormat.Int16, SampleFormat.UInt8));
    }

    [Fact]
    public void Float64_PassThrough_And_Float32()
    {
        var d = new double[] { 0.25, -0.5, 1.5 };
        var src = MemoryMarshal.AsBytes<double>(d).ToArray();
        Assert.Equal(src, Convert(src, SampleFormat.Float64, SampleFormat.Float64));

        var f = MemoryMarshal.Cast<byte, float>(Convert(src, SampleFormat.Float64, SampleFormat.Float32));
        Assert.Equal(0.25f, f[0]);
        Assert.Equal(1.5f, f[2]);   // not clipped (D4)

        var back = MemoryMarshal.Cast<byte, double>(Convert(MemoryMarshal.AsBytes<float>(f).ToArray(), SampleFormat.Float32, SampleFormat.Float64));
        Assert.Equal(d, back.ToArray());
    }

    [Fact]
    public void Int32_Float_RoundTrip()
    {
        var ints = new[] { int.MaxValue, int.MinValue, 0, 1 << 8, -(1 << 8) };
        var src = MemoryMarshal.AsBytes<int>(ints).ToArray();
        var f = MemoryMarshal.Cast<byte, float>(Convert(src, SampleFormat.Int32, SampleFormat.Float32));
        Assert.Equal(-1f, f[1]);
        Assert.Equal(0f, f[2]);
        // float32 has 24 bits of mantissa: values with ≤ 24 significant bits round-trip exactly
        var back = MemoryMarshal.Cast<byte, int>(Convert(MemoryMarshal.AsBytes<float>(f).ToArray(), SampleFormat.Float32, SampleFormat.Int32));
        Assert.Equal(int.MinValue, back[1]);
        Assert.Equal(0, back[2]);
        Assert.Equal(1 << 8, back[3]);
        Assert.Equal(-(1 << 8), back[4]);
        Assert.Equal(int.MaxValue, back[0]);  // 0.99999994 * 2^31 rounds to 2147483520 → wait: saturation branch keeps max
    }

    [Fact]
    public void Int16_Int32_Widen_Narrow_Exact()
    {
        var shorts = new short[] { short.MaxValue, short.MinValue, 0, 1234, -1234 };
        var src = MemoryMarshal.AsBytes<short>(shorts).ToArray();
        var wide = Convert(src, SampleFormat.Int16, SampleFormat.Int32);
        Assert.Equal(short.MaxValue << 16, MemoryMarshal.Cast<byte, int>(wide)[0]);
        Assert.Equal(src, Convert(wide, SampleFormat.Int32, SampleFormat.Int16));
        var i24 = Convert(src, SampleFormat.Int16, SampleFormat.Int24);
        Assert.Equal(src, Convert(i24, SampleFormat.Int24, SampleFormat.Int16));
    }

    [Fact]
    public void SameFormat_IsCopy_And_MultiChannel()
    {
        var src = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        Assert.Equal(src, Convert(src, SampleFormat.Int16, SampleFormat.Int16, ch: 2));
        // 2 channels: 2 frames of Int16 → 2 frames of Float32 = 16 bytes
        var f = Convert(src, SampleFormat.Int16, SampleFormat.Float32, ch: 2);
        Assert.Equal(16, f.Length);
    }

    [Fact]
    public void Convert_RejectsRateOrChannelMismatch()
    {
        var src = new byte[8]; var dst = new byte[16];
        Assert.Throws<ArgumentException>(() => AudioSampleConvert.Convert(src, new AudioFormat(44100, 1, SampleFormat.Int16), dst, new AudioFormat(48000, 1, SampleFormat.Float32)));
        Assert.Throws<ArgumentException>(() => AudioSampleConvert.Convert(src, new AudioFormat(48000, 1, SampleFormat.Int16), dst, new AudioFormat(48000, 2, SampleFormat.Float32)));
    }

    [Fact]
    public void Convert_ReturnsMinFrames()
    {
        var src = new byte[8];   // 4 Int16 frames
        var dst = new byte[8];   // 2 Float32 frames
        Assert.Equal(2, AudioSampleConvert.Convert(src, F(SampleFormat.Int16), dst, F(SampleFormat.Float32)));
    }
}