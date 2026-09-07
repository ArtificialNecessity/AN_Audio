using AN.Audio;

namespace AN.Audio.Midi.Platforms.Windows;

/// <summary>
/// IMidiInput_DeviceManager on WinMM (SPEC-30 D10). WinMM has no arrival/removal notification, so a background
/// thread re-enumerates once per second while anyone is subscribed; NotifyDeviceChange forces an immediate scan.
/// </summary>
internal sealed class WinMm_MidiInDeviceManager : IMidiInput_DeviceManager
{
    private const int PollIntervalMs = 1000;

    private static readonly Lazy<WinMm_MidiInDeviceManager> s_instance = new(() => new WinMm_MidiInDeviceManager());
    public static WinMm_MidiInDeviceManager Instance => s_instance.Value;

    private readonly object _lock = new();
    private readonly AutoResetEvent _wake = new(false);
    private Dictionary<MidiInput_DeviceKey, MidiInput_DeviceInfo> _known = new();
    private Thread? _poller;
    private volatile bool _disposed;
    private Action<DeviceChangeType, MidiInput_DeviceInfo?>? _deviceListChanged;

    private WinMm_MidiInDeviceManager() { }

    public IReadOnlyList<MidiInput_DeviceInfo> GetInputDevices()
    {
        var entries = WinMm_MidiInEnumerator.EnumerateInputs();
        var list = new List<MidiInput_DeviceInfo>(entries.Count);
        foreach (var e in entries) list.Add(e.ToDeviceInfo());
        return list;
    }

    public event Action<DeviceChangeType, MidiInput_DeviceInfo?>? DeviceListChanged
    {
        add
        {
            lock (_lock)
            {
                if (_deviceListChanged is null) Snapshot();   // baseline before first subscriber
                _deviceListChanged += value;
                EnsurePoller();
            }
        }
        remove { lock (_lock) _deviceListChanged -= value; }
    }

    public void NotifyDeviceChange() => _wake.Set();

    public void Dispose()
    {
        _disposed = true;
        _wake.Set();
    }

    private void EnsurePoller()
    {
        if (_poller is not null) return;
        _poller = new Thread(PollLoop) { Name = "AN.Audio.Midi WinMM device poller", IsBackground = true };
        _poller.Start();
    }

    private void Snapshot()
    {
        var map = new Dictionary<MidiInput_DeviceKey, MidiInput_DeviceInfo>();
        foreach (var info in GetInputDevices()) map[info.Key] = info;
        _known = map;
    }

    private void PollLoop()
    {
        while (!_disposed)
        {
            _wake.WaitOne(PollIntervalMs);
            if (_disposed) break;

            Dictionary<MidiInput_DeviceKey, MidiInput_DeviceInfo> current;
            try
            {
                current = new Dictionary<MidiInput_DeviceKey, MidiInput_DeviceInfo>();
                foreach (var info in GetInputDevices()) current[info.Key] = info;
            }
            catch { continue; }

            var previous = _known;
            _known = current;
            var handler = _deviceListChanged;
            if (handler is null) continue;

            foreach (var (key, info) in previous)
                if (!current.ContainsKey(key)) SafeInvoke(handler, DeviceChangeType.Removed, info);
            foreach (var (key, info) in current)
                if (!previous.ContainsKey(key)) SafeInvoke(handler, DeviceChangeType.Added, info);
        }
    }

    private static void SafeInvoke(Action<DeviceChangeType, MidiInput_DeviceInfo?> handler, DeviceChangeType type, MidiInput_DeviceInfo info)
    {
        try { handler(type, info); } catch { }
    }
}