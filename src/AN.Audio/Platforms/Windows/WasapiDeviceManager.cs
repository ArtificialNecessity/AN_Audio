using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static AN.Audio.Platforms.Windows.WasapiInterop;

namespace AN.Audio.Platforms.Windows;

/// <summary>
/// Windows device manager using MMDevice API.
/// Implements IMMNotificationClient via manual COM vtable for NativeAOT compatibility.
/// Singleton per process.
/// </summary>
internal sealed unsafe class WasapiDeviceManager : IAudioDeviceManager
{
    private static WasapiDeviceManager? _instance;
    private static readonly object _lock = new();

    private nint _enumerator;
    private nint _notificationClient; // Pointer to our COM object in unmanaged memory
    private GCHandle _selfHandle;
    private bool _disposed;

    // Static vtable (allocated once, lives forever)
    private static nint _vtable;
    private static bool _vtableInitialized;

    public event Action<AudioDeviceInfo?>? DefaultDeviceChanged;
    public event Action<DeviceChangeType, AudioDeviceInfo?>? DeviceListChanged;

    private WasapiDeviceManager()
    {
        InitializeCom();
        CreateNotificationClient();
        RegisterNotifications();
    }

    public static WasapiDeviceManager Instance
    {
        get
        {
            if (_instance != null) return _instance;
            lock (_lock)
            {
                _instance ??= new WasapiDeviceManager();
                return _instance;
            }
        }
    }

    // ── Device Enumeration ──────────────────────────────────────────

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        var devices = new List<AudioDeviceInfo>();

        // Get the default device ID for comparison
        string? defaultId = GetDefaultDeviceId();

        // Enumerate active render endpoints
        nint collection = 0;
        int hr = EnumAudioEndpoints(_enumerator, eRender, DEVICE_STATE_ACTIVE, out collection);
        if (hr < 0 || collection == 0)
            return devices;

        try
        {
            uint count = 0;
            hr = DeviceCollectionGetCount(collection, out count);
            if (hr < 0) return devices;

            for (uint i = 0; i < count; i++)
            {
                nint device = 0;
                hr = DeviceCollectionItem(collection, i, out device);
                if (hr < 0 || device == 0) continue;

                try
                {
                    var info = GetDeviceInfo(device, defaultId);
                    if (info != null)
                        devices.Add(info);
                }
                finally
                {
                    Release(device);
                }
            }
        }
        finally
        {
            Release(collection);
        }

        return devices;
    }

    /// <summary>Get device info for a specific device ID, or null if not found.</summary>
    public AudioDeviceInfo? GetDeviceById(string deviceId)
    {
        nint device = 0;
        int hr = GetDevice(_enumerator, deviceId, out device);
        if (hr < 0 || device == 0) return null;

        try
        {
            string? defaultId = GetDefaultDeviceId();
            return GetDeviceInfo(device, defaultId);
        }
        finally
        {
            Release(device);
        }
    }

    /// <summary>Get the current default device ID, or null.</summary>
    public string? GetDefaultDeviceId()
    {
        nint device = 0;
        int hr = GetDefaultAudioEndpoint(_enumerator, eRender, eConsole, out device);
        if (hr < 0 || device == 0) return null;

        try
        {
            return GetDeviceIdString(device);
        }
        finally
        {
            Release(device);
        }
    }

    // ── COM Initialization ──────────────────────────────────────────

    private void InitializeCom()
    {
        int hr = CoInitializeEx(0, COINIT_MULTITHREADED);
        if (hr < 0 && hr != unchecked((int)0x80010106)) // RPC_E_CHANGED_MODE ok
            Marshal.ThrowExceptionForHR(hr);

        Guid clsid = CLSID_MMDeviceEnumerator;
        Guid iid = IID_IMMDeviceEnumerator;
        hr = CoCreateInstance(ref clsid, 0, CLSCTX_ALL, ref iid, out _enumerator);
        Marshal.ThrowExceptionForHR(hr);
    }

    // ── IMMNotificationClient Implementation ───────────────────────

    // Memory layout of our fake COM object:
    // [0] nint: pointer to vtable
    // [1] nint: reference count (not really used, but COM expects it)
    // [2] nint: GCHandle to this WasapiDeviceManager instance
    private const int ComObjectSize = 3 * 8; // 3 pointer-sized fields

    private void CreateNotificationClient()
    {
        // Initialize static vtable once
        if (!_vtableInitialized)
        {
            // IMMNotificationClient vtable: IUnknown (3) + 5 notification methods = 8 entries
            _vtable = Marshal.AllocHGlobal(8 * sizeof(nint));
            nint* vt = (nint*)_vtable;
            vt[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&QueryInterface;
            vt[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef;
            vt[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&ReleaseRef;
            vt[3] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, int>)&OnDeviceStateChanged;
            vt[4] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&OnDeviceAdded;
            vt[5] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&OnDeviceRemoved;
            vt[6] = (nint)(delegate* unmanaged[Stdcall]<nint, int, int, nint, int>)&OnDefaultDeviceChanged;
            vt[7] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int>)&OnPropertyValueChanged;
            _vtableInitialized = true;
        }

        // Allocate the COM object
        _notificationClient = Marshal.AllocHGlobal(ComObjectSize);
        nint* obj = (nint*)_notificationClient;
        obj[0] = _vtable;  // vtable pointer
        obj[1] = 1;        // ref count

        // Store a GCHandle to ourselves so callbacks can get back to managed code
        _selfHandle = GCHandle.Alloc(this);
        obj[2] = GCHandle.ToIntPtr(_selfHandle);
    }

    private void RegisterNotifications()
    {
        int hr = RegisterEndpointNotificationCallback(_enumerator, _notificationClient);
        Marshal.ThrowExceptionForHR(hr);
    }

    // ── Static COM Callback Methods ─────────────────────────────────

    private static WasapiDeviceManager? GetSelf(nint thisPtr)
    {
        nint* obj = (nint*)thisPtr;
        nint handlePtr = obj[2];
        if (handlePtr == 0) return null;
        var handle = GCHandle.FromIntPtr(handlePtr);
        return handle.Target as WasapiDeviceManager;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int QueryInterface(nint thisPtr, Guid* riid, nint* ppvObject)
    {
        // We only need to support IUnknown and IMMNotificationClient
        *ppvObject = thisPtr;
        return 0; // S_OK
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint AddRef(nint thisPtr)
    {
        nint* obj = (nint*)thisPtr;
        obj[1]++;
        return (uint)obj[1];
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint ReleaseRef(nint thisPtr)
    {
        nint* obj = (nint*)thisPtr;
        obj[1]--;
        return (uint)obj[1];
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnDeviceStateChanged(nint thisPtr, nint deviceIdPtr, uint newState)
    {
        var self = GetSelf(thisPtr);
        if (self == null) return 0;

        try
        {
            string deviceId = Marshal.PtrToStringUni(deviceIdPtr) ?? "";
            var changeType = (newState & DEVICE_STATE_ACTIVE) != 0
                ? DeviceChangeType.Added
                : DeviceChangeType.Removed;

            var info = self.GetDeviceById(deviceId);
            self.DeviceListChanged?.Invoke(changeType, info);
        }
        catch { /* Never let exceptions escape to COM */ }
        return 0; // S_OK
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnDeviceAdded(nint thisPtr, nint deviceIdPtr)
    {
        var self = GetSelf(thisPtr);
        if (self == null) return 0;

        try
        {
            string deviceId = Marshal.PtrToStringUni(deviceIdPtr) ?? "";
            var info = self.GetDeviceById(deviceId);
            self.DeviceListChanged?.Invoke(DeviceChangeType.Added, info);
        }
        catch { /* Never let exceptions escape to COM */ }
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnDeviceRemoved(nint thisPtr, nint deviceIdPtr)
    {
        var self = GetSelf(thisPtr);
        if (self == null) return 0;

        try
        {
            string deviceId = Marshal.PtrToStringUni(deviceIdPtr) ?? "";
            // Device is already removed, so we can't query it — create minimal info
            var info = new AudioDeviceInfo(deviceId, deviceId, false);
            self.DeviceListChanged?.Invoke(DeviceChangeType.Removed, info);
        }
        catch { /* Never let exceptions escape to COM */ }
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnDefaultDeviceChanged(nint thisPtr, int flow, int role, nint deviceIdPtr)
    {
        // We only care about render (output) devices, console role
        if (flow != eRender || role != eConsole) return 0;

        var self = GetSelf(thisPtr);
        if (self == null) return 0;

        try
        {
            AudioDeviceInfo? info = null;
            if (deviceIdPtr != 0)
            {
                string deviceId = Marshal.PtrToStringUni(deviceIdPtr) ?? "";
                info = self.GetDeviceById(deviceId);
            }
            self.DefaultDeviceChanged?.Invoke(info);
        }
        catch { /* Never let exceptions escape to COM */ }
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnPropertyValueChanged(nint thisPtr, nint deviceIdPtr, nint key)
    {
        // We don't currently use property change notifications
        return 0;
    }

    // ── Helper Methods ───────────────────────────────────────────────

    private AudioDeviceInfo? GetDeviceInfo(nint device, string? defaultId)
    {
        string? id = GetDeviceIdString(device);
        if (id == null) return null;

        string name = GetDeviceFriendlyName(device) ?? id;
        bool isDefault = string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase);

        return new AudioDeviceInfo(id, name, isDefault);
    }

    private static string? GetDeviceIdString(nint device)
    {
        nint idPtr = 0;
        int hr = DeviceGetId(device, out idPtr);
        if (hr < 0 || idPtr == 0) return null;

        try
        {
            return Marshal.PtrToStringUni(idPtr);
        }
        finally
        {
            CoTaskMemFree(idPtr);
        }
    }

    private static string? GetDeviceFriendlyName(nint device)
    {
        nint propertyStore = 0;
        int hr = DeviceOpenPropertyStore(device, STGM_READ, out propertyStore);
        if (hr < 0 || propertyStore == 0) return null;

        try
        {
            // PKEY_Device_FriendlyName: {A45C254E-DF1C-4EFD-8020-67D146A850E0}, pid 14
            var fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0");
            PROPERTYKEY key = new() { fmtid = fmtid, pid = 14 };

            PROPVARIANT pv = default;
            hr = PropertyStoreGetValue(propertyStore, ref key, out pv);
            if (hr < 0) return null;

            try
            {
                if (pv.vt == VT_LPWSTR && pv.pwszVal != 0)
                    return Marshal.PtrToStringUni(pv.pwszVal);
                return null;
            }
            finally
            {
                PropVariantClear(ref pv);
            }
        }
        finally
        {
            Release(propertyStore);
        }
    }

    // ── Dispose ─────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_enumerator != 0 && _notificationClient != 0)
            UnregisterEndpointNotificationCallback(_enumerator, _notificationClient);

        if (_selfHandle.IsAllocated)
            _selfHandle.Free();

        if (_notificationClient != 0)
        {
            Marshal.FreeHGlobal(_notificationClient);
            _notificationClient = 0;
        }

        if (_enumerator != 0)
        {
            Release(_enumerator);
            _enumerator = 0;
        }
    }
}