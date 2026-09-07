using System.Diagnostics;
using AN.Audio.Midi;

// SimpleMidiTest — manual hardware smoke test for SPEC-30 Sprint 1.
//   1. lists attached MIDI input ports
//   2. opens them all and prints every message (both timestamps)
//   3. prints hot-plug (opened/lost), identity replies, SysEx, overflow
//   Keys:  i = re-send Identity Request to all ports   s = stats   q / Enter = quit

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (!MidiInput.IsAvailable)
{
    Console.WriteLine("MIDI input is not available on this platform.");
    return 1;
}

var manager = MidiInput.GetDeviceManager()!;
Console.WriteLine("=== MIDI input ports present ===");
var devices = manager.GetInputDevices();
if (devices.Count == 0) Console.WriteLine("  (none — plug something in; hot-plug is polled every second)");
foreach (var d in devices)
    Console.WriteLine($"  {d.Name}\n      key   = {d.Key}\n      type  = {d.TypeId}\n      out?  = {d.HasPairedOutput}");

manager.DeviceListChanged += (change, info) =>
    Log(ConsoleColor.Magenta, $"[manager] {change}: {info?.Name}");

using var midi = MidiInput.Create(new MidiInput_Options
{
    RingInitialCapacity = 256,
    RingMaxCapacity = 4096,
});

midi.DeviceOpened += info => Log(ConsoleColor.Green, $"[opened] {info}");
midi.DeviceLost += (info, reason) => Log(ConsoleColor.Red, $"[lost:{reason}] {info.Name}");
midi.IdentityResolved += (info, id) => Log(ConsoleColor.Cyan, $"[identity] {info.Name}: {id}  → type={info.TypeId}");
midi.SysExReceived += sx => Log(ConsoleColor.DarkYellow, $"[sysex] port {sx.Port.Value} {sx.Bytes.Length} bytes: {Hex(sx.Bytes.Span, 24)}");
midi.Overflow += (port, dropped) => Log(ConsoleColor.Red, $"[OVERFLOW] port {port.Value} dropped total={dropped}");

midi.Start();
Console.WriteLine();
Console.WriteLine($"Listening on {midi.OpenPorts.Count} port(s). Play something. i=identity, s=stats, q=quit");
Console.WriteLine();

// Consumer thread: stands in for an audio callback draining the ring every few ms.
var stop = new CancellationTokenSource();
long messagesSeen = 0;
var startTicks = Stopwatch.GetTimestamp();
var drainThread = new Thread(() =>
{
    Span<MidiInput_Message> batch = stackalloc MidiInput_Message[64];
    while (!stop.IsCancellationRequested)
    {
        int n = midi.Ring.DequeueAll(batch);
        for (int i = 0; i < n; i++)
        {
            ref readonly var m = ref batch[i];
            messagesSeen++;
            double sinceStartMs = (m.ArrivalTicks - startTicks) * 1000.0 / Stopwatch.Frequency;
            string portName = midi.TryGetPort(m.Port, out var info) ? info.Name : $"port{m.Port.Value}";
            var colour = m.IsNoteOn ? ConsoleColor.White : m.IsNoteOff ? ConsoleColor.Gray : ConsoleColor.DarkCyan;
            var (st, d1, d2) = m.RawMidi1Bytes;   // diagnostics only (D25) — musical code uses typed accessors
            Log(colour, $"{sinceStartMs,10:F3}ms  drv={m.DriverTimestamp,8}ms  [{portName}]  {st:X2} {d1:X2} {d2:X2}  {m}");
        }
        if (n == 0) Thread.Sleep(2);
    }
}) { IsBackground = true, Name = "drain" };
drainThread.Start();

while (true)
{
    var key = Console.ReadKey(intercept: true);
    if (key.Key is ConsoleKey.Q or ConsoleKey.Enter or ConsoleKey.Escape) break;
    if (key.Key == ConsoleKey.I)
    {
        foreach (var p in midi.OpenPorts)
        {
            // find the slot for this port
            for (int slot = 0; slot < 256; slot++)
                if (midi.TryGetPort(new MidiInput_PortIndex((byte)slot), out var info) && info.Key == p.Key)
                { midi.RequestIdentity(new MidiInput_PortIndex((byte)slot)); Log(ConsoleColor.Cyan, $"[identity request] {p.Name}"); }
        }
    }
    if (key.Key == ConsoleKey.S)
    {
        Log(ConsoleColor.Yellow, $"[stats] messages={messagesSeen} ring cap={midi.Ring.CurrentCapacity}/{midi.Ring.MaxCapacity} grew={midi.Ring.GrowCount} dropped={midi.Ring.DroppedCount} lag={midi.Ring.LagCount} sysexDiscarded={midi.SysExDiscardedCount}");
        foreach (var p in midi.OpenPorts) Log(ConsoleColor.Yellow, $"        open: {p}");
    }
}

stop.Cancel();
midi.Stop();
Console.WriteLine($"Stopped. {messagesSeen} messages seen.");
return 0;

static void Log(ConsoleColor colour, string text)
{
    lock (Console.Out)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = colour;
        Console.WriteLine(text);
        Console.ForegroundColor = prev;
    }
}

static string Hex(ReadOnlySpan<byte> bytes, int max)
{
    var sb = new System.Text.StringBuilder();
    for (int i = 0; i < bytes.Length && i < max; i++) sb.Append(bytes[i].ToString("X2")).Append(' ');
    if (bytes.Length > max) sb.Append('…');
    return sb.ToString();
}