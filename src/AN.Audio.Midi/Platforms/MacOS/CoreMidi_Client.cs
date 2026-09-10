using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AN.Audio.Midi.Platforms.MacOS;

#pragma warning disable AN0100

/// <summary>
/// Process-wide CoreMIDI client (SPEC-30 D35). Owns ONE dedicated thread that calls MIDIClientCreate and then runs a CFRunLoop,
/// because CoreMIDI delivers setup notifications only to the run loop of the thread that created the client (header text), and a
/// .NET process has no run loop of its own. Ports are created per IMidiInput instance against this client.
/// Also establishes the timestamp relationship (D36): MIDITimeStamp and Stopwatch are both mach_absolute_time on macOS.
/// </summary>
internal sealed unsafe class CoreMidi_Client
{
    private static readonly Lazy<CoreMidi_Client> s_instance = new(() => new CoreMidi_Client());
    public static CoreMidi_Client Instance => s_instance.Value;

    private readonly object _lock = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private Thread? _runLoopThread;
    private nint _runLoop;
    private GCHandle _selfHandle;
    private Action? _setupChanged;

    public CoreMidi_ClientRef Client { get; private set; }
    public CoreMidi_Status CreateStatus { get; private set; }

    /// <summary>
    /// D36 (as measured 2026-09-10): Stopwatch on macOS is the SAME clock as mach_absolute_time but in nanoseconds
    /// (CoreCLR PAL: clock_gettime_nsec_np(CLOCK_UPTIME_RAW) = mach_absolute_time × numer/denom). True when that was proven at start-up
    /// (scaled values agree within 1 ms), so <see cref="MachToStopwatchTicks"/> is an exact conversion and no anchor is needed.
    /// </summary>
    public bool MachClockIsStopwatchClock { get; private set; }
    private uint _timebaseNumer = 1, _timebaseDenom = 1;

    /// <summary>Convert a MIDITimeStamp (mach ticks) into Stopwatch ticks. Exact integer math when the clocks are the same; otherwise a best-effort scale.</summary>
    public long MachToStopwatchTicks(ulong machTicks)
    {
        // mach ticks × numer / denom. numer/denom are small (125/3 on Apple silicon, 1/1 on x86_64); ulong headroom is ample for uptime-scale values.
        return (long)(machTicks * _timebaseNumer / _timebaseDenom);
    }

    /// <summary>Raised on the run-loop thread for ANY CoreMIDI setup notification (D34). Handlers must be quick: set a wake event and return.</summary>
    public event Action SetupChanged
    {
        add { lock (_lock) _setupChanged += value; }
        remove { lock (_lock) _setupChanged -= value; }
    }

    private CoreMidi_Client()
    {
        MeasureTimebase();
        _selfHandle = GCHandle.Alloc(this);
        _runLoopThread = new Thread(RunLoopThread) { Name = "AN.Audio.Midi CoreMIDI client run loop", IsBackground = true };
        _runLoopThread.Start();
        _ready.Wait();
    }

    private void RunLoopThread()
    {
        nint name = CoreFoundation_Interop.CreateCFString("AN.Audio.Midi");
        try
        {
            uint client = 0;
            var notify = (delegate* unmanaged[Cdecl]<CoreMidi_Notification*, void*, void>)&NotifyProc;
            CreateStatus = CoreMidi_Interop.MIDIClientCreate(name, (nint)notify, (void*)GCHandle.ToIntPtr(_selfHandle), &client);
            Client = new CoreMidi_ClientRef(CreateStatus == CoreMidi_Status.NoError ? client : 0);
        }
        finally
        {
            CoreFoundation_Interop.CFRelease(name);
        }

        _runLoop = CoreFoundation_Interop.CFRunLoopGetCurrent();
        _ready.Set();
        if (Client.IsNull) return;

        // Blocks for the life of the process (background thread). CoreMIDI's notification source keeps the loop alive.
        CoreFoundation_Interop.CFRunLoopRun();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void NotifyProc(CoreMidi_Notification* message, void* refCon)
    {
        try
        {
            if (GCHandle.FromIntPtr((nint)refCon).Target is not CoreMidi_Client self) return;
            switch (message->MessageId)
            {
                case CoreMidi_NotificationId.SetupChanged:
                case CoreMidi_NotificationId.ObjectAdded:
                case CoreMidi_NotificationId.ObjectRemoved:
                case CoreMidi_NotificationId.IOError:
                    self.RaiseSetupChanged();
                    break;
                case CoreMidi_NotificationId.PropertyChanged:
                {
                    // Only kMIDIPropertyOffline flips matter for presence; anything else (name edits) is noise for us.
                    var pc = (CoreMidi_PropertyChangeNotification*)message;
                    if (pc->PropertyName == CoreMidi_PropertyKeys.Offline) self.RaiseSetupChanged();
                    break;
                }
                default:
                    break;
            }
        }
        catch
        {
            // never let an exception cross the native boundary
        }
    }

    private void RaiseSetupChanged()
    {
        Action? handler;
        lock (_lock) handler = _setupChanged;
        if (handler is null) return;
        try { handler(); } catch { }
    }

    private void MeasureTimebase()
    {
        Mach_Interop.TimebaseInfo tb = default;
        if (Mach_Interop.mach_timebase_info(&tb) != 0 || tb.Denom == 0 || tb.Numer == 0) { MachClockIsStopwatchClock = false; return; }
        _timebaseNumer = tb.Numer;
        _timebaseDenom = tb.Denom;

        // Measured 2026-09-10 (Apple silicon, .NET 10): Stopwatch.Frequency == 1e9 and Stopwatch == mach × numer/denom to the nanosecond.
        // Prove it at start-up rather than assume (D36): if the scaled clocks disagree by more than 1 ms, the port falls back to the D6 anchor.
        if (Stopwatch.Frequency != 1_000_000_000) { MachClockIsStopwatchClock = false; return; }
        long sw = Stopwatch.GetTimestamp();
        long machAsSw = MachToStopwatchTicks(Mach_Interop.mach_absolute_time());
        double diffMs = Math.Abs(machAsSw - sw) / 1e6;
        MachClockIsStopwatchClock = diffMs < 1.0;
    }
}

#pragma warning restore AN0100