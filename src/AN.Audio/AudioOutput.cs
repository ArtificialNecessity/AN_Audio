using System.Runtime.InteropServices;
using AN.Audio.Platforms.Windows;
using AN.Audio.Platforms.MacOS;

namespace AN.Audio;

/// <summary>
/// Factory for creating the platform-appropriate audio output.
/// </summary>
public static class AudioOutput
{
    /// <summary>
    /// Returns true if the current platform has a working audio backend.
    /// Use this to gracefully degrade on platforms where audio is not yet implemented.
    /// </summary>
    public static bool IsAvailable =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        // Future:
        // || RuntimeInformation.IsOSPlatform(OSPlatform.Linux)

    /// <summary>
    /// Create the platform-appropriate audio output with default options.
    /// The consumer's callback always works in the specified format — AN.Audio
    /// handles format conversion to the device's native format internally.
    /// Default switch policy is <see cref="AudioSwitchPolicy.FollowDefault"/>.
    /// </summary>
    /// <param name="format">Desired PCM format for the consumer callback.</param>
    /// <param name="bufferSizeMs">Desired buffer size in milliseconds (affects latency).</param>
    /// <returns>An IAudioOutput ready to Start().</returns>
    /// <exception cref="PlatformNotSupportedException">No backend for this OS.</exception>
    public static IAudioOutput Create(AudioFormat format, int bufferSizeMs = 20)
    {
        return Create(format, new AudioOutputOptions { BufferSizeMs = bufferSizeMs });
    }

    /// <summary>
    /// Create the platform-appropriate audio output with explicit options.
    /// The consumer's callback always works in the specified format — AN.Audio
    /// handles format conversion to the device's native format internally.
    /// </summary>
    /// <param name="format">Desired PCM format for the consumer callback.</param>
    /// <param name="options">Options controlling device selection, switch policy, and buffer size.</param>
    /// <returns>An IAudioOutput ready to Start().</returns>
    /// <exception cref="PlatformNotSupportedException">No backend for this OS.</exception>
    public static IAudioOutput Create(AudioFormat format, AudioOutputOptions options)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new WasapiAudioOutput(format, options);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return new CoreAudioOutput(format, options?.BufferSizeMs ?? 20);

        // Future:
        // if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        //     return new AlsaAudioOutput(format, options);

        throw new PlatformNotSupportedException(
            $"AN.Audio has no backend for {RuntimeInformation.OSDescription}");
    }

    /// <summary>
    /// Get the platform device manager for querying available audio devices
    /// and subscribing to device change notifications.
    /// Singleton per process — there is one set of hardware devices.
    /// Returns null on platforms where device management is not yet implemented.
    /// </summary>
    public static IAudioDeviceManager? GetDeviceManager()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return WasapiDeviceManager.Instance;

        // TODO: macOS device manager (CoreAudio property listeners)
        // TODO: Linux device manager (ALSA device hints + udev)
        return null;
    }
}