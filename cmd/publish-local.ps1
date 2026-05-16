#!/usr/bin/env pwsh
# publish-local.ps1 — Build and pack AN.Audio packages to local NuGet feed
#
# Versioning flow:
#   1. Stable: increment buildNumberOffset in version.jsonc
#   2. Regenerate AN.Audio.Version.generated.props via gen-version-file.ps1
#   3. Build + pack + deploy to local NuGet feed
#
# Usage:
#   ./cmd/publish-local.ps1                    # stable build + pack + deploy
#   ./cmd/publish-local.ps1 -Release           # Release configuration
#   ./cmd/publish-local.ps1 -Prerelease        # prerelease versions (no auto-increment)
#
# Requires: LOCAL_NUGET_REPO environment variable set to local feed path

param(
    [switch]$Release,
    [switch]$Prerelease
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$configuration = if ($Release) { "Release" } else { "Debug" }
$prereleaseFlag = if ($Prerelease) { "-p:Prerelease=true" } else { "" }
$versionLabel = if ($Prerelease) { "prerelease" } else { "stable" }

Write-Host "=== AN.Audio publish-local ($configuration, $versionLabel) ===" -ForegroundColor Cyan

if (-not $env:LOCAL_NUGET_REPO) {
    Write-Host "ERROR: LOCAL_NUGET_REPO environment variable not set." -ForegroundColor Red
    Write-Host 'Set it to your local NuGet feed path, e.g.: $env:LOCAL_NUGET_REPO = "C:\PROJECTS\LocalNuGet"' -ForegroundColor Yellow
    exit 1
}

Write-Host "Local NuGet feed: $env:LOCAL_NUGET_REPO" -ForegroundColor Gray

# Increment buildNumberOffset for stable versions (before any build/pack)
if (-not $Prerelease) {
    # Dynamically find latest stable version of JsonPeek from NuGet cache
    $packageBase = Join-Path $env:USERPROFILE ".nuget\packages\artificialnecessity.codeanalyzers"
    if (-not (Test-Path $packageBase)) { Write-Host "ERROR: Package artificialnecessity.codeanalyzers not found in NuGet cache." -ForegroundColor Red; exit 1 }
    $latestVersion = Get-ChildItem $packageBase -Directory |
        Where-Object { $_.Name -notmatch '-' } |
        Sort-Object { [version]$_.Name } |
        Select-Object -Last 1
    if (-not $latestVersion) { Write-Host "ERROR: No stable version of artificialnecessity.codeanalyzers found." -ForegroundColor Red; exit 1 }
    $jsonPeekExePath = Join-Path $latestVersion.FullName "tools\net8.0\any\JsonPeek.exe"
    if (-not (Test-Path $jsonPeekExePath)) { Write-Host "ERROR: JsonPeek.exe not found at $jsonPeekExePath" -ForegroundColor Red; exit 1 }
    $versionJsoncPath = Join-Path $repoRoot "version.jsonc"
    $newBuildOffset = & $jsonPeekExePath --inc-integer $versionJsoncPath buildNumberOffset
    if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: Failed to increment buildNumberOffset" -ForegroundColor Red; exit 1 }
    $baseVersion = & $jsonPeekExePath $versionJsoncPath version
    Write-Host "Version: $baseVersion.$newBuildOffset (buildNumberOffset incremented)" -ForegroundColor Yellow
}

# Regenerate AN.Audio.Version.generated.props from version.jsonc + git
$genVersionScript = Join-Path $repoRoot "cmd\gen-version-file.ps1"
Write-Host "`n[0/2] Generating version props..." -ForegroundColor Green
& $genVersionScript
if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: gen-version-file.ps1 failed" -ForegroundColor Red; exit 1 }

# Capture timestamp before build/pack so we can identify newly deployed packages
$deployStartTime = Get-Date

# Build the solution
Write-Host "`n[1/2] Building solution..." -ForegroundColor Green
dotnet build "$repoRoot\AN.Audio.slnx" -c $configuration $prereleaseFlag
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Pack AN.Audio library
Write-Host "`n[2/2] Packing AN.Audio library..." -ForegroundColor Green
dotnet pack "$repoRoot\src\AN.Audio\AN.Audio.csproj" -c $configuration $prereleaseFlag
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Show only packages deployed during this run (modified after $deployStartTime)
$deployedPackages = Get-ChildItem "$env:LOCAL_NUGET_REPO\*.nupkg" -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTime -ge $deployStartTime } |
    Sort-Object Name
if ($deployedPackages) {
    Write-Host "`nDeployed packages:" -ForegroundColor Cyan
    foreach ($deployedPackage in $deployedPackages) {
        $sizeKB = [math]::Round($deployedPackage.Length / 1024, 1)
        Write-Host "  $($deployedPackage.Name)  (${sizeKB} KB)" -ForegroundColor Green
    }
} else {
    Write-Host "`nWARNING: No packages were deployed to $env:LOCAL_NUGET_REPO" -ForegroundColor Yellow
}