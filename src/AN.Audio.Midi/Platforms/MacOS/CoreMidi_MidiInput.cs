using System.Buffers;
using System.Collections.Concurrent;
using AN.Audio.Midi.Internal;

namespace AN.Audio.Midi.Platforms.MacOS;

/// <summary>
/// IMidiInput on CoreMIDI (SPEC-30 §10). Same shape as WinMm_MidiInput: owns the policy, the worker thread, the slot table and identity
/// orchestration. Differences: ONE CoreMIDI port with a connection per selected source (slot = srcConnRefCon); hot-plug comes from the
/// process client's setup notifications (D34) instead of / in addition to polling; ports are shareable so InUseByAnotherApplication never occurs (D37).
/// </summary>
internal sealed class CoreMidi_MidiInput : IMidiInput
{
    private const int MaxPortSlots = 256;   // MidiInput_PortIndex is a byte
    private const int NotificationDebounceMs = 50;   // D34

    private readonly MidiInput_Options _options;
    private readonly object _controlLock = new();
    private readonly HashSet<MidiInput_DeviceKey> _failedKeys = new();   // don't retry a source whose connect failed until it changes
    private readonly ConcurrentQueue<SysExWorkItem> _sysExQueue = new();
    private readonly ConcurrentQueue<MidiInput_PortIndex> _identityRequests = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly bool _usePolling;
    private readonly bool _useNotifications;

    private CoreMidi_MidiInPort? _port;
    private MidiInput_Callback? _rawCallback;
    private Thread? _worker;
    private volatile bool _running;
    private volatile bool _stopRequested;
    private volatile bool _pendingRescan;
    private ManualResetEventSlim? _firstScanDone;
    private long _lastReportedDropped;
    private int _lastDropPort;
    private long _lastOverflowEventTicks;
    private MidiInput_OpenPolicy _openPolicy;
    private IReadOnlyList<MidiInput_DeviceKey>? _preferredDevices;

    [ThreadStatic] private static int t_callbackDepth;

    private readonly record struct SysExWorkItem(MidiInput_PortIndex Port, long ArrivalTicks, byte[] Buffer, int Length);

    public CoreMidi_MidiInput(MidiInput_Options options)
    {
        options.Validate();
        _options = options;
        _openPolicy = options.OpenPolicy;
        _preferredDevices = options.PreferredDevices;
        Ring = new MidiInput_MessageRing(options.RingInitialCapacity, options.RingMaxCapacity);

        // D34: OsNotification = CoreMIDI notifications only; Poll = notifications AND the timer (the timer is a harmless safety net on macOS).
        switch (options.HotPlugSource)
        {
            case MidiInput_HotPlugSource.OsNotification: _useNotifications = true; _usePolling = false; break;
            case MidiInput_HotPlugSource.Poll: _useNotifications = true; _usePolling = true; break;
            default: throw new PlatformNotSupportedException($"MidiInput_HotPlugSource.{options.HotPlugSource} is Windows-only; use OsNotification or Poll on macOS (SPEC-30 D34).");
        }
    }

    // ── IMidiInput ─────────────────────────────────────────────────────────────────────────────

    public MidiInput_MessageRing Ring { get; }
    public bool IsRunning => _running;
    public bool IsInsideCallback => t_callbackDepth > 0;

    public long SysExDiscardedCount
    {
        get
        {
            var port = _port; if (port is null) return 0;
            long n = 0;
            for (int i = 0; i < MaxPortSlots; i++) if (port[i] is { } s) n += s.Reassembler.DiscardedCount;
            return n;
        }
    }

    public IReadOnlyList<MidiInput_DeviceInfo> OpenPorts
    {
        get
        {
            var list = new List<MidiInput_DeviceInfo>();
            var port = _port; if (port is null) return list;
            for (int i = 0; i < MaxPortSlots; i++) if (port[i] is { Connected: true } s) list.Add(s.Info);
            return list;
        }
    }

    public bool TryGetPort(MidiInput_PortIndex index, out MidiInput_DeviceInfo info)
    {
        var s = _port?[index.Value];
        if (s is { Connected: true }) { info = s.Info; return true; }
        info = null!;
        return false;
    }

    public MidiInput_OpenPolicy OpenPolicy
    {
        get => _openPolicy;
        set { _openPolicy = value; RequestRescan(); }
    }

    public IReadOnlyList<MidiInput_DeviceKey>? PreferredDevices
    {
        get => _preferredDevices;
        set { _preferredDevices = value; RequestRescan(); }
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
            _worker = new Thread(WorkerLoop) { Name = "AN.Audio.Midi CoreMIDI worker", IsBackground = true };
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
            throw new InvalidOperationException("This operation is not allowed from inside the MIDI driver callback (SPEC-30 D14).");
    }

    private void RequestRescan() { _pendingRescan = true; _wake.Set(); }

    // ── CoreMIDI-thread entry points (hot path) ────────────────────────────────────────────────────────

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

    // ── worker thread ───────────────────────────────────────────────────────────────────────

    private void OnSetupChanged() => RequestRescan();   // run-loop thread: just wake the worker

    private void WorkerLoop()
    {
        bool subscribed = false;
        try
        {
            _port = new CoreMidi_MidiInPort(this, _options);
            var created = _port.Create();
            if (created != CoreMidi_Status.NoError)
            {
                _port = null;   // no client/port: nothing can open; Start returns with zero ports, like a machine with no devices
                _firstScanDone?.Set();
                return;
            }

            if (_useNotifications) { CoreMidi_Client.Instance.SetupChanged += OnSetupChanged; subscribed = true; }

            Rescan();
            _firstScanDone?.Set();

            long nextPollTicks = Environment.TickCount64 + _options.PollIntervalMs;
            while (!_stopRequested)
            {
                int wait = _usePolling ? (int)Math.Max(0, nextPollTicks - Environment.TickCount64) : Timeout.Infinite;
                _wake.WaitOne(wait);
                if (_stopRequested) break;

                DrainSysEx();
                DrainIdentityRequests();
                ReportOverflowIfAny();

                bool pollDue = _usePolling && Environment.TickCount64 >= nextPollTicks;
                if (_pendingRescan || pollDue)
                {
                    if (_pendingRescan && !pollDue) Thread.Sleep(NotificationDebounceMs);   // coalesce the burst of notifications one plug event produces
                    _pendingRescan = false;
                    Rescan();
                    nextPollTicks = Environment.TickCount64 + _options.PollIntervalMs;
                }
            }
        }
        finally
        {
            _firstScanDone?.Set();
            if (subscribed) CoreMidi_Client.Instance.SetupChanged -= OnSetupChanged;
            CloseAllPorts(MidiInput_LostReason.Stopped);
            _port?.Dispose();
            _port = null;
            DrainSysEx();   // return pooled buffers
        }
    }

    private void Rescan()
    {
        var port = _port; if (port is null) return;
        List<CoreMidi_MidiInDeviceEntry> present;
        try { present = CoreMidi_MidiInEnumerator.EnumerateInputs(); }
        catch { return; }

        var presentKeys = new HashSet<MidiInput_DeviceKey>(present.Select(e => e.Key));
        _failedKeys.RemoveWhere(k => !presentKeys.Contains(k));

        // 1. Disconnect sources that vanished or that the policy no longer selects.
        for (int i = 0; i < MaxPortSlots; i++)
        {
            var s = port[i];
            if (s is null) continue;
            if (!presentKeys.Contains(s.Entry.Key))
                ClosePort(i, MidiInput_LostReason.Unplugged);
            else if (!IsSelectedByPolicy(s.Entry.Key))
                ClosePort(i, MidiInput_LostReason.Stopped);
        }

        // 2. Connect selected sources that are present but not connected.
        foreach (var entry in present)
        {
            if (!IsSelectedByPolicy(entry.Key)) continue;
            if (_failedKeys.Contains(entry.Key)) continue;
            if (FindSlotByKey(entry.Key) >= 0) continue;

            int slot = FindFreeSlot();
            if (slot < 0) break;

            var r = port.Connect(slot, entry);
            if (r == CoreMidi_Status.NoError)
            {
                var info = port[slot]!.Info;
                SafeInvoke(() => DeviceOpened?.Invoke(info));
                if (_options.RequestIdentityOnOpen && entry.HasPairedOutput) _identityRequests.Enqueue(new MidiInput_PortIndex((byte)slot));
            }
            else
            {
                _failedKeys.Add(entry.Key);
                var reason = MapLostReason(r);
                var info = entry.ToDeviceInfo();
                SafeInvoke(() => DeviceLost?.Invoke(info, reason));
            }
        }

        DrainIdentityRequests();
    }

    /// <summary>D37: CoreMIDI is multi-client, so InUseByAnotherApplication is never produced here.</summary>
    private static MidiInput_LostReason MapLostReason(CoreMidi_Status r) =>
        CoreMidi_Interop.IsDeviceGone(r) ? MidiInput_LostReason.Unplugged : MidiInput_LostReason.DriverError;

    private bool IsSelectedByPolicy(MidiInput_DeviceKey key) => _openPolicy switch
    {
        MidiInput_OpenPolicy.AllDevices => true,
        MidiInput_OpenPolicy.PreferenceList => _preferredDevices is { } list && list.Contains(key),
        _ => false,
    };

    private int FindSlotByKey(MidiInput_DeviceKey key)
    {
        var port = _port!;
        for (int i = 0; i < MaxPortSlots; i++)
            if (port[i] is { } s && s.Entry.Key == key) return i;
        return -1;
    }

    private int FindFreeSlot()
    {
        var port = _port!;
        for (int i = 0; i < MaxPortSlots; i++) if (port[i] is null) return i;
        return -1;
    }

    private void ClosePort(int slot, MidiInput_LostReason reason)
    {
        var port = _port; if (port is null) return;
        var s = port[slot];
        if (s is null) return;
        var info = s.Info;
        port.Disconnect(slot);
        SafeInvoke(() => DeviceLost?.Invoke(info, reason));
    }

    private void CloseAllPorts(MidiInput_LostReason reason)
    {
        if (_port is null) return;
        for (int i = 0; i < MaxPortSlots; i++) ClosePort(i, reason);
    }

    private void DrainSysEx()
    {
        while (_sysExQueue.TryDequeue(out var item))
        {
            try
            {
                var bytes = item.Buffer.AsSpan(0, item.Length);
                if (Midi_IdentityReplyParser.TryParse(bytes, out var identity) && _port?[item.Port.Value] is { } s)
                {
                    s.Info = s.Entry.ToDeviceInfo(identity);
                    var info = s.Info;
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
            var s = _port?[portIndex.Value];
            if (s is not { Connected: true } || !s.Entry.HasPairedOutput) continue;
            // Re-resolve at send time: the destination ref captured at enumeration can be stale after a replug (D32).
            var destination = CoreMidi_MidiInEnumerator.ResolveDestinationForSend(s.Entry);
            if (destination.IsNull) destination = s.Entry.PairedDestination;
            CoreMidi_SysexSender.TrySend(destination, request, _options.IdentityReplyTimeoutMs);   // D9/D33: silent on failure
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