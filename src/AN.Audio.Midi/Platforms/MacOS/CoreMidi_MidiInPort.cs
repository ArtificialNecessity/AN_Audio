using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AN.Audio.Midi.Internal;

namespace AN.Audio.Midi.Platforms.MacOS;

#pragma warning disable AN0100

/// <summary>One connected CoreMIDI source inside a CoreMidi_MidiInPort: the slot, its entry/info, and per-source SysEx reassembly state.</summary>
internal sealed class CoreMidi_MidiInSource
{
    public MidiInput_PortIndex Index { get; }
    public CoreMidi_MidiInDeviceEntry Entry { get; }
    public MidiInput_DeviceInfo Info { get; set; }
    public Midi_SysExReassembler Reassembler { get; }
    public volatile bool Connected;

    public CoreMidi_MidiInSource(MidiInput_PortIndex index, CoreMidi_MidiInDeviceEntry entry, MidiInput_Options options)
    {
        Index = index;
        Entry = entry;
        Info = entry.ToDeviceInfo();
        Reassembler = new Midi_SysExReassembler(options.SysExBufferBytes * 2, options.SysExMaxBytes);
    }
}

/// <summary>
/// ONE CoreMIDI input port per IMidiInput instance (SPEC-30 §10.2): every selected source is connected to it and the
/// srcConnRefCon identifies the slot. Owns the [UnmanagedCallersOnly] MIDIReadProc — the hot path: stamp, walk packets, hand off.
/// </summary>
internal sealed unsafe class CoreMidi_MidiInPort
{
    private const int MaxPortSlots = 256;

    private readonly CoreMidi_MidiInput _owner;
    private readonly MidiInput_Options _options;
    private readonly CoreMidi_MidiInSource?[] _sources = new CoreMidi_MidiInSource?[MaxPortSlots];
    private GCHandle _selfHandle;
    private CoreMidi_PortRef _port;
    private int _callbacksInFlight;
    private volatile bool _disposing;

    public bool IsCreated => !_port.IsNull;

    public CoreMidi_MidiInPort(CoreMidi_MidiInput owner, MidiInput_Options options)
    {
        _owner = owner;
        _options = options;
    }

    public CoreMidi_MidiInSource? this[int slot] => _sources[slot];

    /// <summary>Create the MIDIPortRef against the process client. Never throws.</summary>
    public CoreMidi_Status Create()
    {
        var client = CoreMidi_Client.Instance;
        if (client.Client.IsNull) return client.CreateStatus == CoreMidi_Status.NoError ? CoreMidi_Status.InvalidClient : client.CreateStatus;

        _selfHandle = GCHandle.Alloc(this);
        nint name = CoreFoundation_Interop.CreateCFString("AN.Audio.Midi input");
        try
        {
            uint port = 0;
            var readProc = (delegate* unmanaged[Cdecl]<byte*, void*, void*, void>)&ReadProc;
            var r = CoreMidi_Interop.MIDIInputPortCreate(client.Client.Value, name, (nint)readProc, (void*)GCHandle.ToIntPtr(_selfHandle), &port);
            if (r != CoreMidi_Status.NoError) { _selfHandle.Free(); return r; }
            _port = new CoreMidi_PortRef(port);
            return CoreMidi_Status.NoError;
        }
        finally { CoreFoundation_Interop.CFRelease(name); }
    }

    /// <summary>Connect a source into <paramref name="slot"/>. Never throws.</summary>
    public CoreMidi_Status Connect(int slot, CoreMidi_MidiInDeviceEntry entry)
    {
        var source = new CoreMidi_MidiInSource(new MidiInput_PortIndex((byte)slot), entry, _options);
        _sources[slot] = source;
        var r = CoreMidi_Interop.MIDIPortConnectSource(_port.Value, entry.Source.Value, (void*)(nint)slot);
        if (r != CoreMidi_Status.NoError) { _sources[slot] = null; return r; }
        source.Connected = true;
        return CoreMidi_Status.NoError;
    }

    /// <summary>Disconnect and free the slot. Waits for any in-flight callback so the reassembler is never touched after we let go.</summary>
    public void Disconnect(int slot)
    {
        var source = _sources[slot];
        if (source is null) return;
        source.Connected = false;
        CoreMidi_Interop.MIDIPortDisconnectSource(_port.Value, source.Entry.Source.Value);   // NoConnection/UnknownEndpoint if already gone: fine
        WaitForCallbacksToDrain();
        source.Reassembler.Reset();
        _sources[slot] = null;
    }

    public void Dispose()
    {
        if (_port.IsNull) return;
        _disposing = true;
        for (int i = 0; i < MaxPortSlots; i++) Disconnect(i);
        WaitForCallbacksToDrain();
        CoreMidi_Interop.MIDIPortDispose(_port.Value);
        _port = new CoreMidi_PortRef(0);
        if (_selfHandle.IsAllocated) _selfHandle.Free();
    }

    private void WaitForCallbacksToDrain()
    {
        var spin = new SpinWait();
        while (Volatile.Read(ref _callbacksInFlight) != 0) spin.SpinOnce();
    }

    // ── test hooks (InternalsVisibleTo AN.Audio.Midi.Tests): exercise the walker without hardware ─────────────────────────────

    /// <summary>Place an entry in a slot WITHOUT calling MIDIPortConnectSource (no real endpoint needed).</summary>
    internal CoreMidi_MidiInSource AttachForTest(int slot, CoreMidi_MidiInDeviceEntry entry)
    {
        var source = new CoreMidi_MidiInSource(new MidiInput_PortIndex((byte)slot), entry, _options) { Connected = true };
        _sources[slot] = source;
        return source;
    }

    /// <summary>Run the packet walk on a hand-built MIDIPacketList image as if the CoreMIDI thread had delivered it.</summary>
    internal void WalkPacketListForTest(byte* packetList, CoreMidi_MidiInSource source, long arrival) => WalkPacketList(packetList, source, arrival);

    // ── CoreMIDI receive thread ──────────────────────────────────────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ReadProc(byte* packetList, void* readProcRefCon, void* srcConnRefCon)
    {
        long arrival = Stopwatch.GetTimestamp();
        if (GCHandle.FromIntPtr((nint)readProcRefCon).Target is not CoreMidi_MidiInPort port) return;

        Interlocked.Increment(ref port._callbacksInFlight);
        CoreMidi_MidiInput.EnterCallback();
        try
        {
            int slot = (int)(nint)srcConnRefCon;
            if ((uint)slot < MaxPortSlots && port._sources[slot] is { Connected: true } source && !port._disposing)
                port.WalkPacketList(packetList, source, arrival);
        }
        catch
        {
            // Never let an exception cross the native boundary.
        }
        finally
        {
            CoreMidi_MidiInput.ExitCallback();
            Interlocked.Decrement(ref port._callbacksInFlight);
        }
    }

    /// <summary>MIDIPacketList walk per the header macros (D38): variable-length packets, ARM rounds to 4, timestamp unaligned.</summary>
    private void WalkPacketList(byte* list, CoreMidi_MidiInSource source, long arrival)
    {
        uint numPackets = Unsafe.ReadUnaligned<uint>(list);
        byte* packet = list + (int)CoreMidi_Layout.PacketListFirstPacketOffset;
        for (uint i = 0; i < numPackets; i++)
        {
            ulong timeStamp = Unsafe.ReadUnaligned<ulong>(packet + (int)CoreMidi_Layout.PacketTimeStampOffset);
            ushort length = Unsafe.ReadUnaligned<ushort>(packet + (int)CoreMidi_Layout.PacketLengthOffset);
            byte* data = packet + (int)CoreMidi_Layout.PacketDataOffset;

            long driverTicks = ConvertDriverTimestamp(timeStamp, arrival);
            ParsePacketBytes(new ReadOnlySpan<byte>(data, length), source, arrival, driverTicks);

            packet = CoreMidi_Interop.PacketNext(packet, length);
        }
    }

    /// <summary>
    /// One packet holds either several complete non-SysEx messages back to back (no running status) or SysEx bytes (a whole message or a
    /// fragment). System Real-Time bytes may be interleaved anywhere. Data bytes with no status in hand are dropped.
    /// </summary>
    private void ParsePacketBytes(ReadOnlySpan<byte> bytes, CoreMidi_MidiInSource source, long arrival, long driverTicks)
    {
        int i = 0;
        while (i < bytes.Length)
        {
            byte b = bytes[i];

            if (b >= (byte)Midi_Status.TimingClock)   // F8..FF: 1-byte real-time, may sit inside a SysEx stream
            {
                Deliver(source, arrival, driverTicks, b, 0, 0);
                i++;
                continue;
            }

            if (b == (byte)Midi_Status.SysExStart || b == (byte)Midi_Status.SysExEnd || (b < 0x80 && source.Reassembler.InMessage))
            {
                // Run of SysEx bytes: F0 and/or data bytes, terminated by F7 (inclusive) or by the packet end or by a real-time byte.
                int start = i;
                if (bytes[i] == (byte)Midi_Status.SysExStart) i++;
                while (i < bytes.Length && bytes[i] < 0x80) i++;
                if (i < bytes.Length && bytes[i] == (byte)Midi_Status.SysExEnd) i++;
                if (source.Reassembler.Append(bytes[start..i], out var complete))
                    _owner.QueueSysExFromDriver(source.Index, arrival, complete);
                continue;
            }

            if (b < 0x80) { i++; continue; }   // orphan data byte: nothing to attach it to

            int len = Midi_Wire.ShortMessageLength(b);
            if (i + len > bytes.Length) break;   // truncated packet: drop the tail
            Deliver(source, arrival, driverTicks, b, len > 1 ? bytes[i + 1] : (byte)0, len > 2 ? bytes[i + 2] : (byte)0);
            i += len;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Deliver(CoreMidi_MidiInSource source, long arrival, long driverTicks, byte status, byte d1, byte d2)
    {
        // D25: MIDI 1.0 bytes → UMP MT 0x1/0x2 word, group 0 (one source per slot).
        var message = MidiInput_Message.FromMidi1(arrival, driverTicks, status, d1, d2, source.Index);
        _owner.DeliverFromDriver(in message);
    }

    // ── timestamps (D36) ─────────────────────────────────────────────────────────────────────────

    private long _driverAnchorTicks = long.MaxValue;

    /// <summary>MIDITimeStamp is mach_absolute_time; Stopwatch on macOS is the same clock in nanoseconds (D36, measured). When the client
    /// proved that at start-up the conversion is an exact scale. Fallback (unproven clocks): scale, then anchor by the minimum observed
    /// delivery latency (D6) so DriverTimestamp still lands in the ArrivalTicks base.</summary>
    private long ConvertDriverTimestamp(ulong machTimeStamp, long arrivalTicks)
    {
        if (machTimeStamp == 0) return arrivalTicks;   // "unknown" per header
        var client = CoreMidi_Client.Instance;
        long scaled = client.MachToStopwatchTicks(machTimeStamp);
        if (client.MachClockIsStopwatchClock) return scaled;

        long candidate = arrivalTicks - scaled;
        if (candidate < _driverAnchorTicks) _driverAnchorTicks = candidate;
        return _driverAnchorTicks + scaled;
    }
}

#pragma warning restore AN0100