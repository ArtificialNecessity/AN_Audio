# AN.Audio

Cross-platform audio playback for .NET via direct PInvoke to native OS APIs. A single managed DLL — no native binaries to bundle, no NuGet packages with precompiled C blobs, no runtime dependencies beyond the operating system itself.

## Why

The .NET audio ecosystem is fragmented: NAudio is Windows-only, OpenAL requires runtime installs, SDL2 wrappers need bundled native libraries, and miniaudio bindings still ship a C binary per platform. The actual OS audio APIs are simple — 4–8 native calls each — they just aren't exposed cleanly from managed code.

AN.Audio calls them directly through PInvoke and manual COM vtable dispatch, producing a single `AnyCPU` MSIL assembly that works everywhere .NET runs.

## Status

| Platform | Backend | Status |
|----------|---------|--------|
| Windows | WASAPI (shared mode, event-driven) | ✅ Working |
| Linux | ALSA (`libasound.so.2`) | 🔲 Planned |
| macOS | AudioQueue (AudioToolbox) | 🔲 Planned |
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
| `IAudioOutput` | Start/Stop/Dispose + `Format` and `LatencyMs` properties |
| `AudioOutput` | Static factory — creates the right backend for the current OS |

### Format Negotiation

On Windows, WASAPI shared mode dictates the endpoint format (typically 48kHz/stereo/float32). The `IAudioOutput.Format` property reports what the backend is actually using — your callback must produce samples in that format. If your source data differs, convert in the callback.

## Building

```
dotnet build AN.Audio.slnx
```

After clone, generate the version props file first:

```
./cmd/gen-version-file.ps1
dotnet build
```

## Publishing to Local NuGet Feed

```
$env:LOCAL_NUGET_REPO = "C:\path\to\local\feed"
./cmd/publish-local.ps1
```

This increments the version, builds, packs `ArtificialNecessity.Audio`, and deploys the `.nupkg` to your local feed.

## Project Structure

```
AN.Audio/
├── src/AN.Audio/                    # The library
│   ├── IAudioOutput.cs              # Interface + AudioCallback delegate
│   ├── AudioFormat.cs               # Format descriptor + SampleFormat enum
│   ├── AudioOutput.cs               # Platform-detecting factory
│   └── Platforms/
│       └── Windows/
│           ├── WasapiAudioOutput.cs  # WASAPI event-driven backend
│           └── WasapiInterop.cs     # COM vtable structs + PInvoke
├── tests/SimpleAudioTest/           # Standalone console test (plays a WAV file)
├── AN.Audio.Build.props             # Shared build infrastructure
├── version.jsonc                    # Version source of truth
└── cmd/
    ├── gen-version-file.ps1         # Generate version props from version.jsonc + git
    └── publish-local.ps1            # Build + pack + deploy in one step
```

## Design Principles

- **Zero native dependencies** — the audio APIs are part of the OS. Nothing to bundle or install.
- **Event-driven** — the OS signals when it needs samples. No polling, no spin-waits.
- **Zero-allocation hot path** — the audio callback receives a `Span<byte>` over the hardware buffer. No copies, no GC pressure.
- **One backend per platform** — each is ~200–400 lines of interop code wrapping 4–8 native calls.
- **Playback only** — capture/recording is a separate concern for the future.