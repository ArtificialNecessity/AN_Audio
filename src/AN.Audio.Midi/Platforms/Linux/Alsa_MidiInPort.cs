using System.Diagnostics;
using AN.Audio.Midi.Internal;

namespace AN.Audio.Midi.Platforms.Linux;

#pragma warning disable AN0100 // raw fds

/// <summary>
/// One open ALSA rawmidi port (SPEC-30 §5, Linux). Owns the fd, a reader thread, the running-status byte-stream
/// parser and the SysEx reassembler. Rawmidi delivers raw MIDI 1.0 bytes with no driver timestamp, so
/// DriverTimestamp == ArrivalTicks (delivery lag reads as 0; the poll+read wakeup is the true floor).
/// </summary>
internal sealed class Alsa_MidiInPort
{
    private const int ReadChunkBytes = 512;

    private readonly Alsa_MidiInput _owner;
    private readonly MidiInput_Options _options;
    private readonly Midi_SysExReassembler _reassembler;
    private readonly object _writeLock = new();
    private int _fd = -1;
    private Thread? _reader;
    private volatile bool _closing;

    // ── parser state (reader thread only) ─────────────────────────────────────────────────────────────────
    private byte _runningStatus;      // 0 = none
    private byte _data1;
    private int _dataCount;           // data bytes gathered for the pending status
    private bool _inSysEx;
    private readonly byte[] _sysExStage = new byte[256];   // flushed to the reassembler when full or on F7
    private int _sysExStageLength;

    public MidiInput_PortIndex Index { get; }
    public Alsa_MidiInDeviceEntry Entry { get; }
    public MidiInput_DeviceInfo Info { get; set; }
    public bool IsOpen => _fd >= 0;
    public bool CanWrite { get; private set; }
    public long SysExDiscardedCount => _reassembler.DiscardedCount;

    public Alsa_MidiInPort(Alsa_MidiInput owner, MidiInput_Options options, MidiInput_PortIndex index, Alsa_MidiInDeviceEntry entry)
    {
        _owner = owner;
        _options = options;
        Index = index;
        Entry = entry;
        Info = entry.ToDeviceInfo();
        _reassembler = new Midi_SysExReassembler(options.SysExBufferBytes * 2, options.SysExMaxBytes);
    }

    /// <summary>Open non-blocking (R/W when the node has an output substream, for Identity Requests) and start
    /// the reader thread. Returns false with a reason on failure. Never throws.</summary>
    public bool Open(out MidiInput_LostReason reason)
    {
        reason = MidiInput_LostReason.DriverError;
        int fd = -1;
        if (Entry.HasPairedOutput)
            fd = Alsa_Interop.Open(Entry.DevicePath, Alsa_Interop.O_RDWR | Alsa_Interop.O_NONBLOCK);
        if (fd < 0)
        {
            fd = Alsa_Interop.Open(Entry.DevicePath, Alsa_Interop.O_RDONLY | Alsa_Interop.O_NONBLOCK);
            CanWrite = false;
        }
        else CanWrite = true;

        if (fd < 0)
        {
            int errno = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            reason = errno == 16 /* EBUSY */ ? MidiInput_LostReason.InUseByAnotherApplication : MidiInput_LostReason.DriverError;
            return false;
        }

        _fd = fd;
        _closing = false;
        _reader = new Thread(ReadLoop) { Name = $"AN.Audio.Midi ALSA reader C{Entry.Card}D{Entry.Device}", IsBackground = true };
        _reader.Start();
        return true;
    }

    /// <summary>Signal the reader, join it, close the fd. Idempotent; safe from the worker thread.</summary>
    public void Close()
    {
        if (_fd < 0) return;
        _closing = true;
        _reader?.Join();   // reader wakes within its poll timeout (100 ms)
        _reader = null;
        lock (_writeLock)
        {
            Alsa_Interop.Close(_fd);
            _fd = -1;
        }
        _reassembler.Reset();
        ResetParser();
    }

    /// <summary>Best-effort write (Identity Request, D8/D9). Silent on failure. Worker thread only.</summary>
    public unsafe bool TryWrite(ReadOnlySpan<byte> bytes)
    {
        lock (_writeLock)
        {
            if (_fd < 0 || !CanWrite) return false;
            fixed (byte* p = bytes)
                return Alsa_Interop.Write(_fd, p, (nuint)bytes.Length) == bytes.Length;
        }
    }

    // ── reader thread ──────────────────────────────────────────────────────────────────────────────────────

    private unsafe void ReadLoop()
    {
        var buffer = new byte[ReadChunkBytes];
        while (!_closing)
        {
            var pfd = new Alsa_Interop.PollFd { Fd = _fd, Events = Alsa_Interop.POLLIN };
            int pr = Alsa_Interop.Poll(&pfd, 1, 100);
            if (_closing) break;
            if (pr < 0)
            {
                if (System.Runtime.InteropServices.Marshal.GetLastPInvokeError() == 4 /* EINTR */) continue;
                _owner.NotePortClosedByDriver(Index);
                return;
            }
            if (pr == 0) continue;
            if ((pfd.Revents & (Alsa_Interop.POLLERR | Alsa_Interop.POLLHUP)) != 0)
            {
                _owner.NotePortClosedByDriver(Index);   // unplugged
                return;
            }

            nint n;
            fixed (byte* p = buffer)
                n = Alsa_Interop.Read(_fd, p, ReadChunkBytes);
            long arrival = Stopwatch.GetTimestamp();
            if (n < 0)
            {
                if (System.Runtime.InteropServices.Marshal.GetLastPInvokeError() == Alsa_Interop.EAGAIN) continue;
                _owner.NotePortClosedByDriver(Index);
                return;
            }
            if (n == 0) continue;

            Alsa_MidiInput.EnterCallback();
            try { Parse(buffer.AsSpan(0, (int)n), arrival); }
            catch { /* never take down the reader thread */ }
            finally { Alsa_MidiInput.ExitCallback(); }
        }
    }

    // ── MIDI 1.0 byte-stream parser (running status, interleaved realtime, SysEx) ─────────────────────────

    private void ResetParser()
    {
        _runningStatus = 0;
        _dataCount = 0;
        _inSysEx = false;
        _sysExStageLength = 0;
    }

    private void Parse(ReadOnlySpan<byte> bytes, long arrival)
    {
        foreach (byte b in bytes)
        {
            if (b >= (byte)Midi_Status.TimingClock)   // F8..FF: realtime, legal anywhere (even mid-SysEx)
            {
                Deliver(arrival, b, 0, 0);
                continue;
            }

            if (_inSysEx)
            {
                if (b < 0x80) { StageSysExByte(b); continue; }
                if (b == (byte)Midi_Status.SysExEnd)
                {
                    StageSysExByte(b);
                    FlushSysExStage(arrival, complete: true);
                    _inSysEx = false;
                    continue;
                }
                // Any other status aborts the SysEx (device unplugged mid-message, etc.); fall through to handle it.
                _inSysEx = false;
                _sysExStageLength = 0;
                _reassembler.Reset();
            }

            if (b >= 0x80)   // new status
            {
                if (b == (byte)Midi_Status.SysExStart)
                {
                    _runningStatus = 0;
                    _inSysEx = true;
                    _sysExStageLength = 0;
                    StageSysExByte(b);
                    continue;
                }
                if (b >= 0xF0)   // system common F1..F7: cancels running status
                {
                    _runningStatus = 0;
                    int need = SystemCommonDataBytes(b);
                    if (need == 0) { Deliver(arrival, b, 0, 0); }
                    else { _pendingSystemCommon = b; _pendingSystemNeed = need; _dataCount = 0; }
                    continue;
                }
                _runningStatus = b;
                _pendingSystemCommon = 0;
                _dataCount = 0;
                continue;
            }

            // data byte
            if (_pendingSystemCommon != 0)
            {
                if (_pendingSystemNeed == 1) { Deliver(arrival, _pendingSystemCommon, b, 0); _pendingSystemCommon = 0; }
                else if (_dataCount == 0) { _data1 = b; _dataCount = 1; }
                else { Deliver(arrival, _pendingSystemCommon, _data1, b); _pendingSystemCommon = 0; _dataCount = 0; }
                continue;
            }
            if (_runningStatus == 0) continue;   // stray data byte (joined a stream mid-message)

            int dataNeeded = ChannelDataBytes(_runningStatus);
            if (dataNeeded == 1) { Deliver(arrival, _runningStatus, b, 0); }
            else if (_dataCount == 0) { _data1 = b; _dataCount = 1; }
            else { Deliver(arrival, _runningStatus, _data1, b); _dataCount = 0; }   // running status persists
        }
    }

    private byte _pendingSystemCommon;
    private int _pendingSystemNeed;

    private static int ChannelDataBytes(byte status) => (status & 0xF0) is 0xC0 or 0xD0 ? 1 : 2;

    private static int SystemCommonDataBytes(byte status) => status switch
    {
        0xF1 or 0xF3 => 1,   // MTC quarter frame, song select
        0xF2 => 2,           // song position pointer
        _ => 0,              // F6 tune request, F4/F5 undefined, F7 stray end
    };

    private void Deliver(long arrival, byte status, byte d1, byte d2)
    {
        var message = MidiInput_Message.FromMidi1(arrival, arrival, status, d1, d2, Index);
        _owner.DeliverFromDriver(in message);
    }

    private void StageSysExByte(byte b)
    {
        if (_sysExStageLength == _sysExStage.Length) FlushSysExStage(0, complete: false);
        _sysExStage[_sysExStageLength++] = b;
    }

    private void FlushSysExStage(long arrival, bool complete)
    {
        if (_sysExStageLength == 0) return;
        if (_reassembler.Append(_sysExStage.AsSpan(0, _sysExStageLength), out var whole) && complete)
            _owner.QueueSysExFromDriver(Index, arrival, whole);
        _sysExStageLength = 0;
    }
}

#pragma warning restore AN0100