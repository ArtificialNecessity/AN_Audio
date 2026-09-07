#!/usr/bin/env -S dotnet run
// nuget-publish-audio.cs — Cross-platform Release pack + push of ArtificialNecessity.Audio to NuGet.org.
//
// Versioning is timestamp-based (v2) via AN.Audio.Build.props; the stamp is captured once here so
// AN.Audio.dll, AN.Audio.Midi.dll and the nupkg share one version. Only src/AN.Audio.Package is packed
// (SPEC-30 D21) — it bundles both DLLs.
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

string packageProject   = Path.Combine(repoRoot, "src", "AN.Audio.Package", "AN.Audio.Package.csproj");
string releaseOutputDir = Path.Combine(repoRoot, "artifacts", "Packages", "Release");
string? localFeed       = Environment.GetEnvironmentVariable("LOCAL_NUGET_REPO");
string? apiKey          = Environment.GetEnvironmentVariable("NUGET_API_KEY");
const string NuGetSource = "https://api.nuget.org/v3/index.json";

var stamp = BuildStamp.Now();
string expectedPackage = Path.Combine(releaseOutputDir, $"ArtificialNecessity.Audio.{stamp.PackageVersion}.nupkg");

WriteColored("\n=== Packing ArtificialNecessity.Audio (Release) ===", ConsoleColor.Cyan);
WriteColored($"Version: {stamp.AssemblyVersion} (pkg: {stamp.PackageVersion})", ConsoleColor.DarkGray);

if (Run("dotnet", $"pack \"{packageProject}\" -c Release /nodeReuse:false {stamp.MsBuildArgs}") is int packExit and not 0)
    return Failed($"dotnet pack exited with code {packExit}");

if (!File.Exists(expectedPackage))
    return Failed($"Expected package not found: {expectedPackage}");

WriteColored($"\nPackage: {Path.GetFileName(expectedPackage)}  ({Math.Round(new FileInfo(expectedPackage).Length / 1024.0, 1)} KB)", ConsoleColor.Green);

if (dryRun)
{
    WriteColored($"\n[DRY RUN] Would push: {expectedPackage}", ConsoleColor.Yellow);
    WriteColored($"[DRY RUN] To: {NuGetSource}", ConsoleColor.Yellow);
    return 0;
}

WriteColored("\n=== Pushing to NuGet.org ===", ConsoleColor.Cyan);
string keyArg = string.IsNullOrWhiteSpace(apiKey) ? "" : $" --api-key {apiKey}";
if (Run("dotnet", $"nuget push \"{expectedPackage}\" --source {NuGetSource} --skip-duplicate{keyArg}") is int pushExit and not 0)
    return Failed($"dotnet nuget push exited with code {pushExit}");

if (!string.IsNullOrWhiteSpace(localFeed))
{
    string feed = Path.GetFullPath(localFeed);
    Directory.CreateDirectory(feed);
    File.Copy(expectedPackage, Path.Combine(feed, Path.GetFileName(expectedPackage)), overwrite: true);
    WriteColored($"Also deployed to local feed: {feed}", ConsoleColor.DarkGray);
}

WriteColored("\n=== Done ===", ConsoleColor.Green);
WriteColored($"Published: {Path.GetFileName(expectedPackage)}", ConsoleColor.Green);
WriteColored("View at:   https://www.nuget.org/packages/ArtificialNecessity.Audio/", ConsoleColor.Green);
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