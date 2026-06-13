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

    private bool _disposed;

    // ── IAudioOutput Properties ──────────────────────────────────────────────────

    public AudioFormat Format => _consumerFormat;
    public AudioFormat DeviceFormat => _deviceFormat;
    public double LatencyMs => _latencyMs;

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

        // Activate IAudioClient
        Guid audioClientIid = IID_IAudioClient;
        hr = DeviceActivate(_device, ref audioClientIid, CLSCTX_ALL, 0, out _audioClient);
        Marshal.ThrowExceptionForHR(hr);

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

        // Calculate buffer duration
        long hnsBufferDuration = (long)_bufferSizeMs * REFTIMES_PER_MS;

        // Initialize audio client: shared mode, event-driven
        hr = AudioClientInitialize(
            _audioClient,
            AUDCLNT_SHAREMODE_SHARED,
            AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
            hnsBufferDuration,
            0,
            mixFormat,
            0);

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