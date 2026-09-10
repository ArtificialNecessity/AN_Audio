using System.Text.RegularExpressions;

namespace AN.Audio.Midi.Platforms.Linux;

/// <summary>One ALSA rawmidi input substream as enumerated right now (SPEC-30 D40/D44). One entry per cable of a multi-port
/// device. DevicePath/Card are volatile (card numbers renumber across replug); the Key is built from the USB serial when available.</summary>
internal sealed record Alsa_MidiInDeviceEntry(
    string DevicePath,           // /dev/snd/midiC{card}D{device}
    string ControlPath,          // /dev/snd/controlC{card} — PREFER_SUBDEVICE target
    int Card,
    int Device,
    int Subdevice,               // input substream index; 0 = the node's default
    string Name,
    MidiInput_DeviceKey Key,
    MidiInput_DeviceTypeId FallbackTypeId,
    bool HasPairedOutput)        // an Output substream with the same index exists (same node, opened R/W)
{
    public MidiInput_DeviceInfo ToDeviceInfo(MidiInput_DeviceIdentity? identity = null) =>
        new(Key, identity is null ? FallbackTypeId : MidiInput_DeviceTypeId.FromIdentity(identity), Name, identity, HasPairedOutput);
}

/// <summary>
/// Enumerates ALSA rawmidi input substreams by scanning /dev/snd/midiC*D* and reading names/substream counts
/// from /proc/asound and the USB serial from /sys/class/sound. No libasound dependency. A device with N input
/// substreams (a multi-cable USB controller) yields N entries sharing one /dev node; the port selects the
/// substream with SNDRV_CTL_IOCTL_RAWMIDI_PREFER_SUBDEVICE before open() (D44).
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

            var (name, inputs, outputs) = ReadRawMidiProcInfo(card, device);
            if (inputs == 0) continue;   // output-only rawmidi device; nothing to receive
            name ??= $"rawmidi C{card}D{device}";

            string cardId = ReadTrimmedOrNull($"/sys/class/sound/card{card}/id") ?? card.ToString();
            string? usbSerial = ReadUsbSerial(card);
            string controlPath = $"/dev/snd/controlC{card}";

            for (int sub = 0; sub < inputs; sub++)
            {
                string portName = sub == 0 ? name : $"{name} [{sub + 1}]";   // cable numbering 1-based, like WinMM's "MIDIIN2 (...)"
                string baseKey = usbSerial is not null
                    ? $"alsa-serial:{usbSerial}|{name}|D{device}S{sub}"
                    : $"alsa:{cardId}|{name}|D{device}S{sub}";

                ordinalByBaseKey.TryGetValue(baseKey, out int ordinal);
                ordinalByBaseKey[baseKey] = ordinal + 1;
                string keyText = ordinal == 0 ? baseKey : $"{baseKey}#{ordinal}";

                result.Add(new Alsa_MidiInDeviceEntry(
                    DevicePath: path,
                    ControlPath: controlPath,
                    Card: card,
                    Device: device,
                    Subdevice: sub,
                    Name: portName,
                    Key: new MidiInput_DeviceKey(keyText),
                    FallbackTypeId: MidiInput_DeviceTypeId.UnknownFromDriverStrings(portName, cardId, null),
                    HasPairedOutput: sub < outputs));   // output substream with the same index → same node opened R/W
            }
        }
        return result;
    }

    /// <summary>Parse /proc/asound/card{c}/midi{d}: first line is the rawmidi name; each "Input i" / "Output i" section header
    /// is one substream. Missing file => assume one input substream, no output.</summary>
    private static (string? Name, int Inputs, int Outputs) ReadRawMidiProcInfo(int card, int device)
    {
        try
        {
            string? name = null;
            int inputs = 0, outputs = 0;
            foreach (var raw in File.ReadLines($"/proc/asound/card{card}/midi{device}"))
            {
                var line = raw.TrimEnd();
                if (name is null && line.Length > 0) { name = line.Trim(); continue; }
                if (line.StartsWith("Input ", StringComparison.Ordinal)) inputs++;
                else if (line.StartsWith("Output ", StringComparison.Ordinal)) outputs++;
            }
            return (name, inputs, outputs);
        }
        catch
        {
            return (null, 1, 0);
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