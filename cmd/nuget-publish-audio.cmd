@echo off
REM nuget-publish-audio.cmd — Thin wrapper that launches the cross-platform C# NuGet.org publish script.
REM Usage: cmd\nuget-publish-audio.cmd [--dry-run]
dotnet run --file "%~dp0nuget-publish-audio.cs" -- %*