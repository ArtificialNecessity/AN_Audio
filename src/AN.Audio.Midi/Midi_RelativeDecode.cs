namespace AN.Audio.Midi;

/// <summary>
/// Stateless decoders for the folk encodings MIDI 1.0 endless encoders use to send a signed step in a 7-bit CC value
/// (SPEC-30 D26). The library never applies these itself — which encoding a given controller uses is knowledge the wire
/// does not carry, so the consumer's per-device profile decides and calls the matching helper.
/// MIDI 2.0 Relative Registered/Assignable Controllers need none of this: see <see cref="MidiInput_Message.RelativeDelta32"/>.
/// </summary>
public static class Midi_RelativeDecode
{
    /// <summary>1..63 = +n, 127..65 = −(128−v), 0 and 64 = 0. The most common (Novation, Akai, Arturia …).</summary>
    public static int TwosComplement7(byte value7) => value7 >= 64 ? value7 - 128 : value7;

    /// <summary>64 = 0, 65+ = +n, 63− = −n. Second most common (Mackie-style, some Behringer).</summary>
    public static int BinaryOffset64(byte value7) => value7 - 64;

    /// <summary>Bit 6 = direction (set = negative), bits 0..5 = magnitude. Older gear.</summary>
    public static int SignMagnitude(byte value7) => (value7 & 0x40) != 0 ? -(value7 & 0x3F) : value7 & 0x3F;

    /// <summary>Bit 6 = direction (set = positive), bits 0..5 = magnitude. The inverted sign-magnitude variant.</summary>
    public static int SignMagnitudeInverted(byte value7) => (value7 & 0x40) != 0 ? value7 & 0x3F : -(value7 & 0x3F);
}