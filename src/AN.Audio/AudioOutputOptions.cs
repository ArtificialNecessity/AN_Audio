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
}