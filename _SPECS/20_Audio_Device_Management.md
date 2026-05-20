# SPEC-ANAudio-DeviceManagement: Device Enumeration, Selection & Change Notification

**Status:** Draft  
**Last Updated:** 2026-05-19  
**Parent:** SPEC-ANAudio (Cross-Platform Audio via Direct PInvoke)

- [ ] Milestone 1 — Abstraction layer (`IAudioDeviceManager`, `AudioDeviceInfo`)
- [ ] Milestone 2 — Windows implementation (MMDevice API)
- [ ] Milestone 3 — Linux implementation (ALSA device hints + error recovery)
- [ ] Milestone 4 — macOS implementation (CoreAudio property listeners)
- [ ] Milestone 5 — Wire into `IAudioOutput` — device selection at creation, auto-recovery on device loss

## Overview

This spec extends AN.Audio with device enumeration (list available outputs), device selection (open a specific device instead of always "default"), and device change notification (detect when the user switches headphones, unplugs USB audio, changes system default, etc.).

The goal is event-driven notification with zero polling. Windows and macOS provide proper OS-level callbacks for device changes. Linux ALSA does not — but since modern Linux desktops route ALSA through PipeWire/PulseAudio, the "default" device follows the sound server's routing, and device loss surfaces as a write error that we can handle reactively.

### Use Cases

1. **TTS word-highlight sync (Mirica)** — If the audio device changes mid-stream, the playback must reinitialize on the new device without losing the callback/highlight synchronization. The consumer needs a `DeviceChanged` event to pause the highlight state machine, reinitialize audio, and resume.
2. **Settings UI** — Let the user pick an output device from a dropdown (Mirica preferences, Arcane Siege audio settings).
3. **Hot-plug recovery** — User unplugs USB headset mid-playback. Audio should recover to the new default device automatically or notify the consumer to handle it.

---

## Abstraction Layer

### AudioDeviceInfo

```csharp
// pseudocode — illustrative only

/// <summary>
/// Describes an available audio output device.
/// </summary>
public sealed class AudioDeviceInfo
{
    /// <summary>
    /// Platform-specific device identifier. Opaque string — pass this to
    /// AudioOutput.Create() to open a specific device.
    /// Windows: endpoint ID string (e.g. "{0.0.0.00000000}.{guid}")
    /// Linux: ALSA device name (e.g. "default", "hw:0,0", "plughw:1,0")
    /// macOS: AudioDeviceID as string (e.g. "74")
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Human-readable name for display in UI.
    /// Windows: "Speakers (Realtek High Definition Audio)"
    /// Linux: "HDA Intel PCH, ALC892 Analog"
    /// macOS: "MacBook Pro Speakers"
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// True if this is the current system default output device.
    /// </summary>
    public bool IsDefault { get; }

    /// <summary>
    /// Whether this device supports output (playback).
    /// For V1 this is always true since we only enumerate output devices.
    /// </summary>
    public bool SupportsOutput { get; }
}
```

### IAudioDeviceManager

```csharp
// pseudocode — illustrative only

public interface IAudioDeviceManager : IDisposable
{
    /// <summary>
    /// Returns all currently active audio output devices.
    /// The list includes a device marked IsDefault = true.
    /// This is a snapshot — call again to refresh, or subscribe to events.
    /// </summary>
    IReadOnlyList<AudioDeviceInfo> GetOutputDevices();

    /// <summary>
    /// Fired when the default output device changes.
    /// Includes the new default device info (or null if no device available).
    /// Fired on a background thread — consumer must marshal to UI if needed.
    /// </summary>
    event Action<AudioDeviceInfo?> DefaultDeviceChanged;

    /// <summary>
    /// Fired when a device is added (plugged in) or removed (unplugged).
    /// Consumer should call GetOutputDevices() to get the updated list.
    /// Fired on a background thread.
    /// </summary>
    event Action<DeviceChangeType, AudioDeviceInfo> DeviceListChanged;
}

public enum DeviceChangeType
{
    Added,
    Removed,
    StateChanged,  // e.g. disabled → active
}
```

### Updated IAudioOutput

```csharp
// pseudocode — changes from SPEC-ANAudio

public interface IAudioOutput : IDisposable
{
    // ... existing members from SPEC-ANAudio ...

    /// <summary>
    /// The device this output is connected to. Null if using system default.
    /// </summary>
    AudioDeviceInfo? Device { get; }

    /// <summary>
    /// Fired when the underlying device is lost (unplugged, disabled, or
    /// default changed while using default). The audio stream has stopped.
    /// Consumer should dispose this instance and create a new one.
    /// Fired on the audio thread — keep the handler fast.
    /// </summary>
    event Action<DeviceLostReason> DeviceLost;
}

public enum DeviceLostReason
{
    DeviceRemoved,       // hardware unplugged
    DeviceDisabled,      // user disabled in system settings
    DefaultChanged,      // was using default, and default switched to another device
    StreamError,         // unrecoverable stream error (ALSA -ENODEV, etc.)
}
```

### Updated Factory

```csharp
// pseudocode

public static class AudioOutput
{
    /// <summary>
    /// Create audio output on the system default device.
    /// </summary>
    public static IAudioOutput Create(AudioFormat format, int bufferSizeMs = 20);

    /// <summary>
    /// Create audio output on a specific device.
    /// Pass AudioDeviceInfo.Id from the device manager.
    /// </summary>
    public static IAudioOutput Create(AudioFormat format, string deviceId, int bufferSizeMs = 20);

    /// <summary>
    /// Get the platform device manager. Singleton per process.
    /// </summary>
    public static IAudioDeviceManager GetDeviceManager();
}
```

---

## Platform: Windows (MMDevice API)

Windows has the cleanest device management story. The MMDevice API provides both enumeration and change notification through COM interfaces you already have from SPEC-ANAudio.

### Device Enumeration

Uses `IMMDeviceEnumerator` (already created for `IAudioClient` activation):

```csharp
// pseudocode — device enumeration sequence

// You already have the enumerator from SPEC-ANAudio:
// CoCreateInstance(CLSID_MMDeviceEnumerator, IID_IMMDeviceEnumerator, out enumerator);

// Enumerate all active render endpoints:
enumerator.EnumAudioEndpoints(
    eRender,                    // EDataFlow: output devices only
    DEVICE_STATE_ACTIVE,        // only currently active/plugged devices
    out deviceCollection        // IMMDeviceCollection*
);

deviceCollection.GetCount(out count);

for (int i = 0; i < count; i++)
{
    deviceCollection.Item(i, out device);  // IMMDevice*

    // Get the endpoint ID string (used to reopen this specific device):
    device.GetId(out deviceIdPtr);  // LPWSTR — Marshal.PtrToStringUni

    // Get the friendly name via IPropertyStore:
    device.OpenPropertyStore(STGM_READ, out propertyStore);
    propertyStore.GetValue(PKEY_Device_FriendlyName, out propVariant);
    // propVariant contains the display name string
}
```

To get the current default:

```csharp
// pseudocode
enumerator.GetDefaultAudioEndpoint(eRender, eConsole, out defaultDevice);
defaultDevice.GetId(out defaultId);
// Compare defaultId against enumerated device IDs to set IsDefault
```

### Device Selection

To open a specific device instead of the default, use `IMMDeviceEnumerator.GetDevice()`:

```csharp
// pseudocode — opening a specific device by ID
enumerator.GetDevice(deviceIdString, out device);  // instead of GetDefaultAudioEndpoint
device.Activate(IID_IAudioClient, CLSCTX_ALL, null, out audioClient);
// ... rest of WASAPI init from SPEC-ANAudio
```

### Change Notification

Register an `IMMNotificationClient` callback. This is a COM interface that **you implement** — Windows calls your methods when devices change. For NativeAOT, this means creating a managed object whose vtable Windows can call into.

```csharp
// pseudocode — IMMNotificationClient vtable layout

// IMMNotificationClient inherits IUnknown (3 methods) + 5 notification methods:
struct IMMNotificationClientVtbl
{
    // IUnknown
    nint QueryInterface;    // index 0
    nint AddRef;            // index 1
    nint Release;           // index 2

    // IMMNotificationClient
    nint OnDeviceStateChanged;      // index 3 — (LPCWSTR deviceId, DWORD newState)
    nint OnDeviceAdded;             // index 4 — (LPCWSTR deviceId)
    nint OnDeviceRemoved;           // index 5 — (LPCWSTR deviceId)
    nint OnDefaultDeviceChanged;    // index 6 — (EDataFlow flow, ERole role, LPCWSTR defaultDeviceId)
    nint OnPropertyValueChanged;    // index 7 — (LPCWSTR deviceId, PROPERTYKEY key)
}
```

Implementation approach for NativeAOT:

```csharp
// pseudocode — implementing IMMNotificationClient for native COM callback

// 1. Allocate a block of unmanaged memory laid out as:
//    [vtable_ptr][ref_count][GCHandle_to_managed_callback]
//    where vtable_ptr points to a static vtable of function pointers.

// 2. The static vtable entries are [UnmanagedCallersOnly] static methods:
[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
static int OnDefaultDeviceChanged(nint thisPtr, int flow, int role, nint deviceIdPtr)
{
    if (flow != eRender) return 0;  // we only care about output devices
    string newDeviceId = Marshal.PtrToStringUni(deviceIdPtr);
    // retrieve the managed delegate from GCHandle, invoke it
    return 0; // S_OK
}

// 3. Register with:
enumerator.RegisterEndpointNotificationCallback(notificationClientPtr);

// 4. Unregister on dispose:
enumerator.UnregisterEndpointNotificationCallback(notificationClientPtr);
```

The key event is `OnDefaultDeviceChanged` — it fires with the new default device ID. The `DeviceListChanged` events map from `OnDeviceAdded`, `OnDeviceRemoved`, and `OnDeviceStateChanged`.

### COM Interfaces and IIDs

Additional interfaces beyond SPEC-ANAudio:

```
IID_IMMDeviceCollection:    0BD7A1BE-7A1A-44DB-8397-CC5392387B5E
IID_IMMNotificationClient:  7991EEC9-7E89-4D85-8390-6C703CEC60C0
IID_IPropertyStore:         886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99

PKEY_Device_FriendlyName:   {A45C254E-DF1C-4EFD-8020-67D146A850E0}, pid 14

EDataFlow.eRender:   0
EDataFlow.eCapture:  1
EDataFlow.eAll:      2

ERole.eConsole:         0
ERole.eMultimedia:      1
ERole.eCommunications:  2

DEVICE_STATE_ACTIVE:     0x00000001
DEVICE_STATE_DISABLED:   0x00000002
DEVICE_STATE_NOTPRESENT: 0x00000004
DEVICE_STATE_UNPLUGGED:  0x00000008
```

### Vtable Indices (additional interfaces)

```
IMMDeviceEnumerator (extends IUnknown):
  [3] EnumAudioEndpoints(EDataFlow, DWORD stateMask, out IMMDeviceCollection)
  [4] GetDefaultAudioEndpoint(EDataFlow, ERole, out IMMDevice)
  [5] GetDevice(LPCWSTR id, out IMMDevice)
  [6] RegisterEndpointNotificationCallback(IMMNotificationClient*)
  [7] UnregisterEndpointNotificationCallback(IMMNotificationClient*)

IMMDeviceCollection (extends IUnknown):
  [3] GetCount(out UINT)
  [4] Item(UINT index, out IMMDevice)

IMMDevice (extends IUnknown):
  [3] Activate(REFIID iid, DWORD clsCtx, PROPVARIANT*, out void*)
  [4] OpenPropertyStore(DWORD access, out IPropertyStore)
  [5] GetId(out LPWSTR)
  [6] GetState(out DWORD)

IPropertyStore (extends IUnknown):
  [3] GetCount(out DWORD)
  [4] GetAt(DWORD index, out PROPERTYKEY)
  [5] GetValue(ref PROPERTYKEY, out PROPVARIANT)
  [6] SetValue(ref PROPERTYKEY, ref PROPVARIANT)
  [7] Commit()
```

### References

- [IMMDeviceEnumerator (MSDN)](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-immdeviceenumerator)
- [IMMNotificationClient (MSDN)](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-immnotificationclient)
- [EnumAudioEndpoints (MSDN)](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdeviceenumerator-enumaudioendpoints)
- [Device Properties: PKEY_Device_FriendlyName (MSDN)](https://learn.microsoft.com/en-us/windows/win32/coreaudio/pkey-device-friendlyname)

---

## Platform: Linux (ALSA)

ALSA has device enumeration but no real-time change notification. This is the weakest platform for this feature, but the pragmatic answer works well in practice.

### Device Enumeration

ALSA provides `snd_device_name_hint()` which enumerates all known PCM devices, including virtual ones defined by PipeWire/PulseAudio ALSA plugins:

```csharp
// pseudocode — ALSA device enumeration

// Get all PCM device hints. Card -1 means "all cards":
snd_device_name_hint(-1, "pcm", out hints);  // void** hints

// Iterate the null-terminated array:
int i = 0;
while (hints[i] != IntPtr.Zero)
{
    string name = snd_device_name_get_hint(hints[i], "NAME");
    string desc = snd_device_name_get_hint(hints[i], "DESC");
    string ioid = snd_device_name_get_hint(hints[i], "IOID");

    // IOID: null = both directions, "Input" = capture only, "Output" = playback only
    // Filter: include if ioid is null or "Output"

    // name is the ALSA device string (e.g. "default", "hw:0,0", "plughw:1,0")
    // desc is the human-readable description

    // Free each string after copying:
    free(name); free(desc); free(ioid);
    i++;
}

snd_device_name_free_hint(hints);
```

### PInvoke Surface (additional to SPEC-ANAudio)

```csharp
// pseudocode — 4 additional PInvoke declarations

// libasound.so.2:
int snd_device_name_hint(int card, string iface, out nint hints);
string snd_device_name_get_hint(nint hint, string id);  // returns malloc'd string
int snd_device_name_free_hint(nint hints);

// Plus standard libc:
void free(nint ptr);  // to free the strings returned by get_hint
```

### Device Selection

The ALSA device name string from enumeration (e.g. `"hw:1,0"`, `"plughw:0,0"`) is passed directly to `snd_pcm_open()`:

```csharp
// pseudocode — opening a specific ALSA device
snd_pcm_open(out pcm_handle, deviceName, SND_PCM_STREAM_PLAYBACK, 0);
// deviceName = "default" for system default, or a specific device from enumeration
```

### Filtering the Device List

`snd_device_name_hint` returns a LOT of virtual devices (dmix, dsnoop, surround51, null, etc.). For a user-facing dropdown, filter to:
- `"default"` — always include, mark as IsDefault
- Devices starting with `"plughw:"` — these are hardware devices with automatic format conversion (what users expect)
- Devices starting with `"hw:"` — raw hardware access, only include if you want to show "advanced" options
- PulseAudio/PipeWire virtual devices if present (`"pulse"`, `"pipewire"`)

Skip: `null`, `dmix`, `dsnoop`, `surround*`, `iec958`, `spdif`, and other virtual routing devices unless the user specifically needs them.

### Change Notification

ALSA has no equivalent to Windows' `IMMNotificationClient`. The options:

1. **Reactive error handling (recommended for V1)**: When `snd_pcm_writei` returns `-ENODEV` (device removed) or `-EIO`, fire the `DeviceLost` event. The consumer closes and reopens on `"default"`, which now points to whatever the sound server has designated.

2. **udev monitoring (future)**: Linux device hotplug events come through udev/netlink. You can monitor for `sound` subsystem events via a netlink socket. This gives you device add/remove but NOT "default changed" — that's a sound-server-level concept.

3. **PulseAudio/PipeWire native API (future, probably not worth it)**: `pa_context_subscribe()` or PipeWire's event system would give proper default-changed notification. But this adds a dependency on a specific sound server and defeats the "ALSA everywhere" strategy.

**Pragmatic V1 approach**: Enumerate on demand (when the user opens the settings UI). Detect device loss reactively from stream errors. Accept that Linux users who hot-swap audio devices will see a brief interruption before recovery.

### References

- [snd_device_name_hint (ALSA)](https://www.alsa-project.org/alsa-doc/alsa-lib/group___control.html)
- [ALSA device naming](https://www.alsa-project.org/wiki/DeviceNames)
- [Unofficial ALSA API docs (device enumeration)](https://www.alemauri.eu/alsa/part1.html)

---

## Platform: macOS (CoreAudio)

macOS CoreAudio provides both enumeration and change notification through a unified property-listener system. Everything is an `AudioObject` with properties you can query and subscribe to.

### Device Enumeration

Query the system object for the list of devices, then query each device for its name and output capability:

```csharp
// pseudocode — macOS device enumeration

// 1. Get the array of all AudioDeviceIDs:
AudioObjectPropertyAddress devicesAddr = new()
{
    mSelector = kAudioHardwarePropertyDevices,       // 0x64657623 "dev#"
    mScope    = kAudioObjectPropertyScopeGlobal,     // 0x676C6F62 "glob"
    mElement  = kAudioObjectPropertyElementMain,     // 0
};

AudioObjectGetPropertyDataSize(
    kAudioObjectSystemObject,  // 1
    ref devicesAddr,
    0, IntPtr.Zero,
    out uint dataSize
);

int deviceCount = dataSize / sizeof(uint);  // AudioDeviceID is UInt32
uint[] deviceIds = new uint[deviceCount];

AudioObjectGetPropertyData(
    kAudioObjectSystemObject,
    ref devicesAddr,
    0, IntPtr.Zero,
    ref dataSize,
    deviceIds
);

// 2. For each device, get the name:
foreach (uint deviceId in deviceIds)
{
    AudioObjectPropertyAddress nameAddr = new()
    {
        mSelector = kAudioObjectPropertyName,  // 0x6C6E616D "lnam"
        mScope    = kAudioObjectPropertyScopeGlobal,
        mElement  = kAudioObjectPropertyElementMain,
    };

    // Returns a CFStringRef — marshal to string
    AudioObjectGetPropertyData(deviceId, ref nameAddr, ...);

    // 3. Check if it has output streams (is it a playback device?):
    AudioObjectPropertyAddress streamsAddr = new()
    {
        mSelector = kAudioDevicePropertyStreams,         // 0x73746D23 "stm#"
        mScope    = kAudioObjectPropertyScopeOutput,     // 0x6F757470 "outp"
        mElement  = kAudioObjectPropertyElementMain,
    };

    AudioObjectGetPropertyDataSize(deviceId, ref streamsAddr, 0, IntPtr.Zero, out uint streamsSize);
    bool hasOutput = streamsSize > 0;
}

// 4. Get the current default output device:
AudioObjectPropertyAddress defaultAddr = new()
{
    mSelector = kAudioHardwarePropertyDefaultOutputDevice,  // 0x64486F74 "dOut"
    mScope    = kAudioObjectPropertyScopeGlobal,
    mElement  = kAudioObjectPropertyElementMain,
};

AudioObjectGetPropertyData(kAudioObjectSystemObject, ref defaultAddr, ..., out uint defaultDeviceId);
```

### Device Selection

To open a specific device with AudioQueue, set `kAudioQueueProperty_CurrentDevice` on the queue before starting:

```csharp
// pseudocode — selecting a specific output device
// deviceUID is a CFString — get it via kAudioDevicePropertyDeviceUID on the AudioDeviceID

AudioQueueSetProperty(
    queue,
    kAudioQueueProperty_CurrentDevice,   // 0x61716364 "aqcd"
    ref deviceUID,
    (uint)sizeof(nint)
);
```

Alternatively, if using AudioUnit instead of AudioQueue, set `kAudioOutputUnitProperty_CurrentDevice` on the output unit.

### Change Notification

CoreAudio's property listener system is event-driven and covers everything:

```csharp
// pseudocode — registering for device change notifications

// Default device changed:
AudioObjectPropertyAddress defaultChangedAddr = new()
{
    mSelector = kAudioHardwarePropertyDefaultOutputDevice,
    mScope    = kAudioObjectPropertyScopeGlobal,
    mElement  = kAudioObjectPropertyElementMain,
};

AudioObjectAddPropertyListener(
    kAudioObjectSystemObject,
    ref defaultChangedAddr,
    defaultChangedCallback,   // AudioObjectPropertyListenerProc
    userData
);

// Device list changed (add/remove):
AudioObjectPropertyAddress devicesChangedAddr = new()
{
    mSelector = kAudioHardwarePropertyDevices,
    mScope    = kAudioObjectPropertyScopeGlobal,
    mElement  = kAudioObjectPropertyElementMain,
};

AudioObjectAddPropertyListener(
    kAudioObjectSystemObject,
    ref devicesChangedAddr,
    devicesChangedCallback,
    userData
);

// Callback signature:
// typedef OSStatus (*AudioObjectPropertyListenerProc)(
//     AudioObjectID objectID,
//     UInt32 numberAddresses,
//     const AudioObjectPropertyAddress* addresses,
//     void* clientData
// );
```

The callback fires on a CoreAudio internal thread. Inside the callback, re-query the property to get the new value (new default device ID, or new device list).

### PInvoke Surface

All calls go to `/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox` (or `CoreAudio.framework` for the HAL functions):

```csharp
// pseudocode — the ~4 PInvoke declarations for device management

// CoreAudio.framework (AudioHardware.h):
OSStatus AudioObjectGetPropertyDataSize(
    uint objectID,
    ref AudioObjectPropertyAddress address,
    uint qualifierDataSize,
    IntPtr qualifierData,
    out uint dataSize
);

OSStatus AudioObjectGetPropertyData(
    uint objectID,
    ref AudioObjectPropertyAddress address,
    uint qualifierDataSize,
    IntPtr qualifierData,
    ref uint dataSize,
    void* data   // output buffer
);

OSStatus AudioObjectAddPropertyListener(
    uint objectID,
    ref AudioObjectPropertyAddress address,
    nint callback,  // function pointer
    IntPtr clientData
);

OSStatus AudioObjectRemovePropertyListener(
    uint objectID,
    ref AudioObjectPropertyAddress address,
    nint callback,
    IntPtr clientData
);
```

### Key Constants (Four-Character Codes)

```
kAudioObjectSystemObject:                    1
kAudioHardwarePropertyDevices:               0x64657623  "dev#"
kAudioHardwarePropertyDefaultOutputDevice:   0x644F7574  "dOut"
kAudioObjectPropertyScopeGlobal:             0x676C6F62  "glob"
kAudioObjectPropertyScopeOutput:             0x6F757470  "outp"
kAudioObjectPropertyElementMain:             0           (was kAudioObjectPropertyElementMaster, renamed)
kAudioObjectPropertyName:                    0x6C6E616D  "lnam"
kAudioDevicePropertyDeviceUID:               0x75696420  "uid "
kAudioDevicePropertyStreams:                  0x73746D23  "stm#"
kAudioQueueProperty_CurrentDevice:           0x61716364  "aqcd"
```

### References

- [Audio Object Properties (Apple)](https://developer.apple.com/documentation/coreaudio/audio_object_properties)
- [AudioObjectAddPropertyListener (Apple)](https://developer.apple.com/documentation/coreaudio/1422472-audioobjectaddpropertylistener)
- [AudioQueueSetProperty (Apple)](https://developer.apple.com/documentation/audiotoolbox/1502090-audioqueuesetproperty)

---

## Auto-Recovery Pattern

The recommended consumer pattern for handling device changes — this lives above the AN.Audio layer, in the application code:

```csharp
// pseudocode — illustrative auto-recovery pattern

class AudioPlaybackManager : IDisposable
{
    IAudioOutput? _output;
    IAudioDeviceManager _devices;
    AudioCallback _callback;
    string? _preferredDeviceId;  // null = use default

    public void Start(AudioCallback callback, string? deviceId = null)
    {
        _callback = callback;
        _preferredDeviceId = deviceId;
        _devices = AudioOutput.GetDeviceManager();
        _devices.DefaultDeviceChanged += OnDefaultChanged;
        CreateOutput();
    }

    void CreateOutput()
    {
        var format = new AudioFormat(48000, 2, SampleFormat.Float32);
        _output = _preferredDeviceId != null
            ? AudioOutput.Create(format, _preferredDeviceId)
            : AudioOutput.Create(format);
        _output.DeviceLost += OnDeviceLost;
        _output.Start(_callback);
    }

    void OnDeviceLost(DeviceLostReason reason)
    {
        // Audio thread — keep this fast
        _output?.Dispose();
        _output = null;

        // Marshal to main thread for recovery:
        PostToMainThread(() =>
        {
            if (_preferredDeviceId != null && reason == DeviceLostReason.DeviceRemoved)
                _preferredDeviceId = null;  // fall back to default

            CreateOutput();  // reopen on new/default device
        });
    }

    void OnDefaultChanged(AudioDeviceInfo? newDefault)
    {
        if (_preferredDeviceId != null) return;  // user chose a specific device, don't switch
        // If using default, the backend should fire DeviceLost, which triggers recovery
    }
}
```

---

## Open Questions

- **Linux udev monitoring**: Worth adding in V2 for USB audio hotplug? The reactive error-handling approach works but there's a brief audio gap. udev would let us detect the device change before the stream errors.
- **Device capability querying**: Should `AudioDeviceInfo` include supported formats (sample rates, channel counts)? Useful for an advanced settings UI but adds complexity to enumeration. Leaning toward: defer, let format negotiation happen at `IAudioOutput.Create()` time and throw if the device can't handle it.
- **Multiple simultaneous outputs**: Arcane Siege might want to play sound effects on one device and voice chat on another (communications device). The abstraction supports this (just create two `IAudioOutput` instances with different device IDs), but the `DeviceLostReason.DefaultChanged` logic gets more nuanced with the Windows `ERole` distinction (console vs multimedia vs communications).

## Alternatives Considered

- **PulseAudio native API for Linux**: `pa_context_subscribe()` gives proper real-time device change events, including default-changed. However, it requires linking against `libpulse`, which isn't present on ALSA-only or PipeWire-without-pulse-compat systems. Breaks the "ALSA everywhere" strategy for a marginal improvement.
- **PipeWire native API for Linux**: Same issue — adds a dependency on a specific sound server. PipeWire is dominant now but not universal.
- **Polling-based device list refresh**: Could poll `GetOutputDevices()` every N seconds to detect changes on Linux. Violates Rule #3. The reactive error-handling approach is better.