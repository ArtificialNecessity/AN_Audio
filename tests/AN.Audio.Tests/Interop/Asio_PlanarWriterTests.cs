using AN.Audio.Platforms.Windows.Asio;
using Xunit;

namespace AN.Audio.Tests.Interop;

/// <summary>Spec 70 §4 — interleaved Float32 → one planar channel in the channel's own sample type; tail zeroing; no allocation.</summary>
public unsafe class Asio_PlanarWriterTests
{
    // 3 frames × 2 channels: L = 0.5, -1, 0.25 ; R = -0.5, 1, 0
    private static readonly float[] Interleaved = [0.5f, -0.5f, -1f, 1f, 0.25f, 0f];

    [Fact]
    public void Int32LSB_channel_1_with_tail_zeroed()
    {
        int* buf = stackalloc int[5]; for (int i = 0; i < 5; i++) buf[i] = 0x7777;
        Asio_PlanarWriter.WriteChannel(Interleaved, frames: 3, channels: 2, channel: 1, Asio_SampleType.ASIOSTInt32LSB, (byte*)buf, bufferFrames: 5);
        Assert.Equal(AudioSampleConvert.FloatToInt32(-0.5f), buf[0]);
        Assert.Equal(int.MaxValue, buf[1]);           // +1.0 saturates symmetric
        Assert.Equal(0, buf[2]);
        Assert.Equal(0, buf[3]); Assert.Equal(0, buf[4]); // tail
    }

    [Fact]
    public void Int16LSB_channel_0()
    {
        short* buf = stackalloc short[3];
        Asio_PlanarWriter.WriteChannel(Interleaved, 3, 2, 0, Asio_SampleType.ASIOSTInt16LSB, (byte*)buf, 3);
        Assert.Equal(16384, buf[0]); Assert.Equal(-32768, buf[1]); Assert.Equal(8192, buf[2]);
    }

    [Fact]
    public void Int24LSB_is_packed_little_endian()
    {
        byte* buf = stackalloc byte[9];
        Asio_PlanarWriter.WriteChannel(Interleaved, 3, 2, 0, Asio_SampleType.ASIOSTInt24LSB, buf, 3);
        int v0 = buf[0] | buf[1] << 8 | (sbyte)buf[2] << 16;
        Assert.Equal(AudioSampleConvert.FloatToInt24(0.5f), v0);
        int v1 = buf[3] | buf[4] << 8 | (sbyte)buf[5] << 16;
        Assert.Equal(-8388608, v1);
    }

    [Fact]
    public void Float32_and_Float64_copy_exactly()
    {
        float* f = stackalloc float[3]; double* d = stackalloc double[3];
        Asio_PlanarWriter.WriteChannel(Interleaved, 3, 2, 1, Asio_SampleType.ASIOSTFloat32LSB, (byte*)f, 3);
        Asio_PlanarWriter.WriteChannel(Interleaved, 3, 2, 1, Asio_SampleType.ASIOSTFloat64LSB, (byte*)d, 3);
        Assert.Equal(-0.5f, f[0]); Assert.Equal(1f, f[1]); Assert.Equal(0f, f[2]);
        Assert.Equal(-0.5, d[0]); Assert.Equal(1.0, d[1]);
    }

    [Theory]
    [InlineData((int)Asio_SampleType.ASIOSTInt32MSB)]
    [InlineData((int)Asio_SampleType.ASIOSTInt32LSB24)]
    [InlineData((int)Asio_SampleType.ASIOSTDSDInt8LSB1)]
    public void Unsupported_types_are_refused_not_guessed(int typeValue)
    {
        var type = (Asio_SampleType)typeValue; // the enum is internal; xunit theory parameters must be public types
        Assert.False(Asio_PlanarWriter.IsSupported(type));
        Assert.Throws<NotSupportedException>(() => Asio_PlanarWriter.BytesPerSample(type));
    }

    [Fact]
    public void Hot_path_does_not_allocate()
    {
        var src = new float[128 * 2]; for (int i = 0; i < src.Length; i++) src[i] = (float)Math.Sin(i * 0.01);
        int* buf = stackalloc int[128];
        Asio_PlanarWriter.WriteChannel(src, 128, 2, 0, Asio_SampleType.ASIOSTInt32LSB, (byte*)buf, 128); // warm up
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) Asio_PlanarWriter.WriteChannel(src, 100, 2, i & 1, Asio_SampleType.ASIOSTInt32LSB, (byte*)buf, 128);
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, delta);
    }
}