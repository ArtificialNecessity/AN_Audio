# AN.Audio

https://github.com/ArtificialNecessity/AN_Audio

(C)opyright David Jeske 2026. Coded with Opus 4.6.
Released under Apache 2.0 for all to share and enjoy.

Cross-platform audio playback and MIDI input for .NET via direct PInvoke to native OS APIs. Managed DLLs only — no native binaries to bundle, no NuGet packages with precompiled C blobs, no runtime dependencies beyond the operating system itself.

## Why

The .NET audio ecosystem is fragmented: NAudio is Windows-only, OpenAL requires runtime installs, SDL2 wrappers need bundled native libraries, and miniaudio bindings still ship a C binary per platform. The actual OS audio APIs are simple — 4–8 native calls each — they just aren't exposed cleanly from managed code.

AN.Audio calls them directly through PInvoke and manual COM vtable dispatch, producing a single `AnyCPU` MSIL assembly that works everywhere .NET runs.

## Status

| Platform | Backend | Status |
|----------|---------|--------|
| Windows | WASAPI (shared mode, event-driven) | ✅ Working |
| macOS | AudioQueue (AudioToolbox) | ✅ Working |
| Linux | ALSA (`libasound.so.2`) | Implemented in source; runtime availability depends on ALSA and a usable output device |
| Android | AAudio | 🔲 Future |
| iOS | AudioQueue (AudioToolbox) | 🔲 Future |

## API

```csharp
using AN.Audio;

// Describe the format you want to work with
var format = new AudioFormat(SampleRate: 48000, Channels: 2, Format: SampleFormat.Float32);

// Create the platform-appropriate output (auto-detects OS)
using var output = AudioOutput.Create(format, bufferSizeMs: 20);

// Start playback — your callback runs on a dedicated audio thread
output.Start((Span<byte> buffer, int frameCount, AudioFormat fmt) =>
{
    // Write interleaved PCM samples into buffer.
    // Return the number of frames actually written.
    // Remainder is filled with silence.
    return frameCount;
});

// ... later
output.Stop();
```

The callback is the only extension point. It runs on a high-priority audio thread and must be fast — no allocations, no blocking locks, no I/O.

### Key Types

| Type | Purpose |
|------|---------|
| `AudioFormat` | Sample rate, channel count, sample format (Int16 or Float32) |
| `AudioCallback` | `delegate int(Span<byte>, int, AudioFormat)` — fills the buffer |
| `IAudioOutput` | Start/Stop/Dispose, fixed consumer `Format`, native `DeviceFormat`, estimated `LatencyMs`, device policy and events |
| `AudioOutput` | Static factory — creates the right backend for the current OS |

### Format Negotiation

`IAudioOutput.Format` is the consumer format requested at creation time and never changes. The callback's `format` argument matches it. `DeviceFormat` reports the current endpoint's native format and may change when the device switches. AN.Audio handles conversion internally, including sample-rate conversion through its sinc resampler when needed. Consumers remain responsible for decoding their assets into the requested PCM format.

### Device Management and Lifecycle

- `AudioOutput.Create(format, AudioOutputOptions)` configures device selection, buffer size, and switching policy. The default policy is `AudioSwitchPolicy.FollowDefault`.
- `AudioOutput.GetDeviceManager()` provides device enumeration and notifications; it returns null where device management is unavailable.
- `IAudioOutput` exposes `CurrentDevice`, `SwitchPolicy`, `PreferredDevices`, and background-thread `DeviceFormatChanged`, `DeviceLost`, and `DeviceSwitched` events. Marshal UI work to the UI thread.
- `Stop()` blocks until the audio thread has drained or stopped; call it from a control thread, not the audio callback. The output can subsequently be started again.
- `LatencyMs` estimates submission-to-DAC latency, not hardware playback position. Requested buffer size is not an end-to-end latency guarantee.
- `AudioOutput.IsAvailable` identifies supported operating systems; it does not guarantee an accessible physical output device.

## MIDI Input

`AN.Audio.Midi.dll` ships in the same package. Default policy: open every input port, merge them into one stream, hot-plug arrivals join automatically — plug in a controller, press a key, get a message.

```csharp
using AN.Audio.Midi;

using var midi = MidiInput.Create();          // all ports, hot-plug polled, identity request on open
midi.DeviceOpened += d => Console.WriteLine($"MIDI: {d.Name} type={d.TypeId}");
midi.Start();

// In your audio callback (exactly one consumer thread), drain BEFORE rendering the block:
while (midi.Ring.TryDequeue(out var m))
{
    if (m.IsNoteOn)       synth.NoteOn(m.Note.Number, m.Velocity.Normalized0To1);
    else if (m.IsNoteOff) synth.NoteOff(m.Note.Number);
}
```

| Type | Purpose |
|------|---------|
| `MidiInput_Message` | 16-byte blittable short message: `ArrivalTicks` (Stopwatch), `DriverTimestamp`, raw `Status`/`Data1`/`Data2`, `Port`; helpers `Kind`, `Channel`, `IsNoteOn`, `IsNoteOff`, `PitchBend14` |
| `MidiInput_MessageRing` | Lock-free SPSC queue filled by the driver thread; grows from `RingInitialCapacity` to `RingMaxCapacity`, then drops newest and counts (`DroppedCount`, `LagCount`, `GrowCount`) |
| `MidiInput_Callback` | Alternative raw delivery on the driver thread (`Start(callback)`); same rules as `AudioCallback` |
| `MidiInput_DeviceInfo` | `Key` (per-port instance, persist this), `TypeId` (per device model, from SysEx Identity Reply), display `Name`, `Identity` |
| `IMidiInput_DeviceManager` | Enumerate ports and get `DeviceListChanged` (polled once/second on WinMM, which has no notification) |

Rules: wire data is never rewritten (a velocity-0 note-on stays a note-on; `IsNoteOff` folds it for you). Control-plane events (`DeviceOpened`, `DeviceLost`, `IdentityResolved`, `SysExReceived`, `Overflow`) fire on a background thread — marshal to UI yourself. Never call `Stop()`/`Dispose()` from inside the raw callback (it throws).

Try it with real hardware:

```
cmd\test-midi.cmd
```

| Platform | MIDI backend | Status |
|----------|--------------|--------|
| Windows | WinMM `midiIn*` (works with legacy stack and Windows MIDI Services) | ✅ Working (input; identity request output only) |
| macOS / Linux | CoreMIDI / ALSA seq | 🔲 Planned |

## Building

```
dotnet build
```

## Publishing to Local NuGet Feed

```
$env:LOCAL_NUGET_REPO = "C:\path\to\local\feed"
./cmd/publish-local.ps1
```

This builds, packs `ArtificialNecessity.Audio`, and deploys the `.nupkg` to your local feed. Versioning is automatic (timestamp-based).

## Project Structure

```
AN.Audio/
├── src/AN.Audio/                    # PCM output + device management (AN.Audio.dll)
│   ├── IAudioOutput.cs              # Interface + AudioCallback delegate
│   ├── AudioFormat.cs               # Format descriptor + SampleFormat enum
│   ├── AudioOutput.cs               # Platform-detecting factory
│   └── Platforms/
│       ├── Windows/
│       │   ├── WasapiAudioOutput.cs  # WASAPI event-driven backend
│       │   └── WasapiInterop.cs     # COM vtable structs + PInvoke
│       ├── MacOS/
│       │   ├── CoreAudioOutput.cs    # AudioQueue callback-driven backend
│       │   └── AudioToolboxInterop.cs # AudioQueue PInvoke
│       └── Linux/                   # ALSA output, interop, and device manager
│   # Each platform also provides a device manager.
│   # Internal/ contains AudioFormatConverter and SincResampler.
├── src/AN.Audio.Midi/               # MIDI input (AN.Audio.Midi.dll) — same layout: Internal/, Platforms/Windows/
│   ├── IMidiInput.cs                # IMidiInput + IMidiInput_DeviceManager
│   ├── MidiInput_Message.cs         # 16-byte hot-path message + MidiInput_Callback
│   ├── MidiInput_MessageRing.cs     # growable SPSC queue (driver thread → your audio thread)
│   └── Platforms/Windows/           # WinMM midiIn*/midiOut* interop, port, input, device manager
├── src/AN.Audio.Package/            # The ONLY packable project: builds the ArtificialNecessity.Audio nupkg (both DLLs)
├── tests/SimpleAudioTest/           # Standalone console test (plays a WAV file)
├── tests/SimpleMidiTest/            # Interactive console: list ports, print messages, hot-plug, identity (cmd/test-midi.cmd)
├── tests/AN.Audio.Tests/             # Automated tests, including sinc resampling
├── tests/AN.Audio.Midi.Tests/        # Automated tests: message decode, ring, SysEx, interop layout
├── AN.Audio.Build.props             # Shared build infrastructure (timestamp versioning v2)
└── cmd/
    ├── publish-local.ps1            # Build + pack + deploy to local feed
    ├── nuget-publish-audio.ps1      # Build + pack + push to NuGet.org
    └── test-midi.cmd                # Run the interactive MIDI smoke test
```

## Design Principles

- **Zero native dependencies** — the audio APIs are part of the OS. Nothing to bundle or install.
- **Event-driven** — the OS signals when it needs samples. No polling, no spin-waits.
- **Allocation-free consumer rendering** — the callback receives a `Span<byte>` to fill. Matching formats can use passthrough; conversion uses intermediate buffers and resampling. Device initialization/recovery is not a zero-allocation operation.
- **One backend per platform** — platform implementations own output, format conversion integration, and device-switch recovery.
- **Scope** — audio output, audio input (capture) and MIDI I/O are all in scope behind one API shape; capture is not yet implemented.