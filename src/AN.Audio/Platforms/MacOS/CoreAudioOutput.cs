using System.Runtime.InteropServices;
using System.Text;
using static AN.Audio.Platforms.MacOS.AudioToolboxInterop;
using static AN.Audio.Platforms.MacOS.CoreAudioInterop;

namespace AN.Audio.Platforms.MacOS;

/// <summary>
/// macOS AudioQueue-based audio output with automatic device switching.
/// Uses AudioToolbox's AudioQueue Services for callback-driven playback
/// and CoreAudio HAL property listeners for device change notification.
/// Rotates 3 buffers to keep the pipeline fed.
/// </summary>
internal sealed unsafe class CoreAudioOutput : IAudioOutput
{
    /// <summary>Number of buffers to rotate. 3 is the standard for AudioQueue.</summary>
    private const int BufferCount = 3;

    private readonly AudioFormat _consumerFormat;
    private readonly int _bufferSizeMs;

    // Device management
    private readonly CoreAudioDeviceManager _deviceManager;
    private AudioSwitchPolicy _switchPolicy;
    private IReadOnlyList<string>? _preferredDevices;
    private AudioDeviceInfo? _currentDevice;
    private readonly object _switchLock = new();

    // AudioQueue handle
    private nint _queue;

    // Pre-allocated buffer pointers
    private readonly AudioQueueBuffer*[] _buffers = new AudioQueueBuffer*[BufferCount];

    // Callback state
    private AudioCallback? _callback;
    private volatile bool _running;
    private volatile bool _deviceSwitchRequested;
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

    // ── IAudioOutput Properties ──────────────────────────────────────────────────

    public AudioFormat Format => _consumerFormat;
    public AudioFormat DeviceFormat => _actualFormat;
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

#pragma warning disable CS0067 // DeviceFormatChanged is never fired — AudioQueue handles format conversion internally
    public event Action<AudioFormat>? DeviceFormatChanged;
#pragma warning restore CS0067
    public event Action<DeviceLostReason>? DeviceLost;
    public event Action<AudioDeviceInfo>? DeviceSwitched;

    // ── Constructor ──────────────────────────────────────────────────────────────

    public CoreAudioOutput(AudioFormat consumerFormat, AudioOutputOptions? options = null)
    {
        _consumerFormat = consumerFormat;
        _bufferSizeMs = options?.BufferSizeMs ?? 20;
        _switchPolicy = options?.SwitchPolicy ?? AudioSwitchPolicy.FollowDefault;
        _preferredDevices = options?.PreferredDevices;

        _deviceManager = CoreAudioDeviceManager.Instance;

        // Open the initial device
        InitializeQueue(ResolveDeviceId());

        // Subscribe to device manager events
        _deviceManager.DefaultDeviceChanged += OnDefaultDeviceChanged;
        _deviceManager.DeviceListChanged += OnDeviceListChanged;
    }

    /// <summary>Backward-compatible constructor (bufferSizeMs only).</summary>
    public CoreAudioOutput(AudioFormat consumerFormat, int bufferSizeMs)
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
            var available = _deviceManager.GetOutputDevices();
            var availableIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in available)
                availableIds.Add(d.Id);

            foreach (var prefId in _preferredDevices)
            {
                if (availableIds.Contains(prefId))
                    return prefId;
            }
        }

        // FollowDefault, None, or no preferred device available → system default
        return null;
    }

    // ── Queue Initialization ─────────────────────────────────────────────────────

    /// <summary>
    /// Create and configure the AudioQueue, optionally targeting a specific device.
    /// deviceId = null means system default.
    /// </summary>
    private void InitializeQueue(string? deviceId)
    {
        // On macOS, AudioQueue accepts whatever format we specify (it resamples internally).
        _actualFormat = _consumerFormat;
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
        if (!_selfHandle.IsAllocated)
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

        // Set the target device if specified
        if (deviceId != null && uint.TryParse(deviceId, out uint audioDeviceId))
        {
            SetQueueDevice(audioDeviceId);
        }

        // Allocate buffers
        for (int i = 0; i < BufferCount; i++)
        {
            status = AudioQueueAllocateBuffer(_queue, (uint)_bufferByteSize, out _buffers[i]);
            ThrowIfError(status, $"AudioQueueAllocateBuffer[{i}]");
        }

        // Update current device info
        _currentDevice = deviceId != null
            ? _deviceManager.GetDeviceById(deviceId)
            : _deviceManager.GetDeviceById(_deviceManager.GetDefaultDeviceIdString() ?? "");
    }

    /// <summary>
    /// Set the output device on the AudioQueue using kAudioQueueProperty_CurrentDevice.
    /// The device UID (CFString) is required, not the AudioDeviceID integer.
    /// </summary>
    private void SetQueueDevice(uint audioDeviceId)
    {
        // Get the device UID string (CFString) for this AudioDeviceID
        string? uid = CoreAudioDeviceManager.GetDeviceUID(audioDeviceId);
        if (uid == null) return;

        // Create a CFString from the UID
        byte[] utf8 = Encoding.UTF8.GetBytes(uid + '\0');
        fixed (byte* utf8Ptr = utf8)
        {
            nint cfUid = CFStringCreateWithCString(0, utf8Ptr, kCFStringEncodingUTF8);
            if (cfUid == 0) return;

            try
            {
                // Set the property — pass the CFStringRef by reference
                int status = AudioQueueSetProperty(
                    _queue,
                    kAudioQueueProperty_CurrentDevice,
                    &cfUid,
                    (uint)sizeof(nint));

                // Non-fatal if this fails — we'll just use the default device
                if (status != noErr)
                {
                    // Log or ignore — device selection failed, will use default
                }
            }
            finally
            {
                CFRelease(cfUid);
            }
        }
    }

    /// <summary>
    /// Tear down the current AudioQueue and free buffers.
    /// </summary>
    private void DestroyQueue()
    {
        if (_queue != nint.Zero)
        {
            AudioQueueDispose(_queue, inImmediate: true);
            _queue = nint.Zero;
        }

        // Buffer pointers are freed by AudioQueueDispose
        Array.Clear(_buffers);
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

        if (_queue != nint.Zero)
        {
            // Stop immediately (don't wait for buffers to drain)
            AudioQueueStop(_queue, inImmediate: true);
            AudioQueueReset(_queue);
        }

        _callback = null;
    }

    // ── Audio Callback ───────────────────────────────────────────────────────────

    /// <summary>
    /// Fill a buffer via the user callback and enqueue it for playback.
    /// Called from the AudioQueue's internal thread (the native callback).
    /// </summary>
    private void FillAndEnqueue(AudioQueueBuffer* buffer)
    {
        // Check for pending device switch
        if (_deviceSwitchRequested)
        {
            _deviceSwitchRequested = false;
            PerformDeviceSwitch();
            return; // After switch, buffers will be re-primed
        }

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
        if (status != noErr && _running)
        {
            // Enqueue failure while running might indicate device loss
            HandleStreamError();
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
                // Only switch if our preferred device is no longer available
                break;

            case AudioSwitchPolicy.None:
                DeviceLost?.Invoke(DeviceLostReason.DefaultChanged);
                break;
        }
    }

    private void OnDeviceListChanged(DeviceChangeType changeType, AudioDeviceInfo? device)
    {
        if (_disposed) return;

        // CoreAudio fires a generic "devices changed" — we need to check if our device is gone
        if (_currentDevice != null)
        {
            var available = _deviceManager.GetOutputDevices();
            bool currentStillAvailable = false;
            foreach (var d in available)
            {
                if (string.Equals(d.Id, _currentDevice.Id, StringComparison.Ordinal))
                {
                    currentStillAvailable = true;
                    break;
                }
            }

            if (!currentStillAvailable)
            {
                // Our device was removed
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
                return;
            }
        }

        // Check if a higher-priority preferred device became available
        if (_switchPolicy == AudioSwitchPolicy.PreferenceList && _preferredDevices != null)
        {
            string? bestId = ResolveDeviceId();
            if (bestId != null && _currentDevice != null
                && !string.Equals(bestId, _currentDevice.Id, StringComparison.Ordinal))
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
    /// Perform a device switch. Tears down the current queue and creates a new one.
    /// Called from the AudioQueue callback thread.
    /// </summary>
    private void PerformDeviceSwitch()
    {
        lock (_switchLock)
        {
            if (!_running) return;

            var oldDevice = _currentDevice;
            var savedCallback = _callback;

            try
            {
                // Stop and destroy the current queue
                if (_queue != nint.Zero)
                {
                    AudioQueueStop(_queue, inImmediate: true);
                }
                DestroyQueue();

                // Resolve and open the new device
                string? newDeviceId = ResolveDeviceId();
                InitializeQueue(newDeviceId);

                // Re-prime buffers and start
                if (savedCallback != null)
                {
                    _callback = savedCallback;
                    for (int i = 0; i < BufferCount; i++)
                    {
                        FillAndEnqueue(_buffers[i]);
                    }

                    int status = AudioQueueStart(_queue, nint.Zero);
                    if (status != noErr)
                    {
                        // Failed — try system default as last resort
                        DestroyQueue();
                        InitializeQueue(null);
                        _callback = savedCallback;
                        for (int i = 0; i < BufferCount; i++)
                        {
                            FillAndEnqueue(_buffers[i]);
                        }
                        status = AudioQueueStart(_queue, nint.Zero);
                        ThrowIfError(status, "AudioQueueStart (fallback)");
                    }
                }

                // Notify consumers
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

    private static uint GetFormatFlags(SampleFormat format) => format switch
    {
        SampleFormat.Float32 => kLinearPCMFormatFlagIsFloat | kLinearPCMFormatFlagIsPacked,
        SampleFormat.Int16 => kLinearPCMFormatFlagIsSignedInteger | kLinearPCMFormatFlagIsPacked,
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    // ── Dispose ──────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Unsubscribe from device manager events
        _deviceManager.DefaultDeviceChanged -= OnDefaultDeviceChanged;
        _deviceManager.DeviceListChanged -= OnDeviceListChanged;

        Stop();
        DestroyQueue();

        // Release the GCHandle
        if (_selfHandle.IsAllocated)
            _selfHandle.Free();

        _nativeCallback = null;
    }
}
