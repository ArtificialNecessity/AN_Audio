#!/usr/bin/env pwsh
# publish-nuget.ps1 — Build a release package and push to NuGet.org
#
# Usage (from anywhere):
#   .\cmd\publish-nuget.ps1          # pack + push
#   .\cmd\publish-nuget.ps1 -DryRun  # pack only, show what would be pushed
#
param(
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Resolve project root (one level up from cmd/)
$projectRoot = Split-Path -Parent $PSScriptRoot
$csprojPath = Join-Path $projectRoot 'src\AN.Audio\AN.Audio.csproj'
$releaseOutputDir = Join-Path $projectRoot 'artifacts\Packages\Release'
$localNuGetFeedPath = 'C:\PROJECTS\LocalNuGet'

# Increment version + regenerate props
$publishLocalScript = Join-Path $projectRoot 'cmd\publish-local.ps1'
Write-Host "`n=== Incrementing version ==="  -ForegroundColor Cyan

$packageBase = Join-Path $env:USERPROFILE ".nuget\packages\artificialnecessity.codeanalyzers"
if (-not (Test-Path $packageBase)) { Write-Host "ERROR: Package artificialnecessity.codeanalyzers not found in NuGet cache." -ForegroundColor Red; exit 1 }
$latestVersion = Get-ChildItem $packageBase -Directory |
    Where-Object { $_.Name -notmatch '-' } |
    Sort-Object { [version]$_.Name } |
    Select-Object -Last 1
if (-not $latestVersion) { Write-Host "ERROR: No stable version of artificialnecessity.codeanalyzers found." -ForegroundColor Red; exit 1 }
$jsonPeekExePath = Join-Path $latestVersion.FullName "tools\net8.0\any\JsonPeek.exe"
if (-not (Test-Path $jsonPeekExePath)) { Write-Host "ERROR: JsonPeek.exe not found at $jsonPeekExePath" -ForegroundColor Red; exit 1 }
$versionJsoncPath = Join-Path $projectRoot "version.jsonc"
$newBuildOffset = & $jsonPeekExePath --inc-integer $versionJsoncPath buildNumberOffset
if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: Failed to increment buildNumberOffset" -ForegroundColor Red; exit 1 }
$baseVersion = & $jsonPeekExePath $versionJsoncPath version
Write-Host "Version: $baseVersion.$newBuildOffset (buildNumberOffset incremented)" -ForegroundColor Yellow

# Regenerate version props
$genVersionScript = Join-Path $projectRoot "cmd\gen-version-file.ps1"
& $genVersionScript
if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: gen-version-file.ps1 failed" -ForegroundColor Red; exit 1 }

Write-Host "`n=== Packing release ===" -ForegroundColor Cyan
dotnet pack $csprojPath -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: dotnet pack failed with exit code $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

# Find the newest .nupkg (excluding prerelease packages with '-' in version)
$newestReleasePackage = Get-ChildItem (Join-Path $releaseOutputDir 'ArtificialNecessity.Audio.*.nupkg') |
    Where-Object { $_.Name -notmatch '\d+-\d+' } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $newestReleasePackage) {
    Write-Host "ERROR: No release .nupkg found in $releaseOutputDir" -ForegroundColor Red
    exit 1
}

Write-Host "`nPackage: $($newestReleasePackage.Name)" -ForegroundColor Green

if ($DryRun) {
    Write-Host "`n[DRY RUN] Would push: $($newestReleasePackage.FullName)" -ForegroundColor Yellow
    Write-Host "[DRY RUN] To: https://api.nuget.org/v3/index.json" -ForegroundColor Yellow
    exit 0
}

Write-Host "`n=== Pushing to NuGet.org ===" -ForegroundColor Cyan
dotnet nuget push $newestReleasePackage.FullName --source https://api.nuget.org/v3/index.json --skip-duplicate
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: dotnet nuget push failed with exit code $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

# Also deploy to local NuGet feed
if (Test-Path $localNuGetFeedPath) {
    Copy-Item $newestReleasePackage.FullName -Destination $localNuGetFeedPath -Force
    Write-Host "Deployed to local feed: $localNuGetFeedPath" -ForegroundColor DarkGray
}

Write-Host "`n=== Done! ===" -ForegroundColor Green
Write-Host "Published: $($newestReleasePackage.Name)" -ForegroundColor Green
Write-Host "View at: https://www.nuget.org/packages/ArtificialNecessity.Audio/" -ForegroundColor Green