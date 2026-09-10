using System.Runtime.InteropServices;
using AN.Audio.Midi.Platforms.MacOS;
using AN.Audio.Midi.Platforms.Windows;

namespace AN.Audio.Midi;

/// <summary>Factory for the platform-appropriate MIDI input (SPEC-30). Mirrors <see cref="AN.Audio.AudioOutput"/>.</summary>
public static class MidiInput
{
    /// <summary>True where a MIDI input backend exists (Windows/WinMM; macOS/CoreMIDI). Does not guarantee any port is present.</summary>
    public static bool IsAvailable => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    /// <exception cref="PlatformNotSupportedException">No backend for this OS.</exception>
    public static IMidiInput Create(MidiInput_Options? options = null)
    {
        options ??= new MidiInput_Options();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new WinMm_MidiInput(options);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return new CoreMidi_MidiInput(options);

        throw new PlatformNotSupportedException($"AN.Audio.Midi has no MIDI input backend for {RuntimeInformation.OSDescription}");
    }

    /// <summary>Singleton per process. Null on platforms without a backend.</summary>
    public static IMidiInput_DeviceManager? GetDeviceManager()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return WinMm_MidiInDeviceManager.Instance;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return CoreMidi_MidiInDeviceManager.Instance;
        return null;
    }
}