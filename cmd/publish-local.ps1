#!/usr/bin/env pwsh
# publish-local.ps1 — Build and pack AN.Audio packages to local NuGet feed
#
# Versioning is timestamp-based (v2) — every build gets a unique version
# automatically via AN.Audio.Build.props. No version files to manage.
#
# Usage:
#   ./cmd/publish-local.ps1                    # Debug build + pack + deploy
#   ./cmd/publish-local.ps1 -Release           # Release configuration
#
# Requires: LOCAL_NUGET_REPO environment variable set to local feed path

param(
    [switch]$Release
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$configuration = if ($Release) { "Release" } else { "Debug" }

Write-Host "=== AN.Audio publish-local ($configuration) ===" -ForegroundColor Cyan

if (-not $env:LOCAL_NUGET_REPO) {
    Write-Host "ERROR: LOCAL_NUGET_REPO environment variable not set." -ForegroundColor Red
    Write-Host '$env:LOCAL_NUGET_REPO = "C:\PROJECTS\LocalNuGet"' -ForegroundColor Yellow
    exit 1
}

Write-Host "Local NuGet feed: $env:LOCAL_NUGET_REPO" -ForegroundColor Gray

# Capture timestamp before build/pack so we can identify newly deployed packages
$deployStartTime = Get-Date

# Build the solution
Write-Host "`n[1/2] Building solution..." -ForegroundColor Green
dotnet build "$repoRoot\AN.Audio.slnx" -c $configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Pack AN.Audio library
Write-Host "`n[2/2] Packing AN.Audio library..." -ForegroundColor Green
dotnet pack "$repoRoot\src\AN.Audio\AN.Audio.csproj" -c $configuration
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