using System.Runtime.CompilerServices;
using AN.Audio.Midi;
using Xunit;

namespace AN.Audio.Midi.Tests;

public class MidiInput_MessageTests
{
    [Fact]
    public void Struct_is_exactly_16_bytes()
    {
        Assert.Equal(MidiInput_Message.SizeInBytes, Unsafe.SizeOf<MidiInput_Message>());
        Assert.Equal(16, Unsafe.SizeOf<MidiInput_Message>());
    }

    [Theory]
    [InlineData(0x80, Midi_MessageKind.NoteOff)]
    [InlineData(0x8F, Midi_MessageKind.NoteOff)]
    [InlineData(0x90, Midi_MessageKind.NoteOn)]
    [InlineData(0x93, Midi_MessageKind.NoteOn)]
    [InlineData(0xA0, Midi_MessageKind.PolyPressure)]
    [InlineData(0xB0, Midi_MessageKind.ControlChange)]
    [InlineData(0xC0, Midi_MessageKind.ProgramChange)]
    [InlineData(0xD0, Midi_MessageKind.ChannelPressure)]
    [InlineData(0xE0, Midi_MessageKind.PitchBend)]
    [InlineData(0xF0, Midi_MessageKind.SysEx)]
    [InlineData(0xF1, Midi_MessageKind.SystemCommon)]
    [InlineData(0xF2, Midi_MessageKind.SystemCommon)]
    [InlineData(0xF3, Midi_MessageKind.SystemCommon)]
    [InlineData(0xF6, Midi_MessageKind.SystemCommon)]
    [InlineData(0xF7, Midi_MessageKind.SysEx)]
    [InlineData(0xF8, Midi_MessageKind.SystemRealTime)]
    [InlineData(0xFA, Midi_MessageKind.SystemRealTime)]
    [InlineData(0xFC, Midi_MessageKind.SystemRealTime)]
    [InlineData(0xFE, Midi_MessageKind.SystemRealTime)]
    [InlineData(0xFF, Midi_MessageKind.SystemRealTime)]
    public void Kind_decodes_every_status(int status, Midi_MessageKind expected)
    {
        var m = new MidiInput_Message(0, 0, (byte)status, 0, 0, default);
        Assert.Equal(expected, m.Kind);
    }

    [Fact]
    public void Channel_is_low_nibble()
    {
        var m = new MidiInput_Message(0, 0, 0x9A, 60, 100, default);
        Assert.Equal(10, m.Channel.Index0To15);
        Assert.Equal(11, m.Channel.DisplayNumber1To16);
    }

    [Fact]
    public void NoteOn_velocity_zero_is_delivered_as_NoteOn_but_IsNoteOff()
    {
        var m = new MidiInput_Message(0, 0, 0x90, 60, 0, default);
        Assert.Equal(Midi_MessageKind.NoteOn, m.Kind);   // D15: wire data untouched
        Assert.Equal(0, m.Data2);
        Assert.False(m.IsNoteOn);
        Assert.True(m.IsNoteOff);
    }

    [Fact]
    public void NoteOn_with_velocity_is_NoteOn()
    {
        var m = new MidiInput_Message(0, 0, 0x90, 60, 1, default);
        Assert.True(m.IsNoteOn);
        Assert.False(m.IsNoteOff);
        Assert.Equal(60, m.Note.Number);
        Assert.Equal(1, m.Velocity.Value);
    }

    [Theory]
    [InlineData(0x00, 0x40, 8192)]
    [InlineData(0x00, 0x00, 0)]
    [InlineData(0x7F, 0x7F, 16383)]
    [InlineData(0x01, 0x40, 8193)]
    public void PitchBend_is_14_bit_lsb_first(int lsb, int msb, int expected)
    {
        var m = new MidiInput_Message(0, 0, 0xE0, (byte)lsb, (byte)msb, default);
        Assert.Equal(expected, m.PitchBend14);
    }
}