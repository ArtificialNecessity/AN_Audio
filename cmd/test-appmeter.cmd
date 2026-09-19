@echo off
REM Smoke test for AN.Audio.AppMeter: prints which processes are making sound. Pass-through args: --duration N --interval ms --top N
cd /d "%~dp0.."
dotnet run --project tests\SimpleAppMeterTest -- %*