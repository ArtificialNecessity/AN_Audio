namespace AN.Audio.Midi.Internal;

/// <summary>
/// Universal Non-Real-Time Identity Request / Reply (SPEC-30 D8; _EXTERNAL_APIS/WinMM_MidiIn.md).
///
/// Request: F0 7E dev 06 01 F7
/// Reply:   F0 7E dev 06 02 mm ff ff dd dd ss ss ss ss F7           (1-byte manufacturer, 15 bytes)
///          F0 7E dev 06 02 00 mm mm ff ff dd dd ss ss ss ss F7     (3-byte manufacturer, 17 bytes)
///
/// Real hardware is not spec-exact: the M-Audio Oxygen 49 MKV answers with a 3-byte id but only TWO revision bytes
/// (F0 7E 7F 06 02 00 01 05 00 02 30 30 35 30 F7, 15 bytes). The parser therefore requires manufacturer + family +
/// member and accepts 1..4 revision bytes (whatever precedes F7), packing them LSB first.
/// </summary>
internal static class Midi_IdentityReplyParser
{
    public const int RequestLength = 6;
    private const int MinReplyLength = 12;          // F0 7E dev 06 02 mm ff ff dd dd ss F7 (1-byte id, 1 revision byte)
    private const int MaxRevisionBytes = 4;

    /// <summary>Writes the 6-byte all-call Identity Request into <paramref name="into"/>.</summary>
    public static void WriteRequest(Span<byte> into)
    {
        into[0] = (byte)Midi_Status.SysExStart;
        into[1] = (byte)Midi_SysExId.UniversalNonRealTime;
        into[2] = (byte)Midi_SysExDeviceId.AllCall;
        into[3] = (byte)Midi_UniversalNonRealTimeSubId1.GeneralInformation;
        into[4] = (byte)Midi_GeneralInformationSubId2.IdentityRequest;
        into[5] = (byte)Midi_Status.SysExEnd;
    }

    /// <summary>True if <paramref name="sysex"/> (F0..F7 inclusive) is an Identity Reply; the device-id byte is ignored.</summary>
    public static bool TryParse(ReadOnlySpan<byte> sysex, out MidiInput_DeviceIdentity identity)
    {
        identity = null!;
        if (sysex.Length < MinReplyLength) return false;
        if (sysex[0] != (byte)Midi_Status.SysExStart) return false;
        if (sysex[1] != (byte)Midi_SysExId.UniversalNonRealTime) return false;
        if (sysex[3] != (byte)Midi_UniversalNonRealTimeSubId1.GeneralInformation) return false;
        if (sysex[4] != (byte)Midi_GeneralInformationSubId2.IdentityReply) return false;
        if (sysex[^1] != (byte)Midi_Status.SysExEnd) return false;

        Midi_ManufacturerId manufacturer;
        int p;
        if (sysex[5] == (byte)Midi_SysExId.ExtendedManufacturer)
        {
            if (sysex.Length < MinReplyLength + 2) return false;
            manufacturer = new Midi_ManufacturerId((sysex[6] << 8) | sysex[7], IsExtended: true);
            p = 8;
        }
        else
        {
            manufacturer = new Midi_ManufacturerId(sysex[5], IsExtended: false);
            p = 6;
        }

        int revisionBytes = sysex.Length - 1 - (p + 4);   // after family(2) + member(2), before F7
        if (revisionBytes < 1 || revisionBytes > MaxRevisionBytes) return false;

        ushort family = (ushort)(sysex[p] | (sysex[p + 1] << 7));
        ushort member = (ushort)(sysex[p + 2] | (sysex[p + 3] << 7));
        uint revision = 0;
        for (int i = 0; i < revisionBytes; i++)
            revision |= (uint)(sysex[p + 4 + i] & 0x7F) << (7 * i);

        identity = new MidiInput_DeviceIdentity(manufacturer, family, member, revision);
        return true;
    }
}