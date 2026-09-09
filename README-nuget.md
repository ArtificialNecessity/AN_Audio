# AN.Audio

Cross-platform audio for .NET via direct PInvoke to the OS audio APIs — PCM output, MIDI input, and pure-managed audio format decoding. Managed `AnyCPU` DLLs only: no native binaries to bundle, no runtime installs, nothing beyond the operating system itself.

[Discussions at Github](https://github.com/ArtificialNecessity/AN_Audio/discussions/)

## Package Summary

| Package | Namespace | What it does | Depends on |
| ------- | --------- | ------------ | ---------- |
| **ArtificialNecessity.Audio** | `AN.Audio` | PCM output through WASAPI (Windows), AudioQueue (macOS), ALSA (Linux). One callback fills the device buffer; device enumeration, default-follow and hot-switch recovery included. | `.Audio.Common` |
| **ArtificialNecessity.Audio.Midi** | `AN.Audio.Midi` | MIDI input via WinMM `midiIn*` (macOS/Linux planned). Opens every port, merges them into one lock-free ring, hot-plug aware. MIDI 2.0-ready message contract. | nothing |
| **ArtificialNecessity.Audio.Formats** | `AN.Audio.Formats` | Decodes **WAV** (PCM 8/16/24/32, float 32/64, EXTENSIBLE, `smpl`/`cue`/`LIST` metadata) and **FLAC** (with seeking, tags, MD5 verify) from any `Stream`, including forward-only network streams. MP3 next. 100 % managed. | `.Audio.Common` |
| **ArtificialNecessity.Audio.Common** | `AN.Audio` | The shared PCM vocabulary the packages above speak: `AudioFormat`, `SampleFormat`, `AudioChannelMask`, `AudioBufferView`, `AudioSampleConvert`. Pulled in transitively; reference it directly only if you need the types without the rest. | nothing |

All packages target `net8.0`, `net9.0` and `net10.0` and share one version number per release.

## Installation

```xml
<PackageReference Include="ArtificialNecessity.Audio"         Version="*" />   <!-- playback -->
<PackageReference Include="ArtificialNecessity.Audio.Midi"    Version="*" />   <!-- MIDI input -->
<PackageReference Include="ArtificialNecessity.Audio.Formats" Version="*" />   <!-- WAV / FLAC decoding -->
```

Reference only what you use — the packages are independent.

## ArtificialNecessity.Audio — PCM output

```csharp
using AN.Audio;

var format = new AudioFormat(SampleRate: 48000, Channels: 2, Format: SampleFormat.Float32);
using var output = AudioOutput.Create(format, bufferSizeMs: 20);

output.Start((Span<byte> buffer, int frameCount, AudioFormat fmt) =>
{
    // Write interleaved PCM into `buffer`; return the frames actually written (the rest is silence).
    return frameCount;
});

// ... later
output.Stop();
```

- The callback is the **only** extension point. It runs on a dedicated high-priority thread: no allocations, no locks, no I/O.
- `IAudioOutput.Format` is the format you asked for and never changes; the library converts to the device format internally (sample format, channel count, and sample rate through a windowed-sinc resampler).
- Device outputs accept `SampleFormat.Int16` and `SampleFormat.Float32`.
- `AudioOutput.Create(format, new AudioOutputOptions { ... })` selects a device and a switch policy (`FollowDefault` by default). `AudioOutput.GetDeviceManager()` enumerates devices and raises `DeviceListChanged`.
- `DeviceFormatChanged`, `DeviceLost` and `DeviceSwitched` fire on a background thread — marshal to your UI yourself. `Stop()` blocks until the audio thread is quiescent; call it from a control thread, never from the callback.

| Platform | Backend | Status |
| -------- | ------- | ------ |
| Windows | WASAPI shared mode, event-driven | ✅ |
| macOS | AudioQueue (AudioToolbox) | ✅ |
| Linux | ALSA (`libasound.so.2`) | ✅ |
| Android / iOS | AAudio / AudioQueue | planned |

## ArtificialNecessity.Audio.Midi — MIDI input

```csharp
using AN.Audio.Midi;

using var midi = MidiInput.Create();          // all ports, hot-plug polled, identity request on open
midi.DeviceOpened += d => Console.WriteLine($"MIDI: {d.Name} type={d.TypeId}");
midi.Start();

// In your audio callback (exactly one consumer thread), drain BEFORE rendering the block:
while (midi.Ring.TryDequeue(out var m))
{
    if (m.IsNoteOn)       synth.NoteOn(m.Note.Number, m.VelocityNormalized);
    else if (m.IsNoteOff) synth.NoteOff(m.Note.Number);
}
```

- `MidiInput_Message` is a 32-byte blittable struct exposing **typed accessors only** in two tiers: native 7/14-bit (`Velocity7`, `ControllerValue7`, `PitchBend14`) and protocol-neutral 16/32-bit (`Velocity16`, `ControllerValue32`, `PitchBend32`). Write against the wide tier and a future UMP / MIDI 2.0 backend changes nothing in your code; `m.Protocol` says which tier is native.
- Wire data is never rewritten: a velocity-0 note-on stays a note-on (`IsNoteOff` folds it for you).
- `MidiInput_MessageRing` is a growable lock-free SPSC queue (driver thread → your thread) with `DroppedCount` / `LagCount` / `GrowCount` telemetry.
- Persist `MidiInput_DeviceInfo.Key`, never the display name — operating systems rename ports.
- Control-plane events (`DeviceOpened`, `DeviceLost`, `IdentityResolved`, `SysExReceived`, `Overflow`) fire on a background thread. Never call `Stop()`/`Dispose()` from inside the raw callback.

| Platform | Backend | Status |
| -------- | ------- | ------ |
| Windows | WinMM `midiIn*` (legacy stack and Windows MIDI Services) | ✅ input; identity request is the only output |
| macOS / Linux | CoreMIDI / ALSA seq | planned |

## ArtificialNecessity.Audio.Formats — WAV / FLAC decoding

Two tiers. The generic facade sniffs the container **by content** (never by extension) and hands back interleaved `float` frames:

```csharp
using AN.Audio.Formats;

// Everything at once (convenience over the streaming path)
AudioDecoder_Pcm pcm = AudioDecoder.DecodeAll("clap-808.wav");
// pcm.Info.SampleRate / Channels / SourceBitDepth / Container, pcm.Interleaved (float[]), pcm.FrameCount

// Streaming — works on ANY Stream, including forward-only, non-seekable, unknown-length (HTTP, zip entry, pipe)
using IAudioDecoder dec = AudioDecoder.Open(httpResponseStream);
var buffer = new float[4096 * dec.Info.Channels];
int frames;
while ((frames = dec.ReadFrames(buffer)) > 0)
    Mix(buffer.AsSpan(0, frames * dec.Info.Channels));
if (dec.EndedEarly) Console.WriteLine("stream ended before its declared length — played what existed");
```

The per-format tier exposes everything the format knows:

```csharp
using AN.Audio.Formats.Wav;
using AN.Audio.Formats.Flac;

using var wav = new Wav_Decoder(File.OpenRead("piano_C4.wav"));
var rootNote = wav.Sampler?.MidiUnityNote;            // smpl chunk
var loops    = wav.Sampler?.Loops;                    // Wav_SampleLoop[] { Start, End, Type, PlayCount }
var title    = wav.InfoTags?.Title;                   // LIST/INFO
var mask     = wav.Format.ChannelMask;                // AudioChannelMask from WAVE_FORMAT_EXTENSIBLE

using var flac = new Flac_Decoder(File.OpenRead("A0v3.flac"), new Flac_DecoderOptions { VerifyMd5 = true });
var artist = flac.Tags?.Artist;                        // VORBIS_COMMENT, case-insensitive multimap
flac.SeekToFrame(48000 * 30);                         // SEEKTABLE or frame-header search
```

**Zero-copy path.** Every decoder reports `NativeFormat` — the layout its bitstream yields without conversion (WAV 16-bit → `Int16`, WAV 24-bit → `Int24`, FLAC → `Int32` with samples sign-extended in the low bits, `Info.SourceBitDepth` telling you how many are significant). `ReadFramesNative(Span<byte>)` writes that layout straight into your buffer. A player whose `IAudioOutput.Format` equals `NativeFormat` decodes directly into the audio callback span; otherwise convert once with `AudioSampleConvert`.

**Output contract.** Interleaved `float32`, nominal range [-1, 1], **not clipped**, source channel count, source sample rate. Integer PCM converts by `/ 2^(bits-1)`. No up/down-mixing, resampling, normalisation or dither — those are the consumer's decisions.

**Tolerant where writers are wrong.** RIFF size 0 / 0xFFFFFFFF / oversize → unknown length; odd chunk with a missing pad byte → accepted; `data` that overruns the stream → clamped and `EndedEarly`; `fmt` after `data` → handled on seekable streams; unknown chunks recorded in `Wav_Decoder.Chunks`. A truncated file returns the frames it has and sets `EndedEarly` instead of throwing.

**Errors.** Malformed input → `AudioDecoder_FormatException` (an `IOException`). Valid-but-unsupported input (WAV ADPCM, A-law/µ-law, RF64, Ogg…) → `AudioDecoder_UnsupportedException` (a `NotSupportedException`) naming the encoding.

**Read-path allocation.** `ReadFrames` / `ReadFramesNative` allocate nothing in steady state (verified by tests). Decoders are meant for worker threads, not the audio callback.

| Format | Status | Notes |
| ------ | ------ | ----- |
| WAV / RIFF | ✅ | PCM 8 (unsigned) / 16 / 24 / 32, IEEE float 32 / 64, `WAVE_FORMAT_EXTENSIBLE` (valid bits, channel mask), any channel count; `smpl`, `cue `, `LIST/INFO`, `inst` |
| FLAC | ✅ | 8–32 bit, all channel layouts, STREAMINFO / VORBIS_COMMENT / SEEKTABLE, MD5 verification, seeking |
| MP3 | planned | via NLayer (managed) |
| AIFF, RF64, A-law/µ-law, Ogg Vorbis/Opus | later | |

## ArtificialNecessity.Audio.Common — shared PCM types

```csharp
using AN.Audio;

public enum SampleFormat { UInt8, Int16, Int24, Int32, Float32, Float64 }
public record struct AudioFormat(int SampleRate, int Channels, SampleFormat Format) { BytesPerSample, BytesPerFrame, BitsPerSample }
[Flags] public enum AudioChannelMask : uint { FrontLeft = 0x1, FrontRight = 0x2, ... }   // SPEAKER_* bits from ksmedia.h
public readonly ref struct AudioBufferView(Span<byte> Bytes, AudioFormat Format) { FrameCount, AsInt16(), AsFloat32(), ... }
public static class AudioSampleConvert { Convert(src, from, dst, to); ToFloat32(...); ToInt16(...); FloatToInt16(v); ... }
```

`AudioSampleConvert` is the one place the PCM scaling rules live: integer → float is `/ 2^(bits-1)`; float → integer is `* 2^(bits-1)`, rounded and **saturated** (+1.0 becomes the maximum, never wraps). Decoder output and device input therefore agree bit-for-bit.

## Design principles

- **Zero native dependencies** — the audio APIs are part of the OS; the decoders are pure managed code.
- **One API shape everywhere** — platform backends implement the interface and never leak platform types.
- **One hot path, allocation-free** — a single callback or ring carries the real-time data; everything else is control plane on background threads.
- **Event-driven, never polled** for data.
- **Never editorialise** — wire data and audio samples are delivered as they are; policy belongs to the consumer.

## License

Apache License, Version 2.0 — see [LICENSE](https://github.com/ArtificialNecessity/AN_Audio/blob/main/LICENSE.txt).
`ArtificialNecessity.Audio.Formats` contains a FLAC bitstream decoder derived from [jdpurcell/SimpleFlac](https://github.com/jdpurcell/SimpleFlac) (MIT); its notice ships in the package as `LICENSE-SimpleFlac.txt`.

## Source

[github.com/ArtificialNecessity/AN_Audio](https://github.com/ArtificialNecessity/AN_Audio)