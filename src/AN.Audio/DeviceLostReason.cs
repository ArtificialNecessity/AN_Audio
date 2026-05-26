namespace AN.Audio;

/// <summary>
/// Describes why an audio device was lost.
/// </summary>
public enum DeviceLostReason
{
    /// <summary>Hardware was unplugged or physically removed.</summary>
    DeviceRemoved,

    /// <summary>User disabled the device in system settings.</summary>
    DeviceDisabled,

    /// <summary>
    /// The system default device changed while using default mode.
    /// (Only relevant when SwitchPolicy is None.)
    /// </summary>
    DefaultChanged,

    /// <summary>Unrecoverable stream error (e.g., ALSA -ENODEV).</summary>
    StreamError,
}