using System.Runtime.InteropServices;
using AN.Audio.Midi.Internal;

namespace AN.Audio.Midi;

/// <summary>Byte-sized index into <see cref="IMidiInput.OpenPorts"/>.</summary>
public readonly record struct MidiInput_PortIndex(byte Value);

/// <summary>
/// The one struct on the hot path (SPEC-30 D6/D7/D25). Blittable, never allocated, MIDI 2.0-ready:
/// the message is stored as its UMP words (word 0 + word 1) regardless of backend, plus which <see cref="Protocol"/>
/// the device actually spoke. <b>Consumers use the typed accessors only</b> — the raw words are internal so a future
/// UMP backend changes nothing above this line. Wire data is delivered as received (D15): nothing is folded or rewritten.
/// <para>Two accessor tiers: native 7/14-bit (<see cref="Velocity7"/>, <see cref="ControllerValue7"/>, <see cref="PitchBend14"/>)
/// are exact for MIDI 1.0 sources; protocol-neutral 16/32-bit (<see cref="Velocity16"/>, <see cref="ControllerValue32"/>,
/// <see cref="PitchBend32"/>) are exact for MIDI 2.0 sources and Min-Center-Max upscaled (reversible) for MIDI 1.0.</para>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct MidiInput_Message
{
    // ---- storage (internal; 32 bytes total, asserted by test — the size is NOT a public promise, D25) ----------
    /// <summary>Stopwatch.GetTimestamp() at driver-callback entry (D6).</summary>
    public long ArrivalTicks { get; }
    /// <summary>Backend-native clock. WinMM: ms since midiInStart. UMP backends: 64-bit host clock.</summary>
    public long DriverTimestamp { get; }
    /// <summary>UMP word 0: mt|group|status|byteA|byteB.</summary>
    internal uint Word0 { get; }
    /// <summary>UMP word 1 (MT 0x4 only): 32-bit data. Zero for 32-bit packets.</summary>
    internal uint Word1 { get; }
    public MidiInput_PortIndex Port { get; }
    public Midi_Protocol Protocol { get; }
    private readonly ushort _reserved;

    internal MidiInput_Message(long arrivalTicks, long driverTimestamp, uint word0, uint word1, MidiInput_PortIndex port, Midi_Protocol protocol)
    {
        ArrivalTicks = arrivalTicks; DriverTimestamp = driverTimestamp; Word0 = word0; Word1 = word1; Port = port; Protocol = protocol; _reserved = 0;
    }

    // ---- constructors used by backends and tests --------------------------------------------------------------

    /// <summary>Wrap a MIDI 1.0 short message (status, data1, data2) as UMP MT 0x1/0x2 on the given group. Allocation-free shift+or.</summary>
    public static MidiInput_Message FromMidi1(long arrivalTicks, long driverTimestamp, byte status, byte data1, byte data2, MidiInput_PortIndex port, Midi_Group group = default)
    {
        var mt = status >= (byte)Midi_Status.SysExStart ? Midi_UmpMessageType.SystemCommonRealTime : Midi_UmpMessageType.Midi1ChannelVoice;
        uint w0 = ((uint)mt << 28) | ((uint)(group.Index0To15 & 0x0F) << 24) | ((uint)status << 16) | ((uint)(data1 & 0x7F) << 8) | (uint)(data2 & 0x7F);
        return new MidiInput_Message(arrivalTicks, driverTimestamp, w0, 0, port, Midi_Protocol.Midi1);
    }

    /// <summary>Wrap a UMP packet (MT 0x1, 0x2 or 0x4). <see cref="Protocol"/> is derived from the message type.</summary>
    public static MidiInput_Message FromUmp(long arrivalTicks, long driverTimestamp, uint word0, uint word1, MidiInput_PortIndex port)
    {
        var protocol = (Midi_UmpMessageType)(word0 >> 28) == Midi_UmpMessageType.Midi2ChannelVoice ? Midi_Protocol.Midi2 : Midi_Protocol.Midi1;
        return new MidiInput_Message(arrivalTicks, driverTimestamp, word0, word1, port, protocol);
    }

    // ---- addressing ------------------------------------------------------------------------------------------

    public Midi_UmpMessageType MessageType => (Midi_UmpMessageType)(Word0 >> 28);
    public Midi_Group Group => new((byte)((Word0 >> 24) & 0x0F));
    /// <summary>Full status byte: opcode in the high nibble, channel in the low nibble for channel messages.</summary>
    internal byte Status => (byte)(Word0 >> 16);
    internal byte ByteA => (byte)(Word0 >> 8);   // MIDI 1.0 data1 / note / controller number
    internal byte ByteB => (byte)Word0;          // MIDI 1.0 data2 / attribute type

    public bool IsChannelMessage => MessageType is Midi_UmpMessageType.Midi1ChannelVoice or Midi_UmpMessageType.Midi2ChannelVoice;
    public bool IsSystemMessage => MessageType == Midi_UmpMessageType.SystemCommonRealTime;

    /// <summary>Channel for channel-voice messages; channel 0 for system messages.</summary>
    public Midi_Channel Channel => new((byte)(IsChannelMessage ? Status & 0x0F : 0));

    /// <summary>Coarse classification. MIDI 2.0-only opcodes (RPN/NRPN, per-note …) report <see cref="Midi_MessageKind.Midi2Extended"/>.</summary>
    public Midi_MessageKind Kind => MessageType switch
    {
        Midi_UmpMessageType.Midi1ChannelVoice => DecodeKind(Status),
        Midi_UmpMessageType.Midi2ChannelVoice => Midi2Opcode switch
        {
            Midi_Midi2Opcode.NoteOff => Midi_MessageKind.NoteOff,
            Midi_Midi2Opcode.NoteOn => Midi_MessageKind.NoteOn,
            Midi_Midi2Opcode.PolyPressure => Midi_MessageKind.PolyPressure,
            Midi_Midi2Opcode.ControlChange => Midi_MessageKind.ControlChange,
            Midi_Midi2Opcode.ProgramChange => Midi_MessageKind.ProgramChange,
            Midi_Midi2Opcode.ChannelPressure => Midi_MessageKind.ChannelPressure,
            Midi_Midi2Opcode.PitchBend => Midi_MessageKind.PitchBend,
            _ => Midi_MessageKind.Midi2Extended,
        },
        Midi_UmpMessageType.SystemCommonRealTime => DecodeKind(Status),
        _ => Midi_MessageKind.Midi2Extended,
    };

    /// <summary>MIDI 2.0 opcode (valid when <see cref="Protocol"/> is Midi2; for Midi1 channel messages it equals the 1.0 opcode nibble).</summary>
    public Midi_Midi2Opcode Midi2Opcode => (Midi_Midi2Opcode)(Status >> 4);

    /// <summary>System status byte (F1..FF) for system messages.</summary>
    public Midi_Status SystemStatus => (Midi_Status)Status;

    // ---- notes -----------------------------------------------------------------------------------------------

    /// <summary>Note-on with non-zero velocity (MIDI 1.0 idiom). For MIDI 2.0 every NoteOn is a note-on (velocity 0 is legal).</summary>
    public bool IsNoteOn => Kind == Midi_MessageKind.NoteOn && (Protocol == Midi_Protocol.Midi2 || ByteB != 0);

    /// <summary>Note-off, OR (MIDI 1.0 only) note-on with velocity 0 (running-status idiom). The wire data is untouched (D15).</summary>
    public bool IsNoteOff => Kind == Midi_MessageKind.NoteOff || (Protocol == Midi_Protocol.Midi1 && Kind == Midi_MessageKind.NoteOn && ByteB == 0);

    public Midi_Note Note => new((byte)(ByteA & 0x7F));

    /// <summary>Native MIDI 1.0 velocity 0..127. Exact for Midi1; truncating downscale for Midi2.</summary>
    public Midi_Velocity Velocity7 => new(Protocol == Midi_Protocol.Midi2 ? Midi_BitScaling.Down16To7((ushort)(Word1 >> 16)) : ByteB);

    /// <summary>MIDI 2.0 velocity 0..65535. Exact for Midi2; Min-Center-Max upscale for Midi1.</summary>
    public Midi_Velocity16 Velocity16 => new(Protocol == Midi_Protocol.Midi2 ? (ushort)(Word1 >> 16) : Midi_BitScaling.Up7To16(ByteB));

    /// <summary>Protocol-neutral velocity 0..1. Prefer this in synth code.</summary>
    public float VelocityNormalized => Velocity16.Normalized0To1;

    /// <summary>MIDI 2.0 note attribute type; <see cref="Midi_NoteAttributeType.None"/> for MIDI 1.0.</summary>
    public Midi_NoteAttributeType NoteAttributeType => Protocol == Midi_Protocol.Midi2 ? (Midi_NoteAttributeType)ByteB : Midi_NoteAttributeType.None;
    public ushort NoteAttributeData => Protocol == Midi_Protocol.Midi2 ? (ushort)Word1 : (ushort)0;

    // ---- controllers -----------------------------------------------------------------------------------------

    public Midi_Controller Controller => (Midi_Controller)(ByteA & 0x7F);

    /// <summary>Native 7-bit CC / pressure value. Exact for Midi1; truncating downscale for Midi2.</summary>
    public byte ControllerValue7 => Protocol == Midi_Protocol.Midi2 ? Midi_BitScaling.Down32To7(Word1) : ByteB;

    /// <summary>32-bit CC / poly-pressure / channel-pressure value. Exact for Midi2; Min-Center-Max upscale for Midi1.</summary>
    public Midi_Value32 ControllerValue32 => new(Protocol == Midi_Protocol.Midi2 ? Word1 : Midi_BitScaling.Up7To32(ByteB));

    /// <summary>True for MIDI 2.0 Relative Registered/Assignable Controller messages — the wire itself declares a signed delta.</summary>
    public bool IsRelativeController => Protocol == Midi_Protocol.Midi2 && Midi2Opcode is Midi_Midi2Opcode.RelativeRegisteredController or Midi_Midi2Opcode.RelativeAssignableController;

    /// <summary>Signed 32-bit delta (two's complement) when <see cref="IsRelativeController"/>; 0 otherwise. MIDI 1.0 encoder deltas are NOT decoded here (D26).</summary>
    public int RelativeDelta32 => IsRelativeController ? (int)Word1 : 0;

    /// <summary>RPN/NRPN bank (MIDI 2.0 Registered/Assignable Controller opcodes) — byteA.</summary>
    public byte RegisteredControllerBank => (byte)(ByteA & 0x7F);
    /// <summary>RPN/NRPN index (MIDI 2.0 Registered/Assignable Controller opcodes) — byteB.</summary>
    public byte RegisteredControllerIndex => (byte)(ByteB & 0x7F);

    // ---- program / bend --------------------------------------------------------------------------------------

    /// <summary>Program number 0..127. MIDI 2.0 carries it in word1 bits 31..24.</summary>
    public byte Program => Protocol == Midi_Protocol.Midi2 ? (byte)((Word1 >> 24) & 0x7F) : (byte)(ByteA & 0x7F);

    /// <summary>Native 14-bit pitch bend 0..16383; 8192 = centre. Exact for Midi1; truncating downscale for Midi2.</summary>
    public int PitchBend14 => Protocol == Midi_Protocol.Midi2 ? Midi_BitScaling.Down32To14(Word1) : (ByteB << 7) | ByteA;

    /// <summary>32-bit pitch bend; 0x80000000 = centre. Exact for Midi2; Min-Center-Max upscale for Midi1.</summary>
    public Midi_PitchBend32 PitchBend32 => new(Protocol == Midi_Protocol.Midi2 ? Word1 : Midi_BitScaling.Up14To32((ByteB << 7) | ByteA));

    // ---- raw access for tooling (monitors, loggers). Not for musical logic. ------------------------------------

    /// <summary>The raw UMP words. Intended for MIDI monitors / diagnostics only; musical code should use typed accessors (D25).</summary>
    public (uint Word0, uint Word1) RawUmpWords => (Word0, Word1);

    /// <summary>The three MIDI 1.0 bytes (status, data1, data2) when <see cref="Protocol"/> is Midi1. For diagnostics only.</summary>
    public (byte Status, byte Data1, byte Data2) RawMidi1Bytes => (Status, ByteA, ByteB);

    // ---- helpers ---------------------------------------------------------------------------------------------

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

    public override string ToString()
    {
        string p = Protocol == Midi_Protocol.Midi2 ? "M2 " : "";
        return Kind switch
        {
            Midi_MessageKind.NoteOn or Midi_MessageKind.NoteOff =>
                $"{p}{Kind} ch{Channel.DisplayNumber1To16} note={Note.Number} vel={Velocity7.Value}/{Velocity16.Value}",
            Midi_MessageKind.ControlChange =>
                $"{p}CC ch{Channel.DisplayNumber1To16} #{(byte)Controller} ({Controller}) = {ControllerValue7}/{ControllerValue32.Value}",
            Midi_MessageKind.PitchBend =>
                $"{p}PitchBend ch{Channel.DisplayNumber1To16} {PitchBend14}/{PitchBend32.Value:X8}",
            Midi_MessageKind.SystemRealTime or Midi_MessageKind.SystemCommon =>
                $"{SystemStatus} {ByteA:X2} {ByteB:X2}",
            Midi_MessageKind.Midi2Extended when IsRelativeController =>
                $"{p}{Midi2Opcode} ch{Channel.DisplayNumber1To16} bank={RegisteredControllerBank} idx={RegisteredControllerIndex} delta={RelativeDelta32}",
            _ => $"{p}{Kind} ch{Channel.DisplayNumber1To16} {Word0:X8} {Word1:X8}",
        };
    }
}

/// <summary>
/// Raw delivery callback (SPEC-30 D4/D14). Runs on the DRIVER thread: no allocation, no locks, no I/O,
/// never call Stop()/Dispose() from inside it. Same rules as AN.Audio's AudioCallback.
/// </summary>
public delegate void MidiInput_Callback(in MidiInput_Message message);