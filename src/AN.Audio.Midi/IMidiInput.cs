using AN.Audio;

namespace AN.Audio.Midi;

/// <summary>
/// Platform-independent MIDI input: all selected ports merged into one stream (SPEC-30).
/// Hot path: either <see cref="Ring"/> (drain from your audio callback) or a raw <see cref="MidiInput_Callback"/>.
/// Everything else is control plane on background threads — marshal to UI yourself (D14).
/// </summary>
public interface IMidiInput : IDisposable
{
    /// <summary>Always present. Filled by the driver thread unless a raw callback was passed to Start.</summary>
    MidiInput_MessageRing Ring { get; }

    /// <summary>Ring delivery. May be called again after <see cref="Stop"/>, in either mode (D14).</summary>
    void Start();

    /// <summary>Raw delivery on the driver thread; the ring is NOT filled and <see cref="Overflow"/> never fires.</summary>
    void Start(MidiInput_Callback rawCallback);

    /// <summary>Close all ports; blocks until the driver callbacks are quiescent. Throws InvalidOperationException if called from inside the callback.</summary>
    void Stop();

    bool IsRunning { get; }

    /// <summary>True only on the driver thread while the callback is executing (D14 guard).</summary>
    bool IsInsideCallback { get; }

    /// <summary>Open ports; the list index equals <see cref="MidiInput_PortIndex.Value"/> in delivered messages. Snapshot.</summary>
    IReadOnlyList<MidiInput_DeviceInfo> OpenPorts { get; }

    /// <summary>Resolve a message's <see cref="MidiInput_Message.Port"/> to its device. Slots are stable while a port is open and reused after it closes.</summary>
    bool TryGetPort(MidiInput_PortIndex port, out MidiInput_DeviceInfo info);

    MidiInput_OpenPolicy OpenPolicy { get; set; }
    IReadOnlyList<MidiInput_DeviceKey>? PreferredDevices { get; set; }

    /// <summary>Send a Universal Identity Request on the paired output port (D8/D9). Async; silent on failure or timeout.</summary>
    void RequestIdentity(MidiInput_PortIndex port);

    /// <summary>Counters for diagnostics: SysEx messages discarded because they exceeded <see cref="MidiInput_Options.SysExMaxBytes"/> (D20).</summary>
    long SysExDiscardedCount { get; }

    event Action<MidiInput_DeviceInfo>? DeviceOpened;
    event Action<MidiInput_DeviceInfo, MidiInput_LostReason>? DeviceLost;
    event Action<MidiInput_DeviceInfo, MidiInput_DeviceIdentity>? IdentityResolved;
    /// <summary>Background thread. The message's buffer is valid only for the duration of the call.</summary>
    event Action<MidiInput_SysExMessage>? SysExReceived;
    /// <summary>Ring was full at max capacity and a message was dropped (D5). Rate-limited; carries the running dropped total.</summary>
    event Action<MidiInput_PortIndex, long>? Overflow;
}

/// <summary>Enumeration and change notification for MIDI input ports. Singleton per process.</summary>
public interface IMidiInput_DeviceManager : IDisposable
{
    /// <summary>Snapshot of currently present input ports (Identity is null; TypeId is the driver-caps fallback).</summary>
    IReadOnlyList<MidiInput_DeviceInfo> GetInputDevices();

    /// <summary>Background thread. Added/Removed; the info is the affected port.</summary>
    event Action<DeviceChangeType, MidiInput_DeviceInfo?>? DeviceListChanged;

    /// <summary>Host-supplied hint that the device list may have changed (e.g. from WM_DEVICECHANGE). Triggers an immediate re-enumeration.</summary>
    void NotifyDeviceChange();
}