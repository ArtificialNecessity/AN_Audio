using System.Diagnostics;
using System.Runtime.InteropServices;
using AN.Audio.Midi.Internal;

namespace AN.Audio.Midi.Platforms.Linux;

#pragma warning disable AN0100 // raw fds

/// <summary>
/// One open ALSA rawmidi port (SPEC-30 §11, Linux). Owns the fd, a reader thread, and the byte-stream parser + SysEx
/// reassembler. Rawmidi delivers raw MIDI 1.0 bytes with no driver timestamp, so DriverTimestamp == ArrivalTicks
/// (D39: delivery lag reads as 0; the poll+read wakeup is the true floor).
/// </summary>
internal sealed class Alsa_MidiInPort : IMidi_ByteStreamSink
{
    private const int ReadChunkBytes = 512;
    private const int PollTimeoutMs = 100;   // Close() latency bound
    private const int EINTR = 4;
    private const int EBUSY = 16;

    private readonly Alsa_MidiInput _owner;
    private readonly Midi_SysExReassembler _reassembler;
    private readonly Midi_ByteStreamParser _parser;
    private readonly object _writeLock = new();
    private int _fd = -1;
    private Thread? _reader;
    private volatile bool _closing;
    private long _currentArrival;   // reader thread: stamp of the read() that produced the bytes being parsed

    public MidiInput_PortIndex Index { get; }
    public Alsa_MidiInDeviceEntry Entry { get; }
    public MidiInput_DeviceInfo Info { get; set; }
    public bool IsOpen => _fd >= 0;
    /// <summary>fd was opened R/W (node has an output substream); Identity Requests can be written.</summary>
    public bool CanWrite { get; private set; }
    public long SysExDiscardedCount => _reassembler.DiscardedCount;

    public Alsa_MidiInPort(Alsa_MidiInput owner, MidiInput_Options options, MidiInput_PortIndex index, Alsa_MidiInDeviceEntry entry)
    {
        _owner = owner;
        Index = index;
        Entry = entry;
        Info = entry.ToDeviceInfo();
        _reassembler = new Midi_SysExReassembler(options.SysExBufferBytes * 2, options.SysExMaxBytes);
        _parser = new Midi_ByteStreamParser(this, _reassembler, options.SysExBufferBytes);
    }

    /// <summary>Select the substream (D44), open non-blocking (R/W when a matching output substream exists, else read-only) and
    /// start the reader thread. Returns false with a reason on failure. Never throws.</summary>
    public bool Open(out MidiInput_LostReason reason)
    {
        reason = MidiInput_LostReason.DriverError;
        int fd, errno;
        lock (s_preferSubdeviceLock)   // prefer-subdevice + open must not interleave with another of our ports on the same card
        {
            fd = -1;
            if (Entry.HasPairedOutput && PreferSubdevice())
                fd = Alsa_Interop.Open(Entry.DevicePath, Alsa_Interop.O_RDWR | Alsa_Interop.O_NONBLOCK);
            CanWrite = fd >= 0;
            if (fd < 0 && PreferSubdevice())
                fd = Alsa_Interop.Open(Entry.DevicePath, Alsa_Interop.O_RDONLY | Alsa_Interop.O_NONBLOCK);
            errno = Marshal.GetLastPInvokeError();
        }

        if (fd < 0)
        {
            reason = errno == EBUSY ? MidiInput_LostReason.InUseByAnotherApplication : MidiInput_LostReason.DriverError;
            return false;
        }

        _fd = fd;
        _closing = false;
        _reader = new Thread(ReadLoop) { Name = $"AN.Audio.Midi ALSA reader C{Entry.Card}D{Entry.Device}", IsBackground = true };
        _reader.Start();
        return true;
    }

    private static readonly object s_preferSubdeviceLock = new();

    /// <summary>D44: tell the card which rawmidi substream the NEXT open() on this process should get. The kernel default is
    /// "first free", which for a multi-cable device would hand cable 1 to whichever of our ports opened first — so we always
    /// set it explicitly, including for substream 0. Returns false only if the control device cannot be opened.</summary>
    private unsafe bool PreferSubdevice()
    {
        int ctl = Alsa_Interop.Open(Entry.ControlPath, Alsa_Interop.O_RDWR);
        if (ctl < 0) return Entry.Subdevice == 0;   // no control access: substream 0 still works by default; others cannot be selected
        try
        {
            int sub = Entry.Subdevice;
            return Alsa_Interop.Ioctl(ctl, Alsa_Interop.SNDRV_CTL_IOCTL_RAWMIDI_PREFER_SUBDEVICE, &sub) == 0;
        }
        finally { Alsa_Interop.Close(ctl); }
    }

    /// <summary>Signal the reader, join it (bounded by the poll timeout), close the fd. Idempotent; worker thread only.</summary>
    public void Close()
    {
        if (_fd < 0) return;
        _closing = true;
        _reader?.Join();
        _reader = null;
        lock (_writeLock)
        {
            Alsa_Interop.Close(_fd);
            _fd = -1;
        }
        _parser.Reset();
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
            int pr = Alsa_Interop.Poll(&pfd, 1, PollTimeoutMs);
            if (_closing) break;
            if (pr < 0)
            {
                if (Marshal.GetLastPInvokeError() == EINTR) continue;
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
                if (Marshal.GetLastPInvokeError() == Alsa_Interop.EAGAIN) continue;
                _owner.NotePortClosedByDriver(Index);
                return;
            }
            if (n == 0) continue;

            _currentArrival = arrival;
            Alsa_MidiInput.EnterCallback();
            try { _parser.Feed(buffer.AsSpan(0, (int)n)); }
            catch { /* never take down the reader thread */ }
            finally { Alsa_MidiInput.ExitCallback(); }
        }
    }

    // ── IMidi_ByteStreamSink (reader thread) ────────────────────────────────────────────────────────────────────

    void IMidi_ByteStreamSink.OnShortMessage(byte status, byte data1, byte data2)
    {
        // D39: no driver timestamp on rawmidi — DriverTimestamp is the arrival stamp itself.
        var message = MidiInput_Message.FromMidi1(_currentArrival, _currentArrival, status, data1, data2, Index);
        _owner.DeliverFromDriver(in message);
    }

    void IMidi_ByteStreamSink.OnSysEx(ReadOnlySpan<byte> complete) => _owner.QueueSysExFromDriver(Index, _currentArrival, complete);
}

#pragma warning restore AN0100