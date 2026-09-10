#!/bin/sh
# test-midi.sh - build and run the interactive SimpleMidiTest console (SPEC-30 hardware smoke test) on macOS / Linux
#   lists MIDI input ports, opens them all, prints messages / hot-plug / identity replies
#   keys inside the app:  i = identity request   s = stats   q = quit
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
exec dotnet run --project "$REPO_ROOT/tests/SimpleMidiTest/SimpleMidiTest.csproj" -c Debug -- "$@"