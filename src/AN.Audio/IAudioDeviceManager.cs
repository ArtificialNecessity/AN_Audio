namespace AN.Audio;

/// <summary>
/// Provides device enumeration and change notification for audio output devices.
/// Obtained via <see cref="AudioOutput.GetDeviceManager()"/>.
/// Singleton per process — there is one set of hardware devices.
/// </summary>
public interface IAudioDeviceManager : IDisposable
{
    /// <summary>
    /// Returns all currently active audio output devices.
    /// The list includes a device marked IsDefault = true (if any device is available).
    /// This is a snapshot — call again to refresh, or subscribe to events.
    /// </summary>
    IReadOnlyList<AudioDeviceInfo> GetOutputDevices();

    /// <summary>
    /// Fired when the default output device changes.
    /// Includes the new default device info (or null if no device available).
    /// Fired on a background thread — consumer must marshal to UI if needed.
    /// </summary>
    event Action<AudioDeviceInfo?>? DefaultDeviceChanged;

    /// <summary>
    /// Fired when a device is added (plugged in) or removed (unplugged).
    /// Consumer should call GetOutputDevices() to get the updated list.
    /// Fired on a background thread.
    /// </summary>
    event Action<DeviceChangeType, AudioDeviceInfo?>? DeviceListChanged;
}