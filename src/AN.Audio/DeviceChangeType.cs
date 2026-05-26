namespace AN.Audio;

/// <summary>
/// Describes the type of device list change.
/// </summary>
public enum DeviceChangeType
{
    /// <summary>A new device was plugged in or became available.</summary>
    Added,

    /// <summary>A device was removed or became unavailable.</summary>
    Removed,

    /// <summary>A device's state changed (e.g., disabled → active).</summary>
    StateChanged,
}