#!/usr/bin/env -S dotnet run
// nuget-publish-audio.cs — Cross-platform Release pack + push of the AN.Audio packages to NuGet.org.
//
// Versioning is timestamp-based (v2) via AN.Audio.Build.props; the stamp is captured once here so
// every DLL and nupkg shares one version. Two independent packages, one per project:
// ArtificialNecessity.Audio (src/AN.Audio) and ArtificialNecessity.Audio.Midi (src/AN.Audio.Midi).
//
// Usage:
//   dotnet run --file cmd/nuget-publish-audio.cs              # pack + push
//   dotnet run --file cmd/nuget-publish-audio.cs --dry-run    # pack only, show what would be pushed
//   cmd\nuget-publish-audio.cmd [--dry-run]                   # Windows convenience runner
//
// Push authentication: `dotnet nuget push` uses your configured NuGet.org API key
// (set NUGET_API_KEY to pass --api-key explicitly).

using System.Diagnostics;

Environment.SetEnvironmentVariable("MSBUILDDISABLENODEREUSE", "1");

Process? activeChild = null;
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    var proc = activeChild;
    if (proc is { HasExited: false }) { try { proc.Kill(entireProcessTree: true); } catch { } }
    Environment.Exit(130);
};

bool dryRun = args.Any(a => a is "--dry-run" or "-n");

string repoRoot = FindRepoRoot(Directory.GetCurrentDirectory())
    ?? FindRepoRoot(AppContext.BaseDirectory)
    ?? Fail("Cannot find repo root (looked for AN.Audio.Build.props walking up from cwd)");

string solutionPath     = Path.Combine(repoRoot, "AN.Audio.slnx");
string releaseOutputDir = Path.Combine(repoRoot, "artifacts", "Packages", "Release");
string? localFeed       = Environment.GetEnvironmentVariable("LOCAL_NUGET_REPO");
string? apiKey          = Environment.GetEnvironmentVariable("NUGET_API_KEY");
const string NuGetSource = "https://api.nuget.org/v3/index.json";
string[] packageIds     = ["ArtificialNecessity.Audio", "ArtificialNecessity.Audio.Midi"];

var stamp = BuildStamp.Now();
string[] expectedPackages = packageIds.Select(id => Path.Combine(releaseOutputDir, $"{id}.{stamp.PackageVersion}.nupkg")).ToArray();

WriteColored("\n=== Packing AN.Audio packages (Release) ===", ConsoleColor.Cyan);
WriteColored($"Version: {stamp.AssemblyVersion} (pkg: {stamp.PackageVersion})", ConsoleColor.DarkGray);

if (Run("dotnet", $"pack \"{solutionPath}\" -c Release /nodeReuse:false {stamp.MsBuildArgs}") is int packExit and not 0)
    return Failed($"dotnet pack exited with code {packExit}");

foreach (string pkg in expectedPackages)
{
    if (!File.Exists(pkg)) return Failed($"Expected package not found: {pkg}");
    WriteColored($"Package: {Path.GetFileName(pkg)}  ({Math.Round(new FileInfo(pkg).Length / 1024.0, 1)} KB)", ConsoleColor.Green);
}

if (dryRun)
{
    foreach (string pkg in expectedPackages) WriteColored($"\n[DRY RUN] Would push: {pkg}", ConsoleColor.Yellow);
    WriteColored($"[DRY RUN] To: {NuGetSource}", ConsoleColor.Yellow);
    return 0;
}

WriteColored("\n=== Pushing to NuGet.org ===", ConsoleColor.Cyan);
string keyArg = string.IsNullOrWhiteSpace(apiKey) ? "" : $" --api-key {apiKey}";
foreach (string pkg in expectedPackages)
{
    if (Run("dotnet", $"nuget push \"{pkg}\" --source {NuGetSource} --skip-duplicate{keyArg}") is int pushExit and not 0)
        return Failed($"dotnet nuget push ({Path.GetFileName(pkg)}) exited with code {pushExit}");
}

if (!string.IsNullOrWhiteSpace(localFeed))
{
    string feed = Path.GetFullPath(localFeed);
    Directory.CreateDirectory(feed);
    foreach (string pkg in expectedPackages) File.Copy(pkg, Path.Combine(feed, Path.GetFileName(pkg)), overwrite: true);
    WriteColored($"Also deployed to local feed: {feed}", ConsoleColor.DarkGray);
}

WriteColored("\n=== Done ===", ConsoleColor.Green);
foreach (string id in packageIds)
    WriteColored($"Published: {id} {stamp.PackageVersion}  https://www.nuget.org/packages/{id}/", ConsoleColor.Green);
return 0;

// ══ helpers ══════════════════════════════════════════════════════════════════════════════

int Run(string fileName, string arguments)
{
    // Don't echo the API key.
    WriteColored($"  > {fileName} {(arguments.Contains("--api-key") ? arguments[..arguments.IndexOf("--api-key")] + "--api-key ***" : arguments)}", ConsoleColor.DarkGray);
    using var process = Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = false })
        ?? throw new InvalidOperationException($"Failed to start: {fileName}");
    activeChild = process;
    try { process.WaitForExit(); } finally { activeChild = null; }
    return cts.IsCancellationRequested ? 130 : process.ExitCode;
}

static int Failed(string message)
{
    WriteColored($"\nPUBLISH FAILED: {message}", ConsoleColor.Red);
    return 1;
}

static string Fail(string message)
{
    WriteColored($"ERROR: {message}", ConsoleColor.Red);
    Environment.Exit(1);
    return "";
}

static string? FindRepoRoot(string startDir)
{
    for (string? dir = Path.GetFullPath(startDir); dir is not null; dir = Path.GetDirectoryName(dir))
        if (File.Exists(Path.Combine(dir, "AN.Audio.Build.props"))) return dir;
    return null;
}

static void WriteColored(string message, ConsoleColor color)
{
    var prev = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.WriteLine(message);
    Console.ForegroundColor = prev;
}

/// <summary>Timestamp captured once, formatted exactly as AN.Audio.Build.props expects (major version 0).</summary>
sealed record BuildStamp(string YYMM, string DDHH, string mmss, string YYMMDD, string HHmmss)
{
    public const string Major = "0";
    public static BuildStamp Now()
    {
        var now = DateTime.Now;
        return new(now.ToString("yyMM"), now.ToString("ddHH"), now.ToString("mmss"), now.ToString("yyMMdd"), now.ToString("HHmmss"));
    }
    public string AssemblyVersion => $"{Major}.{YYMM}.{DDHH}.{mmss}";
    public string PackageVersion  => $"{Major}.{int.Parse(YYMMDD)}.{int.Parse(HHmmss)}";   // NuGet strips leading zeros
    public string MsBuildArgs     => $"/p:_BuildYYMM={YYMM} /p:_BuildDDHH={DDHH} /p:_Buildmmss={mmss} /p:_BuildYYMMDD={YYMMDD} /p:_BuildHHmmss={HHmmss}";
}