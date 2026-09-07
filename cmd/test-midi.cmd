@echo off
rem test-midi.cmd - build and run the interactive SimpleMidiTest console (SPEC-30 hardware smoke test)
rem   lists MIDI input ports, opens them all, prints messages / hot-plug / identity replies
rem   keys inside the app:  i = identity request   s = stats   q = quit
setlocal
set REPO_ROOT=%~dp0..
dotnet run --project "%REPO_ROOT%\tests\SimpleMidiTest\SimpleMidiTest.csproj" -c Debug -- %*
endlocal