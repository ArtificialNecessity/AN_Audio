namespace AN.Audio;

/// <summary>
/// Options for creating an audio output via <see cref="AudioOutput.Create(AudioFormat, AudioOutputOptions?)"/>.
/// </summary>
public sealed class AudioOutputOptions
{
    /// <summary>
    /// Controls how AN.Audio handles device changes.
    /// Default: <see cref="AudioSwitchPolicy.FollowDefault"/>.
    /// </summary>
    public AudioSwitchPolicy SwitchPolicy { get; set; } = AudioSwitchPolicy.FollowDefault;

    /// <summary>
    /// Ordered list of preferred device IDs (highest priority first).
    /// Only used when <see cref="SwitchPolicy"/> is <see cref="AudioSwitchPolicy.PreferenceList"/>.
    /// If none of the preferred devices are available, falls back to system default.
    /// Null or empty means "use system default".
    /// </summary>
    public IReadOnlyList<string>? PreferredDevices { get; set; }

    /// <summary>
    /// Desired buffer size in milliseconds (affects latency). <see cref="AudioOutput_LatencyMode.Default"/> only; ignored in
    /// <see cref="AudioOutput_LatencyMode.LowLatency"/>, where the OS chooses the period (spec 60 D1/D2).
    /// Default: 20ms.
    /// </summary>
    public int BufferSizeMs { get; set; } = 20;

    /// <summary>
    /// Spec 60: ask the OS for its minimum shared-mode period. Default: <see cref="AudioOutput_LatencyMode.Default"/> (today's behaviour).
    /// Opt in only where a human plays live; media playback gains nothing and pays CPU wake-ups.
    /// </summary>
    public AudioOutput_LatencyMode Latency { get; set; } = AudioOutput_LatencyMode.Default;

    /// <summary>
    /// Spec 60: bypass the endpoint's system effects chain (Windows RAW mode). Default: <see cref="AudioOutput_StreamProcessing.SystemEffects"/>.
    /// </summary>
    public AudioOutput_StreamProcessing Processing { get; set; } = AudioOutput_StreamProcessing.SystemEffects;

    /// <summary>
    /// Spec 70 D2: which Windows audio stack to use. Default <see cref="AudioOutput_Backend.Auto"/> = WASAPI unless an <c>asio:</c> device id is
    /// selected through <see cref="PreferredDevices"/>. Ignored on macOS/Linux.
    /// </summary>
    public AudioOutput_Backend Backend { get; set; } = AudioOutput_Backend.Auto;

    /// <summary>
    /// Spec 70 D4 (ASIO only): a window the driver's control panel dialog should be owned by. Null = AN.Audio's own hidden host window.
    /// The driver is always initialised with AN.Audio's window; this only affects dialog ownership.
    /// </summary>
    public AudioOutput_NativeWindowHandle? Asio_OwnerWindow { get; set; }

    /// <summary>
    /// Spec 70 D8 (ASIO only): first hardware output channel to render to. Default 0 = <c>Out 1</c>; 2 targets <c>Out 3/4</c> on a 4-out interface.
    /// Consumer channels beyond the driver's outputs are dropped.
    /// </summary>
    public Asio_ChannelIndex Asio_OutputChannelOffset { get; set; } = new(0);

    /// <summary>
    /// Spec 70 D6 (ASIO only): keep the device clock and resample (default), or switch the device to the consumer's rate. Switching mutes the
    /// outputs while the hardware relocks (measured 1–2 s on the MOTU M4) and changes the rate for every other application.
    /// </summary>
    public Asio_SampleRatePolicy Asio_SampleRate { get; set; } = Asio_SampleRatePolicy.AdoptDriverRate;
}