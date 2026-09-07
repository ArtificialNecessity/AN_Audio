using System.Security.Cryptography;
using System.Text;

namespace AN.Audio.Midi;

/// <summary>
/// Per-port INSTANCE identity, stable across WinMM index renumbering (SPEC-30 D11/D19).
/// Opaque; persist this, never the display name. Same physical port → same key across sessions
/// (for GUID-reporting drivers; see D11 for the ordinal fallback limitation).
/// </summary>
public readonly record struct MidiInput_DeviceKey(string Value)
{
    public override string ToString() => Value;
}

/// <summary>
/// Per device TYPE identity (SPEC-30 D11). Derived from the SysEx Identity Reply when the device answers;
/// otherwise a hash of the driver-reported (name, manufacturer id, product id) flagged <see cref="IsUnknownType"/>.
/// Two units of the same model share a TypeId; use <see cref="MidiInput_DeviceKey"/> to tell them apart.
/// </summary>
public readonly record struct MidiInput_DeviceTypeId(Guid Value, bool IsUnknownType)
{
    public static MidiInput_DeviceTypeId FromIdentity(MidiInput_DeviceIdentity identity)
        => new(StableGuid($"midi-identity|{identity.Manufacturer.Value}|{identity.Manufacturer.IsExtended}|{identity.Family}|{identity.Member}"), IsUnknownType: false);

    public static MidiInput_DeviceTypeId UnknownFromDriverCaps(string portName, ushort driverManufacturerId, ushort driverProductId)
        => new(StableGuid($"midi-drivercaps|{portName}|{driverManufacturerId}|{driverProductId}"), IsUnknownType: true);

    private static Guid StableGuid(string canonical)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(canonical), hash);
        return new Guid(hash[..16]);
    }

    public override string ToString() => IsUnknownType ? $"{Value} (unknown type)" : Value.ToString();
}

/// <summary>Parsed Universal Identity Reply (F0 7E dev 06 02 ...).</summary>
public sealed record MidiInput_DeviceIdentity(
    Midi_ManufacturerId Manufacturer,
    ushort Family,              // 14-bit, LSB first on the wire
    ushort Member,              // 14-bit, LSB first on the wire
    uint SoftwareRevision)      // 4 x 7-bit bytes, vendor-specific layout, packed LSB first
{
    public override string ToString() => $"mfr={Manufacturer} family={Family} member={Member} rev={SoftwareRevision:X8}";
}

/// <summary>A MIDI input port as seen by the consumer. Immutable snapshot; the library publishes a new record when Identity resolves.</summary>
public sealed record MidiInput_DeviceInfo(
    MidiInput_DeviceKey Key,
    MidiInput_DeviceTypeId TypeId,
    string Name,                             // display only (D19)
    MidiInput_DeviceIdentity? Identity,      // null until the device answers an Identity Request
    bool HasPairedOutput)                    // a same-named output port exists (needed for RequestIdentity)
{
    public override string ToString() => $"{Name} [{Key}] type={TypeId}" + (Identity is null ? "" : $" {Identity}");
}

/// <summary>A reassembled SysEx message (cold path, SPEC-30 D7). <see cref="Bytes"/> is valid only for the duration of the event call.</summary>
public sealed class MidiInput_SysExMessage
{
    public long ArrivalTicks { get; init; }
    public MidiInput_PortIndex Port { get; init; }
    /// <summary>F0 .. F7 inclusive.</summary>
    public ReadOnlyMemory<byte> Bytes { get; init; }
}

public enum MidiInput_LostReason
{
    Unplugged,
    InUseByAnotherApplication,
    DriverError,
    /// <summary>Closed because the consumer called Stop()/Dispose() or the policy no longer selects it.</summary>
    Stopped,
}

public enum MidiInput_OpenPolicy
{
    /// <summary>Open every input port; hot-plug arrivals join automatically (default).</summary>
    AllDevices,
    /// <summary>Open only ports whose key is in <see cref="MidiInput_Options.PreferredDevices"/>.</summary>
    PreferenceList,
    /// <summary>Open nothing; enumerate only.</summary>
    None,
}

public enum MidiInput_HotPlugSource
{
    /// <summary>Background thread re-enumerates every <see cref="MidiInput_Options.PollIntervalMs"/> (v1; WinMM has no notification).</summary>
    Poll,
    /// <summary>Sprint 2: host calls <see cref="IMidiInput_DeviceManager.NotifyDeviceChange"/> from its own WM_DEVICECHANGE.</summary>
    HostSupplied,
    /// <summary>Sprint 2: library owns a message-only window on a side thread.</summary>
    LibraryWindow,
}