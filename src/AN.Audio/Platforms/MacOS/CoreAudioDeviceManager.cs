using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static AN.Audio.Platforms.MacOS.AudioToolboxInterop;
using static AN.Audio.Platforms.MacOS.CoreAudioInterop;

namespace AN.Audio.Platforms.MacOS;

/// <summary>
/// macOS device manager using CoreAudio HAL property listeners.
/// Implements device enumeration and change notification via AudioObjectAddPropertyListener.
/// Singleton per process.
/// </summary>
internal sealed unsafe class CoreAudioDeviceManager : IAudioDeviceManager
{
    private static CoreAudioDeviceManager? _instance;
    private static readonly object _lock = new();

    private GCHandle _selfHandle;
    private bool _disposed;

    // Stored addresses for unregistration
    private AudioObjectPropertyAddress _defaultDeviceAddr;
    private AudioObjectPropertyAddress _devicesAddr;

    // Function pointer for the property listener callback
    private static readonly delegate* unmanaged[Cdecl]<uint, uint, AudioObjectPropertyAddress*, nint, int> _listenerPtr =
        &PropertyListenerCallback;

    public event Action<AudioDeviceInfo?>? DefaultDeviceChanged;
    public event Action<DeviceChangeType, AudioDeviceInfo?>? DeviceListChanged;

    private CoreAudioDeviceManager()
    {
        _selfHandle = GCHandle.Alloc(this);
        RegisterPropertyListeners();
    }

    public static CoreAudioDeviceManager Instance
    {
        get
        {
            if (_instance != null) return _instance;
            lock (_lock)
            {
                _instance ??= new CoreAudioDeviceManager();
                return _instance;
            }
        }
    }

    // ── Device Enumeration ──────────────────────────────────────────

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        var devices = new List<AudioDeviceInfo>();

        // Get the default output device ID for comparison
        uint defaultDeviceId = GetDefaultOutputDeviceId();

        // Get all device IDs
        uint[] deviceIds = GetAllDeviceIds();

        foreach (uint deviceId in deviceIds)
        {
            // Check if this device has output streams
            if (!HasOutputStreams(deviceId))
                continue;

            // Get the device name
            string? name = GetDeviceName(deviceId);
            if (name == null) continue;

            // Use the AudioDeviceID as the string identifier
            string id = deviceId.ToString();
            bool isDefault = deviceId == defaultDeviceId;

            devices.Add(new AudioDeviceInfo(id, name, isDefault));
        }

        return devices;
    }

    /// <summary>Get device info for a specific device ID string, or null if not found.</summary>
    public AudioDeviceInfo? GetDeviceById(string deviceIdStr)
    {
        if (!uint.TryParse(deviceIdStr, out uint deviceId))
            return null;

        string? name = GetDeviceName(deviceId);
        if (name == null) return null;

        uint defaultId = GetDefaultOutputDeviceId();
        return new AudioDeviceInfo(deviceIdStr, name, deviceId == defaultId);
    }

    /// <summary>Get the current default output device ID as a string, or null.</summary>
    public string? GetDefaultDeviceIdString()
    {
        uint deviceId = GetDefaultOutputDeviceId();
        return deviceId == 0 ? null : deviceId.ToString();
    }

    // ── Property Listeners ──────────────────────────────────────────

    private void RegisterPropertyListeners()
    {
        nint clientData = GCHandle.ToIntPtr(_selfHandle);

        // Listen for default output device changes
        _defaultDeviceAddr = new AudioObjectPropertyAddress
        {
            mSelector = kAudioHardwarePropertyDefaultOutputDevice,
            mScope = kAudioObjectPropertyScopeGlobal,
            mElement = kAudioObjectPropertyElementMain,
        };

        fixed (AudioObjectPropertyAddress* addr = &_defaultDeviceAddr)
        {
            AudioObjectAddPropertyListener(
                kAudioObjectSystemObject,
                addr,
                (nint)_listenerPtr,
                clientData);
        }

        // Listen for device list changes (add/remove)
        _devicesAddr = new AudioObjectPropertyAddress
        {
            mSelector = kAudioHardwarePropertyDevices,
            mScope = kAudioObjectPropertyScopeGlobal,
            mElement = kAudioObjectPropertyElementMain,
        };

        fixed (AudioObjectPropertyAddress* addr = &_devicesAddr)
        {
            AudioObjectAddPropertyListener(
                kAudioObjectSystemObject,
                addr,
                (nint)_listenerPtr,
                clientData);
        }
    }

    private void UnregisterPropertyListeners()
    {
        nint clientData = GCHandle.ToIntPtr(_selfHandle);

        fixed (AudioObjectPropertyAddress* addr = &_defaultDeviceAddr)
        {
            AudioObjectRemovePropertyListener(
                kAudioObjectSystemObject,
                addr,
                (nint)_listenerPtr,
                clientData);
        }

        fixed (AudioObjectPropertyAddress* addr = &_devicesAddr)
        {
            AudioObjectRemovePropertyListener(
                kAudioObjectSystemObject,
                addr,
                (nint)_listenerPtr,
                clientData);
        }
    }

    /// <summary>
    /// Static callback invoked by CoreAudio on an internal thread when a property changes.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int PropertyListenerCallback(
        uint inObjectID,
        uint inNumberAddresses,
        AudioObjectPropertyAddress* inAddresses,
        nint inClientData)
    {
        if (inClientData == 0) return 0;

        var handle = GCHandle.FromIntPtr(inClientData);
        if (!handle.IsAllocated) return 0;

        var self = handle.Target as CoreAudioDeviceManager;
        if (self == null || self._disposed) return 0;

        for (uint i = 0; i < inNumberAddresses; i++)
        {
            uint selector = inAddresses[i].mSelector;

            if (selector == kAudioHardwarePropertyDefaultOutputDevice)
            {
                self.OnDefaultDevicePropertyChanged();
            }
            else if (selector == kAudioHardwarePropertyDevices)
            {
                self.OnDeviceListPropertyChanged();
            }
        }

        return 0; // noErr
    }

    private void OnDefaultDevicePropertyChanged()
    {
        uint newDefaultId = GetDefaultOutputDeviceId();
        AudioDeviceInfo? info = null;

        if (newDefaultId != 0)
        {
            string? name = GetDeviceName(newDefaultId);
            if (name != null)
                info = new AudioDeviceInfo(newDefaultId.ToString(), name, isDefault: true);
        }

        DefaultDeviceChanged?.Invoke(info);
    }

    private void OnDeviceListPropertyChanged()
    {
        // CoreAudio doesn't tell us which device was added/removed in the callback.
        // We fire a generic StateChanged event — consumers should call GetOutputDevices().
        DeviceListChanged?.Invoke(DeviceChangeType.StateChanged, null);
    }

    // ── CoreAudio HAL Helpers ────────────────────────────────────────

    private static uint GetDefaultOutputDeviceId()
    {
        var addr = new AudioObjectPropertyAddress
        {
            mSelector = kAudioHardwarePropertyDefaultOutputDevice,
            mScope = kAudioObjectPropertyScopeGlobal,
            mElement = kAudioObjectPropertyElementMain,
        };

        uint dataSize = (uint)sizeof(uint);
        uint deviceId = 0;

        int status = AudioObjectGetPropertyData(
            kAudioObjectSystemObject,
            &addr,
            0, 0,
            ref dataSize,
            &deviceId);

        return status == noErr ? deviceId : 0;
    }

    private static uint[] GetAllDeviceIds()
    {
        var addr = new AudioObjectPropertyAddress
        {
            mSelector = kAudioHardwarePropertyDevices,
            mScope = kAudioObjectPropertyScopeGlobal,
            mElement = kAudioObjectPropertyElementMain,
        };

        uint dataSize = 0;
        int status = AudioObjectGetPropertyDataSize(
            kAudioObjectSystemObject,
            &addr,
            0, 0,
            out dataSize);

        if (status != noErr || dataSize == 0)
            return [];

        int count = (int)(dataSize / sizeof(uint));
        uint[] ids = new uint[count];

        fixed (uint* ptr = ids)
        {
            status = AudioObjectGetPropertyData(
                kAudioObjectSystemObject,
                &addr,
                0, 0,
                ref dataSize,
                ptr);
        }

        return status == noErr ? ids : [];
    }

    private static bool HasOutputStreams(uint deviceId)
    {
        var addr = new AudioObjectPropertyAddress
        {
            mSelector = kAudioDevicePropertyStreams,
            mScope = kAudioObjectPropertyScopeOutput,
            mElement = kAudioObjectPropertyElementMain,
        };

        uint dataSize = 0;
        int status = AudioObjectGetPropertyDataSize(
            deviceId,
            &addr,
            0, 0,
            out dataSize);

        return status == noErr && dataSize > 0;
    }

    private static string? GetDeviceName(uint deviceId)
    {
        var addr = new AudioObjectPropertyAddress
        {
            mSelector = kAudioObjectPropertyName,
            mScope = kAudioObjectPropertyScopeGlobal,
            mElement = kAudioObjectPropertyElementMain,
        };

        uint dataSize = (uint)sizeof(nint);
        nint cfString = 0;

        int status = AudioObjectGetPropertyData(
            deviceId,
            &addr,
            0, 0,
            ref dataSize,
            &cfString);

        if (status != noErr || cfString == 0)
            return null;

        try
        {
            return CFStringToString(cfString);
        }
        finally
        {
            CFRelease(cfString);
        }
    }

    /// <summary>
    /// Get the UID string for a device (used for AudioQueue device selection).
    /// </summary>
    public static string? GetDeviceUID(uint deviceId)
    {
        var addr = new AudioObjectPropertyAddress
        {
            mSelector = kAudioDevicePropertyDeviceUID,
            mScope = kAudioObjectPropertyScopeGlobal,
            mElement = kAudioObjectPropertyElementMain,
        };

        uint dataSize = (uint)sizeof(nint);
        nint cfString = 0;

        int status = AudioObjectGetPropertyData(
            deviceId,
            &addr,
            0, 0,
            ref dataSize,
            &cfString);

        if (status != noErr || cfString == 0)
            return null;

        try
        {
            return CFStringToString(cfString);
        }
        finally
        {
            CFRelease(cfString);
        }
    }

    // ── Dispose ─────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        UnregisterPropertyListeners();

        if (_selfHandle.IsAllocated)
            _selfHandle.Free();
    }
}
