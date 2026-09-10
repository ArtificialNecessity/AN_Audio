using System.Buffers;
using System.Collections.Concurrent;
using AN.Audio.Midi.Internal;

namespace AN.Audio.Midi.Platforms.Linux;

/// <summary>
/// IMidiInput on ALSA rawmidi (SPEC-30 §5, Linux). Mirrors WinMm_MidiInput: owns the policy, the poll/worker
/// thread, the open-port slot table, and identity orchestration. Reader-thread entry points are
/// DeliverFromDriver/QueueSysExFromDriver; everything else runs on the worker or control thread.
/// </summary>
internal sealed class Alsa_MidiInput : IMidiInput
{
    private const int MaxPortSlots = 256;   // MidiInput_PortIndex is a byte

    private readonly MidiInput_Options _options;
    private readonly object _controlLock = new();
    private readonly Alsa_MidiInPort?[] _slots = new Alsa_MidiInPort?[MaxPortSlots];
    private readonly HashSet<MidiInput_DeviceKey> _failedKeys = new();          // D16: don't retry until the device changes
    private readonly ConcurrentQueue<SysExWorkItem> _sysExQueue = new();
    private readonly ConcurrentQueue<MidiInput_PortIndex> _identityRequests = new();
    private readonly ConcurrentQueue<MidiInput_PortIndex> _readerClosedPorts = new();
    private readonly AutoResetEvent _wake = new(false);

    private MidiInput_Callback? _rawCallback;
    private Thread? _worker;
    private volatile bool _running;
    private volatile bool _stopRequested;
    private ManualResetEventSlim? _firstScanDone;
    private long _lastReportedDropped;
    private int _lastDropPort;
    private long _lastOverflowEventTicks;
    private MidiInput_OpenPolicy _openPolicy;
    private IReadOnlyList<MidiInput_DeviceKey>? _preferredDevices;

    [ThreadStatic] private static int t_callbackDepth;

    private readonly record struct SysExWorkItem(MidiInput_PortIndex Port, long ArrivalTicks, byte[] Buffer, int Length);

    public Alsa_MidiInput(MidiInput_Options options)
    {
        options.Validate();
        _options = options;
        _openPolicy = options.OpenPolicy;
        _preferredDevices = options.PreferredDevices;
        Ring = new MidiInput_MessageRing(options.RingInitialCapacity, options.RingMaxCapacity);
    }

    // ── IMidiInput ─────────────────────────────────────────────────────────────────────────────

    public MidiInput_MessageRing Ring { get; }
    public bool IsRunning => _running;
    public bool IsInsideCallback => t_callbackDepth > 0;

    public long SysExDiscardedCount
    {
        get { long n = 0; foreach (var p in _slots) if (p is not null) n += p.SysExDiscardedCount; return n; }
    }

    public IReadOnlyList<MidiInput_DeviceInfo> OpenPorts
    {
        get
        {
            var list = new List<MidiInput_DeviceInfo>();
            foreach (var p in _slots) if (p is not null && p.IsOpen) list.Add(p.Info);
            return list;
        }
    }

    public bool TryGetPort(MidiInput_PortIndex port, out MidiInput_DeviceInfo info)
    {
        var p = _slots[port.Value];
        if (p is not null && p.IsOpen) { info = p.Info; return true; }
        info = null!;
        return false;
    }

    public MidiInput_OpenPolicy OpenPolicy
    {
        get => _openPolicy;
        set { _openPolicy = value; _pendingRescan = true; _wake.Set(); }
    }

    public IReadOnlyList<MidiInput_DeviceKey>? PreferredDevices
    {
        get => _preferredDevices;
        set { _preferredDevices = value; _pendingRescan = true; _wake.Set(); }
    }

    public event Action<MidiInput_DeviceInfo>? DeviceOpened;
    public event Action<MidiInput_DeviceInfo, MidiInput_LostReason>? DeviceLost;
    public event Action<MidiInput_DeviceInfo, MidiInput_DeviceIdentity>? IdentityResolved;
    public event Action<MidiInput_SysExMessage>? SysExReceived;
    public event Action<MidiInput_PortIndex, long>? Overflow;

    public void Start() => StartCore(null);
    public void Start(MidiInput_Callback rawCallback) => StartCore(rawCallback ?? throw new ArgumentNullException(nameof(rawCallback)));

    private void StartCore(MidiInput_Callback? raw)
    {
        ThrowIfInsideCallback();
        lock (_controlLock)
        {
            if (_running) throw new InvalidOperationException("MIDI input is already running; call Stop() first.");
            _rawCallback = raw;
            _stopRequested = false;
            _firstScanDone = new ManualResetEventSlim(false);
            _running = true;
            _worker = new Thread(WorkerLoop) { Name = "AN.Audio.Midi ALSA worker", IsBackground = true };
            _worker.Start();
            _firstScanDone.Wait();   // OpenPorts is populated when Start returns
        }
    }

    public void Stop()
    {
        ThrowIfInsideCallback();
        lock (_controlLock)
        {
            if (!_running) return;
            _stopRequested = true;
            _wake.Set();
            _worker?.Join();
            _worker = null;
            _running = false;
            _rawCallback = null;
        }
    }

    public void RequestIdentity(MidiInput_PortIndex port)
    {
        ThrowIfInsideCallback();
        _identityRequests.Enqueue(port);
        _wake.Set();
    }

    public void Dispose()
    {
        Stop();
        _wake.Dispose();
    }

    private void ThrowIfInsideCallback()
    {
        if (IsInsideCallback)
            throw new InvalidOperationException("This operation is not allowed from inside the MIDI reader callback (SPEC-30 D14).");
    }

    // ── reader-thread entry points (hot path) ──────────────────────────────────────────────────

    internal static void EnterCallback() => t_callbackDepth++;
    internal static void ExitCallback() => t_callbackDepth--;

    internal void DeliverFromDriver(in MidiInput_Message message)
    {
        var raw = _rawCallback;
        if (raw is not null) { raw(in message); return; }
        if (!Ring.TryEnqueue(in message))
        {
            Volatile.Write(ref _lastDropPort, message.Port.Value);
            _wake.Set();   // let the worker report Overflow
        }
    }

    /// <summary>Cold path: copies the completed SysEx into a pooled buffer for the worker thread.</summary>
    internal void QueueSysExFromDriver(MidiInput_PortIndex port, long arrivalTicks, ReadOnlySpan<byte> complete)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(complete.Length);
        complete.CopyTo(buffer);
        _sysExQueue.Enqueue(new SysExWorkItem(port, arrivalTicks, buffer, complete.Length));
        _wake.Set();
    }

    internal void NotePortClosedByDriver(MidiInput_PortIndex port)
    {
        _readerClosedPorts.Enqueue(port);
        _wake.Set();
    }

    // ── worker thread ───────────────────────────────────────────────────────────────────────

    private void WorkerLoop()
    {
        try
        {
            Rescan();
            _firstScanDone?.Set();

            long nextPollTicks = Environment.TickCount64 + _options.PollIntervalMs;
            while (!_stopRequested)
            {
                int wait = (int)Math.Max(0, nextPollTicks - Environment.TickCount64);
                _wake.WaitOne(wait);
                if (_stopRequested) break;

                DrainReaderClosedPorts();
                DrainSysEx();
                DrainIdentityRequests();
                ReportOverflowIfAny();

                if (Environment.TickCount64 >= nextPollTicks || _pendingRescan)
                {
                    _pendingRescan = false;
                    Rescan();
                    nextPollTicks = Environment.TickCount64 + _options.PollIntervalMs;
                }
            }
        }
        finally
        {
            _firstScanDone?.Set();
            CloseAllPorts(MidiInput_LostReason.Stopped);
            DrainSysEx();   // return pooled buffers
        }
    }

    private volatile bool _pendingRescan;

    private void Rescan()
    {
        List<Alsa_MidiInDeviceEntry> present;
        try { present = Alsa_MidiInEnumerator.EnumerateInputs(); }
        catch { return; }

        var presentKeys = new HashSet<MidiInput_DeviceKey>(present.Select(e => e.Key));
        _failedKeys.RemoveWhere(k => !presentKeys.Contains(k));   // device went away → eligible again when it returns

        // 1. Close ports that vanished or that the policy no longer selects.
        for (int i = 0; i < MaxPortSlots; i++)
        {
            var port = _slots[i];
            if (port is null) continue;
            if (!presentKeys.Contains(port.Entry.Key))
                ClosePort(i, MidiInput_LostReason.Unplugged);
            else if (!IsSelectedByPolicy(port.Entry.Key))
                ClosePort(i, MidiInput_LostReason.Stopped);
        }

        // 2. Open selected ports that are present but not open.
        foreach (var entry in present)
        {
            if (!IsSelectedByPolicy(entry.Key)) continue;
            if (_failedKeys.Contains(entry.Key)) continue;
            if (FindSlotByKey(entry.Key) >= 0) continue;

            int slot = FindFreeSlot();
            if (slot < 0) break;

            var port = new Alsa_MidiInPort(this, _options, new MidiInput_PortIndex((byte)slot), entry);
            if (port.Open(out var failReason))
            {
                _slots[slot] = port;
                SafeInvoke(() => DeviceOpened?.Invoke(port.Info));
                if (_options.RequestIdentityOnOpen && entry.HasPairedOutput) _identityRequests.Enqueue(port.Index);
            }
            else
            {
                _failedKeys.Add(entry.Key);
                var info = entry.ToDeviceInfo();
                SafeInvoke(() => DeviceLost?.Invoke(info, failReason));
            }
        }

        DrainIdentityRequests();
    }

    private bool IsSelectedByPolicy(MidiInput_DeviceKey key) => _openPolicy switch
    {
        MidiInput_OpenPolicy.AllDevices => true,
        MidiInput_OpenPolicy.PreferenceList => _preferredDevices is { } list && list.Contains(key),
        _ => false,
    };

    private int FindSlotByKey(MidiInput_DeviceKey key)
    {
        for (int i = 0; i < MaxPortSlots; i++)
            if (_slots[i] is { } p && p.Entry.Key == key) return i;
        return -1;
    }

    private int FindFreeSlot()
    {
        for (int i = 0; i < MaxPortSlots; i++) if (_slots[i] is null) return i;
        return -1;
    }

    private void ClosePort(int slot, MidiInput_LostReason reason)
    {
        var port = _slots[slot];
        if (port is null) return;
        port.Close();
        _slots[slot] = null;
        var info = port.Info;
        SafeInvoke(() => DeviceLost?.Invoke(info, reason));
    }

    private void CloseAllPorts(MidiInput_LostReason reason)
    {
        for (int i = 0; i < MaxPortSlots; i++) ClosePort(i, reason);
    }

    private void DrainReaderClosedPorts()
    {
        while (_readerClosedPorts.TryDequeue(out var port))
            if (_slots[port.Value] is not null) ClosePort(port.Value, MidiInput_LostReason.Unplugged);
    }

    private void DrainSysEx()
    {
        while (_sysExQueue.TryDequeue(out var item))
        {
            try
            {
                var bytes = item.Buffer.AsSpan(0, item.Length);
                if (Midi_IdentityReplyParser.TryParse(bytes, out var identity) && _slots[item.Port.Value] is { } port)
                {
                    port.Info = port.Entry.ToDeviceInfo(identity);
                    var info = port.Info;
                    SafeInvoke(() => IdentityResolved?.Invoke(info, identity));
                }

                if (SysExReceived is { } handler)
                {
                    var msg = new MidiInput_SysExMessage { ArrivalTicks = item.ArrivalTicks, Port = item.Port, Bytes = new ReadOnlyMemory<byte>(item.Buffer, 0, item.Length) };
                    SafeInvoke(() => handler(msg));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(item.Buffer);
            }
        }
    }

    private void DrainIdentityRequests()
    {
        Span<byte> request = stackalloc byte[Midi_IdentityReplyParser.RequestLength];
        Midi_IdentityReplyParser.WriteRequest(request);
        while (_identityRequests.TryDequeue(out var portIndex))
        {
            var port = _slots[portIndex.Value];
            if (port is null || !port.IsOpen || !port.CanWrite) continue;
            port.TryWrite(request);   // D9: silent on failure
        }
    }

    private void ReportOverflowIfAny()
    {
        long dropped = Ring.DroppedCount;
        if (dropped == _lastReportedDropped) return;
        long now = Environment.TickCount64;
        if (now - _lastOverflowEventTicks < 250) return;   // rate limit
        _lastReportedDropped = dropped;
        _lastOverflowEventTicks = now;
        var port = new MidiInput_PortIndex((byte)Volatile.Read(ref _lastDropPort));
        SafeInvoke(() => Overflow?.Invoke(port, dropped));
    }

    private static void SafeInvoke(Action action)
    {
        try { action(); }
        catch { /* consumer handler threw; never take down the worker thread */ }
    }
}