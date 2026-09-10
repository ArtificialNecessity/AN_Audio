namespace AN.Audio.Midi.Platforms.Linux;

/// <summary>
/// IMidiInput_DeviceManager on ALSA rawmidi (SPEC-30 D10, Linux). Rawmidi has no arrival/removal notification
/// without libudev, so a background thread re-enumerates once per second while anyone is subscribed;
/// NotifyDeviceChange forces an immediate scan.
/// </summary>
internal sealed class Alsa_MidiInDeviceManager : IMidiInput_DeviceManager
{
    private const int PollIntervalMs = 1000;

    private static readonly Lazy<Alsa_MidiInDeviceManager> s_instance = new(() => new Alsa_MidiInDeviceManager());
    public static Alsa_MidiInDeviceManager Instance => s_instance.Value;

    private readonly object _lock = new();
    private readonly AutoResetEvent _wake = new(false);
    private Dictionary<MidiInput_DeviceKey, MidiInput_DeviceInfo> _known = new();
    private Thread? _poller;
    private volatile bool _disposed;
    private Action<MidiInput_DeviceChangeType, MidiInput_DeviceInfo?>? _deviceListChanged;

    private Alsa_MidiInDeviceManager() { }

    public IReadOnlyList<MidiInput_DeviceInfo> GetInputDevices()
    {
        var entries = Alsa_MidiInEnumerator.EnumerateInputs();
        var list = new List<MidiInput_DeviceInfo>(entries.Count);
        foreach (var e in entries) list.Add(e.ToDeviceInfo());
        return list;
    }

    public event Action<MidiInput_DeviceChangeType, MidiInput_DeviceInfo?>? DeviceListChanged
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
        _poller = new Thread(PollLoop) { Name = "AN.Audio.Midi ALSA device poller", IsBackground = true };
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
                if (!current.ContainsKey(key)) SafeInvoke(handler, MidiInput_DeviceChangeType.Removed, info);
            foreach (var (key, info) in current)
                if (!previous.ContainsKey(key)) SafeInvoke(handler, MidiInput_DeviceChangeType.Added, info);
        }
    }

    private static void SafeInvoke(Action<MidiInput_DeviceChangeType, MidiInput_DeviceInfo?> handler, MidiInput_DeviceChangeType type, MidiInput_DeviceInfo info)
    {
        try { handler(type, info); } catch { }
    }
}