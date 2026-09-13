using System.Runtime.InteropServices;
using AN.Audio.Platforms.Windows;
using AN.Audio.Platforms.MacOS;
using AN.Audio.Platforms.Linux;

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
        || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
        || RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

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
        {
            // Spec 70 D2: ASIO only when asked for explicitly (Backend = Asio) or when the selected device id is an asio: id. Auto never
            // escalates to ASIO by itself (it would change the device's global sample rate).
            var asioDriver = ResolveAsioDriver(options, out bool asioRequestedButUnavailable);
            if (asioDriver is not null)
                return new Platforms.Windows.Asio.AsioAudioOutput(format, options, asioDriver);
            var wasapi = new WasapiAudioOutput(format, options);
            if (asioRequestedButUnavailable) wasapi.MarkBackendUnavailable();
            return wasapi;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return new CoreAudioOutput(format, options);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return new AlsaAudioOutput(format, options);

        throw new PlatformNotSupportedException(
            $"AN.Audio has no backend for {RuntimeInformation.OSDescription}");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Platforms.Windows.Asio.Asio_DriverInfo? ResolveAsioDriver(AudioOutputOptions options, out bool requestedButUnavailable)
    {
        requestedButUnavailable = false;
        if (options.Backend == AudioOutput_Backend.Wasapi) return null;
        // A preferred asio: id wins in both Auto and Asio modes.
        if (options.PreferredDevices is { } preferred)
        {
            foreach (string id in preferred)
            {
                if (!Platforms.Windows.Asio.Asio_DriverKey.TryParse(id, out var key)) continue;
                var found = Platforms.Windows.Asio.Asio_DriverRegistry.Find(key);
                if (found is not null) return found;
                if (options.Backend == AudioOutput_Backend.Asio) requestedButUnavailable = true; // named driver not installed → try any, else WASAPI
            }
        }
        if (options.Backend != AudioOutput_Backend.Asio) return null;
        var drivers = Platforms.Windows.Asio.Asio_DriverRegistry.Enumerate();
        if (drivers.Count > 0) { requestedButUnavailable = false; return drivers[0]; }
        requestedButUnavailable = true;
        return null;
    }

    /// <summary>Spec 70 D3: the device manager for a specific backend. <see cref="AudioOutput_Backend.Asio"/> lists installed ASIO drivers as
    /// <c>asio:{CLSID}</c> devices (Windows only; null elsewhere). <see cref="AudioOutput_Backend.Auto"/>/<see cref="AudioOutput_Backend.Wasapi"/> = <see cref="GetDeviceManager()"/>.</summary>
    public static IAudioDeviceManager? GetDeviceManager(AudioOutput_Backend backend) =>
        backend == AudioOutput_Backend.Asio ? (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? Platforms.Windows.Asio.AsioDeviceManager.Instance : null) : GetDeviceManager();

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

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return CoreAudioDeviceManager.Instance;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return AlsaDeviceManager.Instance;

        return null;
    }
}