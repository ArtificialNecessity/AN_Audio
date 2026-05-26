using System.Runtime.InteropServices;
using static AN.Audio.Platforms.MacOS.AudioToolboxInterop;

namespace AN.Audio.Platforms.MacOS;

/// <summary>
/// macOS AudioQueue-based audio output.
/// Uses AudioToolbox's AudioQueue Services for callback-driven playback.
/// Rotates 3 buffers to keep the pipeline fed.
/// </summary>
internal sealed unsafe class CoreAudioOutput : IAudioOutput
{
    /// <summary>Number of buffers to rotate. 3 is the standard for AudioQueue.</summary>
    private const int BufferCount = 3;

    private readonly AudioFormat _requestedFormat;
    private readonly int _bufferSizeMs;

    // AudioQueue handle
    private nint _queue;

    // Pre-allocated buffer pointers
    private readonly AudioQueueBuffer*[] _buffers = new AudioQueueBuffer*[BufferCount];

    // Callback state
    private AudioCallback? _callback;
    private volatile bool _running;
    private bool _disposed;

    // The format we're actually using
    private AudioFormat _actualFormat;
    private int _frameBytes;
    private int _bufferByteSize;
    private int _framesPerBuffer;

    // Latency estimate
    private double _latencyMs;

    // Must prevent the delegate from being GC'd while AudioQueue holds the function pointer
    private AudioQueueOutputCallback? _nativeCallback;
    private GCHandle _selfHandle;

    public AudioFormat Format => _actualFormat;
    public double LatencyMs => _latencyMs;

    // ── Device Management Stubs ──────────────────────────────────────────────────
    // UNTESTED on macOS: These are stub implementations for the device management
    // interface. Full macOS device management (CoreAudio property listeners for
    // device enumeration and change notification) is planned but not yet implemented.
    // Currently, macOS always uses the system default device with no auto-switching.

    /// <summary>
    /// On macOS, AudioQueue accepts whatever format we specify (it resamples internally),
    /// so DeviceFormat always matches the consumer's requested format.
    /// </summary>
    public AudioFormat DeviceFormat => _actualFormat;

    public AudioSwitchPolicy SwitchPolicy { get; set; } = AudioSwitchPolicy.FollowDefault;
    public IReadOnlyList<string>? PreferredDevices { get; set; }
    public AudioDeviceInfo? CurrentDevice => null; // TODO: Query CoreAudio for current device info

    // Events — declared but never fired until macOS device management is implemented
    public event Action<AudioFormat>? DeviceFormatChanged;
    public event Action<DeviceLostReason>? DeviceLost;
    public event Action<AudioDeviceInfo>? DeviceSwitched;

    // Suppress CS0067 (events never used) — they will be used when macOS device management is implemented
    private void SuppressEventWarnings()
    { _ = DeviceFormatChanged; _ = DeviceLost; _ = DeviceSwitched; }
    // ── End Device Management Stubs ──────────────────────────────────────────────

    public CoreAudioOutput(AudioFormat requestedFormat, int bufferSizeMs)
    {
        _requestedFormat = requestedFormat;
        _bufferSizeMs = bufferSizeMs;
        Initialize();
    }

    private void Initialize()
    {
        // On macOS, AudioQueue accepts whatever format we specify (it resamples internally).
        // We use the requested format directly.
        _actualFormat = _requestedFormat;
        _frameBytes = _actualFormat.BytesPerFrame;

        // Calculate buffer size: frames per buffer based on requested latency
        _framesPerBuffer = (_actualFormat.SampleRate * _bufferSizeMs) / 1000;
        _bufferByteSize = _framesPerBuffer * _frameBytes;

        // Latency = bufferSizeMs * BufferCount (3 buffers in the pipeline)
        _latencyMs = _bufferSizeMs * BufferCount;

        // Build the AudioStreamBasicDescription
        var asbd = new AudioStreamBasicDescription
        {
            mSampleRate = _actualFormat.SampleRate,
            mFormatID = kAudioFormatLinearPCM,
            mFormatFlags = GetFormatFlags(_actualFormat.Format),
            mBytesPerPacket = (uint)_frameBytes,
            mFramesPerPacket = 1,
            mBytesPerFrame = (uint)_frameBytes,
            mChannelsPerFrame = (uint)_actualFormat.Channels,
            mBitsPerChannel = (uint)(_actualFormat.BytesPerSample * 8),
            mReserved = 0
        };

        // Pin ourselves so the native callback can recover 'this' from the user data pointer
        _selfHandle = GCHandle.Alloc(this);

        // Create the native callback delegate and prevent it from being collected
        _nativeCallback = NativeCallback;
        nint callbackPtr = Marshal.GetFunctionPointerForDelegate(_nativeCallback);

        // Create the output queue
        int status = AudioQueueNewOutput(
            &asbd,
            callbackPtr,
            (nint)_selfHandle,
            nint.Zero,  // NULL = use internal run loop thread
            nint.Zero,  // NULL = default run loop mode
            0,
            out _queue);
        ThrowIfError(status, "AudioQueueNewOutput");

        // Allocate buffers
        for (int i = 0; i < BufferCount; i++)
        {
            status = AudioQueueAllocateBuffer(_queue, (uint)_bufferByteSize, out _buffers[i]);
            ThrowIfError(status, $"AudioQueueAllocateBuffer[{i}]");
        }
    }

    public void Start(AudioCallback callback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_running)
            throw new InvalidOperationException("Already started");

        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        _running = true;

        // Prime all buffers with initial data
        for (int i = 0; i < BufferCount; i++)
        {
            FillAndEnqueue(_buffers[i]);
        }

        // Start playback
        int status = AudioQueueStart(_queue, nint.Zero);
        ThrowIfError(status, "AudioQueueStart");
    }

    public void Stop()
    {
        if (!_running) return;

        _running = false;

        // Stop immediately (don't wait for buffers to drain)
        AudioQueueStop(_queue, inImmediate: true);
        AudioQueueReset(_queue);

        _callback = null;
    }

    /// <summary>
    /// Fill a buffer via the user callback and enqueue it for playback.
    /// Called from the AudioQueue's internal thread (the native callback).
    /// </summary>
    private void FillAndEnqueue(AudioQueueBuffer* buffer)
    {
        var span = new Span<byte>((void*)buffer->mAudioData, _bufferByteSize);

        int framesWritten = 0;
        if (_callback != null)
        {
            framesWritten = _callback(span, _framesPerBuffer, _actualFormat);
        }

        // If callback wrote fewer frames, zero the remainder (silence)
        if (framesWritten < _framesPerBuffer)
        {
            int writtenBytes = framesWritten * _frameBytes;
            span.Slice(writtenBytes).Clear();
        }

        buffer->mAudioDataByteSize = (uint)_bufferByteSize;

        int status = AudioQueueEnqueueBuffer(_queue, buffer, 0, nint.Zero);
        // Don't throw on the audio thread — just log/ignore
        // In production, we'd want a way to surface this error
        if (status != noErr)
        {
            // Silently ignore enqueue errors during shutdown
        }
    }

    /// <summary>
    /// Static native callback — recovers the CoreAudioOutput instance from user data
    /// and dispatches to FillAndEnqueue.
    /// </summary>
    private static void NativeCallback(nint inUserData, nint inAQ, AudioQueueBuffer* inBuffer)
    {
        var handle = GCHandle.FromIntPtr(inUserData);
        if (!handle.IsAllocated) return;

        var self = (CoreAudioOutput)handle.Target!;
        if (!self._running) return;

        self.FillAndEnqueue(inBuffer);
    }

    private static uint GetFormatFlags(SampleFormat format) => format switch
    {
        SampleFormat.Float32 => kLinearPCMFormatFlagIsFloat | kLinearPCMFormatFlagIsPacked,
        SampleFormat.Int16 => kLinearPCMFormatFlagIsSignedInteger | kLinearPCMFormatFlagIsPacked,
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();

        if (_queue != nint.Zero)
        {
            // Dispose the queue — this also frees all allocated buffers
            AudioQueueDispose(_queue, inImmediate: true);
            _queue = nint.Zero;
        }

        // Release the GCHandle
        if (_selfHandle.IsAllocated)
            _selfHandle.Free();

        // Clear buffer pointers (they were freed by AudioQueueDispose)
        Array.Clear(_buffers);

        _nativeCallback = null;
    }
}
