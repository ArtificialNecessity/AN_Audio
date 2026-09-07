using System.Runtime.CompilerServices;
using AN.Audio.Midi;
using Xunit;

namespace AN.Audio.Midi.Tests;

public class MidiInput_MessageTests
{
    private static MidiInput_Message M1(byte status, byte d1 = 0, byte d2 = 0) => MidiInput_Message.FromMidi1(0, 0, status, d1, d2, default);
    private static MidiInput_Message M2(Midi_Midi2Opcode op, byte channel, byte byteA, byte byteB, uint word1)
        => MidiInput_Message.FromUmp(0, 0, (0x4u << 28) | ((uint)((byte)op << 4 | channel) << 16) | ((uint)byteA << 8) | byteB, word1, default);

    [Fact]
    public void Struct_size_is_32_bytes_and_blittable()
    {
        // D25: size is an implementation detail, but the ring's memory maths (D5/D23) assumes it, so pin it here.
        Assert.Equal(32, Unsafe.SizeOf<MidiInput_Message>());
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
        Assert.Equal(expected, M1((byte)status).Kind);
    }

    [Fact]
    public void Midi1_wraps_as_UMP_MT2_word_with_group_0()
    {
        var m = M1(0x9A, 60, 100);
        Assert.Equal(Midi_Protocol.Midi1, m.Protocol);
        Assert.Equal(Midi_UmpMessageType.Midi1ChannelVoice, m.MessageType);
        Assert.Equal(0, m.Group.Index0To15);
        Assert.Equal((0x209A3C64u, 0u), m.RawUmpWords);
        Assert.Equal(((byte)0x9A, (byte)60, (byte)100), m.RawMidi1Bytes);
    }

    [Fact]
    public void Midi1_system_message_wraps_as_MT1()
    {
        var m = M1(0xF8);
        Assert.Equal(Midi_UmpMessageType.SystemCommonRealTime, m.MessageType);
        Assert.True(m.IsSystemMessage);
        Assert.Equal(Midi_Status.TimingClock, m.SystemStatus);
        Assert.Equal(0, m.Channel.Index0To15);
    }

    [Fact]
    public void Channel_is_low_nibble()
    {
        var m = M1(0x9A, 60, 100);
        Assert.Equal(10, m.Channel.Index0To15);
        Assert.Equal(11, m.Channel.DisplayNumber1To16);
    }

    [Fact]
    public void Midi1_NoteOn_velocity_zero_is_delivered_as_NoteOn_but_IsNoteOff()
    {
        var m = M1(0x90, 60, 0);
        Assert.Equal(Midi_MessageKind.NoteOn, m.Kind);   // D15: wire data untouched
        Assert.Equal(0, m.Velocity7.Value);
        Assert.False(m.IsNoteOn);
        Assert.True(m.IsNoteOff);
    }

    [Fact]
    public void Midi1_NoteOn_with_velocity_is_NoteOn_and_both_tiers_agree()
    {
        var m = M1(0x90, 60, 127);
        Assert.True(m.IsNoteOn);
        Assert.False(m.IsNoteOff);
        Assert.Equal(60, m.Note.Number);
        Assert.Equal(127, m.Velocity7.Value);
        Assert.Equal(0xFFFF, m.Velocity16.Value);          // Min-Center-Max: max → max
        Assert.Equal(1f, m.VelocityNormalized, 5);
        Assert.Equal(Midi_NoteAttributeType.None, m.NoteAttributeType);
    }

    [Theory]
    [InlineData(0x00, 0x40, 8192, 0x80000000u)]
    [InlineData(0x00, 0x00, 0, 0x00000000u)]
    [InlineData(0x7F, 0x7F, 16383, 0xFFFFFFFFu)]
    [InlineData(0x01, 0x40, 8193, 0x80040020u)]   // Min-Center-Max replicates the low bits downward (M2-115-U §3)
    public void Midi1_PitchBend_is_14_bit_lsb_first_and_upscales(int lsb, int msb, int expected14, uint expected32)
    {
        var m = M1(0xE0, (byte)lsb, (byte)msb);
        Assert.Equal(expected14, m.PitchBend14);
        Assert.Equal(expected32, m.PitchBend32.Value);
    }

    [Fact]
    public void Midi1_CC_exposes_exact_7bit_and_upscaled_32bit()
    {
        var m = M1(0xB0, 16, 127);    // an encoder sending "-1" in two's complement
        Assert.Equal(Midi_Controller.BankSelectMsb + 16, m.Controller);
        Assert.Equal(127, m.ControllerValue7);                 // raw value survives — D26 decode happens above us
        Assert.Equal(0xFFFFFFFFu, m.ControllerValue32.Value);
        Assert.False(m.IsRelativeController);
        Assert.Equal(-1, Midi_RelativeDecode.TwosComplement7(m.ControllerValue7));
    }

    // ---- MIDI 2.0 (MT 0x4) — exercised through FromUmp exactly as a UMP backend would call it ----------------

    [Fact]
    public void Midi2_NoteOn_native_16bit_velocity_and_downscaled_7bit()
    {
        var m = M2(Midi_Midi2Opcode.NoteOn, channel: 3, byteA: 64, byteB: (byte)Midi_NoteAttributeType.Pitch7_9, word1: (0xC104u << 16) | 0x1234u);
        Assert.Equal(Midi_Protocol.Midi2, m.Protocol);
        Assert.Equal(Midi_MessageKind.NoteOn, m.Kind);
        Assert.Equal(3, m.Channel.Index0To15);
        Assert.Equal(64, m.Note.Number);
        Assert.Equal(0xC104, m.Velocity16.Value);
        Assert.Equal(96, m.Velocity7.Value);                  // 0xC104 >> 9 == 96: reverses the 7→16 table
        Assert.Equal(Midi_NoteAttributeType.Pitch7_9, m.NoteAttributeType);
        Assert.Equal(0x1234, m.NoteAttributeData);
    }

    [Fact]
    public void Midi2_NoteOn_velocity_zero_is_still_NoteOn()
    {
        var m = M2(Midi_Midi2Opcode.NoteOn, 0, 60, 0, 0);
        Assert.True(m.IsNoteOn);
        Assert.False(m.IsNoteOff);
    }

    [Fact]
    public void Midi2_CC_native_32bit()
    {
        var m = M2(Midi_Midi2Opcode.ControlChange, 0, 7, 0, 0x80000000u);
        Assert.Equal(Midi_Controller.Volume, m.Controller);
        Assert.Equal(0x80000000u, m.ControllerValue32.Value);
        Assert.Equal(64, m.ControllerValue7);
    }

    [Fact]
    public void Midi2_PitchBend_native_32bit()
    {
        var m = M2(Midi_Midi2Opcode.PitchBend, 0, 0, 0, 0x80000000u);
        Assert.Equal(0x80000000u, m.PitchBend32.Value);
        Assert.Equal(8192, m.PitchBend14);
        Assert.Equal(0f, m.PitchBend32.NormalizedMinus1To1, 6);
    }

    [Fact]
    public void Midi2_relative_registered_controller_is_signed_delta()
    {
        var m = M2(Midi_Midi2Opcode.RelativeRegisteredController, 0, byteA: 0, byteB: 7, word1: unchecked((uint)-22369621));
        Assert.Equal(Midi_MessageKind.Midi2Extended, m.Kind);
        Assert.True(m.IsRelativeController);
        Assert.Equal(-22369621, m.RelativeDelta32);
        Assert.Equal(0, m.RegisteredControllerBank);
        Assert.Equal(7, m.RegisteredControllerIndex);
    }

    [Fact]
    public void Midi2_program_change_reads_program_from_word1()
    {
        var m = M2(Midi_Midi2Opcode.ProgramChange, 0, 0, 0x01, (42u << 24) | (3u << 8) | 5u);
        Assert.Equal(Midi_MessageKind.ProgramChange, m.Kind);
        Assert.Equal(42, m.Program);
    }
}