using AN.Audio.Midi;
using Xunit;

namespace AN.Audio.Midi.Tests;

public class Midi_RelativeDecodeTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(63, 63)]
    [InlineData(64, -64)]
    [InlineData(65, -63)]
    [InlineData(125, -3)]
    [InlineData(127, -1)]
    public void TwosComplement7(int v, int expected) => Assert.Equal(expected, Midi_RelativeDecode.TwosComplement7((byte)v));

    [Theory]
    [InlineData(64, 0)]
    [InlineData(65, 1)]
    [InlineData(67, 3)]
    [InlineData(127, 63)]
    [InlineData(63, -1)]
    [InlineData(61, -3)]
    [InlineData(0, -64)]
    public void BinaryOffset64(int v, int expected) => Assert.Equal(expected, Midi_RelativeDecode.BinaryOffset64((byte)v));

    [Theory]
    [InlineData(0x00, 0)]
    [InlineData(0x01, 1)]
    [InlineData(0x03, 3)]
    [InlineData(0x3F, 63)]
    [InlineData(0x41, -1)]
    [InlineData(0x43, -3)]
    [InlineData(0x7F, -63)]
    public void SignMagnitude(int v, int expected)
    {
        Assert.Equal(expected, Midi_RelativeDecode.SignMagnitude((byte)v));
        Assert.Equal(-expected, Midi_RelativeDecode.SignMagnitudeInverted((byte)v));
    }
}