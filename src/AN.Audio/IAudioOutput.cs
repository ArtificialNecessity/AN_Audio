namespace AN.Audio;

/// <summary>
/// Callback invoked on the audio thread when the backend needs more samples.
/// Write interleaved PCM data into the buffer. Return the number of frames written.
/// If fewer frames are written than requested, remaining samples are filled with silence.
/// The format parameter always matches the Format you requested at creation time.
/// </summary>
public delegate int AudioCallback(Span<byte> buffer, int frameCount, AudioFormat format);

/// <summary>
/// Platform-independent audio output interface.
/// Implementations are event-driven: the OS signals when it needs more data.
/// </summary>
public interface IAudioOutput : IDisposable
{
    /// <summary>
    /// The PCM format the consumer's callback works with.
    /// This is the format requested at creation time and never changes.
    /// </summary>
    AudioFormat Format { get; }

    /// <summary>
    /// The native PCM format of the current audio endpoint.
    /// May differ from Format (in which case AN.Audio converts internally).
    /// May change when the device switches.
    /// </summary>
    AudioFormat DeviceFormat { get; }

    /// <summary>
    /// Start playback. The AudioCallback will be invoked on a dedicated audio thread
    /// whenever the OS needs more samples. This is NOT the calling thread.
    /// </summary>
    void Start(AudioCallback callback);

    /// <summary>
    /// Stop playback. Blocks until the audio thread has drained or stopped.
    /// After Stop(), Start() can be called again.
    /// </summary>
    void Stop();

    /// <summary>
    /// Estimated latency from buffer submission to DAC output, in milliseconds.
    /// </summary>
    double LatencyMs { get; }

    // ── Device Management ──────────────────────────────────────────────

    /// <summary>
    /// Controls how this output handles device changes.
    /// Mutable — can be changed at any time. Takes effect on next device event.
    /// Default: <see cref="AudioSwitchPolicy.FollowDefault"/>.
    /// </summary>
    AudioSwitchPolicy SwitchPolicy { get; set; }

    /// <summary>
    /// Ordered list of preferred device IDs (highest priority first).
    /// Only used when <see cref="SwitchPolicy"/> is <see cref="AudioSwitchPolicy.PreferenceList"/>.
    /// Setting this re-evaluates the current device immediately if policy is PreferenceList.
    /// Null or empty means "use system default" as fallback.
    /// </summary>
    IReadOnlyList<string>? PreferredDevices { get; set; }

    /// <summary>
    /// The device currently being used for output, or null if using system default
    /// and no device manager is available.
    /// </summary>
    AudioDeviceInfo? CurrentDevice { get; }

    /// <summary>
    /// Fired when the native device format changes (e.g., after a device switch).
    /// The new DeviceFormat is passed. Consumer can optionally adapt their audio
    /// generation to match for zero-conversion overhead.
    /// Fired on a background thread.
    /// </summary>
    event Action<AudioFormat>? DeviceFormatChanged;

    /// <summary>
    /// Fired when the active device is lost.
    /// When SwitchPolicy is FollowDefault or PreferenceList, this is informational —
    /// AN.Audio will automatically switch to an appropriate device.
    /// When SwitchPolicy is None, the callback stops being invoked and the client
    /// must handle recovery.
    /// Fired on a background thread.
    /// </summary>
    event Action<DeviceLostReason>? DeviceLost;

    /// <summary>
    /// Fired after a successful automatic device switch.
    /// Includes the new device info. Only fires when SwitchPolicy is not None.
    /// Fired on a background thread.
    /// </summary>
    event Action<AudioDeviceInfo>? DeviceSwitched;
}