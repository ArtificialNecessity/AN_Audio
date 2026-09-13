using AN.Audio.Platforms.Windows.Asio;
using Xunit;

namespace AN.Audio.Tests.Interop;

/// <summary>Spec 70 D3 — driver keys/ids and registry enumeration (hardware-dependent parts skip without a driver).</summary>
public class Asio_DriverRegistryTests
{
    [Fact]
    public void DeviceId_round_trips_and_is_recognisable()
    {
        var key = new Asio_DriverKey(new Guid("3E08EBEA-46E3-45B9-8216-312892B30F94"));
        string id = key.ToDeviceId();
        Assert.Equal("asio:{3E08EBEA-46E3-45B9-8216-312892B30F94}", id);
        Assert.True(Asio_DriverKey.IsAsioDeviceId(id));
        Assert.True(Asio_DriverKey.TryParse(id, out var parsed)); Assert.Equal(key, parsed);
        Assert.True(Asio_DriverKey.TryParse(id.ToLowerInvariant(), out _));
        Assert.False(Asio_DriverKey.IsAsioDeviceId("{0.0.0.00000000}.{guid}"));
        Assert.False(Asio_DriverKey.TryParse("asio:not-a-guid", out _));
        Assert.False(Asio_DriverKey.TryParse(null, out _));
    }

    [Fact]
    public void Enumerate_returns_only_loadable_drivers_for_this_bitness()
    {
        if (!OperatingSystem.IsWindows()) return;
        var drivers = Asio_DriverRegistry.Enumerate();
        foreach (var d in drivers)
        {
            Assert.True(File.Exists(d.DllPath.Trim('"')), d.DllPath);
            Assert.NotEqual(Guid.Empty, d.Key.Clsid);
            Assert.False(string.IsNullOrWhiteSpace(d.Name));
        }
        var manager = AudioOutput.GetDeviceManager(AudioOutput_Backend.Asio);
        Assert.NotNull(manager);
        Assert.Equal(drivers.Count, manager!.GetOutputDevices().Count);
        if (drivers.Count == 0) Console.Error.WriteLine("[skip] no ASIO driver on this machine");
    }

    [Fact]
    public void Backend_Asio_without_driver_falls_back_to_wasapi_with_reason()
    {
        if (!OperatingSystem.IsWindows()) return;
        // A CLSID nobody has: Asio backend requested, named driver missing, no other driver? (depends on machine) — so only assert the
        // named-driver path when the machine has none at all.
        if (Asio_DriverRegistry.Enumerate().Count != 0) return;
        using var output = AudioOutput.Create(new AudioFormat(48000, 2, SampleFormat.Float32), new AudioOutputOptions { Backend = AudioOutput_Backend.Asio });
        Assert.Equal(AudioOutput_LatencyFallbackReason.BackendUnavailable, output.LatencyFallbackReason);
    }
}