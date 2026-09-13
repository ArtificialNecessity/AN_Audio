using System.Runtime.Versioning;
using Microsoft.Win32;

namespace AN.Audio.Platforms.Windows.Asio;

/// <summary>Stable identity of an installed ASIO driver: its COM CLSID. Surfaces to consumers as the device id <c>asio:{CLSID}</c> (spec 70 D3).</summary>
internal readonly record struct Asio_DriverKey(Guid Clsid)
{
    public const string DeviceIdPrefix = "asio:";
    public string ToDeviceId() => DeviceIdPrefix + Clsid.ToString("B").ToUpperInvariant();
    public static bool IsAsioDeviceId(string? id) => id is not null && id.StartsWith(DeviceIdPrefix, StringComparison.OrdinalIgnoreCase);
    public static bool TryParse(string? deviceId, out Asio_DriverKey key)
    {
        key = default;
        if (!IsAsioDeviceId(deviceId) || !Guid.TryParse(deviceId!.AsSpan(DeviceIdPrefix.Length), out Guid g)) return false;
        key = new(g); return true;
    }
}

/// <summary>One <c>HKLM\SOFTWARE\ASIO\&lt;name&gt;</c> entry whose COM server DLL exists on disk.</summary>
internal sealed record Asio_DriverInfo(Asio_DriverKey Key, string Name, string DllPath);

/// <summary>
/// Spec 70 D3: enumerate ASIO drivers from the registry view that matches THIS process's bitness (drivers register per bitness; a 64-bit
/// process cannot load the 32-bit <c>WOW6432Node</c> entries). Read fresh every call — never a cached list.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Asio_DriverRegistry
{
    public static IReadOnlyList<Asio_DriverInfo> Enumerate()
    {
        var list = new List<Asio_DriverInfo>();
        if (!OperatingSystem.IsWindows()) return list;
        var view = Environment.Is64BitProcess ? RegistryView.Registry64 : RegistryView.Registry32;
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var asio = hklm.OpenSubKey(@"SOFTWARE\ASIO");
        if (asio is null) return list;
        foreach (string name in asio.GetSubKeyNames())
        {
            using var k = asio.OpenSubKey(name);
            if (k?.GetValue("CLSID") is not string clsidText || !Guid.TryParse(clsidText, out Guid clsid)) continue;
            string display = k.GetValue("Description") as string ?? name;
            // The COM server must exist: HKCR\CLSID\{..}\InprocServer32 (default) in the same view.
            using var cls = hklm.OpenSubKey(@"SOFTWARE\Classes\CLSID\" + clsid.ToString("B") + @"\InprocServer32");
            string dll = Environment.ExpandEnvironmentVariables(cls?.GetValue("") as string ?? "");
            if (dll.Length == 0 || !File.Exists(dll.Trim('"'))) continue;
            list.Add(new(new(clsid), display, dll));
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;
    }

    public static Asio_DriverInfo? Find(Asio_DriverKey key) => Enumerate().FirstOrDefault(d => d.Key == key);
}