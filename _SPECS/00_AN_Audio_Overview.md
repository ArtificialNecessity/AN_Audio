# 00 — AN.Audio: what this library is

- **Status:** Living document (update when scope changes)
- **Packages:** one per feature area — `ArtificialNecessity.Audio` (`AN.Audio.dll`), `ArtificialNecessity.Audio.Midi` (`AN.Audio.Midi.dll`), `ArtificialNecessity.Audio.Formats` (`AN.Audio.Formats.dll`), future `ArtificialNecessity.Audio.Capture`; plus the shared vocabulary `ArtificialNecessity.Audio.Common` (`AN.Audio.Common.dll`, spec 50 D15) that Audio and Formats depend on
- **Repo:** https://github.com/ArtificialNecessity/AN_Audio — Apache 2.0

## Goal

**Low-level, performant, zero-/low-allocation access to every audio-related OS service — output, input (capture), MIDI in, MIDI out —
through ONE API shape that is identical on Windows, macOS, Linux, Android and iOS, delivered as a single platform-neutral managed DLL.**

Not a mixer, not a decoder, not a DAW toolkit. Those sit above us. AN.Audio is the thinnest correct layer between .NET and the
operating system's audio stack.

## The five invariants

| # | Invariant | What it means in practice |
|---|---|---|
| I1 | **Zero native dependencies** | PInvoke / manual COM vtables into libraries the OS already ships (`winmm`, WASAPI, `libasound.so.2`, AudioToolbox/CoreAudio/CoreMIDI, AAudio, `android.media.midi` via JNI). Nothing to bundle, nothing to install, one `AnyCPU` MSIL assembly per feature area. NativeAOT-compatible (no reflection-based COM, no RCWs). |
| I2 | **One API shape everywhere** | Consumers see `IAudioOutput`, `IAudioInput`, `IMidiInput`, `IMidiOutput`, plus one `*_DeviceManager` per area. A platform backend implements the interface; it never leaks platform types. `Xxx.IsAvailable` says whether a backend exists; the shape never changes. |
| I3 | **One hot path per stream, allocation-free** | Exactly one callback (or one SPSC ring) carries the real-time data: `AudioCallback(Span<byte>, frames, format)`, `MidiInput_Callback(in MidiInput_Message)`. Fixed-size blittable structs, no `string`, no boxing, no locks, no I/O. Everything else is control plane. Initialisation and device recovery are NOT zero-alloc and never run on the hot thread. |
| I4 | **Event-driven, never polled** | The OS wakes us (WASAPI event, ALSA `poll`, AudioQueue/CoreMIDI callback, WinMM driver callback). Where an OS genuinely offers no notification (WinMM MIDI hot-plug, ALSA device changes) we poll **once per second on a background thread** and document it as a platform limitation with an opt-in to an OS hook when one exists (`WM_DEVICECHANGE`, udev). Never poll for data. |
| I5 | **Control plane on background threads, marshalled by the consumer** | `DeviceLost`, `DeviceSwitched`, `DeviceListChanged`, `DeviceOpened`, `Overflow` … fire on whatever thread the OS gave us. Documented once, identically, everywhere: *marshal to UI yourself*. `Stop()` blocks until the hot path is quiescent. |

## Feature areas and platform matrix

| Area | Interface / factory | Windows | macOS | Linux | Android | iOS | Spec |
|---|---|---|---|---|---|---|---|
| PCM output | `IAudioOutput` / `AudioOutput` | ✅ WASAPI shared, event-driven | ✅ AudioQueue | ✅ ALSA (source) | ◻ AAudio | ◻ AudioQueue | `10_Audio_Bringup.md` |
| Output device mgmt | `IAudioDeviceManager` | ✅ MMDevice + `IMMNotificationClient` | ✅ property listeners | ✅ hints + reactive loss | ◻ | ◻ | `20_Audio_Device_Management.md` |
| PCM **input (capture)** | `IAudioInput` / `AudioInput` | ◻ WASAPI capture | ◻ AudioQueue input | ◻ ALSA capture | ◻ AAudio | ◻ | **TBD `40_Audio_Capture.md`** |
| **MIDI input** | `IMidiInput` / `MidiInput` | ✅ WinMM `midiIn*` (Sprint 1, hardware-validated) | ◻ CoreMIDI | ◻ ALSA seq | ◻ `android.media.midi` | ◻ CoreMIDI | `30_MidiInput.md` |
| **MIDI output** | `IMidiOutput` / `MidiOutput` | ◻ WinMM `midiOut*` (Sprint 3) | ◻ CoreMIDI | ◻ ALSA seq | ◻ | ◻ | `30_MidiInput.md` §Sprint 3 (own spec when started) |
| MIDI device mgmt | `IMidiInput_DeviceManager` | ✅ 1 s poll + `NotifyDeviceChange()` host hook; library `WM_DEVICECHANGE` window in Sprint 2 | ◻ `MIDINotifyProc` | ◻ seq announce port | ◻ | ◻ | `30_MidiInput.md` |
| MIDI 2.0 / UMP | same interfaces, richer message struct | ◻ Windows MIDI Services SDK | ◻ CoreMIDI UMP | ◻ ALSA UMP | ◻ | ◻ | later |
| **Format decoding** (no OS API) | `IAudioDecoder` / `AudioDecoder.Open` + `Wav_Decoder`, `Flac_Decoder`, `Mp3_Decoder` | pure managed — identical everywhere | | | | | `50_Audio_Formats.md` |

✅ implemented   ◻ planned, interface shaped for it. **Audio capture is in scope**; it has simply not been sprinted yet.
Earlier documents said "playback only" — that was sprint scope, not library scope.

## Repository shape

```
AN_Audio/
├── _SPECS/                      00 overview (this), 01 sinc resampler, 10 output, 20 devices, 30 MIDI, 40 capture (TBD), 50 formats
├── _EXTERNAL_APIS/              ground truth read from SDK headers / vendor docs, one file per OS API
├── src/AN.Audio.Common/         shared PCM vocabulary ONLY: AudioFormat, SampleFormat, AudioChannelMask, AudioBufferView, AudioSampleConvert (spec 50 D15)
├── src/AN.Audio/                PCM output + device management
│   ├── Internal/                format conversion, sinc resampler, shared allocation-free helpers
│   └── Platforms/{Windows,MacOS,Linux,Android,iOS}/
├── src/AN.Audio.Midi/           MIDI in/out — SAME layout: Internal/, Platforms/…
├── src/AN.Audio.Formats/        format decoding (WAV, FLAC, MP3) — pure managed, no OS API: Wav/, Flac/, Mp3/, Internal/ (spec 50)
├── tests/AN.Audio.Common.Tests/ xunit: sample conversion, buffer view
├── tests/AN.Audio.Tests/        xunit, hardware-free
├── tests/AN.Audio.Midi.Tests/   xunit, hardware-free (interop layout, ring, parsers)
├── tests/AN.Audio.Formats.Tests/ xunit: synthesised WAV fixtures, FLAC/MP3 fixtures, forward-only streaming
├── tests/SimpleAudioTest/       console smoke with real devices (manual)
├── tests/SimpleMidiTest/        console smoke with real devices (manual; cmd/test-midi.cmd)
├── cmd/                         cross-platform C# scripts (dotnet run --file) + .cmd runners: publish-local, nuget-publish-audio, test-midi
└── AN.Audio.Build.props         timestamp versioning, analyzers, artifacts/ output paths — imported by every csproj
```

**Packaging rule (spec 30 D27, amended by spec 50 D15):** one NuGet package per feature-area project, each independent —
`ArtificialNecessity.Audio` (`src/AN.Audio`), `ArtificialNecessity.Audio.Midi` (`src/AN.Audio.Midi`), `ArtificialNecessity.Audio.Formats`
(`src/AN.Audio.Formats`), future `ArtificialNecessity.Audio.Capture`. No umbrella project; **no inter-project references EXCEPT to
`ArtificialNecessity.Audio.Common`** (`src/AN.Audio.Common`), the minimum shared PCM vocabulary, referenced only by projects that
need a PCM type (Audio, Formats — not Midi). `cmd/publish-local.cs` packs the solution with one shared timestamp version, so the
Common dependency is always pinned to the same stamp.

Every feature-area project mirrors this: public interfaces + factory at the root, `Internal/` for platform-neutral machinery,
`Platforms/<OS>/` for one backend each (`<Api>Interop.cs` + `<Api><Area>.cs` + `<Api>DeviceManager.cs`).

## Coding rules that follow from the invariants

1. **Every native constant is an `enum` (or typed const class) in the platform's `*Interop.cs`, used symbolically.** `WinMm_MidiInMessage.Data`, never `0x3C3`. A layout/constant test asserts each enum value against the SDK header value it names.
2. **Read the SDK header, not the blog post.** Struct layouts, pointer-sized fields (`DWORD_PTR`), calling conventions come from `_EXTERNAL_APIS/*.md`, which quote the headers on disk.
3. **Linguistic keying**: scope-prefixed public types (`AudioFormat`, `MidiInput_Message`, `Midi_Status`, `WinMm_*`). No bare `int`/`string` in public data structures; brand them (`MidiInput_DeviceKey`, `Midi_Note`).
4. **Callbacks are `[UnmanagedCallersOnly]` static functions** reached through a `GCHandle`/table index in the user-data slot. No closures across the native boundary.
5. **Hot path = no `new`, no `lock`, no `string`, no exceptions.** Tests measure `GC.GetAllocatedBytesForCurrentThread()` deltas of zero across representative loops.
6. **Never rewrite wire data.** A velocity-0 note-on stays a note-on with `Data2 == 0`; a 44.1 kHz stream is resampled only because the consumer asked for a format. Helpers interpret; the library does not editorialise.
7. **Device identity is stable and opaque.** Consumers persist `*_DeviceKey`/`Id`, never display names (OS vendors rename ports — Windows did in 2026).
8. **Failure is a reported event, not a retry loop.** `MMSYSERR_ALLOCATED`, `-ENODEV`, `kAudioHardwareBadDeviceError` → `DeviceLost(reason)` once; re-attempt only when the device list changes.

## Non-goals (permanently above this layer)

For the **OS-facing layer** (`AN.Audio`, `AN.Audio.Midi`, future Capture): decoding, resampling as a service (we resample only
to satisfy a requested output format), effects, sequencing, MIDI file I/O, virtual/loopback ports (until an OS offers them
natively), plugin hosting. Format decoding (WAV/FLAC/MP3) IS in the repo, but as its own sibling package `AN.Audio.Formats`
(spec 50, D14) that touches no OS API and never becomes a dependency of the OS-facing packages.

## Possible future additions (not committed, not specced)

- **Simple mixer** — a helper (`AudioMixer` + `IAudioSource`) that is itself just an `AudioCallback`: sums N float sources with
  per-source volume and hands one buffer to `IAudioOutput`. It touches no OS API, so it would be a small platform-neutral
  addition rather than a feature area. Originally sketched as Milestone 5 of `10_Audio_Bringup.md`, removed from that spec
  2026-09-07 because it is not part of bringup. If a consumer (Arcane Siege layered SFX, Mirica UI sounds over TTS) needs it,
  it gets its own short spec; until then consumers sum their sources in their own callback.
- **Underrun counters** on `IAudioOutput` (open question in spec 10).

## Consumers today

Mirica (UI/TTS audio), Arcane Siege (SFX/music), MusicStudio (synth output, sample rail, MIDI capture per its spec 17).