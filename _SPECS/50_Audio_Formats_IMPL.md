# 50 IMPL — AN.Audio.Formats implementation plan & handoff

- **Design spec (authoritative):** `50_Audio_Formats.md` (v2, D1–D16). This file is the checklist; when they disagree, the design spec wins and this file gets fixed.
- **Written:** 2026-09-08 from a MusicStudio session (`C:\PROJECTS\AN_MusicStudio`) that diagnosed the WAV bug and did the research. Handed off to a session rooted at `C:\PROJECTS\AN_Audio` for LSP coverage of the three projects touched.
- **Ground truth on disk:** `_EXTERNAL_APIS/SimpleFlac_FlacDecoder.md`, `_EXTERNAL_APIS/NLayer_MpegFile.md`, `_EXTERNAL_APIS/WaveFormatExtensible_ChannelMask.md`, `src/AN.Audio.Formats/Flac/Flac_ReferenceDecoder.cs` (subsumed SimpleFlac @ `dc149aa`, adapted with `// AN:` markers) and `Flac/LICENSE-SimpleFlac.txt`.
- **Progress (2026-09-09):** Phases 0, 1a, 1b, 1c, 2 **built and committed** — `81d92b7` (1a), `9584806` (1b+1c), `f31655c` (2). Published to `C:\PROJECTS\LocalNuGet` as `0.260908.234621` (Phase 1) and `0.260908.235755` (Phase 2). 250 tests green (Common 21, Audio 8, Midi 111, Formats 110). **Pending:** Phase 1d (MusicStudio repo), Phase 3 (MP3), Phase 4.

## As built — deviations from the plan below (each agreed or forced; the design spec carries the same notes)

| # | Plan said | Built | Why |
|---|---|---|---|
| A1 | `AudioDecoder_FormatException : InvalidDataException` | `: IOException` | `InvalidDataException` is sealed in .NET. `IOException` is its base, so `catch (IOException)` still works. |
| A2 | Move `AN.Audio/Internal` sample conversion to Common with `git mv`, delete remainder | `AudioFormatConverter.cs` STAYS in `AN.Audio` (it owns resampler glue, channel mapping, ArrayPool = device-side); only its two per-sample helpers now delegate to `AudioSampleConvert.Int16ToFloat`/`FloatToInt16` and it throws `NotSupportedException` for any other `SampleFormat` | Only ~10 lines mapped 1:1; nothing to `git mv`. |
| A3 | float→Int16 write kept `* 32767` | Symmetric `/ 32768` and `* 32768` with saturation (user chose option A) | Exact Int16→Float32→Int16 round-trip; +1.0 saturates instead of wrapping. ≤ 1 LSB change on the device write path. |
| A4 | `Wav_ChannelMask` (§WAV wording) | `AudioChannelMask` in Common (D15) | The design spec contradicted itself; D15 wins. |
| A5 | Separate commits for 1b and 1c | One commit `9584806` | `AudioDecoder.Open` references `Wav_Decoder`; 1b alone would not compile. |
| A6 | `clap-808.wav` is 16-bit | 24-bit mono 44100 Hz (header bytes verified) | Handoff note was wrong. |
| A7 | `CopyFrameFloat(Span<float>, bits)` | `CopyFrameFloat(Span<float>)` — bits come from STREAMINFO | No caller needs a different depth. |
| A8 | `Flac_StreamInfo.cs`, `Flac_Tags.cs`, `Flac_SeekTable.cs`, `Flac_DecoderOptions.cs` | One file `Flac/Flac_MetadataTypes.cs` (+ `Flac_MetadataBlockType`, `Flac_SeekPoint`) | Small types; one file per concept group. |
| A9 | `Wav/Wav_ChunkIndex.cs` | `Wav/Wav_ChunkId.cs` (`Wav_ChunkId` + `Wav_ChunkInfo`) and `Wav/Wav_Metadata.cs` (`Wav_SamplerChunk`, `Wav_SampleLoop`, `Wav_CuePoint`, `Wav_InstrumentChunk`, `Wav_InfoTags`) | Naming follows the types inside. |
| A10 | "MD5 verifies" on Salamander | Salamander files carry an all-zero MD5 (= "not computed" per the FLAC spec); verification is skipped when the signature is zero, `Flac_Decoder.HasMd5` says so | Real-file finding. |
| A11 | (not planned) | Reference decoder's `BitReader` reads through a 64 KiB byte buffer, tracks absolute position, is re-seatable after a seek, and verifies the frame-header CRC-8; `Internal/MpegFrameHeader.cs` validates MPEG headers for sniffing | Needed for seeking and D8. |

Bugs found by the tests while building: seekable `data` overrun did not set `EndedEarly` (fixed); FLAC seek past the last frame left stale frame data deliverable (fixed); CRC-8 accumulator mishandled reads starting mid-byte (fixed).

## Handoff — what the implementing session must know

- **Trigger case:** `C:\Users\david\Documents\AudioSamples\Instruments\NoRedistribution\99Sounds_Drum_Samples\clap-808.wav` — trailing `id3 ` chunk, length 215 (odd), pad byte omitted by the writer; RIFF size is otherwise correct. Header (verified 2026-09-08): PCM **mono 44100 Hz 24-bit** (blockAlign 3), `data` 142080 bytes = 47360 frames — an earlier note saying 16-bit was wrong. Must decode. Second trigger: `C:\PROJECTS\3P_SalamanderGrandPiano\Samples\A0v3.flac` (24-bit stereo, 2.3 MB; 641 siblings). Neither file may be committed.
- **`AudioFormat` moves, it is not duplicated.** `git mv src/AN.Audio/AudioFormat.cs src/AN.Audio.Common/AudioFormat.cs`; namespace stays `AN.Audio`; consumers (Mirica, Arcane Siege, MusicStudio) recompile unchanged and receive `ArtificialNecessity.Audio.Common` transitively. `SampleFormat` widens to `UInt8, Int16, Int24, Int32, Float32, Float64`; the device backends in `AN.Audio` must still REJECT anything but `Int16`/`Float32` at `Start` (find every `switch` on `SampleFormat` — the `_ => throw` arms already do this; add explicit cases where a switch is exhaustive-by-accident).
- **Spec 30 D27 changes** ("no inter-project references") → "none EXCEPT to `AN.Audio.Common`". Edit `30_MidiInput.md` D27 and `00_AN_Audio_Overview.md` (feature table row, repository shape, non-goals wording, packaging rule) in Phase 1 — D14/D15.
- **Streaming is not optional.** Every decoder must pass its tests through the forward-only stream wrapper (no `Seek`, `Length` throws, 1–97-byte reads). Design for that first; `DecodeAll` is a loop over `ReadFrames`.
- **Zero-copy is a contract (D16):** `NativeFormat` + `ReadFramesNative(Span<byte>)` write the bitstream's own layout; WAV PCM is a straight byte copy from the stream to the caller's span. Float output goes through `AN.Audio.AudioSampleConvert` — the ONE place `/ 2^(bits-1)` lives.
- **Linguistic keying:** scope-prefixed public types (`Wav_FormatTag`, `Flac_Tags`, `AudioDecoder_StreamInfo`), every RIFF/FLAC/MPEG constant an `enum`, no bare `int`/`string` in public data structures.
- **Repo conventions:** csproj mirrors `src/AN.Audio.Midi/AN.Audio.Midi.csproj` (imports `AN.Audio.Build.props`, `net8.0;net9.0;net10.0`, `IsPackable`, README/LICENSE pack items, `InternalsVisibleTo` tests). `cmd/publish-local.cs` packs the whole solution → `C:\PROJECTS\LocalNuGet`. Use `git mv` for tracked files; multi-line commits via here-string piped to `git commit -F -`.
- **Definition of done per phase:** `dotnet build AN.Audio.slnx` clean, `dotnet test` green for ALL test projects (the existing `AN.Audio.Tests` and `AN.Audio.Midi.Tests` included), `cmd/publish-local` succeeds, spec checkbox ticked, commit.

## Phase 0 — read before typing

- [x] Read `50_Audio_Formats.md` fully (D1–D16, API blocks, §WAV, test plan)
- [x] Read `00_AN_Audio_Overview.md`, `30_MidiInput.md` §D27, `AN.Audio.Build.props`, `src/AN.Audio.Midi/AN.Audio.Midi.csproj`, `cmd/publish-local.cs`
- [x] Read `src/AN.Audio/AudioFormat.cs` and grep `SampleFormat` / `BytesPerSample` across `src/AN.Audio/` (incl. `Internal/`, `Platforms/`) to list every site the widened enum touches
- [x] Locate the existing sample-format conversion in `src/AN.Audio/Internal/` (candidate for `AudioSampleConvert`) and the sinc resampler (STAYS in `AN.Audio`)

## Phase 1a — `AN.Audio.Common` (D15)

- [x] `src/AN.Audio.Common/AN.Audio.Common.csproj` — PackageId `ArtificialNecessity.Audio.Common`, RootNamespace `AN.Audio`, no dependencies, `AllowUnsafeBlocks=false`; add to `AN.Audio.slnx`
- [x] `git mv src/AN.Audio/AudioFormat.cs src/AN.Audio.Common/AudioFormat.cs`; widen `SampleFormat`; `BytesPerSample` covers all six
- [x] `AudioChannelMask.cs` (`[Flags] uint`, `SPEAKER_*` order from `ksmedia.h` — record the header values in `_EXTERNAL_APIS/WaveFormatExtensible_ChannelMask.md`)
- [x] `AudioBufferView.cs` (`readonly ref struct`: `Span<byte> Bytes`, `AudioFormat Format`, `FrameCount`, `AsFloat32()`, `AsInt16()`, checked casts)
- [x] `AudioSampleConvert.cs` — `Convert(ReadOnlySpan<byte> src, AudioFormat from, Span<byte> dst, AudioFormat to)` for all 6×6 pairs (same rate/channels; throw otherwise) plus `ToFloat32/ToFloat64/ToInt16/ToInt32` span helpers and `FloatToInt16/…/ReadInt24/WriteInt24` scalars; `AudioFormatConverter` stays in `AN.Audio` and delegates its two per-sample helpers (A2)
- [x] `AN.Audio.csproj` gains `<ProjectReference Include="../AN.Audio.Common/AN.Audio.Common.csproj" />`; every `SampleFormat` switch in `AN.Audio` reviewed (reject non-Int16/Float32 at `Start`)
- [x] `tests/AN.Audio.Common.Tests/` — `AudioSampleConvert` round-trips (Int16→Float32→Int16 exact; Int24 sign extension; UInt8 bias; Float64 pass-through), `BytesPerFrame`, `AudioBufferView` guards
- [x] Existing `AN.Audio.Tests` + `AN.Audio.Midi.Tests` still green
- [x] Spec edits: `30_MidiInput.md` D27, `00_AN_Audio_Overview.md` (packaging rule, repository shape `src/AN.Audio.Common/`, feature table row for Formats, non-goals wording), `README.md` package list
- [x] Commit: `AN.Audio.Common: extract AudioFormat/SampleFormat, add AudioChannelMask/AudioBufferView/AudioSampleConvert (spec 50 D15)`

## Phase 1b — `AN.Audio.Formats` skeleton + generic tier

- [x] `src/AN.Audio.Formats/AN.Audio.Formats.csproj` — PackageId `ArtificialNecessity.Audio.Formats`, RootNamespace `AN.Audio.Formats`, `ProjectReference` → Common, NO NLayer yet; add to slnx; `InternalsVisibleTo AN.Audio.Formats.Tests`
- [x] `AudioDecoder_Enums.cs` (`AudioDecoder_Container`, `AudioDecoder_SourceEncoding`, `AudioDecoder_FormatHint`), `AudioDecoder_StreamInfo.cs`, `AudioDecoder_Exceptions.cs` (`AudioDecoder_FormatException : IOException` (the spec first said `InvalidDataException`, which is sealed in .NET; `IOException` is its base), `AudioDecoder_UnsupportedException : NotSupportedException`)
- [x] `IAudioDecoder.cs` exactly as the design spec block (`Info`, `NativeFormat`, `FramesRead`, `EndedEarly`, `ReadFrames(Span<float>)`, `ReadFramesNative(Span<byte>)`, `ReadFramesNative(AudioBufferView)`, `SeekToFrame`)
- [x] `Internal/PeekableStream.cs` — wraps any `Stream`; buffers the first N bytes for sniff + header, replays, then passes through; `CanSeek` mirrors inner; `Position` correct in both modes; `leaveOpen`
- [x] `Internal/BitReader.cs` — little/big-endian header reads over `PeekableStream` (WAV now, AIFF later)
- [x] `AudioDecoder.cs` — `Sniff(ReadOnlySpan<byte>, hint)` per the sniff table; `Open(Stream, hint, leaveOpen)`, `Open(string path)` (`FileStream` 64 KiB `SequentialScan`, hint from extension); `DecodeAll(...)` → `AudioDecoder_Pcm` (capacity hint = `TotalFrames`, growable otherwise)
- [x] `AudioDecoder_Pcm.cs` (`Info`, `float[] Interleaved`, `FrameCount`, `EndedEarly`)
- [x] `tests/AN.Audio.Formats.Tests/` project; `Support/ForwardOnlyStream.cs` (no seek, `Length` throws, random 1–97-byte reads, seeded); sniff tests (each magic, ID3-prefixed MP3 header bytes, garbage → Unsupported, hint tiebreak); `PeekableStream` tests (replay across boundary, seekable + forward-only)
- [x] Commit — landed together with 1c as `9584806` (A5); also `Internal/MpegFrameHeader.cs` (MPEG header validation for the sniff table)

## Phase 1c — WAV done right (§WAV of the design spec)

- [x] `Wav/Wav_FormatChunk.cs` — `Wav_FormatTag` enum (`Pcm=1, IeeeFloat=3, Alaw=6, Mulaw=7, Extensible=0xFFFE`, …), EXTENSIBLE parse (cbSize, `ValidBitsPerSample`, `ChannelMask` → `AudioChannelMask`, SubFormat GUID → `KSDATAFORMAT_SUBTYPE_PCM`/`_IEEE_FLOAT` as named `Guid` consts)
- [x] `Wav/Wav_ChunkId.cs` (A9) — `Wav_ChunkId` (branded FourCC, well-known statics) + `Wav_ChunkInfo` (offset, declared length, clamped length, `PadBytePresent`, `RawBytes` retained for non-data chunks ≤ 1 MiB) → `Wav_Decoder.Chunks`
- [x] `Wav/Wav_Decoder.cs` — chunk walk per §WAV: RIFF size 0/0xFFFFFFFF/oversize → unknown length; **odd chunk without pad byte accepted**; overrun `data` clamped + `EndedEarly`; `fmt` after `data` handled when seekable else `FormatException`; unknown chunks skipped+recorded; chunks after `data` read lazily after audio on forward-only streams
- [x] PCM read path: `NativeFormat` = UInt8/Int16/Int24/Int32/Float32/Float64 by `fmt`; `ReadFramesNative` = straight byte copy in 4096-frame blocks; `ReadFrames` = native block → `AudioSampleConvert` → caller span; `TotalFrames` from clamped `data` length when known
- [x] Metadata (`Wav/Wav_Metadata.cs`, A9): `Wav_SamplerChunk` (`smpl`: `MidiUnityNote`, `MidiPitchFraction`, `Wav_SampleLoop[]`), `Wav_CuePoint[]` (`cue `), `Wav_InfoTags` (`LIST/INFO`), `Wav_InstrumentChunk` (`inst`); `bext` and every other non-data chunk raw in `Chunks`; `Wav_Decoder.MetadataComplete` tells forward-only callers when trailing chunks have been read
- [x] Unsupported (valid) encodings → `AudioDecoder_UnsupportedException` naming the tag (ADPCM, MP3-in-WAV, A-law/µ-law until Phase 4)
- [x] Tests: `Support/Wav_TestWriter.cs` synthesises every fixture in memory — 8/16/24/32 PCM, float32/64, 1/2/6 ch, EXTENSIBLE with masks + 20-valid-bits-in-24, **odd chunk WITHOUT pad**, RIFF size 0 and 0xFFFFFFFF, `data` overrun, `smpl`+`cue`+`LIST` present, chunks after `data`, `fmt` after `data` (seekable ok / forward-only throws); bit-exact expectations for both `ReadFrames` and `ReadFramesNative`; every case run seekable AND through `ForwardOnlyStream`; `DecodeAll == concat(ReadFrames)`; `leaveOpen`; D13 allocation test (0 bytes across 100 reads after warm-up)
- [x] Local (uncommitted-file) test, `[Trait("Category","Local")]`, skipped when absent: decode `clap-808.wav`; assert 24-bit mono, frame count = 142080 / 3, chunk index lists `SAUR`, `LIST`, `id3 ` with the last one un-padded
- [x] `cmd/publish-local` → verify `ArtificialNecessity.Audio.Common`, `.Audio`, `.Audio.Formats` land in `C:\PROJECTS\LocalNuGet` with one shared version; note the version in this file: `0.260908.234621` (Phase 1), `0.260908.235755` (Phase 2)
- [x] Tick Phase 1 in `50_Audio_Formats.md`; commit `9584806` (1b+1c together, A5)

### Phase 1d — MusicStudio adapter (done in the `C:\PROJECTS\AN_MusicStudio` workspace, NOT here)

- [ ] `MusicStudio.Build.props` `ANAudioVersion` → `0.260908.235755` (Phase 2 publish; includes WAV + FLAC); add `<PackageReference Include="ArtificialNecessity.Audio.Formats" />`
- [ ] `Audio/Sampler/InstrumentSample.cs`: delete the hand-rolled RIFF walker (`Decode(ReadOnlySpan<byte>)`); `Load(path)` → `AudioDecoder.DecodeAll(path)` → `InstrumentSample.FromDecoded(AudioDecoder_Pcm)` = the ONE adapter (mono→stereo up-mix, >2 ch→stereo down-mix by simple average of L-ish/R-ish per channel mask or first two channels; keep source rate); `SourceBitsPerSample` ← `Info.SourceBitDepth`; `FormatDescription` gains the container
- [ ] `InstrumentSampleChecks`: replace the in-memory WAV fixtures with the `Wav_TestWriter` shapes that matter here (odd-pad, EXTENSIBLE 24-bit, float32, mono up-mix); `--audio-test` green
- [ ] `SamplesRail_SourceCache` and `InstrumentSample_Cache` need no change (they call `InstrumentSample.Load`); `+ samples…` / Import file filters gain `*.flac;*.mp3` as Phases 2/3 land
- [ ] `_PROJECT_STRUCTURE.md` checkpoint note: decoding now lives in `AN.Audio.Formats`; verify `clap-808.wav` loads in a Sampler row

## Phase 2 — FLAC

- [x] Subsume SimpleFlac → `Flac/Flac_ReferenceDecoder.cs` + `Flac/LICENSE-SimpleFlac.txt` (2026-09-08)
- [x] `git add` both files (first commit of `Flac/`); commit message records upstream URL + commit `dc149aa`
- [x] `// AN:` adaptations inside `Flac_ReferenceDecoder.cs`: namespace `AN.Audio.Formats.Flac`, class `internal sealed`, tabs→spaces, `Options` defaults `ConvertOutputToBytes=false`/`ValidateOutputHash=false`, metadata loop parses STREAMINFO / VORBIS_COMMENT / SEEKTABLE and skips PADDING / APPLICATION / CUESHEET / PICTURE (recording presence), interleaved output `CopyFrameInt32(Span<int>)` / `CopyFrameFloat(Span<float>)` (A7; no intermediate `byte[]`; `ConvertOutputToBytes` kept only for MD5). Also (A11): buffered, position-tracking, re-seatable `BitReader`; coded frame number decoded → `BufferFirstSample`; frame-header CRC-8 verified; `EndedShort` instead of throwing on short streams; MD5 skipped when the STREAMINFO signature is all zero (A10). Each change marked `// AN:`, summary block at the top of the file.
- [x] `Flac/Flac_MetadataTypes.cs` (A8): `Flac_MetadataBlockType`, `Flac_StreamInfo`, `Flac_SeekPoint`/`Flac_SeekTable`, `Flac_Tags` (case-insensitive multimap), `Flac_DecoderOptions` (`VerifyMd5`)
- [x] `Flac/Flac_Decoder.cs : IAudioDecoder` — `NativeFormat = Int32` (sign-extended low bits; `SourceBitDepth` = STREAMINFO bits), frame buffer carried across `ReadFrames` calls of arbitrary size, `TotalFrames` null when STREAMINFO total = 0, `EndedEarly` on `EndOfStreamException` mid-frame
- [x] `AudioDecoder.Open` wires `fLaC` → `Flac_Decoder`
- [x] Fixtures: render `AssetSource/cartesia_tts_test.wav` to FLAC (Q1: `ffmpeg -i in.wav -c:a flac out.flac`, and a 24-bit variant via `-sample_fmt s32 -bits_per_raw_sample 24`); commit under `tests/AN.Audio.Formats.Tests/Fixtures/` with `README.md` command lines
- [x] Tests: bit-exact `ReadFramesNative` vs the WAV master; float parity via `AudioSampleConvert`; `VerifyMd5` on; VORBIS_COMMENT tags; forward-only stream; truncated copy → `EndedEarly`; allocation; Local test on `A0v3.flac` (24-bit, 2 ch, 48 kHz, 1079560 frames, decodes fully; its MD5 is all-zero so `HasMd5 == false` and verification is skipped (A10); seek == linear decode)
- [x] 2b: `SeekToFrame` — SEEKTABLE when present, else frame-header search (sync `0xFFF8/0xFFF9` + CRC-8) when the stream seeks; `CanSeek` honest (built: SEEKTABLE when present, else byte-offset binary search with a forward sync scan validated by CRC-8 + STREAMINFO agreement; tests cover both plus the Salamander file)
- [x] publish-local; tick Phase 2; commit

## Phase 3 — MP3

- [ ] Q2 first: confirm on nuget.org that `NLayer` **3.0.0** targets `net8.0` with no dependencies (the 2.0.1 non-seekable-read bug is fixed in 3.0.0); add `<PackageReference Include="NLayer" Version="3.0.0" />` to Formats only
- [ ] `Mp3/Mp3_Id3v2.cs` (syncsafe size, TIT2/TPE1/TALB/TRCK/TDRC/TYER, APIC skipped) → `Mp3_Id3Tags`; parsed from the `PeekableStream` before NLayer sees the stream
- [ ] `Mp3/Mp3_Decoder.cs : IAudioDecoder` wrapping `NLayer.MpegFile(Stream)` — `NativeFormat = Float32`, `SourceBitDepth = 0`, `TotalFrames` from `Length` when ≥ 0 else null, `GaplessInfo`; NLayer types in NO public signature
- [ ] Sniff: ID3v2-prefixed and bare MPEG sync (two consecutive valid frame headers); hint tiebreak
- [ ] Fixture: `ffmpeg -i in.wav -c:a libmp3lame -b:a 128k out.mp3`; tests: frame count ± 1 granule, rate/channels, tag title, forward-only stream, `TotalFrames == null` path, allocation
- [ ] publish-local; tick Phase 3; commit

## Phase 4 — later (own addendum each; not scheduled)

- [ ] AIFF/AIFC (`FORM`, big-endian PCM, `MARK`/`INST` loops)
- [ ] RF64/W64, A-law/µ-law
- [ ] Ogg container: Vorbis (NVorbis), Opus (Concentus), FLAC-in-Ogg
- [ ] Encoders (WAV/FLAC writers) if a consumer needs export

## Open questions carried from the design spec

- **Q1** ANSWERED 2026-09-08: `ffmpeg` is on the dev machine (chocolatey); fixtures rendered, command lines in `tests/AN.Audio.Formats.Tests/Fixtures/README.md`
- **Q2** NLayer 3.0.0 package TFMs/dependencies — verify before pinning
- **Q3** Per-format metadata on `AudioDecoder_Pcm`? Current answer: no — use the per-format tier (built that way; `AudioDecoder.DecodeAll(IAudioDecoder)` overload lets a caller open the per-format decoder, read metadata, then drain it)