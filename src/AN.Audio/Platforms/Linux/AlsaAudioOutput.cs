using System.Runtime.InteropServices;
using AN.Audio.Internal;
using static AN.Audio.Platforms.Linux.AlsaInterop;

namespace AN.Audio.Platforms.Linux;

/// <summary>
/// ALSA-based audio output using poll() for event-driven playback.
/// Uses a dedicated audio thread that blocks on poll() waiting for buffer space,
/// then invokes the AudioCallback and writes via snd_pcm_writei().
/// Supports automatic device switching and reactive error-based device loss detection.
/// </summary>
internal sealed unsafe class AlsaAudioOutput : IAudioOutput
{
    private readonly AudioFormat _consumerFormat;
    private readonly int _bufferSizeMs;

    // Device management
    private readonly AlsaDeviceManager _deviceManager;
    private AudioSwitchPolicy _switchPolicy;
    private IReadOnlyList<string>? _preferredDevices;
    private AudioDeviceInfo? _currentDevice;
    private readonly object _switchLock = new();

    // ALSA PCM handle
    private nint _pcm;
    private string _deviceName = "default";

    // Audio thread state
    private Thread? _audioThread;
    private volatile bool _running;
    private volatile bool _deviceSwitchRequested;
    private AudioCallback? _callback;

    // Poll descriptors for event-driven waiting
    private PollFd[]? _pollFds;
    private int _pollFdCount;

    // Wakeup pipe: used to interrupt poll() when Stop() is called or device switch requested
    private int _wakeupReadFd = -1;
    private int _wakeupWriteFd = -1;

    // Buffer and format
    private AudioFormat _deviceFormat;
    private AudioFormatConverter? _converter;
    private ulong _periodSize; // frames per period (per callback)
    private ulong _bufferSize; // total ring buffer size in frames
    private byte[]? _writeBuffer; // pre-allocated buffer for callback data
    private int _deviceFrameBytes;

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
                RequestDeviceSwitch();
        }
    }

    public AudioDeviceInfo? CurrentDevice => _currentDevice;

    public event Action<AudioFormat>? DeviceFormatChanged;
    public event Action<DeviceLostReason>? DeviceLost;
    public event Action<AudioDeviceInfo>? DeviceSwitched;

    // ── Constructor ──────────────────────────────────────────────────────────────

    public AlsaAudioOutput(AudioFormat consumerFormat, AudioOutputOptions? options = null)
    {
        _consumerFormat = consumerFormat;
        _bufferSizeMs = options?.BufferSizeMs ?? 20;
        _switchPolicy = options?.SwitchPolicy ?? AudioSwitchPolicy.FollowDefault;
        _preferredDevices = options?.PreferredDevices;

        _deviceManager = AlsaDeviceManager.Instance;

        // Resolve which device to open
        _deviceName = ResolveDeviceName();

        // Open the initial device
        OpenDevice(_deviceName);

        // Create the wakeup pipe
        CreateWakeupPipe();

        // Subscribe to device manager events
        _deviceManager.DefaultDeviceChanged += OnDefaultDeviceChanged;
        _deviceManager.DeviceListChanged += OnDeviceListChanged;
    }

    /// <summary>Backward-compatible constructor (bufferSizeMs only).</summary>
    public AlsaAudioOutput(AudioFormat consumerFormat, int bufferSizeMs)
        : this(consumerFormat, new AudioOutputOptions { BufferSizeMs = bufferSizeMs })
    {
    }

    // ── Device Resolution ────────────────────────────────────────────────────────

    /// <summary>
    /// Determine which ALSA device name to open based on the current policy.
    /// Returns "default" for system default.
    /// </summary>
    private string ResolveDeviceName()
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
        return "default";
    }

    // ── Device Open/Close ────────────────────────────────────────────────────────

    /// <summary>
    /// Open an ALSA PCM device and configure it for playback.
    /// </summary>
    private void OpenDevice(string deviceName)
    {
        // For ALSA, the "device format" is whatever we configure — ALSA handles conversion
        // via the plughw layer. We request the consumer's format directly.
        _deviceFormat = _consumerFormat;
        _deviceFrameBytes = _deviceFormat.BytesPerFrame;

        // Create or update the format converter (passthrough in most cases for ALSA)
        if (_converter == null)
            _converter = new AudioFormatConverter(_consumerFormat, _deviceFormat);
        else
            _converter.UpdateDeviceFormat(_deviceFormat);

        // Open PCM device
        int err = snd_pcm_open(out _pcm, deviceName, SND_PCM_STREAM_PLAYBACK, 0);
        if (err < 0)
            throw new InvalidOperationException(
                $"Failed to open ALSA device '{deviceName}': {GetErrorString(err)}");

        // Set hardware parameters using the simple API
        // latency_us = bufferSizeMs * 1000 (convert ms to microseconds)
        uint latencyUs = (uint)(_bufferSizeMs * 1000);
        int alsaFormat = GetAlsaFormat(_deviceFormat.Format);

        err = snd_pcm_set_params(
            _pcm,
            alsaFormat,
            SND_PCM_ACCESS_RW_INTERLEAVED,
            (uint)_deviceFormat.Channels,
            (uint)_deviceFormat.SampleRate,
            1, // soft_resample = 1 (allow ALSA to resample if needed)
            latencyUs);

        if (err < 0)
        {
            snd_pcm_close(_pcm);
            _pcm = 0;
            throw new InvalidOperationException(
                $"Failed to configure ALSA device '{deviceName}': {GetErrorString(err)}");
        }

        // Get the actual buffer and period sizes
        err = snd_pcm_get_params(_pcm, out _bufferSize, out _periodSize);
        if (err < 0)
        {
            // Fallback: estimate from the requested latency
            _periodSize = (ulong)(_deviceFormat.SampleRate * _bufferSizeMs / 1000);
            _bufferSize = _periodSize * 2;
        }

        // Allocate the write buffer (one period worth of data)
        _writeBuffer = new byte[(int)_periodSize * _deviceFrameBytes];

        // Calculate actual latency
        _latencyMs = (double)_bufferSize / _deviceFormat.SampleRate * 1000.0;

        // Get poll descriptors
        SetupPollDescriptors();

        // Update current device info
        _currentDevice = _deviceManager.GetDeviceById(deviceName);
        _deviceName = deviceName;
    }

    /// <summary>
    /// Close the current ALSA PCM device.
    /// </summary>
    private void CloseDevice()
    {
        if (_pcm != 0)
        {
            snd_pcm_drop(_pcm);
            snd_pcm_close(_pcm);
            _pcm = 0;
        }

        _pollFds = null;
        _pollFdCount = 0;
    }

    /// <summary>
    /// Set up the poll file descriptors from ALSA.
    /// </summary>
    private void SetupPollDescriptors()
    {
        _pollFdCount = snd_pcm_poll_descriptors_count(_pcm);
        if (_pollFdCount <= 0)
        {
            _pollFdCount = 0;
            _pollFds = null;
            return;
        }

        // Allocate space for ALSA fds + 1 for the wakeup pipe
        _pollFds = new PollFd[_pollFdCount + 1];

        fixed (PollFd* fds = _pollFds)
        {
            int filled = snd_pcm_poll_descriptors(_pcm, fds, (uint)_pollFdCount);
            if (filled < 0)
            {
                _pollFdCount = 0;
                _pollFds = null;
            }
            else
            {
                _pollFdCount = filled;
            }
        }
    }

    // ── Wakeup Pipe ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Create a pipe used to wake up the audio thread from poll().
    /// Writing a byte to the pipe makes poll() return immediately.
    /// </summary>
    private void CreateWakeupPipe()
    {
        int* fds = stackalloc int[2];
        int err = pipe(fds);
        if (err < 0)
            throw new InvalidOperationException("Failed to create wakeup pipe for ALSA audio thread");

        _wakeupReadFd = fds[0];
        _wakeupWriteFd = fds[1];
    }

    /// <summary>
    /// Signal the audio thread to wake up from poll().
    /// </summary>
    private void WakeupAudioThread()
    {
        if (_wakeupWriteFd >= 0)
        {
            byte b = 1;
            write(_wakeupWriteFd, &b, 1);
        }
    }

    /// <summary>
    /// Drain any accumulated wakeup bytes from the pipe.
    /// </summary>
    private void DrainWakeupPipe()
    {
        if (_wakeupReadFd >= 0)
        {
            byte* buf = stackalloc byte[64];
            // Non-blocking read to drain — we don't care about the bytes
            read(_wakeupReadFd, buf, 64);
        }
    }

    /// <summary>
    /// Close the wakeup pipe.
    /// </summary>
    private void CloseWakeupPipe()
    {
        if (_wakeupReadFd >= 0) { close(_wakeupReadFd); _wakeupReadFd = -1; }
        if (_wakeupWriteFd >= 0) { close(_wakeupWriteFd); _wakeupWriteFd = -1; }
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

        // Prepare the PCM for playback
        int err = snd_pcm_prepare(_pcm);
        if (err < 0)
            throw new InvalidOperationException(
                $"Failed to prepare ALSA PCM: {GetErrorString(err)}");

        // Start the audio thread
        _audioThread = new Thread(AudioThreadProc)
        {
            Name = "AN.Audio ALSA",
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };
        _audioThread.Start();
    }

    public void Stop()
    {
        if (!_running) return;

        _running = false;

        // Wake up the audio thread so it exits poll()
        WakeupAudioThread();

        _audioThread?.Join(timeout: TimeSpan.FromSeconds(2));
        _audioThread = null;

        if (_pcm != 0)
        {
            snd_pcm_drop(_pcm);
            snd_pcm_prepare(_pcm);
        }

        _callback = null;
    }

    // ── Audio Thread ─────────────────────────────────────────────────────────────

    private void AudioThreadProc()
    {
        // Pre-fill with initial audio data to avoid underruns at start
        PrefillBuffer();

        while (_running)
        {
            // Check if a device switch was requested
            if (_deviceSwitchRequested)
            {
                _deviceSwitchRequested = false;
                PerformDeviceSwitch();
                if (!_running) break;
                continue;
            }

            // Wait for buffer space using poll()
            if (!WaitForBufferSpace())
            {
                if (!_running) break;
                continue;
            }

            // Write audio data
            if (!WriteAudioData())
            {
                if (!_running) break;
                continue;
            }
        }
    }

    /// <summary>
    /// Pre-fill the ALSA buffer with initial audio data to avoid initial underruns.
    /// Writes enough periods to fill the buffer.
    /// </summary>
    private void PrefillBuffer()
    {
        if (_pcm == 0 || _writeBuffer == null || _callback == null) return;

        // Fill one period to get things started
        // (writing more might block if the buffer is small)
        FillAndWrite();
    }

    /// <summary>
    /// Block on poll() until the ALSA buffer has space for writing.
    /// Returns false if interrupted or on error.
    /// </summary>
    private bool WaitForBufferSpace()
    {
        if (_pollFds == null || _pollFdCount == 0)
        {
            // Fallback: no poll descriptors — just sleep a bit and try to write
            Thread.Sleep(1);
            return true;
        }

        // Set up the wakeup fd in the last poll slot
        _pollFds[_pollFdCount].fd = _wakeupReadFd;
        _pollFds[_pollFdCount].events = POLLIN;
        _pollFds[_pollFdCount].revents = 0;

        int totalFds = _pollFdCount + 1;

        int pollResult;
        fixed (PollFd* fds = _pollFds)
        {
            pollResult = poll(fds, (uint)totalFds, 1000); // 1 second timeout
        }

        if (!_running) return false;

        if (pollResult < 0)
        {
            // poll error — check if it's just EINTR
            return true; // Retry
        }

        if (pollResult == 0)
        {
            // Timeout — check state and retry
            return true;
        }

        // Check if wakeup pipe was signaled
        if ((_pollFds[_pollFdCount].revents & POLLIN) != 0)
        {
            DrainWakeupPipe();
            return _running; // Might have been woken up for stop or device switch
        }

        // Check ALSA revents
        ushort revents;
        fixed (PollFd* fds = _pollFds)
        {
            int err = snd_pcm_poll_descriptors_revents(_pcm, fds, (uint)_pollFdCount, out revents);
            if (err < 0)
            {
                HandleStreamError(err);
                return false;
            }
        }

        if ((revents & POLLERR) != 0)
        {
            // Error on the PCM — likely underrun or device loss
            int state = snd_pcm_state(_pcm);
            if (state == SND_PCM_STATE_XRUN)
            {
                RecoverUnderrun();
            }
            else if (state == SND_PCM_STATE_DISCONNECTED)
            {
                HandleDeviceLost();
                return false;
            }
            else
            {
                // Try recovery
                int err = snd_pcm_recover(_pcm, -EPIPE, 1);
                if (err < 0)
                {
                    HandleStreamError(err);
                    return false;
                }
            }
            return true;
        }

        if ((revents & POLLOUT) == 0)
        {
            // No write-ready event — just retry
            return true;
        }

        return true;
    }

    /// <summary>
    /// Fill the write buffer via the callback and write to ALSA.
    /// Returns false on unrecoverable error.
    /// </summary>
    private bool WriteAudioData()
    {
        return FillAndWrite();
    }

    /// <summary>
    /// Core write loop: get data from callback and write to ALSA device.
    /// </summary>
    private bool FillAndWrite()
    {
        if (_pcm == 0 || _writeBuffer == null || _callback == null)
            return false;

        // Check available space
        long avail = snd_pcm_avail_update(_pcm);
        if (avail < 0)
        {
            // Error — try recovery
            int recovered = snd_pcm_recover(_pcm, (int)avail, 1);
            if (recovered < 0)
            {
                if (IsDeviceLostError(recovered))
                {
                    HandleDeviceLost();
                    return false;
                }
                HandleStreamError(recovered);
                return false;
            }
            avail = snd_pcm_avail_update(_pcm);
            if (avail < 0)
                return true; // Will retry next iteration
        }

        // Don't write more than one period at a time
        long framesToWrite = Math.Min(avail, (long)_periodSize);
        if (framesToWrite <= 0)
            return true;

        // Fill the buffer via the format converter
        int totalBytes = (int)framesToWrite * _deviceFrameBytes;
        var bufferSpan = _writeBuffer.AsSpan(0, totalBytes);

        int framesWritten = _converter!.FillDeviceBuffer(
            bufferSpan, (int)framesToWrite, _callback);

        // If fewer frames written, zero the remainder (silence)
        if (framesWritten < (int)framesToWrite)
        {
            int writtenBytes = framesWritten * _deviceFrameBytes;
            bufferSpan.Slice(writtenBytes).Clear();
        }

        // Write to ALSA
        fixed (byte* ptr = _writeBuffer)
        {
            long written = snd_pcm_writei(_pcm, ptr, (ulong)framesToWrite);

            if (written < 0)
            {
                // Try recovery
                int recovered = snd_pcm_recover(_pcm, (int)written, 1);
                if (recovered < 0)
                {
                    if (IsDeviceLostError(recovered))
                    {
                        HandleDeviceLost();
                        return false;
                    }
                    HandleStreamError(recovered);
                    return false;
                }

                // Retry the write after recovery
                written = snd_pcm_writei(_pcm, ptr, (ulong)framesToWrite);
                if (written < 0)
                {
                    // Still failing — check if device is gone
                    if (IsDeviceLostError((int)written))
                    {
                        HandleDeviceLost();
                        return false;
                    }
                    // Non-fatal — will retry
                }
            }
        }

        return true;
    }

    // ── Error Recovery ───────────────────────────────────────────────────────────

    /// <summary>
    /// Recover from an underrun (xrun).
    /// </summary>
    private void RecoverUnderrun()
    {
        int err = snd_pcm_prepare(_pcm);
        if (err < 0)
        {
            if (IsDeviceLostError(err))
            {
                HandleDeviceLost();
                return;
            }
        }
        // After recovery, pre-fill to avoid immediate next underrun
        // (the audio thread will continue writing normally)
    }

    /// <summary>
    /// Handle an unrecoverable stream error.
    /// </summary>
    private void HandleStreamError(int err)
    {
        if (IsDeviceLostError(err))
        {
            HandleDeviceLost();
            return;
        }

        switch (_switchPolicy)
        {
            case AudioSwitchPolicy.FollowDefault:
            case AudioSwitchPolicy.PreferenceList:
                RequestDeviceSwitch();
                break;
            case AudioSwitchPolicy.None:
                DeviceLost?.Invoke(DeviceLostReason.StreamError);
                _running = false;
                break;
        }
    }

    /// <summary>
    /// Handle device loss (device removed or disconnected).
    /// </summary>
    private void HandleDeviceLost()
    {
        // Notify the device manager so other consumers know
        _deviceManager.NotifyDeviceLost(_deviceName);

        switch (_switchPolicy)
        {
            case AudioSwitchPolicy.FollowDefault:
            case AudioSwitchPolicy.PreferenceList:
                RequestDeviceSwitch();
                break;
            case AudioSwitchPolicy.None:
                DeviceLost?.Invoke(DeviceLostReason.DeviceRemoved);
                _running = false;
                break;
        }
    }

    /// <summary>
    /// Request a device switch (thread-safe).
    /// </summary>
    private void RequestDeviceSwitch()
    {
        _deviceSwitchRequested = true;
        WakeupAudioThread();
    }

    // ── Device Switch Logic ──────────────────────────────────────────────────────

    private void OnDefaultDeviceChanged(AudioDeviceInfo? newDefault)
    {
        if (_disposed) return;

        switch (_switchPolicy)
        {
            case AudioSwitchPolicy.FollowDefault:
                RequestDeviceSwitch();
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

        if (changeType == DeviceChangeType.Removed && _currentDevice != null
            && string.Equals(device?.Id, _currentDevice.Id, StringComparison.Ordinal))
        {
            // Our active device was removed
            switch (_switchPolicy)
            {
                case AudioSwitchPolicy.FollowDefault:
                case AudioSwitchPolicy.PreferenceList:
                    RequestDeviceSwitch();
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
            string bestName = ResolveDeviceName();
            if (_currentDevice != null
                && !string.Equals(bestName, _currentDevice.Id, StringComparison.Ordinal))
            {
                RequestDeviceSwitch();
            }
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
            var savedCallback = _callback;

            try
            {
                // Close the current device
                CloseDevice();

                // Resolve and open the new device
                string newDeviceName = ResolveDeviceName();
                OpenDevice(newDeviceName);

                // Prepare and start writing
                int err = snd_pcm_prepare(_pcm);
                if (err < 0)
                {
                    // Failed — try system default as last resort
                    CloseDevice();
                    OpenDevice("default");
                    err = snd_pcm_prepare(_pcm);
                    if (err < 0)
                        throw new InvalidOperationException(
                            $"Failed to prepare ALSA PCM after device switch: {GetErrorString(err)}");
                }

                _callback = savedCallback;

                // Pre-fill to avoid underrun
                PrefillBuffer();

                // Notify consumers of format change if applicable
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
        CloseWakeupPipe();
    }
}
