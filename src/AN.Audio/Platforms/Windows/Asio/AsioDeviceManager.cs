using System.Runtime.Versioning;

namespace AN.Audio.Platforms.Windows.Asio;

/// <summary>
/// Spec 70 D3: ASIO drivers presented as <see cref="AudioDeviceInfo"/> (<c>asio:{CLSID}</c> ids). The registry is static, so
/// <see cref="DeviceListChanged"/>/<see cref="DefaultDeviceChanged"/> never fire; "default" is simply the first driver by name.
/// Obtained via <see cref="AudioOutput.GetDeviceManager(AudioOutput_Backend)"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class AsioDeviceManager : IAudioDeviceManager
{
    public static AsioDeviceManager Instance { get; } = new();
    private AsioDeviceManager() { }

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        var drivers = Asio_DriverRegistry.Enumerate();
        var list = new List<AudioDeviceInfo>(drivers.Count);
        for (int i = 0; i < drivers.Count; i++) list.Add(new(drivers[i].Key.ToDeviceId(), drivers[i].Name + " (ASIO)", isDefault: i == 0));
        return list;
    }

    public event Action<AudioDeviceInfo?>? DefaultDeviceChanged { add { } remove { } }
    public event Action<DeviceChangeType, AudioDeviceInfo?>? DeviceListChanged { add { } remove { } }

    public void Dispose() { }
}