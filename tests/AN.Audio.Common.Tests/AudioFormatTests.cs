using Xunit;

namespace AN.Audio.Common.Tests;

public class AudioFormatTests
{
    [Theory]
    [InlineData(SampleFormat.UInt8, 1)]
    [InlineData(SampleFormat.Int16, 2)]
    [InlineData(SampleFormat.Int24, 3)]
    [InlineData(SampleFormat.Int32, 4)]
    [InlineData(SampleFormat.Float32, 4)]
    [InlineData(SampleFormat.Float64, 8)]
    public void BytesPerSample_CoversAllSix(SampleFormat f, int expected)
    {
        var fmt = new AudioFormat(48000, 2, f);
        Assert.Equal(expected, fmt.BytesPerSample);
        Assert.Equal(expected * 2, fmt.BytesPerFrame);
        Assert.Equal(expected * 8, fmt.BitsPerSample);
    }

    [Fact]
    public void BytesPerSample_UnknownThrows()
    {
        var fmt = new AudioFormat(48000, 2, (SampleFormat)99);
        Assert.Throws<ArgumentOutOfRangeException>(() => fmt.BytesPerSample);
    }

    [Fact]
    public void ChannelMask_PositionCount()
    {
        Assert.Equal(2, AudioChannelMask.Stereo.PositionCount());
        Assert.Equal(6, AudioChannelMask.FivePointOne.PositionCount());
        Assert.Equal(0, AudioChannelMask.All.PositionCount());
        Assert.Equal((AudioChannelMask)0x3F, AudioChannelMask.FivePointOne);
    }
}

public class AudioBufferViewTests
{
    [Fact]
    public void FrameCount_AndTypedViews()
    {
        var bytes = new byte[16];
        var v = new AudioBufferView(bytes, new AudioFormat(48000, 2, SampleFormat.Int16));
        Assert.Equal(4, v.FrameCount);
        Assert.Equal(8, v.AsInt16().Length);
        Assert.Throws<InvalidOperationException>(() => new AudioBufferView(new byte[16], new AudioFormat(48000, 2, SampleFormat.Int16)).AsFloat32().Length);

        var f = new AudioBufferView(bytes, new AudioFormat(48000, 2, SampleFormat.Float32));
        Assert.Equal(2, f.FrameCount);
        Assert.Equal(4, f.AsFloat32().Length);
        Assert.Equal(1, f.SliceFrames(1).FrameCount);
        Assert.Equal(1, f.SliceFrames(1, 1).FrameCount);
    }

    [Fact]
    public void RejectsPartialFrame()
    {
        var bytes = new byte[7];
        Assert.Throws<ArgumentException>(() => new AudioBufferView(bytes, new AudioFormat(48000, 2, SampleFormat.Int16)));
    }

    [Fact]
    public void RejectsZeroChannels()
    {
        var bytes = new byte[8];
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioBufferView(bytes, new AudioFormat(48000, 0, SampleFormat.Int16)));
    }
}