namespace AN.Audio.Midi.Internal;

/// <summary>
/// Universal Non-Real-Time Identity Request / Reply (SPEC-30 D8; _EXTERNAL_APIS/WinMM_MidiIn.md).
///
/// Request: F0 7E dev 06 01 F7
/// Reply:   F0 7E dev 06 02 mm ff ff dd dd ss ss ss ss [vendor bytes…] F7           (1-byte manufacturer)
///          F0 7E dev 06 02 00 mm mm ff ff dd dd ss ss ss ss [vendor bytes…] F7     (3-byte manufacturer)
///
/// Real hardware is not spec-exact, in BOTH directions:
///  - the M-Audio Oxygen 49 MKV answers with a 3-byte id but only TWO revision bytes (15 bytes total);
///  - the Akai MPK mini IV answers with the standard 4 revision bytes FOLLOWED by 20 vendor bytes (an ASCII serial number), 35 bytes total.
/// The parser is therefore LENGTH-AGNOSTIC: it reads the standard prefix (manufacturer + family + member + 1..4 revision bytes, whatever
/// is available before F7 up to four), then hands every byte after the revision to <see cref="Midi_IdentityExtensionParsers"/>, which
/// interprets it per manufacturer (unknown vendors keep the raw bytes). The prefix is what identifies the device type (SPEC-30 D11).
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

    /// <summary>True if <paramref name="sysex"/> (F0..F7 inclusive) is an Identity Reply; the device-id byte is ignored. Any length is
    /// accepted once the standard prefix is present; bytes after the 4th revision byte become <see cref="MidiInput_DeviceIdentity.Extension"/>.</summary>
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

        int bytesAfterMember = sysex.Length - 1 - (p + 4);   // after family(2) + member(2), before F7
        if (bytesAfterMember < 1) return false;               // a reply must carry at least one revision byte
        int revisionBytes = Math.Min(bytesAfterMember, MaxRevisionBytes);

        ushort family = (ushort)(sysex[p] | (sysex[p + 1] << 7));
        ushort member = (ushort)(sysex[p + 2] | (sysex[p + 3] << 7));
        uint revision = 0;
        for (int i = 0; i < revisionBytes; i++)
            revision |= (uint)(sysex[p + 4 + i] & 0x7F) << (7 * i);

        ReadOnlySpan<byte> extension = sysex.Slice(p + 4 + revisionBytes, bytesAfterMember - revisionBytes);
        identity = new MidiInput_DeviceIdentity(manufacturer, family, member, revision)
        {
            Extension = extension.ToArray(),
            SerialNumber = Midi_IdentityExtensionParsers.SerialNumber(manufacturer, extension),
        };
        return true;
    }
}

/// <summary>Per-manufacturer interpretation of the bytes an Identity Reply carries AFTER the standard prefix. The prefix parser never
/// rejects a reply because of these bytes; this class only adds meaning where the vendor's layout is known.</summary>
internal static class Midi_IdentityExtensionParsers
{
    /// <summary>Akai Professional (manufacturer id 0x47). The MPK mini IV appends <c>00 00 00 00</c> + an ASCII serial + <c>00</c>.</summary>
    public const int AkaiManufacturerId = 0x47;

    /// <summary>The device's serial number when the vendor layout is known and the bytes decode; null otherwise.</summary>
    public static string? SerialNumber(Midi_ManufacturerId manufacturer, ReadOnlySpan<byte> extension)
    {
        if (extension.IsEmpty) return null;
        if (!manufacturer.IsExtended && manufacturer.Value == AkaiManufacturerId) return AkaiSerial(extension);
        return null;
    }

    /// <summary>Akai: skip leading zero bytes, take printable ASCII (0x20..0x7E) up to the first NUL. Null if nothing printable.</summary>
    private static string? AkaiSerial(ReadOnlySpan<byte> extension)
    {
        int start = 0;
        while (start < extension.Length && extension[start] == 0) start++;
        int end = start;
        while (end < extension.Length && extension[end] >= 0x20 && extension[end] <= 0x7E) end++;
        return end > start ? System.Text.Encoding.ASCII.GetString(extension[start..end]) : null;
    }
}