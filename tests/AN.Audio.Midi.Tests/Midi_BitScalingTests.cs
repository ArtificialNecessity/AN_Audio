using AN.Audio.Midi;
using AN.Audio.Midi.Internal;
using Xunit;

namespace AN.Audio.Midi.Tests;

/// <summary>Min-Center-Max scaling against the numeric tables in M2-115-U (see _EXTERNAL_APIS/UMP_MIDI2_Format.md).</summary>
public class Midi_BitScalingTests
{
    [Theory]
    [InlineData(0, 0x0000)]
    [InlineData(5, 0x0A00)]
    [InlineData(30, 0x3C00)]
    [InlineData(32, 0x4000)]
    [InlineData(64, 0x8000)]
    [InlineData(70, 0x8C30)]
    [InlineData(96, 0xC104)]
    [InlineData(120, 0xF1C7)]
    [InlineData(127, 0xFFFF)]
    public void Upscale_7_to_16_matches_spec_table(int v7, int expected16)
    {
        Assert.Equal(expected16, Midi_BitScaling.Up7To16((byte)v7));
    }

    [Theory]
    [InlineData(0, 0x00000000u)]
    [InlineData(5, 0x0A000000u)]
    [InlineData(30, 0x3C000000u)]
    [InlineData(32, 0x40000000u)]
    [InlineData(64, 0x80000000u)]
    [InlineData(70, 0x8C30C30Cu)]
    [InlineData(96, 0xC1041041u)]
    [InlineData(120, 0xF1C71C71u)]
    [InlineData(127, 0xFFFFFFFFu)]
    public void Upscale_7_to_32_matches_spec_table(int v7, uint expected32)
    {
        Assert.Equal(expected32, Midi_BitScaling.Up7To32((byte)v7));
    }

    [Theory]
    [InlineData(0, 0x00000000u)]
    [InlineData(8192, 0x80000000u)]
    [InlineData(16383, 0xFFFFFFFFu)]
    public void Upscale_14_to_32_preserves_min_centre_max(int v14, uint expected32)
    {
        Assert.Equal(expected32, Midi_BitScaling.Up14To32(v14));
    }

    [Fact]
    public void Downscale_of_upscale_is_identity_for_every_7bit_value()
    {
        for (int v = 0; v < 128; v++)
        {
            Assert.Equal(v, Midi_BitScaling.Down16To7(Midi_BitScaling.Up7To16((byte)v)));
            Assert.Equal(v, Midi_BitScaling.Down32To7(Midi_BitScaling.Up7To32((byte)v)));
        }
        for (int v = 0; v < 16384; v++)
            Assert.Equal(v, Midi_BitScaling.Down32To14(Midi_BitScaling.Up14To32(v)));
    }

    [Fact]
    public void Upscale_is_monotonic()
    {
        uint prev = 0;
        for (int v = 1; v < 128; v++)
        {
            uint cur = Midi_BitScaling.Up7To32((byte)v);
            Assert.True(cur > prev, $"{v}: {cur:X8} <= {prev:X8}");
            prev = cur;
        }
    }
}