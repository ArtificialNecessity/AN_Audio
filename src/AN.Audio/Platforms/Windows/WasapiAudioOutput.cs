using System.Runtime.InteropServices;
using AN.Audio.Internal;
using static AN.Audio.Platforms.Windows.WasapiInterop;

namespace AN.Audio.Platforms.Windows;

/// <summary>
/// WASAPI shared-mode event-driven audio output with automatic device switching
/// and format conversion.
/// </summary>
internal sealed unsafe class WasapiAudioOutput : IAudioOutput
{
    private readonly AudioFormat _consumerFormat;
    private readonly int _bufferSizeMs;

    // Device management
    private readonly WasapiDeviceManager _deviceManager;
    private AudioSwitchPolicy _switchPolicy;
    private IReadOnlyList<string>? _preferredDevices;
    private AudioDeviceInfo? _currentDevice;
    private readonly object _switchLock = new();

    // COM interface pointers for the active device (released on device switch or Dispose)
    private nint _device;
    private nint _audioClient;
    private nint _renderClient;

    // Event-driven signaling
    private nint _bufferEvent;

    // Audio thread state
    private Thread? _audioThread;
    private volatile bool _running;
    private volatile bool _deviceSwitchRequested;
    private AudioCallback? _callback;

    // Format
    private AudioFormat _deviceFormat;
    private AudioFormatConverter? _converter;
    private uint _bufferFrameCount;
    private int _deviceFrameBytes; // bytes per frame in the device format

    // Latency
    private double _latencyMs;
    // Spec 60: requested vs actual scheduling
    private readonly AudioOutput_LatencyMode _latencyRequested;
    private readonly AudioOutput_StreamProcessing _processingRequested;
    private AudioOutput_LatencyMode _latencyActual;
    private AudioOutput_StreamProcessing _processingActual;
    private AudioOutput_LatencyFallbackReason _fallbackReason;
    private int _periodFrames;
    private long _underrunCount;
    private bool _firstFillDone;
    /// <summary>Which IAudioClient generation Activate gave us: 1, 2 or 3.</summary>
    private int _audioClientVersion;
    /// <summary>Exclusive mode in effect: the buffer IS the period and is written whole every event; padding-based underrun detection does not apply.</summary>
    private bool _exclusive;
    /// <summary>The format the exclusive stream was initialised with (storage for the pointer the parser/converter read).</summary>
    private WAVEFORMATEXTENSIBLE _exclusiveFormat;

    private bool _disposed;

    // ── IAudioOutput Properties ──────────────────────────────────────────────────

    public AudioFormat Format => _consumerFormat;
    public AudioFormat DeviceFormat => _deviceFormat;
    public double LatencyMs => _latencyMs;
    public int PeriodFrames => _periodFrames;
    public AudioOutput_LatencyMode LatencyModeActual => _latencyActual;
    public AudioOutput_StreamProcessing StreamProcessingActual => _processingActual;
    public AudioOutput_LatencyFallbackReason LatencyFallbackReason => _fallbackReason;
    public long UnderrunCount => Interlocked.Read(ref _underrunCount);

    public AudioSwitchPolicy SwitchPolicy
    {
        get => _switchPolicy;
        set => _switchPolicy = value;
    }

    public IReadOnlyList<string>? PreferredDevices
    {
        get => _preferredDevices;
        set
        {
            _preferredDevices = value;
            // If policy is PreferenceList and we're running, re-evaluate
            if (_switchPolicy == AudioSwitchPolicy.PreferenceList && _running)
                _deviceSwitchRequested = true;
        }
    }

    public AudioDeviceInfo? CurrentDevice => _currentDevice;

    public event Action<AudioFormat>? DeviceFormatChanged;
    public event Action<DeviceLostReason>? DeviceLost;
    public event Action<AudioDeviceInfo>? DeviceSwitched;

    // ── Constructor ──────────────────────────────────────────────────────────────

    public WasapiAudioOutput(AudioFormat consumerFormat, AudioOutputOptions? options = null)
    {
        _consumerFormat = consumerFormat;
        _bufferSizeMs = options?.BufferSizeMs ?? 20;
        _switchPolicy = options?.SwitchPolicy ?? AudioSwitchPolicy.FollowDefault;
        _preferredDevices = options?.PreferredDevices;
        _latencyRequested = options?.Latency ?? AudioOutput_LatencyMode.Default;
        _processingRequested = options?.Processing ?? AudioOutput_StreamProcessing.SystemEffects;

        _deviceManager = WasapiDeviceManager.Instance;

        // Open the initial device
        OpenDevice(ResolveDeviceId());

        // Subscribe to device manager events
        _deviceManager.DefaultDeviceChanged += OnDefaultDeviceChanged;
        _deviceManager.DeviceListChanged += OnDeviceListChanged;
    }

    // For backward compatibility
    public WasapiAudioOutput(AudioFormat consumerFormat, int bufferSizeMs)
        : this(consumerFormat, new AudioOutputOptions { BufferSizeMs = bufferSizeMs })
    {
    }

    // ── Device Resolution ────────────────────────────────────────────────────────

    /// <summary>
    /// Determine which device ID to open based on the current policy.
    /// Returns null for system default.
    /// </summary>
    private string? ResolveDeviceId()
    {
        if (_switchPolicy == AudioSwitchPolicy.PreferenceList && _preferredDevices != null)
        {
            // Get available devices
            var available = _deviceManager.GetOutputDevices();
            var availableIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in available)
                availableIds.Add(d.Id);

            // Find highest-priority preferred device that's available
            foreach (var prefId in _preferredDevices)
            {
                if (availableIds.Contains(prefId))
                    return prefId;
            }
        }

        // FollowDefault, None, or no preferred device available → system default
        return null;
    }

    // ── Device Open/Close ────────────────────────────────────────────────────────

    /// <summary>
    /// Open a WASAPI device and initialize the audio client.
    /// deviceId = null means system default.
    /// </summary>
    private void OpenDevice(string? deviceId)
    {
        // Init COM on this thread
        int hr = CoInitializeEx(0, COINIT_MULTITHREADED);
        if (hr < 0 && hr != unchecked((int)0x80010106))
            Marshal.ThrowExceptionForHR(hr);

        // Get the device
        if (deviceId != null)
        {
            // Open specific device by ID
            nint enumerator = 0;
            Guid clsid = CLSID_MMDeviceEnumerator;
            Guid iid = IID_IMMDeviceEnumerator;
            hr = CoCreateInstance(ref clsid, 0, CLSCTX_ALL, ref iid, out enumerator);
            Marshal.ThrowExceptionForHR(hr);

            try
            {
                hr = GetDevice(enumerator, deviceId, out _device);
                Marshal.ThrowExceptionForHR(hr);
            }
            finally
            {
                if (enumerator != 0) Release(enumerator);
            }
        }
        else
        {
            // Open system default
            nint enumerator = 0;
            Guid clsid = CLSID_MMDeviceEnumerator;
            Guid iid = IID_IMMDeviceEnumerator;
            hr = CoCreateInstance(ref clsid, 0, CLSCTX_ALL, ref iid, out enumerator);
            Marshal.ThrowExceptionForHR(hr);

            try
            {
                hr = GetDefaultAudioEndpoint(enumerator, eRender, eConsole, out _device);
                Marshal.ThrowExceptionForHR(hr);
            }
            finally
            {
                if (enumerator != 0) Release(enumerator);
            }
        }

        // Spec 60: the newest IAudioClient generation the OS offers. IAudioClient3 (Win10 1607+) carries the low-latency shared path,
        // IAudioClient2 (Win8+) carries SetClientProperties (RAW). The object is the same; only the vtable length differs.
        _fallbackReason = AudioOutput_LatencyFallbackReason.None;
        _latencyActual = _latencyRequested;
        _processingActual = _processingRequested;
        _audioClientVersion = ActivateNewestAudioClient(out _audioClient);
        if (_latencyRequested == AudioOutput_LatencyMode.LowLatency && _audioClientVersion < 3)
        {
            _latencyActual = AudioOutput_LatencyMode.Default;
            _fallbackReason = AudioOutput_LatencyFallbackReason.OsTooOld;
        }

        // RAW processing (bypass the endpoint's APO chain) must be declared BEFORE GetMixFormat/Initialize (the mix format may differ in raw mode).
        if (_processingRequested == AudioOutput_StreamProcessing.Raw)
        {
            if (_audioClientVersion >= 2)
            {
                var props = new AudioClientProperties { cbSize = (uint)sizeof(AudioClientProperties), bIsOffload = 0, eCategory = AudioCategory_Other, Options = AUDCLNT_STREAMOPTIONS_RAW };
                int rawHr = AudioClientSetClientProperties(_audioClient, ref props);
                if (rawHr < 0)
                {
                    // AUDCLNT_E_RAW_MODE_UNSUPPORTED or anything else: the stream still works, with system effects.
                    _processingActual = AudioOutput_StreamProcessing.SystemEffects;
                    if (_fallbackReason == AudioOutput_LatencyFallbackReason.None) _fallbackReason = AudioOutput_LatencyFallbackReason.RawModeUnsupported;
                }
            }
            else
            {
                _processingActual = AudioOutput_StreamProcessing.SystemEffects;
                if (_fallbackReason == AudioOutput_LatencyFallbackReason.None) _fallbackReason = AudioOutput_LatencyFallbackReason.OsTooOld;
            }
        }

        // Get the endpoint's mix format
        WAVEFORMATEX* mixFormat;
        hr = AudioClientGetMixFormat(_audioClient, out mixFormat);
        Marshal.ThrowExceptionForHR(hr);

        // Parse the device format
        _deviceFormat = ParseWaveFormat(mixFormat);
        _deviceFrameBytes = _deviceFormat.BytesPerFrame;

        // Create or update the format converter
        if (_converter == null)
            _converter = new AudioFormatConverter(_consumerFormat, _deviceFormat);
        else
            _converter.UpdateDeviceFormat(_deviceFormat);

        // ── Initialise: low-latency shared stream when asked for and available, else the classic buffered path ──
        uint lowLatencyPeriodFrames = 0;
        _exclusive = false;
        if (_latencyActual == AudioOutput_LatencyMode.Exclusive)
        {
            hr = InitializeExclusive(mixFormat, out lowLatencyPeriodFrames);
            if (hr >= 0)
            {
                _exclusive = true;
                _processingActual = AudioOutput_StreamProcessing.Raw; // exclusive streams bypass the endpoint effects chain by construction
                fixed (WAVEFORMATEXTENSIBLE* pExclusive = &_exclusiveFormat) { _deviceFormat = ParseWaveFormat((WAVEFORMATEX*)pExclusive); }
                _deviceFrameBytes = _deviceFormat.BytesPerFrame;
                _converter.UpdateDeviceFormat(_deviceFormat);
            }
            else
            {
                // Endpoint busy, exclusive disabled in Sound settings, or no exclusive-capable Int16/Float32 format: shared it is.
                _latencyActual = _audioClientVersion >= 3 ? AudioOutput_LatencyMode.LowLatency : AudioOutput_LatencyMode.Default;
                _fallbackReason = AudioOutput_LatencyFallbackReason.ExclusiveRefused;
            }
        }
        if (_latencyActual == AudioOutput_LatencyMode.LowLatency)
        {
            hr = InitializeLowLatency(mixFormat, out lowLatencyPeriodFrames);
            if (hr < 0)
            {
                // The engine/driver refused the small period: fall back to the classic path on the same client (Initialize is still legal —
                // a failed InitializeSharedAudioStream leaves the client uninitialised).
                _latencyActual = AudioOutput_LatencyMode.Default;
                _fallbackReason = AudioOutput_LatencyFallbackReason.DriverRefused;
            }
        }
        if (_latencyActual == AudioOutput_LatencyMode.Default)
        {
            long hnsBufferDuration = (long)_bufferSizeMs * REFTIMES_PER_MS;
            hr = AudioClientInitialize(_audioClient, AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_EVENTCALLBACK, hnsBufferDuration, 0, mixFormat, 0);
        }

        CoTaskMemFree((nint)mixFormat);
        Marshal.ThrowExceptionForHR(hr);

        // Create the buffer-ready event (auto-reset)
        _bufferEvent = CreateEventW(0, 0, 0, 0);
        if (_bufferEvent == 0)
            throw new InvalidOperationException("Failed to create audio buffer event");

        hr = AudioClientSetEventHandle(_audioClient, _bufferEvent);
        Marshal.ThrowExceptionForHR(hr);

        hr = AudioClientGetBufferSize(_audioClient, out _bufferFrameCount);
        Marshal.ThrowExceptionForHR(hr);

        // Period actually in effect (D4): the frames per wake. Low latency = what we initialised with; default = the engine's default period.
        if (lowLatencyPeriodFrames > 0) _periodFrames = (int)lowLatencyPeriodFrames;
        else
        {
            _periodFrames = AudioClientGetDevicePeriod(_audioClient, out long hnsDefaultPeriod, out _) >= 0
                ? (int)Math.Max(1, Math.Round(hnsDefaultPeriod * (double)_deviceFormat.SampleRate / (REFTIMES_PER_MS * 1000.0)))
                : (int)_bufferFrameCount;
        }
        _firstFillDone = false;

        // Get latency
        long hnsLatency;
        hr = AudioClientGetStreamLatency(_audioClient, out hnsLatency);
        _latencyMs = hr >= 0 ? hnsLatency / (double)REFTIMES_PER_MS : _bufferSizeMs;

        // Get render client
        Guid renderIid = IID_IAudioRenderClient;
        hr = AudioClientGetService(_audioClient, ref renderIid, out _renderClient);
        Marshal.ThrowExceptionForHR(hr);

        // Update current device info
        _currentDevice = _deviceManager.GetDeviceById(deviceId ?? _deviceManager.GetDefaultDeviceId() ?? "");
    }

    /// <summary>Activate IAudioClient3, then 2, then 1. Returns the generation obtained (3/2/1); throws only if even IAudioClient fails.</summary>
    private int ActivateNewestAudioClient(out nint client)
    {
        Guid iid3 = IID_IAudioClient3;
        if (DeviceActivate(_device, ref iid3, CLSCTX_ALL, 0, out client) >= 0 && client != 0) return 3;
        Guid iid2 = IID_IAudioClient2;
        if (DeviceActivate(_device, ref iid2, CLSCTX_ALL, 0, out client) >= 0 && client != 0) return 2;
        Guid iid1 = IID_IAudioClient;
        Marshal.ThrowExceptionForHR(DeviceActivate(_device, ref iid1, CLSCTX_ALL, 0, out client));
        return 1;
    }

    /// <summary>Release the current IAudioClient and Activate a fresh one (same generation), re-applying RAW if it was granted. Required after a
    /// failed <c>Initialize</c> in exclusive mode (<c>AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED</c> documents this; other failures leave the object unusable too).</summary>
    private void ReactivateClient()
    {
        if (_audioClient != 0) { Release(_audioClient); _audioClient = 0; }
        _audioClientVersion = ActivateNewestAudioClient(out _audioClient);
        if (_processingActual == AudioOutput_StreamProcessing.Raw && _audioClientVersion >= 2)
        {
            var props = new AudioClientProperties { cbSize = (uint)sizeof(AudioClientProperties), bIsOffload = 0, eCategory = AudioCategory_Other, Options = AUDCLNT_STREAMOPTIONS_RAW };
            AudioClientSetClientProperties(_audioClient, ref props);
        }
    }

    /// <summary>Spec 60 §4 (Exclusive): event-driven exclusive stream at the driver's MINIMUM device period. Tries the endpoint's mix format, then
    /// Int16/Float32 WAVEFORMATEXTENSIBLE, then plain PCM16 (the formats <see cref="AudioFormatConverter"/> can produce). Handles
    /// <c>AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED</c> by re-initialising at the driver's aligned buffer size. On success <see cref="_exclusiveFormat"/>
    /// holds the format in use and <paramref name="periodFrames"/> the buffer (= period) length. On failure the client has been re-activated and
    /// is ready for a shared-mode Initialize.</summary>
    private int InitializeExclusive(WAVEFORMATEX* mixFormat, out uint periodFrames)
    {
        periodFrames = 0;
        int hr = AudioClientGetDevicePeriod(_audioClient, out _, out long hnsMinPeriod);
        if (hr < 0) return hr;
        bool trace = Environment.GetEnvironmentVariable("AN_AUDIO_TRACE") == "1";

        // Candidate formats in preference order. All share the endpoint's rate and channel count.
        uint rate = mixFormat->nSamplesPerSec; ushort channels = mixFormat->nChannels;
        uint channelMask = mixFormat->wFormatTag == WAVE_FORMAT_EXTENSIBLE ? ((WAVEFORMATEXTENSIBLE*)mixFormat)->dwChannelMask : (channels == 1 ? 0x4u : 0x3u);
        WAVEFORMATEXTENSIBLE* candidates = stackalloc WAVEFORMATEXTENSIBLE[4];
        int candidateCount = 0;
        // 0: the mix format itself (copied; it may be a plain WAVEFORMATEX, which is fine — cbSize decides what the driver reads)
        candidates[candidateCount++] = mixFormat->wFormatTag == WAVE_FORMAT_EXTENSIBLE ? *(WAVEFORMATEXTENSIBLE*)mixFormat : new WAVEFORMATEXTENSIBLE { Format = *mixFormat };
        candidates[candidateCount++] = MakeExtensible(rate, channels, 16, channelMask, KSDATAFORMAT_SUBTYPE_PCM);
        candidates[candidateCount++] = MakeExtensible(rate, channels, 32, channelMask, KSDATAFORMAT_SUBTYPE_IEEE_FLOAT);
        candidates[candidateCount++] = new WAVEFORMATEXTENSIBLE { Format = new WAVEFORMATEX { wFormatTag = WAVE_FORMAT_PCM, nChannels = channels, nSamplesPerSec = rate, wBitsPerSample = 16, nBlockAlign = (ushort)(2 * channels), nAvgBytesPerSec = rate * 2u * channels, cbSize = 0 } };

        int chosen = -1;
        for (int i = 0; i < candidateCount && chosen < 0; i++)
        {
            // Only formats the converter can render into: 16-bit PCM or 32-bit float.
            var probe = ParseWaveFormat((WAVEFORMATEX*)&candidates[i]);
            bool renderable = (probe.Format == SampleFormat.Int16 && candidates[i].Format.wBitsPerSample == 16) || (probe.Format == SampleFormat.Float32 && candidates[i].Format.wBitsPerSample == 32);
            if (!renderable) continue;
            hr = AudioClientIsFormatSupported(_audioClient, AUDCLNT_SHAREMODE_EXCLUSIVE, (WAVEFORMATEX*)&candidates[i], out WAVEFORMATEX* closest);
            if (closest != null) CoTaskMemFree((nint)closest);
            if (trace) Console.Error.WriteLine($"[AN.Audio] exclusive IsFormatSupported[{i}] tag=0x{candidates[i].Format.wFormatTag:X} bits={candidates[i].Format.wBitsPerSample} -> 0x{hr:X8}");
            if (hr == 0) chosen = i;
        }
        if (chosen < 0) return unchecked((int)AudioClientHResult.AUDCLNT_E_UNSUPPORTED_FORMAT);
        _exclusiveFormat = candidates[chosen];

        fixed (WAVEFORMATEXTENSIBLE* pFormat = &_exclusiveFormat)
        {
            hr = AudioClientInitialize(_audioClient, AUDCLNT_SHAREMODE_EXCLUSIVE, AUDCLNT_STREAMFLAGS_EVENTCALLBACK, hnsMinPeriod, hnsMinPeriod, (WAVEFORMATEX*)pFormat, 0);
            if (trace) Console.Error.WriteLine($"[AN.Audio] exclusive Initialize minPeriod={hnsMinPeriod / 10000.0:F3} ms -> 0x{hr:X8}");
            if ((uint)hr == (uint)AudioClientHResult.AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED)
            {
                // The driver wants a buffer that is a whole number of its own frames; it tells us the size, we re-init with the matching duration.
                if (AudioClientGetBufferSize(_audioClient, out uint alignedFrames) >= 0 && alignedFrames > 0)
                {
                    long hnsAligned = (long)Math.Round(10_000_000.0 * alignedFrames / rate);
                    ReactivateClient();
                    hr = AudioClientInitialize(_audioClient, AUDCLNT_SHAREMODE_EXCLUSIVE, AUDCLNT_STREAMFLAGS_EVENTCALLBACK, hnsAligned, hnsAligned, (WAVEFORMATEX*)pFormat, 0);
                    if (trace) Console.Error.WriteLine($"[AN.Audio] exclusive Initialize aligned {alignedFrames} frames = {hnsAligned / 10000.0:F3} ms -> 0x{hr:X8}");
                }
            }
        }
        if (hr < 0) { ReactivateClient(); return hr; }
        hr = AudioClientGetBufferSize(_audioClient, out uint bufferFrames);
        if (hr < 0) { ReactivateClient(); return hr; }
        periodFrames = bufferFrames; // exclusive event-driven: buffer == period, filled whole every event
        return 0;
    }

    private static WAVEFORMATEXTENSIBLE MakeExtensible(uint rate, ushort channels, ushort bits, uint channelMask, Guid subFormat)
    {
        ushort blockAlign = (ushort)(bits / 8 * channels);
        return new WAVEFORMATEXTENSIBLE
        {
            Format = new WAVEFORMATEX { wFormatTag = WAVE_FORMAT_EXTENSIBLE, nChannels = channels, nSamplesPerSec = rate, wBitsPerSample = bits, nBlockAlign = blockAlign, nAvgBytesPerSec = rate * blockAlign, cbSize = 22 },
            wValidBitsPerSample = bits, dwChannelMask = channelMask, SubFormat = subFormat,
        };
    }

    /// <summary>Spec 60 §4: ask the engine for its supported period range at this format and initialise at the MINIMUM. If another stream has
    /// already locked the engine to a different small period, adopt that one (informational fallback reason). Returns the HRESULT of the
    /// initialisation; <paramref name="periodFrames"/> is the period used on success.</summary>
    private int InitializeLowLatency(WAVEFORMATEX* mixFormat, out uint periodFrames)
    {
        periodFrames = 0;
        int hr = AudioClientGetSharedModeEnginePeriod(_audioClient, mixFormat, out uint defaultPeriod, out uint fundamentalPeriod, out uint minPeriod, out uint maxPeriod);
        if (Environment.GetEnvironmentVariable("AN_AUDIO_TRACE") == "1")
            Console.Error.WriteLine($"[AN.Audio] GetSharedModeEnginePeriod hr=0x{hr:X8} default={defaultPeriod} fundamental={fundamentalPeriod} min={minPeriod} max={maxPeriod} frames @ {mixFormat->nSamplesPerSec} Hz");
        if (hr < 0) return hr;
        hr = AudioClientInitializeSharedAudioStream(_audioClient, AUDCLNT_STREAMFLAGS_EVENTCALLBACK, minPeriod, mixFormat, 0);
        if (hr >= 0) { periodFrames = minPeriod; return hr; }
        if ((uint)hr != (uint)AudioClientHResult.AUDCLNT_E_ENGINE_PERIODICITY_LOCKED) return hr;

        // Engine already running at someone else's period: take it.
        hr = AudioClientGetCurrentSharedModeEnginePeriod(_audioClient, out WAVEFORMATEX* currentFormat, out uint currentPeriod);
        if (hr < 0) return hr;
        CoTaskMemFree((nint)currentFormat);
        hr = AudioClientInitializeSharedAudioStream(_audioClient, AUDCLNT_STREAMFLAGS_EVENTCALLBACK, currentPeriod, mixFormat, 0);
        if (hr >= 0) { periodFrames = currentPeriod; _fallbackReason = AudioOutput_LatencyFallbackReason.EnginePeriodLocked; }
        return hr;
    }

    /// <summary>
    /// Release all COM objects for the current device.
    /// </summary>
    private void CloseDevice()
    {
        if (_renderClient != 0) { Release(_renderClient); _renderClient = 0; }
        if (_audioClient != 0) { Release(_audioClient); _audioClient = 0; }
        if (_device != 0) { Release(_device); _device = 0; }
        if (_bufferEvent != 0) { CloseHandle(_bufferEvent); _bufferEvent = 0; }
    }

    // ── Start / Stop ─────────────────────────────────────────────────────────────

    public void Start(AudioCallback callback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_running)
            throw new InvalidOperationException("Already started");

        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        _running = true;
        _deviceSwitchRequested = false;

        PrefillSilence();

        int hr = AudioClientStart(_audioClient);
        Marshal.ThrowExceptionForHR(hr);

        _audioThread = new Thread(AudioThreadProc)
        {
            Name = "AN.Audio WASAPI",
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };
        _audioThread.Start();
    }

    public void Stop()
    {
        if (!_running) return;

        _running = false;
        _callback = null; // Clear before join so audio thread sees it immediately
        _audioThread?.Join(timeout: TimeSpan.FromSeconds(2));
        _audioThread = null;

        if (_audioClient != 0)
        {
            AudioClientStop(_audioClient);
            AudioClientReset(_audioClient);
        }
    }

    // ── Audio Thread ─────────────────────────────────────────────────────────────

    private void AudioThreadProc()
    {
        CoInitializeEx(0, COINIT_MULTITHREADED);
        // Spec 60 D5: register with the Multimedia Class Scheduler as "Pro Audio" (real-time class). Applies in BOTH latency modes — it costs
        // nothing and removes scheduler jitter. ThreadPriority.Highest (set by Start) remains the fallback if avrt refuses.
        nint mmcssHandle = 0;
        try
        {
            uint taskIndex = 0;
            mmcssHandle = AvSetMmThreadCharacteristicsW(MMCSS_TASK_PRO_AUDIO, ref taskIndex);
        }
        catch (DllNotFoundException) { mmcssHandle = 0; }

        try
        {
        while (_running)
        {
            // Snapshot callback early — Stop() may null it at any time
            var callback = _callback;
            if (callback == null) break;

            // Check if a device switch was requested
            if (_deviceSwitchRequested)
            {
                _deviceSwitchRequested = false;
                PerformDeviceSwitch();
                if (!_running) break;
                continue; // Re-enter the loop after switch
            }

            // Block until WASAPI signals it needs more data
            uint waitResult = WaitForSingleObject(_bufferEvent, 2000);
            if (!_running) break;
            if (waitResult != WAIT_OBJECT_0) continue;

            // How many frames can we write?
            uint padding;
            int hr = AudioClientGetCurrentPadding(_audioClient, out padding);
            if (hr < 0)
            {
                // Stream error — might indicate device loss
                HandleStreamError();
                continue;
            }

            uint framesAvailable = _bufferFrameCount - padding;
            if (framesAvailable == 0) continue;
            // Spec 60 D7: after the first fill, waking to a COMPLETELY empty buffer means the engine ran dry before we got here.
            if (!_exclusive && padding == 0 && _firstFillDone) Interlocked.Increment(ref _underrunCount);

            // Get the hardware buffer pointer
            byte* dataPtr;
            hr = RenderClientGetBuffer(_renderClient, framesAvailable, out dataPtr);
            if (hr < 0)
            {
                HandleStreamError();
                continue;
            }

            // Fill the device buffer via the format converter
            var converter = _converter;
            if (converter == null)
            {
                // Stop() or device switch nulled the converter — release buffer as silent and exit
                RenderClientReleaseBuffer(_renderClient, framesAvailable, AUDCLNT_BUFFERFLAGS_SILENT);
                break;
            }

            int totalBytes = (int)framesAvailable * _deviceFrameBytes;
            var bufferSpan = new Span<byte>(dataPtr, totalBytes);

            int framesWritten = converter.FillDeviceBuffer(
                bufferSpan, (int)framesAvailable, callback);

            // If fewer frames written, silence the remainder
            if (framesWritten < (int)framesAvailable)
            {
                int writtenBytes = framesWritten * _deviceFrameBytes;
                bufferSpan.Slice(writtenBytes).Clear();
            }

            uint flags = (framesWritten == 0) ? AUDCLNT_BUFFERFLAGS_SILENT : 0;
            RenderClientReleaseBuffer(_renderClient, framesAvailable, flags);
            _firstFillDone = true;
        }
        }
        finally
        {
            if (mmcssHandle != 0) { try { AvRevertMmThreadCharacteristics(mmcssHandle); } catch (DllNotFoundException) { } }
        }
    }

    // ── Device Switch Logic ──────────────────────────────────────────────────────

    private void OnDefaultDeviceChanged(AudioDeviceInfo? newDefault)
    {
        if (_disposed) return;

        switch (_switchPolicy)
        {
            case AudioSwitchPolicy.FollowDefault:
                _deviceSwitchRequested = true;
                break;

            case AudioSwitchPolicy.PreferenceList:
                // Only switch to default if our preferred device is no longer available
                // The device list change handler will catch preferred device removal
                break;

            case AudioSwitchPolicy.None:
                DeviceLost?.Invoke(DeviceLostReason.DefaultChanged);
                break;
        }
    }

    private void OnDeviceListChanged(DeviceChangeType changeType, AudioDeviceInfo? device)
    {
        if (_disposed) return;

        if (changeType == DeviceChangeType.Removed && _currentDevice != null
            && string.Equals(device?.Id, _currentDevice.Id, StringComparison.OrdinalIgnoreCase))
        {
            // Our active device was removed
            switch (_switchPolicy)
            {
                case AudioSwitchPolicy.FollowDefault:
                case AudioSwitchPolicy.PreferenceList:
                    _deviceSwitchRequested = true;
                    break;
                case AudioSwitchPolicy.None:
                    DeviceLost?.Invoke(DeviceLostReason.DeviceRemoved);
                    _running = false;
                    break;
            }
        }
        else if (changeType == DeviceChangeType.Added && _switchPolicy == AudioSwitchPolicy.PreferenceList)
        {
            // A device was added — check if it's higher priority than our current device
            string? bestId = ResolveDeviceId();
            if (bestId != null && _currentDevice != null
                && !string.Equals(bestId, _currentDevice.Id, StringComparison.OrdinalIgnoreCase))
            {
                _deviceSwitchRequested = true;
            }
        }
    }

    private void HandleStreamError()
    {
        switch (_switchPolicy)
        {
            case AudioSwitchPolicy.FollowDefault:
            case AudioSwitchPolicy.PreferenceList:
                _deviceSwitchRequested = true;
                break;
            case AudioSwitchPolicy.None:
                DeviceLost?.Invoke(DeviceLostReason.StreamError);
                _running = false;
                break;
        }
    }

    /// <summary>
    /// Perform a device switch on the audio thread.
    /// Tears down the current device and opens a new one.
    /// </summary>
    private void PerformDeviceSwitch()
    {
        lock (_switchLock)
        {
            if (!_running) return;

            var oldDeviceFormat = _deviceFormat;
            var oldDevice = _currentDevice;

            try
            {
                // Stop and release the current device
                if (_audioClient != 0)
                {
                    AudioClientStop(_audioClient);
                    AudioClientReset(_audioClient);
                }
                CloseDevice();

                // Resolve and open the new device
                string? newDeviceId = ResolveDeviceId();
                OpenDevice(newDeviceId);

                // Pre-fill and start the new device
                PrefillSilence();
                int hr = AudioClientStart(_audioClient);
                if (hr < 0)
                {
                    // Failed to start new device — try system default as last resort
                    CloseDevice();
                    OpenDevice(null);
                    PrefillSilence();
                    hr = AudioClientStart(_audioClient);
                    Marshal.ThrowExceptionForHR(hr);
                }

                // Notify consumers
                if (_deviceFormat != oldDeviceFormat)
                    DeviceFormatChanged?.Invoke(_deviceFormat);

                if (_currentDevice != null)
                    DeviceSwitched?.Invoke(_currentDevice);
            }
            catch
            {
                // If we can't open any device, fire DeviceLost and stop
                DeviceLost?.Invoke(DeviceLostReason.StreamError);
                _running = false;
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private void PrefillSilence()
    {
        byte* dataPtr;
        int hr = RenderClientGetBuffer(_renderClient, _bufferFrameCount, out dataPtr);
        if (hr >= 0)
        {
            new Span<byte>(dataPtr, (int)_bufferFrameCount * _deviceFrameBytes).Clear();
            RenderClientReleaseBuffer(_renderClient, _bufferFrameCount, AUDCLNT_BUFFERFLAGS_SILENT);
        }
    }

    private static AudioFormat ParseWaveFormat(WAVEFORMATEX* wfx)
    {
        SampleFormat sf;
        if (wfx->wFormatTag == WAVE_FORMAT_EXTENSIBLE)
        {
            var ext = (WAVEFORMATEXTENSIBLE*)wfx;
            if (ext->SubFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT)
                sf = SampleFormat.Float32;
            else if (ext->SubFormat == KSDATAFORMAT_SUBTYPE_PCM)
                sf = wfx->wBitsPerSample == 16 ? SampleFormat.Int16 : SampleFormat.Float32;
            else
                sf = SampleFormat.Float32;
        }
        else if (wfx->wFormatTag == WAVE_FORMAT_IEEE_FLOAT)
        {
            sf = SampleFormat.Float32;
        }
        else
        {
            sf = wfx->wBitsPerSample == 16 ? SampleFormat.Int16 : SampleFormat.Float32;
        }

        return new AudioFormat(
            SampleRate: (int)wfx->nSamplesPerSec,
            Channels: wfx->nChannels,
            Format: sf);
    }

    // ── Dispose ──────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Unsubscribe from device manager events
        _deviceManager.DefaultDeviceChanged -= OnDefaultDeviceChanged;
        _deviceManager.DeviceListChanged -= OnDeviceListChanged;

        Stop();
        CloseDevice();
    }
}