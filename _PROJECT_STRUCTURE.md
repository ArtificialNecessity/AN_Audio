# AN.Audio — Project Structure

## Overview

**ArtificialNecessity.Audio** is a family of small, independent .NET packages giving low-level, allocation-free access to the
operating system's audio services — PCM output, MIDI input (capture and MIDI output planned) — through ONE API shape on every
platform, plus a sibling **pure-managed format-decoding** package (WAV, FLAC; MP3 next). Everything is `AnyCPU` MSIL: direct
PInvoke / manual COM vtables into libraries the OS already ships, never a bundled native binary. Apache 2.0.

Consumers: MusicStudio (`C:\PROJECTS\AN_MusicStudio` — synth output, Sampler rows, MIDI), Mirica (UI/TTS playback), Arcane Siege (SFX/music).

Living design document: `_SPECS/00_AN_Audio_Overview.md` (goal, the five invariants I1–I5, feature/platform matrix, coding rules).

## Sibling Repositories — `C:\PROJECTS` Naming Convention

Repos alongside this one (`C:\PROJECTS\AN_Audio`) follow a strict prefix convention:

**`3P_*` = third-party UNADULTERATED sources.** A tracking checkout of an external project's mainline, never a divergent fork.
Used here purely as **audit and reference material** — the code we ship is either ours or explicitly subsumed (see D6 below).

- `3P_NLayer/` — `naudio/NLayer` (MIT). Managed MPEG-1/2 layer I–III decoder; the Phase 3 MP3 dependency (NuGet `NLayer` 3.0.0).
  Audited 2026-09-09 @ `046c7ce`: no `unsafe`, no P/Invoke, zero package deps, TFMs `netstandard2.0;net8.0`.
- `3P_NVorbis/` — `NVorbis/NVorbis` (MIT). Managed Ogg Vorbis decoder; Phase 4 candidate. Audited @ `abd594e`: no `unsafe`, no P/Invoke.
- `3P_Concentus/` — `lostromb/concentus` (BSD-3). Managed Opus codec; Phase 4 candidate. Audited @ `3885c4e`: the managed codec is
  clean but the shipped DLL has `AllowUnsafeBlocks=True`, a `Native/` P/Invoke layer and a factory that probes for libopus at
  runtime — see **Dependency policy** below for the rule and the tracked cleanup item.
- `3P_SalamanderGrandPiano/` — the 641-file 24-bit FLAC piano sample set (NOT redistributable; local-only tests read `Samples/A0v3.flac`).

**`AN_*` = ours.** `AN_Audio` is 100 % our code except one subsumed file: `src/AN.Audio.Formats/Flac/Flac_ReferenceDecoder.cs`
(jdpurcell/SimpleFlac @ `dc149aa`, MIT, dead upstream — subsumed per spec 50 D6, every change marked `// AN:`, licence kept beside it).

When auditing or citing `3P_*` source, record findings WITH the upstream commit hash — the checkout tracks a moving mainline.

## Tech Stack

- **Language:** C# (`LangVersion=preview`), TFMs `net8.0;net9.0;net10.0` for libraries, `net10.0` for tests. SDK 10.0.x.
- **OS APIs (direct PInvoke / COM vtables, no wrappers):** Windows WASAPI + MMDevice (`AN.Audio`), WinMM `midiIn*` (`AN.Audio.Midi`);
  macOS AudioQueue/AudioToolbox; Linux ALSA (`libasound.so.2`). Ground truth for each in `_EXTERNAL_APIS/`.
- **Formats (pure managed, no OS API):** our WAV (incl. RF64/W64, G.711); subsumed SimpleFlac for FLAC; NLayer 3.0.0's `MpegFrameDecoder` for MP3 frames (framing ours).
- **Analyzers:** `ArtificialNecessity.CodeAnalyzers` (e.g. AN0002: public `const` → `static readonly`) via `AN.Audio.Build.props`.
- **Tests:** xunit 2.x; `[Trait("Category","Local")]` marks tests that read non-redistributable files and skip when absent.
- **Tools present on the dev box:** `ffmpeg` (chocolatey) — renders test fixtures; command lines recorded in `tests/AN.Audio.Formats.Tests/Fixtures/README.md`.

## Solution Structure

```
AN.Audio.slnx                                 — Solution (slnx format)
├── src/AN.Audio.Common/                      — Shared PCM vocabulary ONLY (AudioFormat, SampleFormat, AudioChannelMask, AudioBufferView, AudioSampleConvert)
├── src/AN.Audio/                             — PCM output + output-device management (WASAPI / AudioQueue / ALSA)
├── src/AN.Audio.Midi/                        — MIDI input (WinMM), MIDI 2.0-ready message contract; NO dependency on AN.Audio or Common
├── src/AN.Audio.Formats/                     — Format decoding: IAudioDecoder facade, Wav/, Flac/, (Mp3/ Phase 3); depends only on Common
├── tests/AN.Audio.Common.Tests/              — xunit: sample conversion, buffer view, channel mask
├── tests/AN.Audio.Tests/                     — xunit: sinc resampler
├── tests/AN.Audio.Midi.Tests/                — xunit: message decode, ring, SysEx, interop struct layout
├── tests/AN.Audio.Formats.Tests/             — xunit: sniff, PeekableStream, WAV (synthesised), FLAC (fixtures), Local files
├── tests/SimpleAudioTest/                    — console smoke: plays a WAV through the real device (manual)
├── tests/SimpleMidiTest/                     — console smoke: real MIDI ports, hot-plug, identity (manual; cmd/test-midi.cmd)
├── cmd/                                      — publish-local.cs / nuget-publish-audio.cs (+ .cmd runners), test-midi.cmd
├── AN.Audio.Build.props                      — imported by EVERY csproj: timestamp versioning v2, analyzers, artifacts/ paths, LocalNuGet deploy target
├── AssetSource/cartesia_tts_test.wav         — our WAV master for fixtures
├── _SPECS/                                   — design specs (numbered feature areas) + IMPL checklists
└── _EXTERNAL_APIS/                           — ground truth read from SDK headers / vendor sources, one file per API
```

Packages (one per library project, all `IsPackable`, one shared timestamp version per publish):
`ArtificialNecessity.Audio.Common`, `ArtificialNecessity.Audio`, `ArtificialNecessity.Audio.Midi`, `ArtificialNecessity.Audio.Formats`.
**Packaging rule (spec 30 D27 as amended by spec 50 D15):** no umbrella project; no inter-project references EXCEPT to `AN.Audio.Common`,
and only from projects that need a PCM type (Audio, Formats — not Midi).

### Subproject Descriptions

**Libraries:**
- `src/AN.Audio.Common/` (`ArtificialNecessity.Audio.Common`, namespace `AN.Audio`, `AllowUnsafeBlocks=false`) — the minimum shared
  vocabulary so decoder output can land in an `IAudioOutput` callback buffer with zero copies when formats agree (spec 50 D15/D16).
  - `AudioFormat.cs` — `record struct AudioFormat(SampleRate, Channels, SampleFormat)`; `BytesPerSample/BytesPerFrame/BitsPerSample`.
    `enum SampleFormat { UInt8, Int16, Int24, Int32, Float32, Float64 }` (device backends accept only Int16/Float32).
  - `AudioChannelMask.cs` — `[Flags] uint`, `SPEAKER_*` bits from `ksmedia.h` + `KSAUDIO_SPEAKER_*` layouts; `PositionCount()`.
  - `AudioBufferView.cs` — `readonly ref struct` (bytes + format, whole-frame validated; `AsInt16/AsFloat32/AsInt32/AsFloat64`, `SliceFrames`).
  - `AudioSampleConvert.cs` — **the ONE place PCM scaling lives**: int→float `/ 2^(bits-1)`, float→int symmetric `* 2^(bits-1)` with
    saturation (never wraps), 6×6 `Convert`, `ToFloat32/ToInt16/…` span helpers, scalar helpers incl. `ReadInt24/WriteInt24`.
- `src/AN.Audio/` (`ArtificialNecessity.Audio`, namespace `AN.Audio`) — PCM output.
  - `IAudioOutput.cs` — interface + `AudioCallback(Span<byte>, int frames, AudioFormat)` — the ONLY hot path (I3).
  - `AudioOutput.cs` — platform-detecting factory (`Create`, `IsAvailable`, `GetDeviceManager`).
  - `AudioOutputOptions.cs`, `AudioSwitchPolicy.cs`, `AudioDeviceInfo.cs`, `DeviceChangeType.cs`, `DeviceLostReason.cs`, `IAudioDeviceManager.cs` — device policy/events (spec 20).
  - `Internal/AudioFormatConverter.cs` — consumer↔device format bridge (rate via `SincResampler`, channel map, Int16↔Float32 via `AudioSampleConvert`; rejects other formats).
  - `Internal/SincResampler.cs` — windowed-sinc resampler (spec 01). STAYS here: it is device-side policy.
  - `Platforms/Windows/` — `WasapiAudioOutput.cs`, `WasapiDeviceManager.cs`, `WasapiInterop.cs` (COM vtables + PInvoke).
  - `Platforms/MacOS/` — `CoreAudioOutput.cs`, `CoreAudioDeviceManager.cs`, `AudioToolboxInterop.cs`, `CoreAudioInterop.cs`.
  - `Platforms/Linux/` — `AlsaAudioOutput.cs`, `AlsaDeviceManager.cs`, `AlsaInterop.cs`.
- `src/AN.Audio.Midi/` (`ArtificialNecessity.Audio.Midi`, namespace `AN.Audio.Midi`) — MIDI input, independent of the other packages (spec 30 D24).
  - `IMidiInput.cs`, `MidiInput.cs`, `MidiInput_Options.cs`, `MidiInput_Devices.cs` — factory, options, device manager (1 s poll on WinMM).
  - `MidiInput_Message.cs` — 32-byte blittable UMP-word message; typed accessors in two tiers (7/14-bit native, 16/32-bit protocol-neutral).
  - `MidiInput_MessageRing.cs` — growable lock-free SPSC ring (driver thread → consumer's audio thread).
  - `Midi_Wire.cs` (wire vocabulary enums), `Midi_RelativeDecode.cs` (stateless encoder-delta helpers).
  - `Internal/` — `Midi_BitScaling.cs`, `Midi_IdentityReplyParser.cs`, `Midi_SysExReassembler.cs`.
  - `Platforms/Windows/` — `WinMm_MidiInterop.cs`, `WinMm_MidiInPort.cs`, `WinMm_MidiInput.cs`, `WinMm_MidiInEnumerator.cs`, `WinMm_MidiInDeviceManager.cs`, `WinMm_MidiOutLongSender.cs` (identity request only).
- `src/AN.Audio.Formats/` (`ArtificialNecessity.Audio.Formats`, namespace `AN.Audio.Formats`, `AllowUnsafeBlocks=false`) — spec 50.
  Two tiers: per-format public types that expose everything the format knows, and ONE generic `IAudioDecoder` facade for "just give me floats".
  Streaming-first: every decoder works on a forward-only, non-seekable, unknown-length `Stream`; `DecodeAll` is a loop over `ReadFrames`.
  - `IAudioDecoder.cs` — `Info`, `NativeFormat` (zero-copy layout, D16), `FramesRead`, `EndedEarly`, `ReadFrames(Span<float>)`, `ReadFramesNative(Span<byte>|AudioBufferView)`, `SeekToFrame`.
  - `AudioDecoder.cs` — `Open(Stream|path)`, `Sniff` (content only; hint is a tiebreaker), `DecodeAll(...)`, `HintFromExtension`.
  - `AudioDecoder_Enums.cs`, `AudioDecoder_StreamInfo.cs`, `AudioDecoder_Pcm.cs`, `AudioDecoder_Exceptions.cs` (`_FormatException : IOException`, `_UnsupportedException : NotSupportedException`).
  - `AudioDecoder_Picture.cs` — `AudioDecoder_PictureType` (0–20, the numbering ID3 APIC and FLAC PICTURE share), `AudioDecoder_PictureInfo`, `AudioDecoder_PictureCallback(in info, ReadOnlySpan<byte>)`: pictures travel by callback, never by retention.
  - `Internal/PeekableStream.cs` — bounded look-ahead + replay over any stream (why non-seekable sources work); `Internal/BitReader.cs` (LE/BE header reads); `Internal/MpegFrameHeader.cs` (full MPEG header parser, `MpegFrameHeader_*` enums); `Internal/MpegXingHeader.cs` (Xing/Info/LAME/VBRI); `Internal/G711.cs` (A-law/µ-law → Int16 tables).
  - `Wav/` — `Wav_Decoder.cs` (tolerant chunk walk: RIFF size 0/0xFFFFFFFF/oversize, odd chunk without pad byte, clamped `data` + `EndedEarly`, `fmt` after `data` when seekable, lazy trailing chunks on forward-only streams; three layouts `Wav_ContainerLayout { Riff, Rf64, Wave64 }` with `ds64`/GUID headers; G.711 expansion to Int16),
    `Wav_FormatChunk.cs` (`Wav_FormatTag`, EXTENSIBLE → `EffectiveTag`), `Wav_ChunkId.cs` (`Wav_ChunkId` FourCC + W64 GUID tails, `Wav_ChunkInfo`, `Wav_DataSize64Chunk`), `Wav_Metadata.cs` (`Wav_SamplerChunk`, `Wav_SampleLoop`, `Wav_CuePoint`, `Wav_InstrumentChunk`, `Wav_InfoTags`).
  - `Flac/` — `Flac_ReferenceDecoder.cs` (subsumed SimpleFlac bitstream decoder, `internal`, `// AN:` adaptations: buffered re-seatable bit reader, metadata parsing, CRC-8 check, span output), `LICENSE-SimpleFlac.txt` (packed),
    `Flac_Decoder.cs` (`IAudioDecoder`; `NativeFormat = Int32` low-justified; MD5 verify option; seek via SEEKTABLE or CRC-8-validated frame-header binary search), `Flac_MetadataTypes.cs` (`Flac_StreamInfo`, `Flac_Tags`, `Flac_SeekTable`, `Flac_DecoderOptions`, …).
  - `Mp3/` — Phase 3. **NLayer 3.0.0 is the frame decoder only (`MpegFrameDecoder`); the framing is ours** (IMPL A13 explains why `MpegFile` was abandoned: one-frame seek re-prime that silently swallows reservoir-starved frames, gapless only for `LAME…` strings, raw/trimmed position flip).
    `Mp3_FrameReader.cs` (`Mp3_Frame : IMpegFrame` reused per frame; sync/resync with second-header confirmation, ID3 tags anywhere skipped, header-only mode for the seek index, `EndedShort`),
    `Mp3_Decoder.cs` (`NativeFormat = Float32`; gapless trim with the LAME/ffmpeg rule delay+529 / padding−529 so the output is time-aligned and exactly the source length; frame-offset index + pre-roll seek bit-identical to linear; `TotalFrames` from Xing/VBRI or a header scan when seekable; `CorruptFrames`; `Mp3_DecoderOptions { OnPicture, ReadId3v1 }`),
    `Mp3_Id3v2.cs` (ID3v2.2/2.3/2.4 + ID3v1 → `Mp3_Id3Tags`; APIC/PIC to the callback or skipped, `PictureCount` always), `Mp3_StreamInfo.cs` (`Mp3_StreamInfo`, `Mp3_GaplessInfo`, `Mp3_MpegVersion/Layer/ChannelMode`).

**Tests:**
- `tests/AN.Audio.Common.Tests/` — `AudioFormatTests.cs`, `AudioSampleConvertTests.cs` (exact round-trips, saturation, Int24 sign extension, UInt8 bias).
- `tests/AN.Audio.Tests/` — `SincResamplerTests.cs`, `SincResamplerDiagnosticTests.cs`.
- `tests/AN.Audio.Midi.Tests/` — message decode theory, ring (incl. two-thread + zero-alloc), SysEx/identity, `WinMm_InteropLayoutTests` (struct sizes vs SDK).
- `tests/AN.Audio.Formats.Tests/` — `SniffTests`, `PeekableStreamTests`, `WavDecoderTests` (every case seekable AND through `Support/ForwardOnlyStream` with 1–97-byte reads), `WavG711Tests`, `WavRf64W64Tests`,
  `FlacDecoderTests` (bit-exact vs WAV master, MD5, tags, truncation, seek), `Mp3DecoderTests` (exact length vs master, time alignment, tags, picture callback, seek == linear, D13), `LocalFileTests` (`Category=Local`: `clap-808.wav`, Salamander `A0v3.flac`).
  `Support/Wav_TestWriter.cs` synthesises every WAV shape in memory (RIFF, RF64/BW64, Wave64); `Support/Mp3_TestTagWriter.cs` synthesises ID3v2 tags; `Support/FlacFixtureTools.cs` splices SEEKTABLEs / zeroes totals; `Fixtures/` holds ffmpeg-rendered FLAC/MP3/WAV + `README.md` provenance.
- `tests/SimpleAudioTest/`, `tests/SimpleMidiTest/` — manual console smoke tests against real hardware.

## Dependency policy — managed only, no unsafe code paths

Invariant I1 (zero native dependencies) extends to third-party packages: **`AN.Audio.Formats` must never execute an `unsafe` or
P/Invoke code path, its own or a dependency's.** Our own Common/Formats/Formats.Tests projects build with `AllowUnsafeBlocks=false`
(`AN.Audio`/`AN.Audio.Midi` use `unsafe` only for their OS interop, which is their whole purpose).

Audit of the Phase 3/4 dependencies (2026-09-09, sources in `3P_*`; details in `_SPECS/50_Audio_Formats.md` §Dependency audit):

| Package | Status |
|---|---|
| NLayer 3.0.0 | ✅ 100 % managed, no unsafe, no P/Invoke, no deps |
| NVorbis | ✅ 100 % managed, no unsafe, no P/Invoke |
| Concentus 2.2.2 | ⚠️ managed codec is clean, but the DLL is built `AllowUnsafeBlocks=True`, contains a `Native/` P/Invoke layer, and `OpusCodecFactory` probes for libopus at runtime by default |

**Concentus rule (if/when Phase 4 Opus lands):** never call `OpusCodecFactory`; construct `Concentus.Structs.OpusDecoder` /
`OpusMSDecoder` directly and set `OpusCodecFactory.AttemptToUseNativeLibrary = false`; a test asserts no `Native*` type is ever used.

**🧹 Eventual cleanup (tracked):** that still ships unsafe IL and P/Invoke stubs we never execute. To get a provably unsafe-free,
native-free DLL, subsume the managed subset as source like SimpleFlac (`Concentus/{Opus,Celt,Silk,Common,Enums}` minus `Native/`,
BSD-3, ≈90 files, built `AllowUnsafeBlocks=false`) or maintain a package build with `Native/` excluded. Do this when Opus is picked
up or when a consumer needs an auditable no-unsafe dependency tree, whichever comes first.

## Specs: _SPECS/

Numbered by feature area; a `*_IMPL.md` beside a design spec is its build checklist (design spec wins on conflict).

- `00_AN_Audio_Overview.md` — **Living document.** Goal, invariants I1–I5, feature/platform matrix, repository shape, packaging rule, coding rules, non-goals.
- `01_SincResampler_Upgrade.md` — windowed-sinc resampler design and quality targets.
- `10_Audio_Bringup.md` — PCM output bring-up (WASAPI / AudioQueue / ALSA), callback contract.
- `20_Audio_Device_Management.md` — device enumeration, default-follow/preferred policies, loss/switch events.
- `30_MidiInput.md` — MIDI input: WinMM backend, MIDI 2.0-ready message contract (D25), growable ring (D23), packaging D27.
- `50_Audio_Formats.md` — **Key spec for decoding:** D1–D16, `AN.Audio.Common` (D15), zero-copy (D16), §WAV, §FLAC, §MP3, dependency audit, phases.
- `50_Audio_Formats_IMPL.md` — the Phase 0–4 checklist, "As built" deviation table A1–A11, handoff notes (e.g. `clap-808.wav` is 24-bit mono).
- `60_Low_Latency_Output.md` — **DRAFT.** `AudioOutputOptions.Latency = LowLatency` on all three platforms: Windows `IAudioClient3::InitializeSharedAudioStream` at the engine minimum period + MMCSS "Pro Audio" (Phase A, in progress); Linux explicit `hw_params` period + `SCHED_FIFO` (+ rtkit later); macOS AUHAL render callback replacing AudioQueue; new `IAudioOutput.PeriodFrames / LatencyModeActual / LatencyFallbackReason / UnderrunCount`. Driven by MusicStudio's measured key→sound budget (its spec Bringup/18).

## External API notes: _EXTERNAL_APIS/

One file per API, quoting the header/source actually read, with the version/commit: `WinMM_MidiIn.md`, `UMP_MIDI2_Format.md`,
`WaveFormatExtensible_ChannelMask.md` (`SPEAKER_*`, `KSDATAFORMAT_SUBTYPE_*`), `SimpleFlac_FlacDecoder.md`, `NLayer_MpegFile.md`, `WASAPI_IAudioClient3_MMCSS.md` (IAudioClient2/3 vtable, IIDs, `AUDCLNT_E_*`, `avrt.dll`).
Rule 2 of the overview: read the SDK header, not the blog post.

## Architecture Patterns

- **One API shape per feature area:** `IAudioOutput` / `IMidiInput` / `IAudioDecoder` + a static factory; platform backends never leak platform types.
- **One hot path, allocation-free:** `AudioCallback` and the MIDI ring are the only real-time carriers; no `new`, `lock`, `string`, exceptions or I/O there. Tests measure `GC.GetAllocatedBytesForCurrentThread()` deltas of zero (note: xunit's `Assert.Equal<T>` allocates — count in the loop, assert after).
- **Event-driven, never polled** for data; 1 s background poll only where the OS offers no notification (WinMM hot-plug).
- **Control plane on background threads; the consumer marshals** (`DeviceLost`, `DeviceSwitched`, `DeviceOpened`, …).
- **Every native constant is an enum** in the platform's `*Interop.cs`; layout tests assert struct sizes/values against the SDK.
- **Linguistic keying:** scope-prefixed public types (`MidiInput_Message`, `Wav_FormatTag`, `AudioDecoder_StreamInfo`), branded ids (`MidiInput_DeviceKey`, `Wav_ChunkId`), no bare `int`/`string` in public data.
- **Never rewrite wire data / never editorialise:** velocity-0 note-on stays a note-on; decoders output source rate/channels, unclipped float — mixing, resampling and normalisation belong to the consumer.
- **Streaming-first decoding:** forward-only streams are the design case; `PeekableStream` gives bounded look-ahead; `TotalFrames` is `long?` (unknown is first-class); truncation → `EndedEarly`, not an exception.
- **Zero-copy contract (D16):** `NativeFormat` + `ReadFramesNative` write the bitstream's own layout; a player whose device format matches decodes straight into the callback span.
- **Subsume, don't submodule** dead-upstream single-file dependencies (D6): provenance header, licence kept verbatim, every change marked `// AN:`.

## Building, Testing, Publishing

```powershell
dotnet build AN.Audio.slnx
dotnet test  AN.Audio.slnx                                   # all four xunit projects (Local-category tests skip when files are absent)
dotnet test tests/AN.Audio.Formats.Tests --filter Category=Local

$env:LOCAL_NUGET_REPO = "C:\PROJECTS\LocalNuGet"
cmd\publish-local.cmd            # build + pack ALL packages with one timestamp version, deploy to the local feed
cmd\nuget-publish-audio.cmd      # build + pack + push to NuGet.org
cmd\test-midi.cmd                # interactive MIDI smoke test with real hardware
```

Versioning is timestamp-based (`AN.Audio.Build.props` v2): `{major}.{YYMMDD}.{HHmmss}` package versions, one stamp per publish so every
DLL/nupkg in a publish agrees. Consumers pin `ANAudioVersion` in their own build props (MusicStudio: `0.260908.235755` as of 2026-09-09).

Definition of done per spec phase: `dotnet build` clean, `dotnet test` green for ALL test projects, `publish-local` succeeds, spec
checkbox ticked, commit (multi-line messages via here-string piped to `git commit -F -`; `git mv` for tracked files).

## Status snapshot (2026-09-09, after spec 50 Phases 3 / 4a / 4c)

| Area | State |
|---|---|
| PCM output | ✅ Windows WASAPI, ✅ macOS AudioQueue, ✅ Linux ALSA |
| Output device mgmt | ✅ all three |
| MIDI input | ✅ Windows WinMM (hardware-validated); macOS/Linux planned |
| Formats | ✅ WAV incl. A-law/µ-law, RF64/BW64, Wave64 (Phases 1, 4a, 4c), ✅ FLAC incl. seeking (Phase 2), ✅ MP3 with exact seek + picture callback (Phase 3, NLayer 3.0.0 as frame decoder only), ◻ AIFF / Ogg (Phase 4) |
| Capture / MIDI output | ◻ planned, interfaces shaped |
| Tests | 301 (Common 21, Audio 8, Midi 111, Formats 161) |