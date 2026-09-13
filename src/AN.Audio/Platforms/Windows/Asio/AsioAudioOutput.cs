using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AN.Audio.Internal;

namespace AN.Audio.Platforms.Windows.Asio;

/// <summary>
/// Spec 70 — <see cref="IAudioOutput"/> over the user's installed ASIO driver. Buffer size is the driver's preferred size (D5), the sample
/// rate is the consumer's if the driver accepts it, else the driver's with resampling (D6), channel types are read per channel (D7),
/// outputs start at <see cref="AudioOutputOptions.Asio_OutputChannelOffset"/> (D8). The RT callback pulls interleaved Float32 through
/// <see cref="AudioFormatConverter"/> and deinterleaves through <see cref="Asio_PlanarWriter"/> (§4). Reset/loss per D13.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class AsioAudioOutput : IAudioOutput
{
    private readonly AudioFormat _consumerFormat;
    private readonly AudioOutputOptions _options;
    private readonly Asio_DriverHost _host;
    private readonly AudioDeviceInfo _device;

    // Stream configuration (rebuilt on reset)
    private AudioFormat _deviceFormat;
    private AudioFormat _renderFormat;         // (driverRate, usedChannels, Float32) — what the converter produces
    private AudioFormatConverter? _converter;
    private int _periodFrames;
    private int[] _hwChannels = [];             // hardware output indices we render to
    private Asio_SampleType[] _channelTypes = [];
    private nint[] _buffers0 = [], _buffers1 = [];
    private float[] _scratch = [];              // interleaved Float32, periodFrames × usedChannels
    private double _latencyMs;
    private AudioOutput_LatencyFallbackReason _fallbackReason;

    // Hot-path state
    private volatile AudioCallback? _callback;
    private volatile bool _running;
    private int _inCallback;                    // re-entrancy flag (D12)
    private long _underrunCount;
    private int _resetPending;                  // coalesces duplicate kAsioResetRequest deliveries
    private volatile bool _lost;                // D13: device gone — reported once, no further resets
    private bool _disposed;

    // ── IAudioOutput surface ──
    public AudioFormat Format => _consumerFormat;
    public AudioFormat DeviceFormat => _deviceFormat;
    public double LatencyMs => _latencyMs;
    public int PeriodFrames => _periodFrames;
    public AudioOutput_LatencyMode LatencyModeActual => AudioOutput_LatencyMode.LowLatency;      // ASIO has no "default" path (D5)
    public AudioOutput_StreamProcessing StreamProcessingActual => AudioOutput_StreamProcessing.Raw; // no APO chain by construction
    public AudioOutput_LatencyFallbackReason LatencyFallbackReason => _fallbackReason;
    public long UnderrunCount => Interlocked.Read(ref _underrunCount);
    public AudioSwitchPolicy SwitchPolicy { get => AudioSwitchPolicy.None; set { /* D14: no default ASIO driver exists; a fixed device is the only meaning */ } }
    public IReadOnlyList<string>? PreferredDevices { get; set; }
    public AudioDeviceInfo? CurrentDevice => _device;
    public event Action<AudioFormat>? DeviceFormatChanged;
    public event Action<DeviceLostReason>? DeviceLost;
    public event Action<AudioDeviceInfo>? DeviceSwitched;

    /// <summary>Driver facts for diagnostics (SimpleAudioTest --asio-probe).</summary>
    public Asio_DriverHost Host => _host;

    public AsioAudioOutput(AudioFormat consumerFormat, AudioOutputOptions options, Asio_DriverInfo driver)
    {
        _consumerFormat = consumerFormat;
        _options = options;
        PreferredDevices = options.PreferredDevices;
        _device = new AudioDeviceInfo(driver.Key.ToDeviceId(), driver.Name + " (ASIO)", isDefault: false);
        _host = Asio_DriverHost.Open(driver, options.Asio_OwnerWindow);
        _host.BufferSwitch = OnBufferSwitch;
        _host.Message = OnMessage;
        _host.SampleRateDidChange = _ => RequestReset();
        try { _host.RunOnHost(Configure); }
        catch { _host.Dispose(); throw; }
    }

    // ─── configuration (host thread) ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>D6 rate → D7 channel types → D5 preferred size → createBuffers. Runs on the host thread at open and after a reset.</summary>
    private void Configure()
    {
        _fallbackReason = AudioOutput_LatencyFallbackReason.None;

        // D6 (amended): by default keep the device clock and resample — switching the hardware rate mutes the M4's outputs for 1–2 s while it
        // relocks (the head of the stream is lost). SetDeviceRate opts in; setSampleRate must precede createBuffers (SDK).
        double wanted = _consumerFormat.SampleRate;
        if (Math.Abs(_host.SampleRate - wanted) > 0.5)
        {
            bool set = _options.Asio_SampleRate == Asio_SampleRatePolicy.SetDeviceRate
                && _host.CanSampleRate(wanted) == Asio_Error.ASE_OK && _host.SetSampleRate(wanted) == Asio_Error.ASE_OK && Math.Abs(_host.SampleRate - wanted) <= 0.5;
            if (!set) _fallbackReason = AudioOutput_LatencyFallbackReason.DriverRateAdopted;
        }
        if (_host.SampleRate <= 0) throw new InvalidOperationException($"ASIO driver {_host.DriverName} reports no sample rate (ASE_NoClock)");

        // D8: hardware outputs [offset, offset + consumer channels) clipped to what the driver has.
        int offset = Math.Max(0, _options.Asio_OutputChannelOffset.Value);
        int count = Math.Min(_consumerFormat.Channels, _host.OutputChannels - offset);
        if (count <= 0) throw new InvalidOperationException($"ASIO driver {_host.DriverName} has {_host.OutputChannels} outputs; offset {offset} leaves none");
        _hwChannels = Enumerable.Range(offset, count).ToArray();

        // D7: per-channel sample types; refuse anything the planar writer cannot render.
        _channelTypes = new Asio_SampleType[count];
        for (int i = 0; i < count; i++)
        {
            var ci = _host.GetChannelInfo(_hwChannels[i], input: false);
            if (!Asio_PlanarWriter.IsSupported(ci.Type))
                throw new NotSupportedException($"ASIO output {_hwChannels[i]} ('{Asio_DriverHost.ChannelName(ci)}') uses sample type {ci.Type}, which AN.Audio does not render");
            _channelTypes[i] = ci.Type;
        }

        // D5: the driver's preferred size, never anything else.
        _periodFrames = _host.BufferPreferred;
        int maxBps = 0; foreach (var t in _channelTypes) maxBps = Math.Max(maxBps, Asio_PlanarWriter.BytesPerSample(t));
        var table = _host.CreateBuffers(_hwChannels, ReadOnlySpan<int>.Empty, _periodFrames, maxBps);
        _buffers0 = new nint[count]; _buffers1 = new nint[count];
        for (int i = 0; i < count; i++) { _buffers0[i] = table[i].Buffers0; _buffers1[i] = table[i].Buffers1; }

        int rate = (int)Math.Round(_host.SampleRate);
        _renderFormat = new AudioFormat(rate, count, SampleFormat.Float32);
        _deviceFormat = new AudioFormat(rate, count, Asio_PlanarWriter.ToSampleFormat(_channelTypes[0]));
        if (_converter is null) _converter = new AudioFormatConverter(_consumerFormat, _renderFormat); else _converter.UpdateDeviceFormat(_renderFormat);
        _scratch = new float[_periodFrames * count];
        _latencyMs = _host.OutputLatencyFrames * 1000.0 / rate;
    }

    private void Teardown()
    {
        _host.DisposeBuffers();
        _buffers0 = []; _buffers1 = [];
    }

    // ─── Start / Stop ────────────────────────────────────────────────────────────────────────────────────────────────────

    public void Start(AudioCallback callback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running) throw new InvalidOperationException("Already started");
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        _converter?.Reset();
        _running = true;
        Interlocked.Exchange(ref _underrunCount, 0);
        try { _host.RunOnHost(_host.Start); }
        catch { _running = false; _callback = null; throw; }
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _callback = null;
        if (_host.IsHostThread) throw new InvalidOperationException("Stop() must not be called from a driver callback");
        _host.RunOnHost(_host.Stop); // SDK: on return from stop() the driver no longer calls bufferSwitch (I5)
    }

    // ─── hot path (driver RT thread) ──────────────────────────────────────────────────────────────────────────────────

    private void OnBufferSwitch(int index, Asio_Time* time)
    {
        if (Interlocked.Exchange(ref _inCallback, 1) == 1) { Interlocked.Increment(ref _underrunCount); return; } // D12: still busy with the previous half
        try
        {
            var buffers = index == 0 ? _buffers0 : _buffers1;
            var callback = _callback; var converter = _converter;
            int channels = _hwChannels.Length, frames = _periodFrames;
            if (buffers.Length != channels || frames == 0) return; // mid-reset
            int written = 0;
            if (_running && callback is not null && converter is not null)
                written = converter.FillDeviceBuffer(MemoryMarshal.AsBytes(_scratch.AsSpan(0, frames * channels)), frames, callback);
            for (int c = 0; c < channels; c++)
            {
                if (written > 0) Asio_PlanarWriter.WriteChannel(_scratch, written, channels, c, _channelTypes[c], (byte*)buffers[c], frames);
                else Asio_PlanarWriter.Clear(_channelTypes[c], (byte*)buffers[c], frames);
            }
            _host.OutputReady(); // D9: no-op when the driver said ASE_NotPresent
        }
        finally { Volatile.Write(ref _inCallback, 0); }
    }

    // ─── driver messages / reset protocol (D12, D13) ──────────────────────────────────────────────────────────────────

    private int OnMessage(Asio_MessageSelector selector, int value)
    {
        switch (selector)
        {
            case Asio_MessageSelector.kAsioOverload:
                Interlocked.Increment(ref _underrunCount); return 1;
            case Asio_MessageSelector.kAsioResyncRequest:
                Interlocked.Increment(ref _underrunCount); RequestReset(); return 1;
            case Asio_MessageSelector.kAsioResetRequest:
                RequestReset(); return 1;
            case Asio_MessageSelector.kAsioLatenciesChanged:
                _host.PostToHost(() => { _host.ReadLatencies(); _latencyMs = _host.OutputLatencyFrames * 1000.0 / Math.Max(1, _deviceFormat.SampleRate); });
                return 1;
            default: return 0;
        }
    }

    /// <summary>D13: stop → disposeBuffers → re-read rate/size/types → createBuffers → start (if we were running), then tell the consumer.</summary>
    private void RequestReset()
    {
        // Drivers fire kAsioResetRequest more than once per event (unplugging the M4 produced two on different threads): coalesce, and never
        // reset again once the device is lost (overview rule 8: report once, no retry loop).
        if (_lost || Interlocked.Exchange(ref _resetPending, 1) == 1) return;
        _host.PostToHost(PerformReset);
    }

    private void PerformReset()
    {
        Volatile.Write(ref _resetPending, 0);
        if (_disposed || _lost) return;
        Asio_DriverHost.Log("reset: begin");
        bool wasRunning = _running;
        var oldFormat = _deviceFormat; int oldPeriod = _periodFrames;
        try
        {
            Teardown();
            Asio_DriverHost.Log("reset: torn down, re-initialising the driver (SDK: ASIOExit + ASIOInit)");
            _host.Reinitialize();
            Configure();
            Asio_DriverHost.Log($"reset: configured period={_periodFrames} rate={_deviceFormat.SampleRate}, wasRunning={wasRunning}");
            if (wasRunning) _host.Start();
            if (_deviceFormat != oldFormat) DeviceFormatChanged?.Invoke(_deviceFormat);
            if (_deviceFormat != oldFormat || _periodFrames != oldPeriod) DeviceSwitched?.Invoke(_device); // PeriodFrames may have changed (60 D4)
        }
        catch (Exception e)
        {
            // Hardware gone or driver in a bad mode: report once, stop (overview rule 8). Never silently.
            Asio_DriverHost.Log("reset FAILED: " + e);
            _lost = true;
            _running = false; _callback = null;
            DeviceLost?.Invoke(DeviceLostReason.DeviceRemoved);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false; _callback = null;
        _host.Dispose(); // host thread teardown does stop → disposeBuffers → Release in SDK order
    }
}