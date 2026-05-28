using System.Runtime.InteropServices;
using static AN.Audio.Platforms.Linux.AlsaInterop;

namespace AN.Audio.Platforms.Linux;

/// <summary>
/// Linux device manager using ALSA device name hints for enumeration.
/// ALSA has no real-time change notification — device loss is detected reactively
/// via stream errors (-ENODEV, -EIO) in AlsaAudioOutput.
/// Singleton per process.
/// </summary>
internal sealed class AlsaDeviceManager : IAudioDeviceManager
{
    private static AlsaDeviceManager? _instance;
    private static readonly object _lock = new();

    private bool _disposed;

    public event Action<AudioDeviceInfo?>? DefaultDeviceChanged;
    public event Action<DeviceChangeType, AudioDeviceInfo?>? DeviceListChanged;

    private AlsaDeviceManager()
    {
    }

    public static AlsaDeviceManager Instance
    {
        get
        {
            if (_instance != null) return _instance;
            lock (_lock)
            {
                _instance ??= new AlsaDeviceManager();
                return _instance;
            }
        }
    }

    // ── Device Enumeration ──────────────────────────────────────────

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        var devices = new List<AudioDeviceInfo>();

        int err = snd_device_name_hint(-1, "pcm", out nint hints);
        if (err < 0 || hints == 0)
            return devices;

        try
        {
            // Iterate the null-terminated array of hint pointers
            nint current = hints;
            while (true)
            {
                nint hintPtr = Marshal.ReadIntPtr(current);
                if (hintPtr == 0) break;

                var info = ParseHint(hintPtr);
                if (info != null)
                    devices.Add(info);

                current += nint.Size;
            }
        }
        finally
        {
            snd_device_name_free_hint(hints);
        }

        // Ensure "default" is always in the list and marked as default
        bool hasDefault = false;
        foreach (var d in devices)
        {
            if (string.Equals(d.Id, "default", StringComparison.Ordinal))
            {
                hasDefault = true;
                break;
            }
        }

        if (!hasDefault)
        {
            devices.Insert(0, new AudioDeviceInfo("default", "Default", isDefault: true));
        }

        return devices;
    }

    /// <summary>
    /// Get device info for a specific device ID string, or null if not found.
    /// </summary>
    public AudioDeviceInfo? GetDeviceById(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
            return null;

        var devices = GetOutputDevices();
        foreach (var d in devices)
        {
            if (string.Equals(d.Id, deviceId, StringComparison.Ordinal))
                return d;
        }

        // If not in the enumerated list, create a basic info for it
        // (the device might still be valid — ALSA has many devices not shown in hints)
        if (string.Equals(deviceId, "default", StringComparison.Ordinal))
            return new AudioDeviceInfo("default", "Default", isDefault: true);

        return new AudioDeviceInfo(deviceId, deviceId, isDefault: false);
    }

    /// <summary>
    /// Notify subscribers that the device situation has changed.
    /// Called by AlsaAudioOutput when it detects a device loss via stream error.
    /// Since ALSA has no proactive notification, this is the reactive path.
    /// </summary>
    internal void NotifyDeviceLost(string deviceId)
    {
        var info = new AudioDeviceInfo(deviceId, deviceId, isDefault: false);
        DeviceListChanged?.Invoke(DeviceChangeType.Removed, info);
    }

    /// <summary>
    /// Notify subscribers that the default device has effectively changed.
    /// Called after recovery to a new device.
    /// </summary>
    internal void NotifyDefaultDeviceChanged()
    {
        // On Linux, "default" always points to whatever the sound server designates.
        // We fire the event with the "default" device info.
        var info = new AudioDeviceInfo("default", "Default", isDefault: true);
        DefaultDeviceChanged?.Invoke(info);
    }

    // ── Hint Parsing ────────────────────────────────────────────────

    /// <summary>
    /// Parse a single device hint into an AudioDeviceInfo, or null if it should be filtered out.
    /// </summary>
    private static AudioDeviceInfo? ParseHint(nint hintPtr)
    {
        nint namePtr = snd_device_name_get_hint(hintPtr, "NAME");
        nint descPtr = snd_device_name_get_hint(hintPtr, "DESC");
        nint ioidPtr = snd_device_name_get_hint(hintPtr, "IOID");

        try
        {
            string? name = namePtr != 0 ? Marshal.PtrToStringUTF8(namePtr) : null;
            string? desc = descPtr != 0 ? Marshal.PtrToStringUTF8(descPtr) : null;
            string? ioid = ioidPtr != 0 ? Marshal.PtrToStringUTF8(ioidPtr) : null;

            if (string.IsNullOrEmpty(name))
                return null;

            // Filter: IOID of "Input" means capture-only — skip
            if (string.Equals(ioid, "Input", StringComparison.OrdinalIgnoreCase))
                return null;

            // Filter out virtual routing devices that aren't useful for user selection
            if (ShouldFilter(name))
                return null;

            // Build display name from description (take first line only)
            string displayName = BuildDisplayName(name, desc);
            bool isDefault = string.Equals(name, "default", StringComparison.Ordinal);

            return new AudioDeviceInfo(name, displayName, isDefault);
        }
        finally
        {
            if (namePtr != 0) free(namePtr);
            if (descPtr != 0) free(descPtr);
            if (ioidPtr != 0) free(ioidPtr);
        }
    }

    /// <summary>
    /// Determine if a device name should be filtered from the user-facing list.
    /// We keep: default, plughw:*, hw:*, pulse, pipewire.
    /// We skip: null, dmix, dsnoop, surround*, iec958, spdif, and other virtual routing devices.
    /// </summary>
    private static bool ShouldFilter(string name)
    {
        // Always include these
        if (string.Equals(name, "default", StringComparison.Ordinal)) return false;
        if (string.Equals(name, "pulse", StringComparison.Ordinal)) return false;
        if (string.Equals(name, "pipewire", StringComparison.Ordinal)) return false;
        if (name.StartsWith("plughw:", StringComparison.Ordinal)) return false;
        if (name.StartsWith("hw:", StringComparison.Ordinal)) return false;

        // Filter out known virtual/routing devices
        if (string.Equals(name, "null", StringComparison.Ordinal)) return true;
        if (name.StartsWith("dmix", StringComparison.Ordinal)) return true;
        if (name.StartsWith("dsnoop", StringComparison.Ordinal)) return true;
        if (name.StartsWith("surround", StringComparison.Ordinal)) return true;
        if (name.StartsWith("iec958", StringComparison.Ordinal)) return true;
        if (name.StartsWith("spdif", StringComparison.Ordinal)) return true;
        if (name.StartsWith("front", StringComparison.Ordinal)) return true;
        if (name.StartsWith("rear", StringComparison.Ordinal)) return true;
        if (name.StartsWith("center_lfe", StringComparison.Ordinal)) return true;
        if (name.StartsWith("side", StringComparison.Ordinal)) return true;
        if (name.StartsWith("hdmi", StringComparison.Ordinal)) return true;

        // Default: include (there may be custom user-defined ALSA devices)
        return false;
    }

    /// <summary>
    /// Build a human-friendly display name from the hint name and description.
    /// Takes the first line of the description if available, otherwise uses the device name.
    /// </summary>
    private static string BuildDisplayName(string name, string? desc)
    {
        if (!string.IsNullOrEmpty(desc))
        {
            // Description often has multiple lines separated by \n
            // Take the first line as the display name
            int newlineIdx = desc.IndexOf('\n');
            if (newlineIdx > 0)
                return desc.Substring(0, newlineIdx);
            return desc;
        }

        // Fallback: use the device name
        return name switch
        {
            "default" => "Default",
            "pulse" => "PulseAudio",
            "pipewire" => "PipeWire",
            _ => name
        };
    }

    // ── Dispose ─────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // No native resources to release for ALSA device enumeration.
        // The hint pointers are freed after each enumeration call.
    }
}
