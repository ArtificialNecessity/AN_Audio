using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static AN.Audio.Platforms.Windows.Asio.AsioInterop;

namespace AN.Audio.Platforms.Windows.Asio;

/// <summary>bufferSwitch(TimeInfo) delivered to a client: fill half <paramref name="index"/> now. RT thread. <paramref name="time"/> is null for the plain bufferSwitch.</summary>
internal unsafe delegate void Asio_BufferSwitchHandler(int index, Asio_Time* time);

/// <summary>
/// Spec 70 D4/D10/D11 — owns ONE ASIO driver instance: the STA host thread, its hidden top-level window, the COM object, the
/// <see cref="Asio_Callbacks"/> table and the driver-owned buffers. Every driver method except <c>outputReady</c> is called on the host
/// thread (drivers are <c>ThreadingModel=Apartment</c>; the MOTU M4 refuses MTA callers outright). <see cref="AsioAudioOutput"/> is a
/// client of this class; spec 40 capture will be the second one.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class Asio_DriverHost : IDisposable
{
    // ── identity / capabilities (read on the host thread during open, immutable afterwards until a reset re-reads them) ──
    public Asio_DriverInfo Info { get; }
    public string DriverName { get; private set; } = "";
    public int DriverVersion { get; private set; }
    public int InputChannels { get; private set; }
    public int OutputChannels { get; private set; }
    public int BufferMin { get; private set; }
    public int BufferMax { get; private set; }
    public int BufferPreferred { get; private set; }
    public int BufferGranularity { get; private set; }
    public double SampleRate { get; private set; }
    public int InputLatencyFrames { get; private set; }
    public int OutputLatencyFrames { get; private set; }
    public bool OutputReadySupported { get; private set; }
    public bool ReportsOverload { get; private set; }

    // ── driver → host events, delivered on the DRIVER'S threads (RT for BufferSwitch) ──
    /// <summary>bufferSwitch(TimeInfo): fill half <c>index</c> now. RT thread.</summary>
    public Asio_BufferSwitchHandler? BufferSwitch;
    /// <summary>asioMessage selectors that need a decision from the client (reset/resync/latencies/overload). Return value is the reply.</summary>
    public Func<Asio_MessageSelector, int, int>? Message;
    public Action<double>? SampleRateDidChange;
    /// <summary>Exceptions thrown by a client callback are fenced here (never unwound into the driver) and counted.</summary>
    public event Action<Exception>? CallbackExceptionReported;
    public long CallbackExceptionCount => Interlocked.Read(ref _callbackExceptions);
    private long _callbackExceptions;

    // ── host thread state ──
    private readonly Thread _thread;
    private readonly nint _wakeEvent;
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _queue = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private Exception? _openFailure;
    private volatile bool _quit;
    private nint _hwnd;
    private nint _driver;
    private readonly AudioOutput_NativeWindowHandle? _owner;
    private Asio_Callbacks* _callbacks;      // unmanaged: the driver keeps this pointer for the buffers' lifetime
    private Asio_BufferInfo* _bufferInfos;   // unmanaged: filled by createBuffers
    private int _bufferInfoCount;
    private bool _buffersCreated;
    private bool _started;
    private bool _disposed;

    /// <summary>D11: callbacks carry no user-data slot; exactly one live host per process.</summary>
    private static Asio_DriverHost? s_active;
    private static readonly object s_activeLock = new();

    /// <summary><c>AN_AUDIO_TRACE=1</c> → stderr trace of every driver call boundary, with thread ids (debugging the reset protocol needs it).</summary>
    internal static readonly bool Trace = Environment.GetEnvironmentVariable("AN_AUDIO_TRACE") == "1";
    internal static void Log(string message) { if (Trace) Console.Error.WriteLine($"[AN.Audio ASIO t{Environment.CurrentManagedThreadId}] {message}"); }

    // ─── open / close ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Starts the host thread, creates the window and the driver, calls <c>init</c> and reads the capabilities. Blocks until done; throws on failure.</summary>
    public static Asio_DriverHost Open(Asio_DriverInfo info, AudioOutput_NativeWindowHandle? ownerWindow)
    {
        Asio_Layouts.AssertLayouts(); // spec 71: never touch the driver with a mis-sized struct
        lock (s_activeLock)
        {
            if (s_active is not null && !s_active._disposed) throw new InvalidOperationException($"An ASIO driver ({s_active.Info.Name}) is already open in this process; ASIO drivers are single-instance (spec 70 D11).");
            var host = new Asio_DriverHost(info, ownerWindow);
            s_active = host;
            host._thread.Start();
            host._ready.Wait();
            if (host._openFailure is not null) { host.Dispose(); throw host._openFailure; }
            return host;
        }
    }

    private Asio_DriverHost(Asio_DriverInfo info, AudioOutput_NativeWindowHandle? owner)
    {
        Info = info; _owner = owner;
        _wakeEvent = CreateEventW(0, 0, 0, 0);
        if (_wakeEvent == 0) throw new InvalidOperationException("CreateEvent failed");
        _thread = new Thread(HostThreadProc) { Name = "AN.Audio ASIO host", IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA); // a REAL STA — not just "our own thread" (measured: MTA → E_NOINTERFACE)
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_thread.IsAlive && Thread.CurrentThread != _thread)
        {
            _quit = true; SetEvent(_wakeEvent);
            _thread.Join(TimeSpan.FromSeconds(5));
        }
        CloseHandle(_wakeEvent);
        _ready.Dispose();
        lock (s_activeLock) if (s_active == this) s_active = null;
    }

    // ─── host thread ─────────────────────────────────────────────────────────────────────────────────────────────

    private void HostThreadProc()
    {
        bool comInitialised = false;
        try
        {
            int hr = CoInitializeEx(0, COINIT_APARTMENTTHREADED);
            if (hr == RPC_E_CHANGED_MODE) throw new InvalidOperationException("ASIO host thread is not an STA (RPC_E_CHANGED_MODE)");
            comInitialised = hr >= 0;
            _hwnd = CreateWindowExW(0, HostWindowClass, "AN.Audio ASIO host", WS_OVERLAPPED, 0, 0, 0, 0, 0, 0, 0, 0);
            if (_hwnd == 0) throw new InvalidOperationException($"CreateWindowExW failed ({Marshal.GetLastPInvokeError()})");
            if (_owner is { } o && o.Value != 0) SetWindowLongPtrW(_hwnd, GWLP_HWNDPARENT, o.Value);

            CreateAndInitDriver();
            _ready.Set();

            // Pump: wake on our event (work queue / quit) or on any window message.
            nint wake = _wakeEvent;
            while (!_quit)
            {
                MsgWaitForMultipleObjectsEx(1, &wake, INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
                while (PeekMessageW(out MSG m, 0, 0, 0, PM_REMOVE) != 0) { TranslateMessage(ref m); DispatchMessageW(ref m); }
                while (_queue.TryDequeue(out var work)) work();
            }
        }
        catch (Exception e)
        {
            _openFailure = e;
            _ready.Set();
        }
        finally
        {
            ReleaseDriver();
            if (_hwnd != 0) { DestroyWindow(_hwnd); _hwnd = 0; }
            if (comInitialised) CoUninitialize();
        }
    }

    /// <summary>CoCreateInstance(iid = clsid) + <c>init(hwnd)</c> + capabilities. Host thread only.</summary>
    private void CreateAndInitDriver()
    {
        Guid clsid = Info.Key.Clsid, iid = clsid; // the ASIO quirk: IID == CLSID
        int hr = CoCreateInstance(ref clsid, 0, CLSCTX_INPROC_SERVER, ref iid, out _driver);
        if (hr < 0 || _driver == 0) throw new InvalidOperationException($"CoCreateInstance({clsid:B}) failed: 0x{hr:X8} — {Info.Name} ({Info.DllPath})");
        Log("init(hwnd)");
        if (Asio_Driver.Init(_driver, (void*)_hwnd) == Asio_Bool.ASIOFalse)
        {
            byte* msg = stackalloc byte[256]; Asio_Driver.GetErrorMessage(_driver, msg);
            throw new InvalidOperationException($"ASIO init failed for {Info.Name}: {Marshal.PtrToStringAnsi((nint)msg)}");
        }
        ReadCapabilities();
    }

    /// <summary>Teardown in SDK order: stop → disposeBuffers → Release. Host thread only.</summary>
    private void ReleaseDriver()
    {
        if (_driver != 0)
        {
            if (_started) { Asio_Driver.Stop(_driver); _started = false; }
            if (_buffersCreated) { Asio_Driver.DisposeBuffers(_driver); _buffersCreated = false; }
            Log("Release()");
            Asio_Driver.Release(_driver); _driver = 0;
        }
        FreeBufferMemory();
    }

    /// <summary>D13 as the SDK actually specifies it: <c>kAsioResetRequest</c> = "close the driver (ASIOExit) and re-open it (ASIOInit)". A
    /// disposeBuffers/createBuffers cycle on the SAME instance is not enough — measured on the MOTU M4: the driver kept reporting the old
    /// preferred size and never called bufferSwitch again. Host thread only; buffers are gone afterwards, the client re-configures.</summary>
    public void Reinitialize()
    {
        RequireHostThread();
        Log("reinitialize: release + create + init");
        ReleaseDriver();
        CreateAndInitDriver();
        Log($"reinitialize: preferred={BufferPreferred} [{BufferMin}..{BufferMax}] rate={SampleRate} outs={OutputChannels}");
    }

    private void ReadCapabilities()
    {
        Log("readCapabilities");
        byte* name = stackalloc byte[128];
        Asio_Driver.GetDriverName(_driver, name);
        DriverName = Marshal.PtrToStringAnsi((nint)name) ?? Info.Name;
        DriverVersion = Asio_Driver.GetDriverVersion(_driver);
        int ins, outs; Check(Asio_Driver.GetChannels(_driver, &ins, &outs), "getChannels"); InputChannels = ins; OutputChannels = outs;
        int min, max, pref, gran; Check(Asio_Driver.GetBufferSize(_driver, &min, &max, &pref, &gran), "getBufferSize");
        BufferMin = min; BufferMax = max; BufferPreferred = pref; BufferGranularity = gran;
        double rate; if (Asio_Driver.GetSampleRate(_driver, &rate) == Asio_Error.ASE_OK) SampleRate = rate;
        ReadLatencies();
        ReportsOverload = Asio_Driver.Future(_driver, (int)Asio_FutureSelector.kAsioCanReportOverload, null) == Asio_Error.ASE_SUCCESS;
    }

    public void ReadLatencies()
    {
        int i, o; if (Asio_Driver.GetLatencies(_driver, &i, &o) == Asio_Error.ASE_OK) { InputLatencyFrames = i; OutputLatencyFrames = o; }
    }

    /// <summary>D13: after <c>kAsioResetRequest</c> the driver's preferred buffer size / rate / channel count may all have changed — re-read them
    /// BEFORE re-creating buffers (creating with the stale size is <c>ASE_InvalidMode</c>).</summary>
    public void RefreshCapabilities()
    {
        RequireHostThread();
        ReadCapabilities();
        Log($"capabilities: preferred={BufferPreferred} [{BufferMin}..{BufferMax}] rate={SampleRate} outs={OutputChannels}");
    }

    // ─── marshalling onto the host thread ─────────────────────────────────────────────────────────────────────────

    public bool IsHostThread => Thread.CurrentThread == _thread;

    /// <summary>Run on the host thread and wait (I5). Exceptions propagate to the caller. Runs inline when already on the host thread.</summary>
    public void RunOnHost(Action action) => RunOnHost<object?>(() => { action(); return null; });

    public T RunOnHost<T>(Func<T> func)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsHostThread) return func();
        T result = default!; Exception? error = null;
        using var done = new ManualResetEventSlim(false);
        _queue.Enqueue(() => { try { result = func(); } catch (Exception e) { error = e; } finally { done.Set(); } });
        SetEvent(_wakeEvent);
        if (!done.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("ASIO host thread did not respond within 10 s");
        if (error is not null) throw new InvalidOperationException("ASIO driver call failed: " + error.Message, error);
        return result;
    }

    /// <summary>Post without waiting (used from driver callbacks that must return immediately, e.g. kAsioResetRequest).</summary>
    public void PostToHost(Action action) { _queue.Enqueue(action); SetEvent(_wakeEvent); }

    // ─── driver operations (host thread only; callers use RunOnHost) ─────────────────────────────────────────────────────────

    private void RequireHostThread([CallerMemberName] string caller = "") { if (!IsHostThread) throw new InvalidOperationException($"{caller} must run on the ASIO host thread (use RunOnHost)"); }
    private static void Check(Asio_Error e, string what) { if (e != Asio_Error.ASE_OK) throw new InvalidOperationException($"ASIO {what} failed: {e}"); }

    public Asio_Error CanSampleRate(double rate) { RequireHostThread(); return Asio_Driver.CanSampleRate(_driver, rate); }
    public Asio_Error SetSampleRate(double rate)
    {
        RequireHostThread();
        var e = Asio_Driver.SetSampleRate(_driver, rate);
        double r; if (Asio_Driver.GetSampleRate(_driver, &r) == Asio_Error.ASE_OK) SampleRate = r;
        return e;
    }

    public Asio_ChannelInfo GetChannelInfo(int channel, bool input)
    {
        RequireHostThread();
        Asio_ChannelInfo ci = default; ci.Channel = channel; ci.IsInput = input ? Asio_Bool.ASIOTrue : Asio_Bool.ASIOFalse;
        Check(Asio_Driver.GetChannelInfo(_driver, &ci), $"getChannelInfo({(input ? "in" : "out")} {channel})");
        return ci;
    }
    public static string ChannelName(in Asio_ChannelInfo ci) { fixed (byte* p = ci.Name) return Marshal.PtrToStringAnsi((nint)p, 32)!.TrimEnd('\0'); }

    /// <summary>D10: one createBuffers for all clients. <paramref name="outputs"/>/<paramref name="inputs"/> are hardware channel indices. Returns the
    /// buffer table (driver-owned memory; valid until <see cref="DisposeBuffers"/>). Prefills both halves with silence (SDK: buffer B must be filled before start).</summary>
    public ReadOnlySpan<Asio_BufferInfo> CreateBuffers(ReadOnlySpan<int> outputs, ReadOnlySpan<int> inputs, int bufferFrames, int outputBytesPerSample)
    {
        RequireHostThread();
        if (_buffersCreated) throw new InvalidOperationException("buffers already created");
        FreeBufferMemory();
        _bufferInfoCount = outputs.Length + inputs.Length;
        _bufferInfos = (Asio_BufferInfo*)NativeMemory.AllocZeroed((nuint)(_bufferInfoCount * sizeof(Asio_BufferInfo)));
        _callbacks = (Asio_Callbacks*)NativeMemory.AllocZeroed((nuint)sizeof(Asio_Callbacks));
        int n = 0;
        foreach (int ch in outputs) { _bufferInfos[n].IsInput = Asio_Bool.ASIOFalse; _bufferInfos[n].ChannelNum = ch; n++; }
        foreach (int ch in inputs) { _bufferInfos[n].IsInput = Asio_Bool.ASIOTrue; _bufferInfos[n].ChannelNum = ch; n++; }
        _callbacks->BufferSwitch = &OnBufferSwitch;
        _callbacks->SampleRateDidChange = &OnSampleRateDidChange;
        _callbacks->AsioMessage = &OnAsioMessage;
        _callbacks->BufferSwitchTimeInfo = &OnBufferSwitchTimeInfo;
        Log($"createBuffers({_bufferInfoCount} ch, {bufferFrames})");
        var created = Asio_Driver.CreateBuffers(_driver, _bufferInfos, _bufferInfoCount, bufferFrames, _callbacks);
        Log($"createBuffers -> {created}");
        Check(created, $"createBuffers({_bufferInfoCount} ch, {bufferFrames})");
        _buffersCreated = true;
        for (int i = 0; i < outputs.Length; i++)
        {
            new Span<byte>((void*)_bufferInfos[i].Buffers0, bufferFrames * outputBytesPerSample).Clear();
            new Span<byte>((void*)_bufferInfos[i].Buffers1, bufferFrames * outputBytesPerSample).Clear();
        }
        ReadLatencies(); // latencies are only meaningful once buffers exist
        // D9: probe outputReady once; ASE_NotPresent = unsupported (MOTU M4), never call it again.
        OutputReadySupported = Asio_Driver.OutputReady(_driver) == Asio_Error.ASE_OK;
        return new ReadOnlySpan<Asio_BufferInfo>(_bufferInfos, _bufferInfoCount);
    }

    public void DisposeBuffers()
    {
        RequireHostThread();
        if (_started) Stop();
        if (_buffersCreated) { Log("disposeBuffers()"); var e = Asio_Driver.DisposeBuffers(_driver); _buffersCreated = false; Log($"disposeBuffers() -> {e}"); }
        FreeBufferMemory();
    }

    private void FreeBufferMemory()
    {
        if (_bufferInfos != null) { NativeMemory.Free(_bufferInfos); _bufferInfos = null; }
        if (_callbacks != null) { NativeMemory.Free(_callbacks); _callbacks = null; }
        _bufferInfoCount = 0;
    }

    public void Start() { RequireHostThread(); Log("start()"); Check(Asio_Driver.Start(_driver), "start"); _started = true; Log("start() ok"); }
    public void Stop() { RequireHostThread(); if (!_started) return; Log("stop()"); var e = Asio_Driver.Stop(_driver); _started = false; Log($"stop() -> {e}"); }
    public bool IsStarted => _started;
    public void ControlPanel() { RequireHostThread(); Asio_Driver.ControlPanel(_driver); }

    /// <summary>The ONE driver call legal from the RT callback (D9).</summary>
    public void OutputReady() { if (OutputReadySupported) Asio_Driver.OutputReady(_driver); }

    // ─── driver → host trampolines (cdecl, no user data → s_active) ────────────────────────────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnBufferSwitch(int index, Asio_Bool directProcess)
    {
        var h = s_active; if (h is null) return;
        try { h.BufferSwitch?.Invoke(index, null); } catch (Exception e) { h.Fence(e); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static Asio_Time* OnBufferSwitchTimeInfo(Asio_Time* time, int index, Asio_Bool directProcess)
    {
        var h = s_active; if (h is null) return time;
        try { h.BufferSwitch?.Invoke(index, time); } catch (Exception e) { h.Fence(e); }
        return time;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSampleRateDidChange(double rate)
    {
        var h = s_active; if (h is null) return;
        try { h.SampleRateDidChange?.Invoke(rate); } catch (Exception e) { h.Fence(e); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnAsioMessage(int selector, int value, void* message, double* opt)
    {
        var h = s_active; if (h is null) return 0;
        Log($"asioMessage({(Asio_MessageSelector)selector}, value={value})");
        try
        {
            switch ((Asio_MessageSelector)selector)
            {
                case Asio_MessageSelector.kAsioSelectorSupported:
                    return (Asio_MessageSelector)value is Asio_MessageSelector.kAsioEngineVersion or Asio_MessageSelector.kAsioResetRequest
                        or Asio_MessageSelector.kAsioResyncRequest or Asio_MessageSelector.kAsioLatenciesChanged or Asio_MessageSelector.kAsioSupportsTimeInfo
                        or Asio_MessageSelector.kAsioOverload ? 1 : 0;
                case Asio_MessageSelector.kAsioEngineVersion: return 2;
                case Asio_MessageSelector.kAsioSupportsTimeInfo: return 1;   // we take bufferSwitchTimeInfo
                case Asio_MessageSelector.kAsioSupportsTimeCode: return 0;
                case Asio_MessageSelector.kAsioBufferSizeChange: return 0;   // SDK: "use kAsioResetRequest instead"
                case Asio_MessageSelector.kAsioResetRequest:
                case Asio_MessageSelector.kAsioResyncRequest:
                case Asio_MessageSelector.kAsioLatenciesChanged:
                case Asio_MessageSelector.kAsioOverload:
                    return h.Message?.Invoke((Asio_MessageSelector)selector, value) ?? 0;
                default: return 0;
            }
        }
        catch (Exception e) { h.Fence(e); return 0; }
    }

    private void Fence(Exception e)
    {
        Interlocked.Increment(ref _callbackExceptions);
        try { CallbackExceptionReported?.Invoke(e); } catch { /* never unwind into the driver */ }
    }
}