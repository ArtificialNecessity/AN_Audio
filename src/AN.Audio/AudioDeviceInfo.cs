namespace AN.Audio;

/// <summary>
/// Describes an available audio output device.
/// </summary>
public sealed class AudioDeviceInfo
{
    /// <summary>
    /// Platform-specific device identifier. Opaque string — pass this to
    /// AudioOutput.Create() or set in PreferredDevices to target a specific device.
    /// Windows: endpoint ID string (e.g., "{0.0.0.00000000}.{guid}")
    /// Linux: ALSA device name (e.g., "default", "hw:0,0", "plughw:1,0")
    /// macOS: AudioDeviceID as string (e.g., "74")
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Human-readable name for display in UI.
    /// Windows: "Speakers (Realtek High Definition Audio)"
    /// Linux: "HDA Intel PCH, ALC892 Analog"
    /// macOS: "MacBook Pro Speakers"
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// True if this is the current system default output device.
    /// </summary>
    public bool IsDefault { get; }

    public AudioDeviceInfo(string id, string displayName, bool isDefault)
    {
        Id = id;
        DisplayName = displayName;
        IsDefault = isDefault;
    }

    public override string ToString() => $"{DisplayName} ({Id}){(IsDefault ? " [Default]" : "")}";
}