using System.Runtime.InteropServices;
using static AN.Audio.Platforms.Windows.WasapiInterop;

namespace AN.Audio.Platforms.Windows;

/// <summary>
/// WASAPI shared-mode event-driven audio output.
/// Zero-allocation on the audio thread hot path.
/// </summary>
internal sealed unsafe class WasapiAudioOutput : IAudioOutput
{
    private readonly AudioFormat _requestedFormat;
    private readonly int _bufferSizeMs;

    // COM interface pointers (released on Dispose)
    private nint _enumerator;
    private nint _device;
    private nint _audioClient;
    private nint _renderClient;

    // Event-driven signaling
    private nint _bufferEvent;

    // Audio thread state
    private Thread? _audioThread;
    private volatile bool _running;
    private AudioCallback? _callback;

    // Format negotiated with the endpoint
    private AudioFormat _actualFormat;
    private uint _bufferFrameCount;
    private int _frameBytes; // cached: bytes per frame in the endpoint format

    // Latency
    private double _latencyMs;

    private bool _disposed;

    public AudioFormat Format => _actualFormat;
    public double LatencyMs => _latencyMs;

    public WasapiAudioOutput(AudioFormat requestedFormat, int bufferSizeMs)
    {
        _requestedFormat = requestedFormat;
        _bufferSizeMs = bufferSizeMs;
        Initialize();
    }

    private void Initialize()
    {
        // Init COM on this thread (may already be initialized, that's fine)
        int hr = CoInitializeEx(0, COINIT_MULTITHREADED);
        // S_OK=0, S_FALSE=1 (already initialized) are both acceptable
        if (hr < 0 && hr != unchecked((int)0x80010106)) // RPC_E_CHANGED_MODE is ok too
            Marshal.ThrowExceptionForHR(hr);

        // Create MMDeviceEnumerator
        Guid clsid = CLSID_MMDeviceEnumerator;
        Guid iid = IID_IMMDeviceEnumerator;
        hr = CoCreateInstance(ref clsid, 0, CLSCTX_ALL, ref iid, out _enumerator);
        Marshal.ThrowExceptionForHR(hr);

        // Get default render endpoint
        hr = GetDefaultAudioEndpoint(_enumerator, eRender, eConsole, out _device);
        Marshal.ThrowExceptionForHR(hr);

        // Activate IAudioClient
        Guid audioClientIid = IID_IAudioClient;
        hr = DeviceActivate(_device, ref audioClientIid, CLSCTX_ALL, 0, out _audioClient);
        Marshal.ThrowExceptionForHR(hr);

        // Get the endpoint's mix format (what shared mode actually uses)
        WAVEFORMATEX* mixFormat;
        hr = AudioClientGetMixFormat(_audioClient, out mixFormat);
        Marshal.ThrowExceptionForHR(hr);

        // Parse the mix format to determine our actual format
        _actualFormat = ParseWaveFormat(mixFormat);
        _frameBytes = _actualFormat.BytesPerFrame;

        // Calculate buffer duration in 100ns units
        long hnsBufferDuration = (long)_bufferSizeMs * REFTIMES_PER_MS;

        // Initialize audio client: shared mode, event-driven
        hr = AudioClientInitialize(
            _audioClient,
            AUDCLNT_SHAREMODE_SHARED,
            AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
            hnsBufferDuration,
            0, // periodicity must be 0 for shared mode
            mixFormat,
            0);

        // Free the CoTaskMem-allocated mix format
        CoTaskMemFree((nint)mixFormat);

        Marshal.ThrowExceptionForHR(hr);

        // Create the buffer-ready event (auto-reset)
        _bufferEvent = CreateEventW(0, 0, 0, 0);
        if (_bufferEvent == 0)
            throw new InvalidOperationException("Failed to create audio buffer event");

        // Tell the audio client to signal our event
        hr = AudioClientSetEventHandle(_audioClient, _bufferEvent);
        Marshal.ThrowExceptionForHR(hr);

        // Get actual buffer size
        hr = AudioClientGetBufferSize(_audioClient, out _bufferFrameCount);
        Marshal.ThrowExceptionForHR(hr);

        // Get latency
        long hnsLatency;
        hr = AudioClientGetStreamLatency(_audioClient, out hnsLatency);
        if (hr >= 0)
            _latencyMs = hnsLatency / (double)REFTIMES_PER_MS;
        else
            _latencyMs = _bufferSizeMs; // fallback estimate

        // Get render client
        Guid renderIid = IID_IAudioRenderClient;
        hr = AudioClientGetService(_audioClient, ref renderIid, out _renderClient);
        Marshal.ThrowExceptionForHR(hr);
    }

    public void Start(AudioCallback callback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_running)
            throw new InvalidOperationException("Already started");

        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        _running = true;

        // Pre-fill the buffer with silence before starting
        PrefillSilence();

        // Start the audio client (begins signaling the event)
        int hr = AudioClientStart(_audioClient);
        Marshal.ThrowExceptionForHR(hr);

        // Spin up the audio feeder thread
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
        _audioThread?.Join(timeout: TimeSpan.FromSeconds(2));
        _audioThread = null;

        AudioClientStop(_audioClient);
        AudioClientReset(_audioClient);

        _callback = null;
    }

    /// <summary>
    /// The audio thread. Waits on the WASAPI event, fills buffers.
    /// Hot path: zero managed allocations.
    /// </summary>
    private void AudioThreadProc()
    {
        // Initialize COM on this thread
        CoInitializeEx(0, COINIT_MULTITHREADED);

        while (_running)
        {
            // Block until WASAPI signals it needs more data
            uint waitResult = WaitForSingleObject(_bufferEvent, 2000);
            if (!_running) break;
            if (waitResult != WAIT_OBJECT_0) continue;

            // How many frames can we write?
            uint padding;
            int hr = AudioClientGetCurrentPadding(_audioClient, out padding);
            if (hr < 0) continue;

            uint framesAvailable = _bufferFrameCount - padding;
            if (framesAvailable == 0) continue;

            // Get the hardware buffer pointer
            byte* dataPtr;
            hr = RenderClientGetBuffer(_renderClient, framesAvailable, out dataPtr);
            if (hr < 0) continue;

            // Invoke the callback — wrap raw pointer as Span<byte> (zero-copy)
            int totalBytes = (int)framesAvailable * _frameBytes;
            var bufferSpan = new Span<byte>(dataPtr, totalBytes);

            int framesWritten = _callback!(bufferSpan, (int)framesAvailable, _actualFormat);

            // If callback wrote fewer frames than available, zero the remainder (silence)
            if (framesWritten < (int)framesAvailable)
            {
                int writtenBytes = framesWritten * _frameBytes;
                bufferSpan.Slice(writtenBytes).Clear();
            }

            // Release the buffer — tell WASAPI how many frames we wrote
            uint flags = (framesWritten == 0) ? AUDCLNT_BUFFERFLAGS_SILENT : 0;
            RenderClientReleaseBuffer(_renderClient, framesAvailable, flags);
        }
    }

    private void PrefillSilence()
    {
        byte* dataPtr;
        int hr = RenderClientGetBuffer(_renderClient, _bufferFrameCount, out dataPtr);
        if (hr >= 0)
        {
            // Zero = silence for both int16 and float32
            new Span<byte>(dataPtr, (int)_bufferFrameCount * _frameBytes).Clear();
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
                sf = SampleFormat.Float32; // assume float for unknown
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();

        if (_renderClient != 0) { Release(_renderClient); _renderClient = 0; }
        if (_audioClient != 0) { Release(_audioClient); _audioClient = 0; }
        if (_device != 0) { Release(_device); _device = 0; }
        if (_enumerator != 0) { Release(_enumerator); _enumerator = 0; }
        if (_bufferEvent != 0) { CloseHandle(_bufferEvent); _bufferEvent = 0; }
    }
}