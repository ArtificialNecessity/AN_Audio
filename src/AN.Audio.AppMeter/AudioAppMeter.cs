using System.Runtime.InteropServices;
using AN.Audio.AppMeter.Internal;
using AN.Audio.AppMeter.Platforms.MacOS;
using AN.Audio.AppMeter.Platforms.Windows;

namespace AN.Audio.AppMeter;

/// <summary>Factory for the platform-appropriate per-application meter (spec 80). Mirrors <c>AN.Audio.AudioOutput</c> / <c>AN.Audio.Midi.MidiInput</c>.</summary>
public static class AudioAppMeter
{
    /// <summary>True when a backend exists AND reports a capability other than <see cref="AudioAppMeter_Capability.None"/>. Cheap; opens no OS handles.</summary>
    public static bool IsAvailable => Capability != AudioAppMeter_Capability.None;

    /// <summary>What this OS can do, decided from the OS identity alone (no handles opened). A backend may still downgrade to None at <see cref="Create"/> time (D7).</summary>
    public static AudioAppMeter_Capability Capability
    {
        get
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return AudioAppMeter_Capability.Metering;
            return AudioAppMeter_Capability.None;   // Linux: Phase 2 (libpulse); macOS: Phase 3 stub
        }
    }

    /// <summary>Why <see cref="Capability"/> is None on this OS; <see cref="AudioAppMeter_UnavailableReason.None"/> when it isn't.</summary>
    public static AudioAppMeter_UnavailableReason UnavailableReason
    {
        get
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return AudioAppMeter_UnavailableReason.None;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return CoreAudioTap_AppMeter.ProbeReason;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return AudioAppMeter_UnavailableReason.NotImplemented;
            return AudioAppMeter_UnavailableReason.UnsupportedOS;
        }
    }

    /// <summary>
    /// Creates and STARTS a meter (its poll thread runs until Dispose).
    /// </summary>
    /// <exception cref="AudioAppMeter_UnavailableException"><see cref="Capability"/> is None on this OS.</exception>
    public static IAudioAppMeter Create(AudioAppMeter_Options? options = null)
    {
        options ??= new AudioAppMeter_Options();
        if (options.PollIntervalMs <= 0) throw new ArgumentOutOfRangeException(nameof(options), "PollIntervalMs must be positive");
        if (options.ReenumerateIntervalMs <= 0) throw new ArgumentOutOfRangeException(nameof(options), "ReenumerateIntervalMs must be positive");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Wasapi_AppMeter.Create(options);

        var reason = UnavailableReason;
        throw new AudioAppMeter_UnavailableException(reason,
            $"AN.Audio.AppMeter has no per-application metering backend for {RuntimeInformation.OSDescription} ({reason})");
    }

    /// <summary>Like <see cref="Create"/> but never throws: returns a <see cref="AudioAppMeter_Capability.None"/> meter carrying the reason instead.</summary>
    public static IAudioAppMeter CreateOrNull(AudioAppMeter_Options? options = null)
    {
        try { return Create(options); }
        catch (AudioAppMeter_UnavailableException e) { return new NullAppMeter(e.Reason); }
    }
}