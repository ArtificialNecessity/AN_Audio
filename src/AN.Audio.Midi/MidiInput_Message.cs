using System.Runtime.InteropServices;

namespace AN.Audio.Midi;

/// <summary>Byte-sized index into <see cref="IMidiInput.OpenPorts"/>.</summary>
public readonly record struct MidiInput_PortIndex(byte Value);

/// <summary>
/// The one struct on the hot path (SPEC-30 D6/D7): a short MIDI message with two timestamps.
/// Exactly 16 bytes, blittable, never allocated. Wire data is delivered as received (D15).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct MidiInput_Message(
    long ArrivalTicks,              // Stopwatch.GetTimestamp() at callback entry
    uint DriverTimestamp,           // backend-native; WinMM = ms since midiInStart
    byte Status,                    // raw status byte; channel in low nibble for channel messages
    byte Data1,
    byte Data2,
    MidiInput_PortIndex Port)
{
    public static readonly int SizeInBytes = 16;

    public Midi_MessageKind Kind => DecodeKind(Status);

    public bool IsChannelMessage => Status < (byte)Midi_Status.SysExStart;

    /// <summary>Channel for channel-voice messages; meaningless (channel 0) for system messages.</summary>
    public Midi_Channel Channel => new((byte)(Status & 0x0F));

    /// <summary>Note-on with non-zero velocity.</summary>
    public bool IsNoteOn => Kind == Midi_MessageKind.NoteOn && Data2 != 0;

    /// <summary>Note-off, OR note-on with velocity 0 (running-status idiom). The wire data is untouched (D15).</summary>
    public bool IsNoteOff => Kind == Midi_MessageKind.NoteOff || (Kind == Midi_MessageKind.NoteOn && Data2 == 0);

    public Midi_Note Note => new(Data1);
    public Midi_Velocity Velocity => new(Data2);
    public Midi_Controller Controller => (Midi_Controller)Data1;
    public byte ControllerValue => Data2;

    /// <summary>14-bit pitch bend 0..16383; 8192 = centre.</summary>
    public int PitchBend14 => (Data2 << 7) | Data1;

    public static Midi_MessageKind DecodeKind(byte status)
    {
        if (status >= (byte)Midi_Status.TimingClock) return Midi_MessageKind.SystemRealTime;
        if (status == (byte)Midi_Status.SysExStart || status == (byte)Midi_Status.SysExEnd) return Midi_MessageKind.SysEx;
        if (status >= (byte)Midi_Status.SysExStart) return Midi_MessageKind.SystemCommon;
        return (status & 0xF0) switch
        {
            (byte)Midi_Status.NoteOff => Midi_MessageKind.NoteOff,
            (byte)Midi_Status.NoteOn => Midi_MessageKind.NoteOn,
            (byte)Midi_Status.PolyPressure => Midi_MessageKind.PolyPressure,
            (byte)Midi_Status.ControlChange => Midi_MessageKind.ControlChange,
            (byte)Midi_Status.ProgramChange => Midi_MessageKind.ProgramChange,
            (byte)Midi_Status.ChannelPressure => Midi_MessageKind.ChannelPressure,
            _ => Midi_MessageKind.PitchBend,   // 0xE0; values < 0x80 are data bytes and never arrive as status
        };
    }

    public override string ToString() => Kind switch
    {
        Midi_MessageKind.NoteOn or Midi_MessageKind.NoteOff =>
            $"{Kind} ch{Channel.DisplayNumber1To16} note={Data1} vel={Data2}",
        Midi_MessageKind.ControlChange =>
            $"CC ch{Channel.DisplayNumber1To16} #{Data1} ({Controller}) = {Data2}",
        Midi_MessageKind.PitchBend =>
            $"PitchBend ch{Channel.DisplayNumber1To16} {PitchBend14}",
        Midi_MessageKind.SystemRealTime or Midi_MessageKind.SystemCommon =>
            $"{(Midi_Status)Status} {Data1:X2} {Data2:X2}",
        _ => $"{Kind} ch{Channel.DisplayNumber1To16} {Data1:X2} {Data2:X2}",
    };
}

/// <summary>
/// Raw delivery callback (SPEC-30 D4/D14). Runs on the DRIVER thread: no allocation, no locks, no I/O,
/// never call Stop()/Dispose() from inside it. Same rules as <see cref="AN.Audio.AudioCallback"/>.
/// </summary>
public delegate void MidiInput_Callback(in MidiInput_Message message);