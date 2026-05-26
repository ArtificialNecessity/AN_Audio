namespace AN.Audio;

/// <summary>
/// Controls how AN.Audio handles device changes.
/// </summary>
public enum AudioSwitchPolicy
{
    /// <summary>
    /// AN.Audio follows the system default device. When the OS default changes,
    /// the audio stream is seamlessly re-opened on the new default device.
    /// This is the default policy.
    /// </summary>
    FollowDefault,

    /// <summary>
    /// AN.Audio uses a preferred device (or ordered preference list).
    /// If the active preferred device is removed, falls back to the next preferred
    /// device in the list, or to the system default if no preferred devices are available.
    /// </summary>
    PreferenceList,

    /// <summary>
    /// AN.Audio does not automatically switch devices. When the active device is lost,
    /// the DeviceLost event fires and the callback stops being invoked.
    /// The client is responsible for handling the situation.
    /// </summary>
    None,
}