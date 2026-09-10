namespace AN.Audio.Midi.Platforms.MacOS;

/// <summary>
/// IMidiInput_DeviceManager on CoreMIDI (SPEC-30 D34). CoreMIDI delivers real setup notifications (via CoreMidi_Client), so no
/// polling: each notification (debounced) or NotifyDeviceChange() triggers one re-enumeration and a diff by key.
/// </summary>
internal sealed class CoreMidi_MidiInDeviceManager : IMidiInput_DeviceManager
{
    /// <summary>D34: several notifications arrive per plug event; coalesce them.</summary>
    private const int DebounceMs = 50;

    private static readonly Lazy<CoreMidi_MidiInDeviceManager> s_instance = new(() => new CoreMidi_MidiInDeviceManager());
    public static CoreMidi_MidiInDeviceManager Instance => s_instance.Value;

    private readonly object _lock = new();
    private readonly AutoResetEvent _wake = new(false);
    private Dictionary<MidiInput_DeviceKey, MidiInput_DeviceInfo> _known = new();
    private Thread? _worker;
    private volatile bool _disposed;
    private Action<MidiInput_DeviceChangeType, MidiInput_DeviceInfo?>? _deviceListChanged;

    private CoreMidi_MidiInDeviceManager() { }

    public IReadOnlyList<MidiInput_DeviceInfo> GetInputDevices()
    {
        var entries = CoreMidi_MidiInEnumerator.EnumerateInputs();
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
                EnsureWorker();
            }
        }
        remove { lock (_lock) _deviceListChanged -= value; }
    }

    public void NotifyDeviceChange() => _wake.Set();

    public void Dispose()
    {
        _disposed = true;
        CoreMidi_Client.Instance.SetupChanged -= NotifyDeviceChange;
        _wake.Set();
    }

    private void EnsureWorker()
    {
        if (_worker is not null) return;
        CoreMidi_Client.Instance.SetupChanged += NotifyDeviceChange;   // run-loop thread → just wakes us
        _worker = new Thread(WorkerLoop) { Name = "AN.Audio.Midi CoreMIDI device watcher", IsBackground = true };
        _worker.Start();
    }

    private void Snapshot()
    {
        var map = new Dictionary<MidiInput_DeviceKey, MidiInput_DeviceInfo>();
        foreach (var info in GetInputDevices()) map[info.Key] = info;
        _known = map;
    }

    private void WorkerLoop()
    {
        while (!_disposed)
        {
            _wake.WaitOne();
            if (_disposed) break;
            Thread.Sleep(DebounceMs);
            _wake.Reset();   // swallow the burst that arrived during the debounce window

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