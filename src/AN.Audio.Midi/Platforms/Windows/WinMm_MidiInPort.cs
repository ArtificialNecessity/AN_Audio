using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AN.Audio.Midi.Internal;

namespace AN.Audio.Midi.Platforms.Windows;

#pragma warning disable AN0100 // Win32 handles / DWORD_PTR

/// <summary>
/// One open WinMM input port (SPEC-30 §5). Owns the handle, the pinned SysEx headers/buffers, the reassembler,
/// and the [UnmanagedCallersOnly] driver callback. The callback is the hot path: stamp, unpack, hand off.
/// </summary>
internal sealed unsafe class WinMm_MidiInPort
{
    private readonly WinMm_MidiInput _owner;
    private readonly MidiInput_Options _options;
    private readonly Midi_SysExReassembler _reassembler;
    private GCHandle _selfHandle;
    private nint _handle;
    private WinMm_MidiHdr** _sysExHeaders;   // array of pointers to pinned headers
    private int _sysExHeaderCount;
    private volatile bool _closing;
    private int _callbacksInFlight;

    public MidiInput_PortIndex Index { get; }
    public WinMm_MidiInDeviceEntry Entry { get; }
    public MidiInput_DeviceInfo Info { get; set; }
    public bool IsOpen => _handle != 0;
    public long SysExDiscardedCount => _reassembler.DiscardedCount;

    public WinMm_MidiInPort(WinMm_MidiInput owner, MidiInput_Options options, MidiInput_PortIndex index, WinMm_MidiInDeviceEntry entry)
    {
        _owner = owner;
        _options = options;
        Index = index;
        Entry = entry;
        Info = entry.ToDeviceInfo();
        _reassembler = new Midi_SysExReassembler(options.SysExBufferBytes * 2, options.SysExMaxBytes);
    }

    /// <summary>Open, post SysEx buffers, start. Returns the failing MMRESULT (NoError on success). Never throws.</summary>
    public WinMm_Result Open()
    {
        _selfHandle = GCHandle.Alloc(this);
        var flags = WinMm_OpenFlags.CallbackFunction;
        if (_options.EnableIoStatus) flags |= WinMm_OpenFlags.MidiIoStatus;

        var callback = (delegate* unmanaged[Stdcall]<nint, uint, nuint, nuint, nuint, void>)&MidiInProc;
        nint h = 0;
        var r = WinMm_MidiInterop.midiInOpen(&h, Entry.Index, (nuint)callback, (nuint)GCHandle.ToIntPtr(_selfHandle), flags);
        if (r != WinMm_Result.NoError) { _selfHandle.Free(); return r; }
        _handle = h;

        // SysEx buffers: allocate, prepare, add — all before midiInStart (ground truth §SysEx buffer discipline).
        _sysExHeaderCount = _options.SysExBuffersPerPort;
        _sysExHeaders = (WinMm_MidiHdr**)NativeMemory.AllocZeroed((nuint)_sysExHeaderCount, (nuint)sizeof(WinMm_MidiHdr*));
        for (int i = 0; i < _sysExHeaderCount; i++)
        {
            var hdr = (WinMm_MidiHdr*)NativeMemory.AllocZeroed(WinMm_MidiInterop.MidiHdrSize);
            hdr->lpData = (byte*)NativeMemory.Alloc((nuint)_options.SysExBufferBytes);
            hdr->dwBufferLength = (uint)_options.SysExBufferBytes;
            _sysExHeaders[i] = hdr;

            r = WinMm_MidiInterop.midiInPrepareHeader(_handle, hdr, WinMm_MidiInterop.MidiHdrSize);
            if (r != WinMm_Result.NoError) { Close(); return r; }
            r = WinMm_MidiInterop.midiInAddBuffer(_handle, hdr, WinMm_MidiInterop.MidiHdrSize);
            if (r != WinMm_Result.NoError) { Close(); return r; }
        }

        ResetDriverAnchor();   // dwParam2 counts from THIS midiInStart
        r = WinMm_MidiInterop.midiInStart(_handle);
        if (r != WinMm_Result.NoError) { Close(); return r; }
        return WinMm_Result.NoError;
    }

    /// <summary>Stop, reset (returns buffers), wait for callbacks to drain, unprepare, free, close. Idempotent.</summary>
    public void Close()
    {
        if (_handle == 0) return;
        _closing = true;

        WinMm_MidiInterop.midiInStop(_handle);
        WinMm_MidiInterop.midiInReset(_handle);   // every queued header comes back via MIM_LONGDATA with 0 bytes

        var spin = new SpinWait();
        while (Volatile.Read(ref _callbacksInFlight) != 0) spin.SpinOnce();

        if (_sysExHeaders != null)
        {
            for (int i = 0; i < _sysExHeaderCount; i++)
            {
                var hdr = _sysExHeaders[i];
                if (hdr == null) continue;
                if ((hdr->dwFlags & WinMm_HdrFlags.Prepared) != 0)
                    WinMm_MidiInterop.midiInUnprepareHeader(_handle, hdr, WinMm_MidiInterop.MidiHdrSize);
                if (hdr->lpData != null) NativeMemory.Free(hdr->lpData);
                NativeMemory.Free(hdr);
            }
            NativeMemory.Free(_sysExHeaders);
            _sysExHeaders = null;
        }

        WinMm_MidiInterop.midiInClose(_handle);
        _handle = 0;
        _reassembler.Reset();
        if (_selfHandle.IsAllocated) _selfHandle.Free();
    }

    // ── driver thread ─────────────────────────────────────────────────────────────────────────

    private static readonly long s_ticksPerMs = Stopwatch.Frequency / 1000;
    /// <summary>Stopwatch tick that corresponds to the driver's dwParam2 == 0 (midiInStart). Self-calibrating: delivery latency is
    /// never negative, so the smallest observed (arrival - driverMs) is the best estimate of the true origin and only ever moves down.</summary>
    private long _driverAnchorTicks = long.MaxValue;

    /// <summary>Convert the driver's integer-ms-since-Start stamp into the ArrivalTicks time base (SPEC-30 D6 amended).
    /// Driver thread only. Resolution is the driver's 1 ms; the anchor converges from above within a few fast deliveries.</summary>
    private long ConvertDriverTimestamp(uint driverMs, long arrivalTicks)
    {
        long driverRelativeTicks = driverMs * s_ticksPerMs;
        long candidate = arrivalTicks - driverRelativeTicks;
        if (candidate < _driverAnchorTicks) _driverAnchorTicks = candidate;
        return _driverAnchorTicks + driverRelativeTicks;
    }

    /// <summary>Forget the anchor (new midiInStart = new driver origin).</summary>
    private void ResetDriverAnchor() => _driverAnchorTicks = long.MaxValue;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void MidiInProc(nint hmi, uint uMsg, nuint dwInstance, nuint dwParam1, nuint dwParam2)
    {
        long arrival = Stopwatch.GetTimestamp();
        if (GCHandle.FromIntPtr((nint)dwInstance).Target is not WinMm_MidiInPort port) return;

        Interlocked.Increment(ref port._callbacksInFlight);
        WinMm_MidiInput.EnterCallback();
        try
        {
            port.HandleMessage((WinMm_MidiInMessage)uMsg, dwParam1, dwParam2, arrival);
        }
        catch
        {
            // Never let an exception cross the native boundary (it would tear down the process).
        }
        finally
        {
            WinMm_MidiInput.ExitCallback();
            Interlocked.Decrement(ref port._callbacksInFlight);
        }
    }

    private void HandleMessage(WinMm_MidiInMessage msg, nuint dwParam1, nuint dwParam2, long arrival)
    {
        switch (msg)
        {
            case WinMm_MidiInMessage.Data:
            case WinMm_MidiInMessage.MoreData:
            {
                WinMm_MidiInterop.UnpackShortMessage(dwParam1, out byte status, out byte d1, out byte d2);
                // D6 (amended): dwParam2 is integer ms since midiInStart on the driver's clock. Convert it into the
                // ArrivalTicks time base so ArrivalTicks - DriverTimestamp reads as the WinMM delivery latency.
                long driverTicks = ConvertDriverTimestamp((uint)dwParam2, arrival);
                // D25: WinMM speaks MIDI 1.0 only; wrap as a UMP MT 0x1/0x2 word (shift+or, allocation-free). Group is always 0 (one cable per port).
                var message = MidiInput_Message.FromMidi1(arrival, driverTicks, status, d1, d2, Index);
                if (msg == WinMm_MidiInMessage.MoreData) _owner.NoteDriverLag();
                _owner.DeliverFromDriver(in message);
                break;
            }

            case WinMm_MidiInMessage.LongData:
            {
                var hdr = (WinMm_MidiHdr*)dwParam1;
                uint recorded = hdr->dwBytesRecorded;
                if (recorded == 0) return;   // returned by Reset/Close — do NOT re-add

                var fragment = new ReadOnlySpan<byte>(hdr->lpData, (int)recorded);
                if (_reassembler.Append(fragment, out var complete))
                    _owner.QueueSysExFromDriver(Index, arrival, complete);

                if (!_closing) WinMm_MidiInterop.midiInAddBuffer(_handle, hdr, WinMm_MidiInterop.MidiHdrSize);
                break;
            }

            case WinMm_MidiInMessage.LongError:
            {
                _reassembler.Reset();
                var hdr = (WinMm_MidiHdr*)dwParam1;
                if (!_closing && hdr->dwBytesRecorded != 0) WinMm_MidiInterop.midiInAddBuffer(_handle, hdr, WinMm_MidiInterop.MidiHdrSize);
                break;
            }

            case WinMm_MidiInMessage.Close:
                if (!_closing) _owner.NotePortClosedByDriver(Index);
                break;

            case WinMm_MidiInMessage.Open:
            case WinMm_MidiInMessage.Error:
            default:
                break;
        }
    }
}

#pragma warning restore AN0100