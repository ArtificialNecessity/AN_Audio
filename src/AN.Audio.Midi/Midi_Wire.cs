namespace AN.Audio.Midi;

// MIDI 1.0 wire-protocol vocabulary shared by input and output (SPEC-30 D12/D13).
// Every wire constant is an enum member; nothing downstream uses raw literals.

/// <summary>Coarse classification of a short MIDI message, derived from its status byte.</summary>
public enum Midi_MessageKind : byte
{
    NoteOff = 0,
    NoteOn = 1,
    PolyPressure = 2,
    ControlChange = 3,
    ProgramChange = 4,
    ChannelPressure = 5,
    PitchBend = 6,
    SystemCommon = 7,
    SystemRealTime = 8,
    /// <summary>F0/F7 — never appears in the message ring; SysEx has its own path (D7).</summary>
    SysEx = 9,
}

/// <summary>MIDI 1.0 status bytes. Channel-voice values are the high nibble (channel = low nibble).</summary>
public enum Midi_Status : byte
{
    NoteOff = 0x80,
    NoteOn = 0x90,
    PolyPressure = 0xA0,
    ControlChange = 0xB0,
    ProgramChange = 0xC0,
    ChannelPressure = 0xD0,
    PitchBend = 0xE0,

    SysExStart = 0xF0,
    MtcQuarterFrame = 0xF1,
    SongPosition = 0xF2,
    SongSelect = 0xF3,
    TuneRequest = 0xF6,
    SysExEnd = 0xF7,

    TimingClock = 0xF8,
    Start = 0xFA,
    Continue = 0xFB,
    Stop = 0xFC,
    ActiveSensing = 0xFE,
    Reset = 0xFF,
}

/// <summary>Common controller numbers (Data1 of a ControlChange).</summary>
public enum Midi_Controller : byte
{
    BankSelectMsb = 0,
    ModWheel = 1,
    BreathController = 2,
    FootController = 4,
    PortamentoTime = 5,
    DataEntryMsb = 6,
    Volume = 7,
    Balance = 8,
    Pan = 10,
    Expression = 11,
    BankSelectLsb = 32,
    DataEntryLsb = 38,
    Sustain = 64,
    Portamento = 65,
    Sostenuto = 66,
    SoftPedal = 67,
    Legato = 68,
    Hold2 = 69,
    NrpnLsb = 98,
    NrpnMsb = 99,
    RpnLsb = 100,
    RpnMsb = 101,
    AllSoundOff = 120,
    ResetAllControllers = 121,
    LocalControl = 122,
    AllNotesOff = 123,
    OmniOff = 124,
    OmniOn = 125,
    MonoOn = 126,
    PolyOn = 127,
}

/// <summary>First byte after F0 in a SysEx message.</summary>
public enum Midi_SysExId : byte
{
    /// <summary>00 — the manufacturer id is the following two bytes (3-byte id).</summary>
    ExtendedManufacturer = 0x00,
    NonCommercial = 0x7D,
    UniversalNonRealTime = 0x7E,
    UniversalRealTime = 0x7F,
}

/// <summary>Sub-ID#1 values for Universal Non-Real-Time SysEx (F0 7E dev &lt;subid1&gt; ...).</summary>
public enum Midi_UniversalNonRealTimeSubId1 : byte
{
    SampleDumpHeader = 0x01,
    SampleDataPacket = 0x02,
    SampleDumpRequest = 0x03,
    MidiTimeCode = 0x04,
    SampleDumpExtensions = 0x05,
    GeneralInformation = 0x06,
    FileDump = 0x07,
    TuningStandard = 0x08,
    GeneralMidi = 0x09,
    EndOfFile = 0x7B,
    Wait = 0x7C,
    Cancel = 0x7D,
    Nak = 0x7E,
    Ack = 0x7F,
}

/// <summary>Sub-ID#2 values under <see cref="Midi_UniversalNonRealTimeSubId1.GeneralInformation"/>.</summary>
public enum Midi_GeneralInformationSubId2 : byte
{
    IdentityRequest = 0x01,
    IdentityReply = 0x02,
}

/// <summary>The SysEx "device id" byte; 0x7F addresses all devices.</summary>
public enum Midi_SysExDeviceId : byte
{
    AllCall = 0x7F,
}

/// <summary>MIDI channel, 0-based (wire nibble). Display as Index0To15 + 1.</summary>
public readonly record struct Midi_Channel(byte Index0To15)
{
    public int DisplayNumber1To16 => Index0To15 + 1;
}

/// <summary>MIDI note number 0..127. 60 = middle C (C4).</summary>
public readonly record struct Midi_Note(byte Number);

/// <summary>MIDI velocity 0..127.</summary>
public readonly record struct Midi_Velocity(byte Value)
{
    public float Normalized0To1 => Value / 127f;
}

/// <summary>
/// SysEx manufacturer id. 1-byte ids are 0x01..0x7C; 3-byte ids (first byte 00) are packed as
/// (byte1 &lt;&lt; 8) | byte2 with <see cref="IsExtended"/> = true (e.g. Arturia 00 20 6B → 0x206B).
/// </summary>
public readonly record struct Midi_ManufacturerId(int Value, bool IsExtended)
{
    public override string ToString() => IsExtended
        ? $"00 {(Value >> 8) & 0x7F:X2} {Value & 0x7F:X2}"
        : $"{Value:X2}";
}