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
    /// Create the platform-appropriate audio output.
    /// </summary>
    /// <param name="format">Desired PCM format.</param>
    /// <param name="bufferSizeMs">Desired buffer size in milliseconds (affects latency).</param>
    /// <returns>An IAudioOutput ready to Start().</returns>
    /// <exception cref="PlatformNotSupportedException">No backend for this OS.</exception>
    public static IAudioOutput Create(AudioFormat format, int bufferSizeMs = 20)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new WasapiAudioOutput(format, bufferSizeMs);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return new CoreAudioOutput(format, bufferSizeMs);

        // Future:
        // if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        //     return new AlsaAudioOutput(format, bufferSizeMs);

        throw new PlatformNotSupportedException(
            $"AN.Audio has no backend for {RuntimeInformation.OSDescription}");
    }
}