// SimpleAppMeterTest — console smoke for AN.Audio.AppMeter (spec 80): "what on my machine is making sound?"
// Prints the top-N sessions by peak every --interval ms (default 250) for --duration seconds (default 20).
//   dotnet run --project tests/SimpleAppMeterTest -- --duration 30 --interval 250 --top 12

using System.Diagnostics;
using AN.Audio.AppMeter;

int durationSec = ArgInt("--duration", 20);
int intervalMs = ArgInt("--interval", 250);
int top = ArgInt("--top", 12);

Console.WriteLine($"AN.Audio.AppMeter: Capability={AudioAppMeter.Capability} IsAvailable={AudioAppMeter.IsAvailable} Reason={AudioAppMeter.UnavailableReason}");
if (!AudioAppMeter.IsAvailable) return 1;

using var meter = AudioAppMeter.Create(new AudioAppMeter_Options { PollIntervalMs = 100 });
Console.WriteLine($"meter: Capability={meter.Capability} Reason={meter.UnavailableReason}");
if (meter.Capability == AudioAppMeter_Capability.None) return 2;

int changes = 0;
meter.SessionsChanged += () => Interlocked.Increment(ref changes);

var names = new Dictionary<int, string>();
string PidName(AudioAppMeter_ProcessId pid)
{
    if (pid.IsUnknown) return "(unattributed)";
    if (!names.TryGetValue(pid.Value, out var n))
    {
        try { n = Process.GetProcessById(pid.Value).ProcessName; } catch { n = "(gone)"; }
        names[pid.Value] = n;
    }
    return n;
}

var rows = new AudioAppMeter_Sample[512];
var sw = Stopwatch.StartNew();
while (sw.Elapsed.TotalSeconds < durationSec)
{
    Thread.Sleep(intervalMs);
    int n = meter.ReadSessions(rows);
    if (n > rows.Length) { rows = new AudioAppMeter_Sample[n]; n = meter.ReadSessions(rows); }
    float master = meter.ReadMasterPeak();

    var live = rows.AsSpan(0, n).ToArray();
    int active = live.Count(r => r.State == AudioAppMeter_SessionState.Active);
    int endpoints = live.Select(r => r.EndpointId).Distinct().Count();
    Console.WriteLine($"--- t={sw.Elapsed.TotalSeconds,5:F1}s  master={master:F3}  sessions={n} active={active} endpoints={endpoints} changes={changes}");
    foreach (var r in live.OrderByDescending(r => r.Peak.Value).ThenBy(r => r.State).Take(top))
    {
        string bar = new('#', (int)(r.Peak.Value * 40));
        string name = meter.LookupDisplayName(r.SessionId) is { Length: > 0 } dn ? dn : PidName(r.ProcessId);
        Console.WriteLine($"  {r.Peak.Value:F3} {bar,-40} pid={r.ProcessId.Value,-6} {r.State,-8} {(r.Flags == 0 ? "" : r.Flags.ToString()),-24} {name}");
    }
}
Console.WriteLine("done.");
return 0;

int ArgInt(string name, int dflt)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int v) ? v : dflt;
}