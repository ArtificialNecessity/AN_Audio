using System.Diagnostics;
using System.Runtime.InteropServices;
using AN.Audio.AppMeter.Internal;
using static AN.Audio.AppMeter.Platforms.Windows.Wasapi_AppMeterInterop;

namespace AN.Audio.AppMeter.Platforms.Windows;

/// <summary>
/// Windows backend (spec 80 §4): one <c>IAudioSessionManager2</c> per active render endpoint (D4), every session's <c>IAudioMeterInformation</c>
/// polled on our own MTA thread at <see cref="AudioAppMeter_Options.PollIntervalMs"/> (D3/D8), folded into <see cref="AppMeter_SessionTable"/>.
/// Session list refresh = <c>IAudioSessionNotification</c> + unconditional re-enumerate every <see cref="AudioAppMeter_Options.ReenumerateIntervalMs"/> (D5).
/// ALL COM calls happen on the poll thread; the consumer-facing methods read cached state under a lock.
/// </summary>
internal sealed unsafe class Wasapi_AppMeter : IAudioAppMeter
{
    // ─── cached COM graph (poll thread only) ───────────────────────────────────────────

    private sealed class CachedSession
    {
        public AudioAppMeter_SessionId Id;
        public AudioAppMeter_ProcessId Pid;
        public AudioAppMeter_SessionFlags BaseFlags;    // SystemSounds | Unattributed (fixed for the session's life)
        public nint Control2;                           // IAudioSessionControl2
        public nint Meter;                              // IAudioMeterInformation (per session)
        public nint Volume;                             // ISimpleAudioVolume (may be 0)

        public void Release()
        {
            SafeRelease(ref Volume);
            SafeRelease(ref Meter);
            SafeRelease(ref Control2);
        }
    }

    private sealed class CachedEndpoint
    {
        public AudioAppMeter_EndpointId Id;
        public string DeviceIdString = string.Empty;
        public nint Device;                             // IMMDevice
        public nint SessionManager;                     // IAudioSessionManager2
        public bool NotificationRegistered;
        public readonly List<CachedSession> Sessions = new();

        public void Release(nint notificationObject)
        {
            foreach (var s in Sessions) s.Release();
            Sessions.Clear();
            if (NotificationRegistered && SessionManager != 0 && notificationObject != 0)
                SessionManagerUnregisterSessionNotification(SessionManager, notificationObject);
            NotificationRegistered = false;
            SafeRelease(ref SessionManager);
            SafeRelease(ref Device);
        }
    }

    // ─── state ──────────────────────────────────────────────────────────────

    private readonly AudioAppMeter_Options _options;
    private readonly AppMeter_SessionTable _table = new();
    private readonly object _consumerLock = new();
    private readonly Dictionary<AudioAppMeter_SessionId, string> _displayNames = new();   // D12 cache, filled at enumerate
    private float _masterPeakMaxSinceRead;                                                // D3 semantics for the master too

    private readonly Thread _pollThread;
    private readonly ManualResetEventSlim _initDone = new(false);
    private readonly ManualResetEventSlim _stop = new(false);
    private volatile bool _reenumerateRequested = true;   // first poll enumerates
    private volatile bool _disposed;

    private GCHandle _selfHandle;
    private nint _notificationObject;                      // our IAudioSessionNotification (poll thread only after init)

    // poll-thread-only
    private nint _deviceEnumerator;                        // IMMDeviceEnumerator
    private nint _masterMeter;                             // IAudioMeterInformation on the default endpoint
    private readonly List<CachedEndpoint> _endpoints = new();
    private long _lastEnumerateTicks;

    public AudioAppMeter_Capability Capability { get; private set; } = AudioAppMeter_Capability.Metering;
    public AudioAppMeter_UnavailableReason UnavailableReason { get; private set; } = AudioAppMeter_UnavailableReason.None;
    public event Action? SessionsChanged;

    /// <summary>Starts the poll thread and waits for its COM bring-up. D7: a failed bring-up yields a <see cref="AudioAppMeter_Capability.None"/> meter carrying the reason.</summary>
    public static IAudioAppMeter Create(AudioAppMeter_Options options)
    {
        var meter = new Wasapi_AppMeter(options);
        meter._pollThread.Start();
        meter._initDone.Wait();
        if (meter.Capability == AudioAppMeter_Capability.None)
        {
            var reason = meter.UnavailableReason;
            meter.Dispose();
            return new NullAppMeter(reason);
        }
        return meter;
    }

    private Wasapi_AppMeter(AudioAppMeter_Options options)
    {
        _options = options;
        _pollThread = new Thread(PollThreadMain)
        {
            Name = "AN.Audio.AppMeter WASAPI poll",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
    }

    // ─── consumer surface ────────────────────────────────────────────────────────

    public int ReadSessions(Span<AudioAppMeter_Sample> into) => _table.ReadAndReset(into);

    public float ReadMasterPeak()
    {
        lock (_consumerLock)
        {
            float v = _masterPeakMaxSinceRead;
            _masterPeakMaxSinceRead = 0f;
            return v;
        }
    }

    public string? LookupDisplayName(AudioAppMeter_SessionId sessionId)
    {
        lock (_consumerLock)
            return _displayNames.TryGetValue(sessionId, out var name) ? name : null;
    }

    /// <summary>Called on an audio-service MTA thread via our COM vtable. Only flags; the poll thread does the work (D5).</summary>
    internal void OnSessionCreatedNotification() => _reenumerateRequested = true;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Set();
        if (Thread.CurrentThread != _pollThread && _pollThread.IsAlive)
            _pollThread.Join();
    }

    // ─── poll thread ──────────────────────────────────────────────────────────────

    private void PollThreadMain()
    {
        bool coInitialised = false;
        try
        {
            int hr = CoInitializeEx(0, COINIT_MULTITHREADED);
            coInitialised = Succeeded(hr);   // RPC_E_CHANGED_MODE cannot happen on a thread we own, but be tolerant
            if (!coInitialised && hr != RPC_E_CHANGED_MODE)
            {
                Fail(AudioAppMeter_UnavailableReason.NoAudioServer);
                return;
            }

            var clsid = CLSID_MMDeviceEnumerator;
            var iid = IID_IMMDeviceEnumerator;
            hr = CoCreateInstance(ref clsid, 0, CLSCTX_ALL, ref iid, out _deviceEnumerator);
            if (!Succeeded(hr))
            {
                Fail(AudioAppMeter_UnavailableReason.NoAudioServer);
                return;
            }

            _selfHandle = GCHandle.Alloc(this, GCHandleType.Weak);
            _notificationObject = CreateNotificationObject(_selfHandle);
            _initDone.Set();

            long reenumerateTicks = TimeSpan.FromMilliseconds(_options.ReenumerateIntervalMs).Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond;
            while (!_stop.Wait(_options.PollIntervalMs))
            {
                if (_reenumerateRequested || Stopwatch.GetTimestamp() - _lastEnumerateTicks >= reenumerateTicks)
                {
                    _reenumerateRequested = false;
                    Reenumerate();
                }
                PollOnce();
            }
        }
        catch (Exception)
        {
            // D7: report, don't retry. An unexpected exception on the poll thread ends metering for this instance.
            Fail(AudioAppMeter_UnavailableReason.NoAudioServer);
        }
        finally
        {
            foreach (var ep in _endpoints) ep.Release(_notificationObject);
            _endpoints.Clear();
            SafeRelease(ref _masterMeter);
            SafeRelease(ref _deviceEnumerator);
            DetachAndReleaseNotification(_notificationObject);
            _notificationObject = 0;
            if (_selfHandle.IsAllocated) _selfHandle.Free();
            if (coInitialised) CoUninitialize();
            _table.Clear();
            _initDone.Set();
        }
    }

    private void Fail(AudioAppMeter_UnavailableReason reason)
    {
        Capability = AudioAppMeter_Capability.None;
        UnavailableReason = reason;
        _initDone.Set();
    }

    /// <summary>D5 re-enumerate: release the whole cached graph and rebuild it. Table rows survive by id; vanished sessions strike out over the next polls.</summary>
    private void Reenumerate()
    {
        _lastEnumerateTicks = Stopwatch.GetTimestamp();
        foreach (var ep in _endpoints) ep.Release(_notificationObject);
        _endpoints.Clear();
        SafeRelease(ref _masterMeter);

        // Master meter on the default render endpoint (D9: refreshed only here).
        if (Succeeded(GetDefaultAudioEndpoint(_deviceEnumerator, eRender, eMultimedia, out nint defaultDevice)) && defaultDevice != 0)
        {
            var meterIid = IID_IAudioMeterInformation;
            if (!Succeeded(DeviceActivate(defaultDevice, ref meterIid, CLSCTX_ALL, 0, out _masterMeter))) _masterMeter = 0;
            if (_options.Scope == AudioAppMeter_Scope.DefaultRenderEndpointOnly)
                AddEndpoint(defaultDevice);      // takes ownership
            else
                Release(defaultDevice);
        }

        if (_options.Scope == AudioAppMeter_Scope.AllRenderEndpoints)
        {
            if (Succeeded(EnumAudioEndpoints(_deviceEnumerator, eRender, DEVICE_STATE_ACTIVE, out nint collection)) && collection != 0)
            {
                if (Succeeded(DeviceCollectionGetCount(collection, out uint count)))
                {
                    for (uint i = 0; i < count; i++)
                    {
                        if (Succeeded(DeviceCollectionItem(collection, i, out nint device)) && device != 0)
                            AddEndpoint(device);
                    }
                }
                Release(collection);
            }
        }

        // Drop cached names for sessions no longer present anywhere.
        lock (_consumerLock)
        {
            var live = new HashSet<AudioAppMeter_SessionId>();
            foreach (var ep in _endpoints) foreach (var s in ep.Sessions) live.Add(s.Id);
            var stale = _displayNames.Keys.Where(k => !live.Contains(k)).ToList();
            foreach (var k in stale) _displayNames.Remove(k);
        }
    }

    /// <summary>Takes ownership of <paramref name="device"/>. A per-endpoint COM failure drops that endpoint only (D7).</summary>
    private void AddEndpoint(nint device)
    {
        var ep = new CachedEndpoint { Device = device };
        if (Succeeded(DeviceGetId(device, out nint idPtr)))
            ep.DeviceIdString = TakeCoTaskString(idPtr);
        ep.Id = new AudioAppMeter_EndpointId(AppMeter_SessionTable.Fnv1a64(ep.DeviceIdString));

        var mgrIid = IID_IAudioSessionManager2;
        if (!Succeeded(DeviceActivate(device, ref mgrIid, CLSCTX_ALL, 0, out ep.SessionManager)) || ep.SessionManager == 0)
        {
            ep.Release(_notificationObject);
            return;
        }

        // Register BEFORE GetSessionEnumerator — the notification only arms after an enumerate (documented quirk).
        ep.NotificationRegistered = Succeeded(SessionManagerRegisterSessionNotification(ep.SessionManager, _notificationObject));

        if (Succeeded(SessionManagerGetSessionEnumerator(ep.SessionManager, out nint sessionEnum)) && sessionEnum != 0)
        {
            if (Succeeded(SessionEnumeratorGetCount(sessionEnum, out int count)))
            {
                for (int i = 0; i < count; i++)
                {
                    if (!Succeeded(SessionEnumeratorGetSession(sessionEnum, i, out nint control)) || control == 0) continue;
                    var session = WrapSession(control);
                    Release(control);
                    if (session is not null) ep.Sessions.Add(session);
                }
            }
            Release(sessionEnum);
        }
        _endpoints.Add(ep);
    }

    /// <summary>QI the session's Control2 / Meter / Volume and read its fixed identity. Does NOT take ownership of <paramref name="control"/>.</summary>
    private CachedSession? WrapSession(nint control)
    {
        var s = new CachedSession();
        var c2Iid = IID_IAudioSessionControl2;
        if (!Succeeded(QueryInterface(control, ref c2Iid, out s.Control2)) || s.Control2 == 0) { s.Release(); return null; }
        var meterIid = IID_IAudioMeterInformation;
        if (!Succeeded(QueryInterface(control, ref meterIid, out s.Meter)) || s.Meter == 0) { s.Release(); return null; }
        var volIid = IID_ISimpleAudioVolume;
        if (!Succeeded(QueryInterface(control, ref volIid, out s.Volume))) s.Volume = 0;

        string instanceId = Succeeded(SessionControl2GetSessionInstanceIdentifier(s.Control2, out nint idPtr)) ? TakeCoTaskString(idPtr) : string.Empty;
        if (instanceId.Length == 0) { s.Release(); return null; }
        s.Id = new AudioAppMeter_SessionId(AppMeter_SessionTable.Fnv1a64(instanceId));

        int hr = SessionControl2GetProcessId(s.Control2, out uint pid);
        if (!Succeeded(hr) || hr == AUDCLNT_S_NO_SINGLE_PROCESS || pid == 0)
        {
            s.Pid = new AudioAppMeter_ProcessId(0);
            s.BaseFlags |= AudioAppMeter_SessionFlags.Unattributed;
        }
        else s.Pid = new AudioAppMeter_ProcessId((int)pid);

        if (SessionControl2IsSystemSoundsSession(s.Control2) == S_OK)
            s.BaseFlags |= AudioAppMeter_SessionFlags.SystemSounds;

        string name = Succeeded(SessionControlGetDisplayName(s.Control2, out nint namePtr)) ? TakeCoTaskString(namePtr) : string.Empty;
        lock (_consumerLock) _displayNames[s.Id] = name;
        return s;
    }

    /// <summary>One D3 tick: fold every cached session's peak/state/mute into the table; fire <see cref="SessionsChanged"/> once if membership moved.</summary>
    private void PollOnce()
    {
        _table.BeginPoll();
        foreach (var ep in _endpoints)
        {
            for (int i = ep.Sessions.Count - 1; i >= 0; i--)
            {
                var s = ep.Sessions[i];
                if (!Succeeded(SessionControlGetState(s.Control2, out var osState)))
                {
                    // Session object died under us: stop polling it; the table strikes it out as absent.
                    s.Release();
                    ep.Sessions.RemoveAt(i);
                    continue;
                }
                float peak = 0f;
                if (osState == AudioSessionState.Active && Succeeded(MeterGetPeakValue(s.Meter, out float p))) peak = p;

                var flags = s.BaseFlags;
                if (s.Volume != 0 && Succeeded(SimpleAudioVolumeGetMute(s.Volume, out int mute)) && mute != 0)
                    flags |= AudioAppMeter_SessionFlags.Muted;

                var state = osState switch
                {
                    AudioSessionState.Active => AudioAppMeter_SessionState.Active,
                    AudioSessionState.Inactive => AudioAppMeter_SessionState.Inactive,
                    _ => AudioAppMeter_SessionState.Expired,
                };
                _table.Observe(s.Id, ep.Id, s.Pid, peak, state, flags);

                if (state == AudioAppMeter_SessionState.Expired)
                {
                    // Expired never comes back; release the COM object now. Table drops the row after ExpiryStrikes.
                    s.Release();
                    ep.Sessions.RemoveAt(i);
                }
            }
        }
        bool changed = _table.EndPoll();

        if (_masterMeter != 0 && Succeeded(MeterGetPeakValue(_masterMeter, out float master)))
        {
            lock (_consumerLock)
                if (master > _masterPeakMaxSinceRead) _masterPeakMaxSinceRead = master;
        }

        if (changed)
        {
            try { SessionsChanged?.Invoke(); }
            catch { /* consumer's problem; never kill the poll thread over it */ }
        }
    }
}