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
    /// Desired buffer size in milliseconds (affects latency).
    /// Default: 20ms.
    /// </summary>
    public int BufferSizeMs { get; set; } = 20;
}