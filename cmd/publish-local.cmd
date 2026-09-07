@echo off
REM publish-local.cmd — Thin wrapper that launches the cross-platform C# publish script.
REM Usage: cmd\publish-local.cmd [--release] [--dry-run]
dotnet run --file "%~dp0publish-local.cs" -- %*