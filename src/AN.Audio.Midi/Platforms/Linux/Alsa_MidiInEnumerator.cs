using System.Text.RegularExpressions;

namespace AN.Audio.Midi.Platforms.Linux;

/// <summary>One ALSA rawmidi port as enumerated right now (SPEC-30 D11 analogue for Linux).
/// DevicePath is volatile (card numbers renumber across replug); the Key is built from the USB serial when available.</summary>
internal sealed record Alsa_MidiInDeviceEntry(
    string DevicePath,           // /dev/snd/midiC{card}D{device}
    int Card,
    int Device,
    string Name,
    MidiInput_DeviceKey Key,
    MidiInput_DeviceTypeId FallbackTypeId,
    bool HasPairedOutput)        // rawmidi node has an Output substream (same node, opened R/W)
{
    public MidiInput_DeviceInfo ToDeviceInfo(MidiInput_DeviceIdentity? identity = null) =>
        new(Key, identity is null ? FallbackTypeId : MidiInput_DeviceTypeId.FromIdentity(identity), Name, identity, HasPairedOutput);
}

/// <summary>
/// Enumerates ALSA rawmidi input ports by scanning /dev/snd/midiC*D* and reading names/capabilities
/// from /proc/asound and /sys/class/sound. No libasound dependency; v1 covers subdevice 0 of each
/// rawmidi device (one /dev node per (card, device) pair; extra subdevices need an ioctl to select).
/// </summary>
internal static partial class Alsa_MidiInEnumerator
{
    [GeneratedRegex(@"^midiC(\d+)D(\d+)$")]
    private static partial Regex MidiNodeRegex();

    public static List<Alsa_MidiInDeviceEntry> EnumerateInputs()
    {
        var result = new List<Alsa_MidiInDeviceEntry>();
        string[] nodes;
        try { nodes = Directory.GetFiles("/dev/snd", "midiC*D*"); }
        catch { return result; }

        // Same dedup rule as WinMM: whatever the base key is, duplicates get an ordinal suffix.
        var ordinalByBaseKey = new Dictionary<string, int>(StringComparer.Ordinal);
        Array.Sort(nodes, StringComparer.Ordinal);   // stable ordinals

        foreach (var path in nodes)
        {
            var m = MidiNodeRegex().Match(Path.GetFileName(path));
            if (!m.Success) continue;
            int card = int.Parse(m.Groups[1].Value);
            int device = int.Parse(m.Groups[2].Value);

            var (name, hasInput, hasOutput) = ReadRawMidiProcInfo(card, device);
            if (!hasInput) continue;   // output-only rawmidi port; nothing to receive
            name ??= $"rawmidi C{card}D{device}";

            string cardId = ReadTrimmedOrNull($"/sys/class/sound/card{card}/id") ?? card.ToString();
            string? usbSerial = ReadUsbSerial(card);

            string baseKey = usbSerial is not null
                ? $"alsa-serial:{usbSerial}|{name}|D{device}"
                : $"alsa:{cardId}|{name}|D{device}";

            ordinalByBaseKey.TryGetValue(baseKey, out int ordinal);
            ordinalByBaseKey[baseKey] = ordinal + 1;
            string keyText = ordinal == 0 ? baseKey : $"{baseKey}#{ordinal}";

            result.Add(new Alsa_MidiInDeviceEntry(
                DevicePath: path,
                Card: card,
                Device: device,
                Name: name,
                Key: new MidiInput_DeviceKey(keyText),
                FallbackTypeId: MidiInput_DeviceTypeId.UnknownFromDriverStrings(name, cardId, null),
                HasPairedOutput: hasOutput));
        }
        return result;
    }

    /// <summary>Parse /proc/asound/card{c}/midi{d}: first line is the rawmidi name; "Output "/"Input " section
    /// headers reveal the directions the device supports. Missing file => assume input-capable, no output.</summary>
    private static (string? Name, bool HasInput, bool HasOutput) ReadRawMidiProcInfo(int card, int device)
    {
        try
        {
            string? name = null;
            bool hasInput = false, hasOutput = false;
            foreach (var raw in File.ReadLines($"/proc/asound/card{card}/midi{device}"))
            {
                var line = raw.TrimEnd();
                if (name is null && line.Length > 0) { name = line.Trim(); continue; }
                if (line.StartsWith("Input", StringComparison.Ordinal)) hasInput = true;
                else if (line.StartsWith("Output", StringComparison.Ordinal)) hasOutput = true;
            }
            return (name, hasInput, hasOutput);
        }
        catch
        {
            return (null, true, false);
        }
    }

    /// <summary>USB serial of the card's parent device, when the card is USB. /sys/class/sound/card{c}/device
    /// symlinks to the USB interface; the serial attribute lives on the parent USB device.</summary>
    private static string? ReadUsbSerial(int card)
    {
        return ReadTrimmedOrNull($"/sys/class/sound/card{card}/device/../serial")
            ?? ReadTrimmedOrNull($"/sys/class/sound/card{card}/device/../../serial");
    }

    private static string? ReadTrimmedOrNull(string path)
    {
        try
        {
            var text = File.ReadAllText(path).Trim();
            return text.Length == 0 ? null : text;
        }
        catch { return null; }
    }
}