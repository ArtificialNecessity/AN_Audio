namespace AN.Audio.Midi;

/// <summary>Options for <see cref="MidiInput.Create"/>. All defaults satisfy "plug in, press a key, hear a note".</summary>
public sealed class MidiInput_Options
{
    public MidiInput_OpenPolicy OpenPolicy { get; init; } = MidiInput_OpenPolicy.AllDevices;

    /// <summary>Used when <see cref="OpenPolicy"/> is <see cref="MidiInput_OpenPolicy.PreferenceList"/>.</summary>
    public IReadOnlyList<MidiInput_DeviceKey>? PreferredDevices { get; init; }

    /// <summary>D23: starting capacity of the message ring, in messages. Any positive value.</summary>
    public int RingInitialCapacity { get; init; } = 1024;

    /// <summary>D23: the ring grows (off the steady-state path) up to this many messages. Equal to initial = fixed size.</summary>
    public int RingMaxCapacity { get; init; } = 16384;

    /// <summary>D10: hot-plug re-enumeration interval for <see cref="MidiInput_HotPlugSource.Poll"/>.</summary>
    public int PollIntervalMs { get; init; } = 1000;

    public MidiInput_HotPlugSource HotPlugSource { get; init; } = MidiInput_HotPlugSource.Poll;

    /// <summary>D8: send a Universal Identity Request to each port when it opens (requires a paired output port).</summary>
    public bool RequestIdentityOnOpen { get; init; } = true;

    /// <summary>D9: how long to wait for an Identity Reply before giving up silently.</summary>
    public int IdentityReplyTimeoutMs { get; init; } = 500;

    /// <summary>D22: open with MIDI_IO_STATUS so the driver reports lag (MIM_MOREDATA → LagCount). Disable for misbehaving legacy drivers.</summary>
    public bool EnableIoStatus { get; init; } = true;

    /// <summary>Number of pre-posted SysEx receive buffers per port.</summary>
    public int SysExBuffersPerPort { get; init; } = 2;

    /// <summary>Size of each SysEx receive buffer. Longer messages arrive as fragments and are reassembled.</summary>
    public int SysExBufferBytes { get; init; } = 256;

    /// <summary>D20: reassembly cap. A SysEx message longer than this is discarded and counted.</summary>
    public int SysExMaxBytes { get; init; } = 64 * 1024;

    internal void Validate()
    {
        if (RingInitialCapacity <= 0) throw new ArgumentException("RingInitialCapacity must be positive", nameof(RingInitialCapacity));
        if (RingMaxCapacity < RingInitialCapacity) throw new ArgumentException("RingMaxCapacity must be >= RingInitialCapacity", nameof(RingMaxCapacity));
        if (PollIntervalMs <= 0) throw new ArgumentException("PollIntervalMs must be positive", nameof(PollIntervalMs));
        if (SysExBuffersPerPort <= 0) throw new ArgumentException("SysExBuffersPerPort must be positive", nameof(SysExBuffersPerPort));
        if (SysExBufferBytes < 16) throw new ArgumentException("SysExBufferBytes must be >= 16", nameof(SysExBufferBytes));
        if (SysExMaxBytes < SysExBufferBytes) throw new ArgumentException("SysExMaxBytes must be >= SysExBufferBytes", nameof(SysExMaxBytes));
        if (OpenPolicy == MidiInput_OpenPolicy.PreferenceList && (PreferredDevices is null || PreferredDevices.Count == 0))
            throw new ArgumentException("PreferenceList policy requires a non-empty PreferredDevices list", nameof(PreferredDevices));
    }
}