namespace AN.Audio.Midi.Platforms.Windows;

#pragma warning disable AN0100 // nuint for UINT_PTR device ids

/// <summary>One WinMM input port as enumerated right now. The Index is volatile (renumbers on unplug); the Key is not (D11).</summary>
internal sealed record WinMm_MidiInDeviceEntry(
    uint Index,
    string Name,
    ushort DriverManufacturerId,
    ushort DriverProductId,
    MidiInput_DeviceKey Key,
    MidiInput_DeviceTypeId FallbackTypeId,
    bool HasPairedOutput)
{
    public MidiInput_DeviceInfo ToDeviceInfo(MidiInput_DeviceIdentity? identity = null) =>
        new(Key, identity is null ? FallbackTypeId : MidiInput_DeviceTypeId.FromIdentity(identity), Name, identity, HasPairedOutput);
}

/// <summary>Enumerates WinMM MIDI ports and builds stable keys (SPEC-30 D11).</summary>
internal static unsafe class WinMm_MidiInEnumerator
{
    public static List<WinMm_MidiInDeviceEntry> EnumerateInputs()
    {
        uint count = WinMm_MidiInterop.midiInGetNumDevs();
        var outputNames = EnumerateOutputNames();
        var result = new List<WinMm_MidiInDeviceEntry>((int)count);
        // Some drivers report the SAME NameGuid for every port of a multi-port device (observed 2026-09-07),
        // so whatever the base key is, duplicates get an ordinal suffix. Same-name ordinal is a special case of this.
        var ordinalByBaseKey = new Dictionary<string, int>(StringComparer.Ordinal);

        for (uint i = 0; i < count; i++)
        {
            WinMm_MidiInCaps2W caps = default;
            var r = WinMm_MidiInterop.midiInGetDevCapsW(i, &caps, WinMm_MidiInterop.MidiInCaps2Size);
            if (r != WinMm_Result.NoError) continue;   // vanished between GetNumDevs and here

            string name = caps.Name;

            string baseKey;
            if (caps.NameGuid != Guid.Empty)
                baseKey = $"nameguid:{caps.NameGuid:D}|{name}";
            else if (caps.ProductGuid != Guid.Empty)
                baseKey = $"productguid:{caps.ProductGuid:D}|{name}";
            else
                baseKey = $"caps:{name}|{caps.wMid}|{caps.wPid}";

            ordinalByBaseKey.TryGetValue(baseKey, out int ordinal);
            ordinalByBaseKey[baseKey] = ordinal + 1;
            string keyText = ordinal == 0 ? baseKey : $"{baseKey}#{ordinal}";

            result.Add(new WinMm_MidiInDeviceEntry(
                Index: i,
                Name: name,
                DriverManufacturerId: caps.wMid,
                DriverProductId: caps.wPid,
                Key: new MidiInput_DeviceKey(keyText),
                FallbackTypeId: MidiInput_DeviceTypeId.UnknownFromDriverCaps(name, caps.wMid, caps.wPid),
                HasPairedOutput: outputNames.Contains(name)));
        }
        return result;
    }

    /// <summary>Output port index whose szPname equals <paramref name="inputPortName"/>, or null (pairing convention; see USB notes).</summary>
    public static uint? FindPairedOutputIndex(string inputPortName)
    {
        uint count = WinMm_MidiInterop.midiOutGetNumDevs();
        for (uint i = 0; i < count; i++)
        {
            WinMm_MidiOutCapsW caps = default;
            if (WinMm_MidiInterop.midiOutGetDevCapsW(i, &caps, WinMm_MidiInterop.MidiOutCapsSize) != WinMm_Result.NoError) continue;
            if (string.Equals(caps.Name, inputPortName, StringComparison.Ordinal)) return i;
        }
        return null;
    }

    private static HashSet<string> EnumerateOutputNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        uint count = WinMm_MidiInterop.midiOutGetNumDevs();
        for (uint i = 0; i < count; i++)
        {
            WinMm_MidiOutCapsW caps = default;
            if (WinMm_MidiInterop.midiOutGetDevCapsW(i, &caps, WinMm_MidiInterop.MidiOutCapsSize) == WinMm_Result.NoError)
                names.Add(caps.Name);
        }
        return names;
    }
}

#pragma warning restore AN0100