#!/usr/bin/env -S dotnet run
// publish-local.cs — Cross-platform build + pack + deploy of the AN.Audio packages to the local NuGet feed.
//
// Versioning is timestamp-based (v2) — every build gets a unique version automatically via
// AN.Audio.Build.props. The timestamp is captured ONCE here and passed to MSBuild so every DLL and
// nupkg carries the exact same version (no inter-project skew).
//
// Packages (one per library project; Audio and Formats depend on Common, Midi on nothing):
// ArtificialNecessity.Audio.Common, .Audio, .Audio.Midi, .Audio.Formats — see PackageIds below.
// Keep that list in sync with the IsPackable projects in AN.Audio.slnx and with cmd/nuget-publish-audio.cs.
//
// Usage:
//   dotnet run --file cmd/publish-local.cs                  # Debug build + pack + deploy
//   dotnet run --file cmd/publish-local.cs --release        # Release configuration
//   dotnet run --file cmd/publish-local.cs --dry-run        # show what would happen
//   cmd\publish-local.cmd [--release] [--dry-run]           # Windows convenience runner
//
// Requires: LOCAL_NUGET_REPO environment variable set to the local feed directory.

using System.Diagnostics;

// ── Disable MSBuild node reuse to prevent zombie worker nodes (Linux Ctrl+C) ─────────
Environment.SetEnvironmentVariable("MSBUILDDISABLENODEREUSE", "1");

// ── Ctrl+C: kill the active child process tree so nothing is left holding locks ─────
Process? activeChild = null;
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    var proc = activeChild;
    if (proc is { HasExited: false })
    {
        WriteColored($"\n[Ctrl+C] Killing child process tree (PID {proc.Id})...", ConsoleColor.Yellow);
        try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
    }
    try
    {
        using var shutdown = Process.Start(new ProcessStartInfo("dotnet", "build-server shutdown") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
        shutdown?.WaitForExit(5000);
    }
    catch { /* best effort */ }
    Environment.Exit(130);
};

// ── Arguments ────────────────────────────────────────────────────────────────────────
bool release = args.Any(a => a is "--release" or "-release" or "-c" or "Release");
bool dryRun  = args.Any(a => a is "--dry-run" or "-n");
string configuration = release ? "Release" : "Debug";

// ── Repo root: walk up from cwd (and from the script's own directory) to AN.Audio.Build.props ──
string repoRoot = FindRepoRoot(Directory.GetCurrentDirectory())
    ?? FindRepoRoot(AppContext.BaseDirectory)
    ?? Fail("Cannot find repo root (looked for AN.Audio.Build.props walking up from cwd)");

string solutionPath   = Path.Combine(repoRoot, "AN.Audio.slnx");
string[] PackageIds   = ["ArtificialNecessity.Audio.Common", "ArtificialNecessity.Audio", "ArtificialNecessity.Audio.Midi", "ArtificialNecessity.Audio.Formats"];

// ── LOCAL_NUGET_REPO ────────────────────────────────────────────────────────────────
string? localNuGetFeedPath = Environment.GetEnvironmentVariable("LOCAL_NUGET_REPO");
if (string.IsNullOrWhiteSpace(localNuGetFeedPath))
{
    WriteColored("ERROR: LOCAL_NUGET_REPO environment variable not set.", ConsoleColor.Red);
    WriteColored("  Linux/macOS: export LOCAL_NUGET_REPO=\"$HOME/LocalNuGet\"", ConsoleColor.Yellow);
    WriteColored("  Windows:     $env:LOCAL_NUGET_REPO = \"C:\\PROJECTS\\LocalNuGet\"", ConsoleColor.Yellow);
    return 1;
}
localNuGetFeedPath = ExpandHome(localNuGetFeedPath);

// ── One timestamp for everything ────────────────────────────────────────────────────────
var stamp = BuildStamp.Now();

WriteColored($"\n=== AN.Audio publish-local ({configuration}) ===", ConsoleColor.Cyan);
WriteColored($"Version stamp:    {stamp.AssemblyVersion} (pkg: {stamp.PackageVersion})", ConsoleColor.DarkGray);
WriteColored($"Local NuGet feed: {localNuGetFeedPath}", ConsoleColor.DarkGray);

if (dryRun)
{
    WriteColored($"\n[DRY RUN] Would build + pack {solutionPath}, deploying to {localNuGetFeedPath}:", ConsoleColor.Yellow);
    foreach (string id in PackageIds) WriteColored($"  {id}.{stamp.PackageVersion}.nupkg", ConsoleColor.Yellow);
    return 0;
}

Directory.CreateDirectory(localNuGetFeedPath);
DateTime deployStartUtc = DateTime.UtcNow;

// ── [1/2] Build everything (tests included — a broken test project should block a publish) ──
WriteColored("\n[1/2] Building solution...", ConsoleColor.Green);
if (Run("dotnet", $"build \"{solutionPath}\" -c {configuration} /nodeReuse:false {stamp.MsBuildArgs}") is int buildExit and not 0)
    return Failed($"dotnet build exited with code {buildExit}");

// ── [2/2] Pack every IsPackable project in the solution (DeployToLocalNuGet target copies each to the feed) ──
WriteColored($"\n[2/2] Packing {string.Join(", ", PackageIds)}...", ConsoleColor.Green);
if (Run("dotnet", $"pack \"{solutionPath}\" -c {configuration} --no-build /nodeReuse:false /p:LocalNuGetFeedPath=\"{localNuGetFeedPath}\" {stamp.MsBuildArgs}") is int packExit and not 0)
    return Failed($"dotnet pack exited with code {packExit}");

// ── Report ─────────────────────────────────────────────────────────────────────────────
var deployed = new DirectoryInfo(localNuGetFeedPath).GetFiles("ArtificialNecessity.Audio*.nupkg")
    .Where(f => f.LastWriteTimeUtc >= deployStartUtc).OrderBy(f => f.Name).ToList();

var missing = PackageIds.Where(id => !deployed.Any(f => f.Name.Equals($"{id}.{stamp.PackageVersion}.nupkg", StringComparison.OrdinalIgnoreCase))).ToList();
if (missing.Count > 0)
    return Failed($"Expected {PackageIds.Length} packages in {localNuGetFeedPath}; missing: {string.Join(", ", missing)}");

WriteColored("\nPUBLISH SUCCEEDED — deployed packages:", ConsoleColor.Green);
foreach (var pkg in deployed)
    WriteColored($"  {pkg.Name}  ({Math.Round(pkg.Length / 1024.0, 1)} KB)", ConsoleColor.Green);
Console.WriteLine();
return 0;

// ══ helpers ══════════════════════════════════════════════════════════════════════════════

int Run(string fileName, string arguments)
{
    WriteColored($"  > {fileName} {arguments}", ConsoleColor.DarkGray);
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

static string ExpandHome(string path)
{
    if (path == "~" || path.StartsWith("~/") || path.StartsWith("~\\"))
        path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : "");
    return Path.GetFullPath(path);
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