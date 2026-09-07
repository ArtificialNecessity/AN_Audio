namespace AN.Audio.Midi.Internal;

/// <summary>
/// Universal Non-Real-Time Identity Request / Reply (SPEC-30 D8; _EXTERNAL_APIS/WinMM_MidiIn.md).
///
/// Request: F0 7E dev 06 01 F7
/// Reply:   F0 7E dev 06 02 mm ff ff dd dd ss ss ss ss F7           (1-byte manufacturer, 15 bytes)
///          F0 7E dev 06 02 00 mm mm ff ff dd dd ss ss ss ss F7     (3-byte manufacturer, 17 bytes)
/// </summary>
internal static class Midi_IdentityReplyParser
{
    public const int RequestLength = 6;
    private const int ReplyLengthShortId = 15;
    private const int ReplyLengthExtendedId = 17;

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
        if (sysex.Length < ReplyLengthShortId) return false;
        if (sysex[0] != (byte)Midi_Status.SysExStart) return false;
        if (sysex[1] != (byte)Midi_SysExId.UniversalNonRealTime) return false;
        if (sysex[3] != (byte)Midi_UniversalNonRealTimeSubId1.GeneralInformation) return false;
        if (sysex[4] != (byte)Midi_GeneralInformationSubId2.IdentityReply) return false;
        if (sysex[^1] != (byte)Midi_Status.SysExEnd) return false;

        Midi_ManufacturerId manufacturer;
        int p;
        if (sysex[5] == (byte)Midi_SysExId.ExtendedManufacturer)
        {
            if (sysex.Length < ReplyLengthExtendedId) return false;
            manufacturer = new Midi_ManufacturerId((sysex[6] << 8) | sysex[7], IsExtended: true);
            p = 8;
        }
        else
        {
            manufacturer = new Midi_ManufacturerId(sysex[5], IsExtended: false);
            p = 6;
        }

        if (sysex.Length < p + 8 + 1) return false;   // family(2) member(2) revision(4) F7

        ushort family = (ushort)(sysex[p] | (sysex[p + 1] << 7));
        ushort member = (ushort)(sysex[p + 2] | (sysex[p + 3] << 7));
        uint revision = (uint)(sysex[p + 4] | (sysex[p + 5] << 7) | (sysex[p + 6] << 14) | (sysex[p + 7] << 21));

        identity = new MidiInput_DeviceIdentity(manufacturer, family, member, revision);
        return true;
    }
}