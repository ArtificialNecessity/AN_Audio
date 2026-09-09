# 50 — AN.Audio.Formats: managed audio FORMAT decoding (WAV, FLAC, MP3, …)

- **Status:** Draft v2 (2026-09-08) — decisions D1–D16 agreed with the user; SimpleFlac subsumed (D6 done); Phase 1 not started
- **Package:** `ArtificialNecessity.Audio.Formats` (`AN.Audio.Formats.dll`), `src/AN.Audio.Formats/`, namespace `AN.Audio.Formats`
- **Depends only on** `ArtificialNecessity.Audio.Common` (D15: the shared PCM vocabulary) and, from Phase 3, `NLayer`. Never on
  `ArtificialNecessity.Audio` itself. Pure managed, `AnyCPU`, NativeAOT-safe.
- **Consumers:** MusicStudio (Sampler rows, samples rail — today a 60-line WAV-only hand-rolled decoder in
  `src/AN.MusicStudio/Audio/Sampler/InstrumentSample.cs`), Mirica (TTS playback), Arcane Siege (SFX/music).

## Why

MusicStudio's WAV reader fails on real files: 99Sounds `clap-808.wav` ends with an odd-length `id3 ` chunk whose RIFF pad byte
the writer omitted ("Truncated WAV chunk"); every 24-bit DAW export uses `WAVE_FORMAT_EXTENSIBLE` (0xFFFE), which it rejects;
8-bit and >2 channels are rejected. The Salamander Grand Piano is 641 **FLAC** files (24-bit stereo); users have MP3s. Patching
one chunk-walker corner case at a time is the wrong shape. There is no single 100 %-managed .NET library covering these
formats (research 2026-09-08: SMAL ships WAV only; SpawnDev.Codecs is a GPU/ILGPU experiment at 0.3-rc; NAudio's MP3/FLAC
paths are Windows Media Foundation = native). The managed building blocks that DO exist and are worth using:

| Format | Source | License | Shape | Verdict |
|---|---|---|---|---|
| FLAC | [jdpurcell/SimpleFlac](https://github.com/jdpurcell/SimpleFlac) `FlacDecoder.cs` (541 lines, port of Nayuki's reference decoder) | MIT | single file, .NET 8+, no `unsafe`, no deps, `FlacDecoder(Stream)`, frame-at-a-time | **vendor as source** (D6) |
| MP3 | [NLayer](https://www.nuget.org/packages/NLayer) 2.0.1 (NAudio family; port of JavaLayer) | MIT | `netstandard2.0`+`net8.0`, no deps, `MpegFile(Stream).ReadSamples(float[])`, MPEG-1/2 layers I–III, gapless via LAME tag | **NuGet dependency** (D7) |
| WAV / AIFF | ourselves | — | small; no third-party package earns a dependency | **write properly** (D5) |
| Ogg Vorbis | NVorbis | MIT | managed | later (Phase 4) |
| Opus | Concentus | BSD-3 | managed | later (Phase 4) |

Ground truth for the two third-party surfaces: `_EXTERNAL_APIS/SimpleFlac_FlacDecoder.md`, `_EXTERNAL_APIS/NLayer_MpegFile.md`.

## Decisions

| # | Decision | Rationale |
|---|---|---|
| D1 | **Name is `Formats`, not `Files`.** Nothing in the public API says "file"; every entry point takes a `Stream` (a `string path` overload is sugar that opens a `FileStream`). | Sources are files, HTTP responses, zip entries, memory — the package decodes a *format*. |
| D2 | **Streaming is the primary mode.** Decoders are pull-based, frame-block-at-a-time, and work on a **forward-only, non-seekable, unknown-length** `Stream`. Decoding an HTTP FLAC response starts after the header arrives and never requires the whole body as a `byte[]`. `DecodeAll` is a convenience built ON the streaming path, not the other way round. | The user's requirement; also bounds memory for multi-hour files. |
| D3 | **Two API tiers**: (a) per-format public types (`Wav_Decoder`, `Flac_Decoder`, `Mp3_Decoder`) exposing everything that format knows (chunks, tags, loop points, integer samples…); (b) ONE generic `IAudioDecoder` + `AudioDecoder.Open(...)` facade that sniffs the format and returns interleaved `float` frames for clients that just want audio. Every per-format decoder implements `IAudioDecoder`. | Sampler wants `smpl` loops/root note; a TTS player wants floats. |
| D4 | **Output is interleaved `float32`, nominal range [-1, 1], NOT clipped, source channel count, source sample rate.** Integer PCM converts by `/ 2^(bits-1)`. No up/down-mixing, no resampling, no normalisation, no dither — the consumer decides (overview rule 6: the library does not editorialise). | MusicStudio's sampler already resamples; mixing policy belongs to the client. |
| D5 | **WAV is written by us, to the spec, tolerant of the wild.** See §WAV. | Small; the failures we hit are exactly the things packages also get wrong. |
| D6 | **FLAC = SimpleFlac SUBSUMED, not submoduled.** Upstream is a finished one-week project (5 commits Aug 9–16 2025, one author, 0 forks, nothing since): there is no update stream to track. DONE 2026-09-08: `Flac/Flac_ReferenceDecoder.cs` = upstream `FlacDecoder.cs` @ `dc149aa` with our provenance header prepended and the original MIT notice verbatim below it; `Flac/LICENSE-SimpleFlac.txt` beside it. Our changes go in that file, each marked `// AN:`. Namespace becomes `AN.Audio.Formats.Flac`, class `internal`. | One file, dead upstream, MIT; a submodule would be ceremony with no counterpart. |
| D7 | **MP3 = `NLayer` NuGet** (the package's ONLY external dependency). Wrapped, never exposed: `Mp3_Decoder` is our type; NLayer types do not appear in any public signature. | Mature, MIT, managed; a wrapper lets us swap it later. |
| D8 | **Format detection is by content (magic bytes), never by extension.** `AudioDecoder_FormatHint` (from extension / MIME) is an optional tiebreaker only for bare MPEG streams whose sync word is a weak signature. | Files lie; streams have no extension. |
| D9 | **Directory per format**: `Wav/`, `Flac/`, `Mp3/`, later `Aiff/`, `Ogg/`. Root = generic tier; `Internal/` = shared machinery (bit reader, look-ahead stream, sample conversion). | User request; mirrors `Platforms/<OS>/` in the other feature areas. |
| D10 | **Unknown length is a first-class state.** `AudioDecoder_StreamInfo.TotalFrames` is `long?`; `null` for RIFF size `0`/`0xFFFFFFFF`, FLAC STREAMINFO total = 0, MP3 without Xing/VBRI on a non-seekable stream. Consumers grow buffers. | Streaming writers and live streams do this. |
| D11 | **Seeking is optional and honest.** `CanSeek` is true only when the underlying stream seeks AND the format supports it (WAV always; FLAC via SEEKTABLE or frame-header scan; MP3 via NLayer's frame index). `SeekToFrame` on `CanSeek == false` throws `NotSupportedException`. | Same shape everywhere (I2). |
| D12 | **Errors**: malformed input → `AudioDecoder_FormatException : InvalidDataException` with a format-specific message; unsupported-but-valid input (e.g. WAV ADPCM, Ogg until Phase 4) → `AudioDecoder_UnsupportedException : NotSupportedException` naming the encoding. Truncated **trailing** data (stream ended mid-frame) is NOT an error: the decoder returns the frames it has and reports `EndedEarly = true`. | Half-downloaded files should play what exists. |
| D13 | **Read path allocates only at open and on frame-size growth.** `ReadFrames(Span<float>)` performs no `new` in steady state (test: `GC.GetAllocatedBytesForCurrentThread()` delta 0 across a decode loop after warm-up). Not a hot-path guarantee in the I3 sense — decoders are called from worker threads, never from the audio callback. | Consistent with overview rule 5 without over-promising. |
| D14 | **Overview spec amendment.** `00_AN_Audio_Overview.md` lists decoding as a permanent non-goal *of the OS-facing layer*; that stays true. This package is a sibling feature area in the same repo that touches no OS API. Update the overview's feature table, repository shape and non-goals wording in Phase 1. | Keep the living document truthful. |
| D15 | **`ArtificialNecessity.Audio.Common` (`src/AN.Audio.Common/`, namespace `AN.Audio`) holds the MINIMUM shared vocabulary** — `AudioFormat`, `SampleFormat`, `AudioChannelMask`, `AudioBufferView` — and NOTHING else (no OS, no I/O, no decoders). `AN.Audio` and `AN.Audio.Formats` (and Midi/Capture if they ever need a PCM type) reference it. **Amends spec 30 D27**: "no inter-project references" becomes "no inter-project references EXCEPT to `AN.Audio.Common`". `AudioFormat`/`SampleFormat` MOVE out of `AN.Audio` (same namespace `AN.Audio`, so consumers recompile without source changes; the package gains a dependency). | Decoder output must land in an `IAudioOutput` callback buffer with zero conversions/copies when formats agree; that needs ONE `AudioFormat` type both sides speak. |
| D16 | **Zero-copy read path.** Every decoder exposes `NativeFormat : AudioFormat` (the layout its bitstream yields without conversion: WAV PCM16 → `Int16`, WAV 24-bit → `Int24`, FLAC → `Int32` (left-justified? NO — see below), MP3 → `Float32`) and `ReadFramesNative(Span<byte>)` writing that layout directly into the caller's buffer. `ReadFrames(Span<float>)` remains the simple path. A player whose device format equals `NativeFormat` decodes straight into the `AudioCallback` span; otherwise it converts once. `SampleFormat` gains `UInt8, Int24, Int32, Float64` so every WAV/FLAC/AIFF layout is nameable; `AN.Audio`'s device backends keep supporting only `Int16`/`Float32` and reject the rest at `Start` (they do today: `BytesPerSample` throws). FLAC native = `Int32` with samples in the LOW bits (sign-extended, exactly as decoded; `SourceBitDepth` says how many are significant) — the consumer's `/ 2^(bits-1)` rule stays uniform with PCM. | The user's requirement: no unnecessary copies between Formats and AN.Audio. |

## Shared vocabulary — `AN.Audio.Common` (D15)

```csharp
namespace AN.Audio;                       // unchanged namespace: MOVED from src/AN.Audio, no consumer source edits

public enum SampleFormat { UInt8, Int16, Int24, Int32, Float32, Float64 }   // was Int16, Float32

public record struct AudioFormat(int SampleRate, int Channels, SampleFormat Format)
{
    public int BytesPerSample => Format switch { UInt8 => 1, Int16 => 2, Int24 => 3, Int32 => 4, Float32 => 4, Float64 => 8 };
    public int BytesPerFrame => BytesPerSample * Channels;
}

[Flags] public enum AudioChannelMask : uint { FrontLeft = 0x1, FrontRight = 0x2, FrontCenter = 0x4, LowFrequency = 0x8, /* … SPEAKER_* order */ }

/// A typed window over interleaved PCM bytes: the ONE way a buffer + its format travel together.
public readonly ref struct AudioBufferView(Span<byte> Bytes, AudioFormat Format)
{
    public int FrameCount => Bytes.Length / Format.BytesPerFrame;
    public Span<float> AsFloat32()  /* Format must be Float32 */ => MemoryMarshal.Cast<byte, float>(Bytes);
    public Span<short> AsInt16()    /* Format must be Int16  */ => MemoryMarshal.Cast<byte, short>(Bytes);
}

/// Span-based conversions between any two SampleFormats; the ONE place the `/ 2^(bits-1)` and `* 2^(bits-1)` rules live.
public static class AudioSampleConvert { public static void Convert(ReadOnlySpan<byte> src, AudioFormat from, Span<byte> dst, AudioFormat to); /* same rate & channels */ }
```

`AudioSampleConvert` moves here from `AN.Audio/Internal/` (format conversion already exists there for device matching) so the
resampler stays in `AN.Audio` and the sample-format conversion is shared. The sinc resampler does NOT move (it is device-side policy).

Repository/packaging consequences:
- `src/AN.Audio.Common/AN.Audio.Common.csproj` → `ArtificialNecessity.Audio.Common`; `AN.Audio.csproj` and `AN.Audio.Formats.csproj` add a
  `ProjectReference` (packed as a NuGet dependency by the same timestamp version — `cmd/publish-local.cs` already packs the whole solution).
- `AN.Audio.Midi` does NOT reference Common (it has no PCM type) — the "minimum" rule is enforced by asking "who needs this?" per type.
- Spec 30 D27 wording updated; overview spec repository shape gains the `AN.Audio.Common/` line (folded into D14's amendment).

## Public API — generic tier (root of `src/AN.Audio.Formats/`)

```csharp
namespace AN.Audio.Formats;

public enum AudioDecoder_Container { Wav, Flac, Mp3, Aiff /*P4*/, Ogg /*P4*/ }
public enum AudioDecoder_SourceEncoding { PcmInt, PcmFloat, Flac, MpegLayer1, MpegLayer2, MpegLayer3, Vorbis, Opus, Alaw, Mulaw, Adpcm }

/// Everything a consumer needs to size buffers and label the source. Known after the header, before any audio.
public readonly record struct AudioDecoder_StreamInfo(
    AudioDecoder_Container Container,
    AudioDecoder_SourceEncoding Encoding,
    int SampleRate,
    int Channels,
    int SourceBitDepth,          // 16/24/32 for PCM & FLAC; 0 when meaningless (MP3)
    long? TotalFrames,           // null = unknown (D10)
    bool CanSeek)
{
    public TimeSpan? Duration => TotalFrames is { } f ? TimeSpan.FromSeconds((double)f / SampleRate) : null;
}

public interface IAudioDecoder : IDisposable
{
    AudioDecoder_StreamInfo Info { get; }
    /// D16: the interleaved PCM layout this bitstream yields WITHOUT conversion (AN.Audio.Common type). Rate/channels == Info.
    AudioFormat NativeFormat { get; }
    long FramesRead { get; }                       // position in frames
    bool EndedEarly { get; }                       // D12: stream ended before the declared length
    /// Simple path. Fills `interleaved` (length must be a multiple of Info.Channels) with as many whole frames as available,
    /// converted to float per D4; returns frames written; 0 = end of stream. Blocks on the stream like Stream.Read does.
    int ReadFrames(Span<float> interleaved);
    /// Zero-copy path (D16). Writes frames in NativeFormat directly into `destination` (length must be a multiple of
    /// NativeFormat.BytesPerFrame); returns frames written. A player whose IAudioOutput.Format == NativeFormat passes the
    /// AudioCallback buffer straight through; otherwise it reads native and converts once with AudioSampleConvert, or uses ReadFrames.
    int ReadFramesNative(Span<byte> destination);
    /// Convenience over ReadFramesNative for callers holding an AudioBufferView; throws if view.Format != NativeFormat.
    int ReadFramesNative(AudioBufferView view);
    void SeekToFrame(long frame);                  // D11
}

public enum AudioDecoder_FormatHint { None, Wav, Flac, Mp3, Aiff, Ogg }

public static class AudioDecoder
{
    /// Sniffs the leading bytes (D8) through an Internal/PeekableStream so non-seekable sources work, then returns the
    /// per-format decoder as IAudioDecoder. `leaveOpen` semantics as in StreamReader.
    public static IAudioDecoder Open(Stream source, AudioDecoder_FormatHint hint = default, bool leaveOpen = false);
    public static IAudioDecoder Open(string path);   // FileStream(SequentialScan, 64 KiB) + hint from extension
    public static AudioDecoder_Container? Sniff(ReadOnlySpan<byte> leadingBytes, AudioDecoder_FormatHint hint = default);

    /// Convenience over the streaming path: decodes everything into one buffer (capacity hint = TotalFrames when known).
    public static AudioDecoder_Pcm DecodeAll(Stream source, AudioDecoder_FormatHint hint = default, bool leaveOpen = false);
    public static AudioDecoder_Pcm DecodeAll(string path);
}

public sealed class AudioDecoder_Pcm
{
    public AudioDecoder_StreamInfo Info { get; }
    public float[] Interleaved { get; }            // exactly FrameCount * Channels
    public int FrameCount { get; }
    public bool EndedEarly { get; }
}
```

**Sniff table** (first 12 bytes, then more if needed):

| Bytes | Container |
|---|---|
| `RIFF` …`WAVE` / `RF64` …`WAVE` (ds64 chunk, Phase 4) | Wav |
| `fLaC` | Flac |
| `ID3` (v2 tag, skipped via its syncsafe size) followed by MPEG sync `0xFFE…` — or bare sync at offset 0 with a valid header (version/layer/bitrate/samplerate all legal) AND a second valid frame at the computed offset | Mp3 |
| `FORM` …`AIFF`/`AIFC` | Aiff (P4) |
| `OggS` | Ogg (P4; codec from the first packet) |
| none | `hint` decides if not `None`, else `AudioDecoder_UnsupportedException("unrecognised audio container")` |

## Public API — per-format tier

### `Wav/` — `Wav_Decoder : IAudioDecoder`

Correct per the RIFF/WAVE spec (MS RIFF 1991, `mmreg.h`, EBU RF64), tolerant where writers are known to be wrong:

- **Chunk walk**: `RIFF` size `0`, `0xFFFFFFFF` or larger than the stream → treat length as unknown and walk to EOF (D10). Odd chunk
  with a missing final pad byte → accept (the `clap-808.wav` case). A chunk whose declared length overruns the stream → clamp; if it
  is `data`, decode what exists and set `EndedEarly`. Unknown chunk ids are skipped but recorded (id, offset, length) in `Wav_Decoder.Chunks`.
- **`fmt `**: `Wav_FormatTag { Pcm = 1, IeeeFloat = 3, Alaw = 6, Mulaw = 7, Extensible = 0xFFFE, … }` as an enum (overview rule 1).
  `Extensible` resolves through the 16-byte SubFormat GUID (`KSDATAFORMAT_SUBTYPE_PCM` / `_IEEE_FLOAT`) and exposes `ValidBitsPerSample` +
  `ChannelMask` (`Wav_ChannelMask` flags enum, `SPEAKER_*`). Supported now: PCM 8 (unsigned), 16, 24, 32 (incl. 20/24 valid bits in
  32 containers), IEEE float 32/64, any channel count. A-law/µ-law: Phase 4 (tables are trivial). ADPCM/MP3-in-WAV: `Unsupported`.
- **`data` before `fmt `** (seen in the wild) → buffered/seek back when the stream seeks; otherwise `FormatException` ("fmt after data on a non-seekable stream").
- **Metadata surfaced** (all optional, read only when present, never required for audio): `smpl` → `Wav_SamplerChunk { MidiUnityNote,
  MidiPitchFraction, Loops: Wav_SampleLoop[] { Start, End, Type, Fraction, PlayCount } }` (the Sampler's free root note + loop points);
  `cue ` → `Wav_CuePoint[]`; `LIST/INFO` → `Wav_InfoTags` (INAM, IART, ICMT, ISFT…); `inst` → `Wav_InstrumentChunk`; `bext` → left as
  raw bytes in `Chunks`. Chunks that follow `data` are read lazily after the audio on non-seekable streams (so `smpl` at the end of an HTTP
  stream is available once `ReadFrames` returns 0).
- Streaming: the decoder reads through `Internal/PeekableStream`; PCM is converted frame-block by frame-block (4096 frames) straight
  from the byte stream into the caller's span. No whole-file buffer ever exists.

### `Flac/` — `Flac_Decoder : IAudioDecoder`

- `Flac/Flac_ReferenceDecoder.cs` (D6, present) is the bitstream decoder, `internal`. Our public `Flac_Decoder` adds: `Flac_StreamInfo`
  (min/max block size, min/max frame size, sample rate, channels, bits, total samples, MD5), `Flac_Tags` from `VORBIS_COMMENT`
  (case-insensitive multimap; `TITLE`, `ARTIST`, …), `Flac_SeekTable`, `Flac_Picture` metadata skipped (recorded as present), and
  `NativeFormat = Int32` (D16: lossless integers, sign-extended in the low bits) via `ReadFramesNative`. The reference decoder skips
  non-STREAMINFO blocks byte by byte — the `// AN:` adaptation replaces that loop with one that parses the blocks we surface.
- Streaming: the reference decoder decodes ONE frame per `DecodeFrame()` into `long[channel][sample]`; the `// AN:` output path
  interleaves that block straight into the caller's `Span<int>`/`Span<float>` (no intermediate `byte[]`; the upstream `ConvertOutputToBytes`
  path stays only for MD5 verification). Works on non-seekable streams as-is. `ValidateOutputHash`
  (MD5 over the whole stream) is OFF by default; `Flac_DecoderOptions.VerifyMd5` turns it on for tests/importers.
- Seek (D11): SEEKTABLE when present; otherwise binary-search on frame headers (sync `0xFFF8`/`0xFFF9` + CRC-8) when the stream seeks. Phase 2b.
- Bit depths: 16/24 (Salamander) and 8/12/20/32 all decode; the float conversion uses the frame's actual bits, so the
  `AllowNonstandardByteOutput` byte path of the reference decoder is irrelevant to us.

### `Mp3/` — `Mp3_Decoder : IAudioDecoder`

- Wraps `NLayer.MpegFile(Stream)`; `StereoMode.Both`; `ReadSamples(float[], …)` → our span API. Info: `Encoding = MpegLayer{1,2,3}`,
  `SourceBitDepth = 0`, `TotalFrames` from `MpegFile.Length` when ≥ 0 (Xing/LAME/VBRI header or seekable full scan), else `null` (D10).
  Gapless trimming (encoder delay/padding from the LAME tag) is NLayer's; we expose `Mp3_Decoder.GaplessInfo { EncoderDelay, EncoderPadding }`.
- `Mp3_Id3Tags`: ID3v2 header is skipped by NLayer's stream reader; we parse the tag ourselves in `Mp3/Mp3_Id3v2.cs` (TIT2, TPE1, TALB, TRCK,
  TDRC/TYER; APIC skipped) since it precedes the audio on any stream. ID3v1 (last 128 bytes) only when the stream seeks.
- Non-seekable streams: NLayer 2.0.1 has a known bug (fixed in 3.0.0's notes) where a non-seekable stream throws on the first read.
  Phase 3 picks **NLayer 3.0.0** (`NLayer` package itself is still `netstandard2.0`+`net8.0`, no deps; only `NLayer.NAudioSupport` moved to net9) and
  adds a forward-only-stream test to prove it.

## Internal machinery (`Internal/`)

- `PeekableStream : Stream` — wraps any `Stream`; buffers up to N bytes for `Sniff` and header parsing, replays them, then passes
  through; `CanSeek` mirrors the inner stream; `Position` is correct in both modes. THE reason non-seekable sources work.
- `ForwardOnlyStream` (tests only) — a `Stream` over a `byte[]` that reports `CanSeek = false`, `Length` throws, and hands out
  bytes in small random-sized chunks (1..97 bytes) to simulate a network read. Every format's tests run once seekable, once forward-only.
- Sample-format conversion is NOT here: it is `AN.Audio.AudioSampleConvert` in `AN.Audio.Common` (D15), shared with the device layer.
- `BitReader` — for WAV/AIFF headers; FLAC keeps the reference decoder's own reader.

## Repository changes

```
src/AN.Audio.Common/               (D15) AudioFormat, SampleFormat, AudioChannelMask, AudioBufferView, AudioSampleConvert — nothing else
  AN.Audio.Common.csproj           net8.0;net9.0;net10.0, PackageId ArtificialNecessity.Audio.Common, no dependencies
src/AN.Audio/                      gains ProjectReference → AN.Audio.Common; AudioFormat.cs and Internal sample conversion MOVE out
src/AN.Audio.Formats/
  AN.Audio.Formats.csproj        net8.0;net9.0;net10.0, PackageId ArtificialNecessity.Audio.Formats, ProjectReference AN.Audio.Common, PackageReference NLayer (Phase 3)
  AudioDecoder.cs                Open / Sniff / DecodeAll
  IAudioDecoder.cs  AudioDecoder_StreamInfo.cs  AudioDecoder_Pcm.cs  AudioDecoder_Exceptions.cs  AudioDecoder_Enums.cs
  Internal/                      PeekableStream, BitReader
  Wav/                           Wav_Decoder, Wav_FormatChunk (+enums), Wav_SamplerChunk, Wav_CuePoint, Wav_InfoTags, Wav_ChunkIndex
  Flac/                          Flac_ReferenceDecoder.cs (subsumed SimpleFlac, MIT notice inside) + LICENSE-SimpleFlac.txt [PRESENT], Flac_Decoder, Flac_StreamInfo, Flac_Tags, Flac_SeekTable
  Mp3/                           Mp3_Decoder, Mp3_Id3v2, Mp3_Id3Tags
tests/AN.Audio.Formats.Tests/    xunit; Fixtures/ (generated WAVs + small redistributable FLAC/MP3); ForwardOnlyStream
cmd/publish-local.cs             already packs the solution; add the project to AN.Audio.slnx
_EXTERNAL_APIS/SimpleFlac_FlacDecoder.md, NLayer_MpegFile.md   [PRESENT]
```

`AN.Audio.Build.props` is imported as in the other projects (timestamp versioning, analyzers, `artifacts/`). `<AllowUnsafeBlocks>false</AllowUnsafeBlocks>`
— nothing here needs it.

## Test plan (every phase adds its rows; all hardware-free)

| Area | Test |
|---|---|
| Sniff | each magic → container; ID3-prefixed MP3; garbage → Unsupported; hint tiebreak |
| WAV | synthesized fixtures written by a test-side `Wav_TestWriter`: 8/16/24/32 PCM, float32/64, 1/2/6 ch, EXTENSIBLE with masks, odd chunk WITHOUT pad (the 99Sounds case), RIFF size 0 and 0xFFFFFFFF, `data` overrun, `smpl`+`cue`+`LIST` present, chunks after `data`, `fmt` after `data`; bit-exact float expectations |
| FLAC | fixture FLAC vs its WAV rendering: bit-exact int32 and float; MD5 verify on; 24-bit stereo (Salamander-shaped); tags; total-samples = 0 → `TotalFrames == null` |
| MP3 | fixture decodes to expected frame count ± 1 granule, sample rate/channels, ID3v2 title; `TotalFrames` null on forward-only stream |
| Streaming | EVERY fixture through `ForwardOnlyStream`; results identical to the seekable run; `EndedEarly` on a truncated copy |
| Allocation | D13: zero bytes allocated across 100 `ReadFrames` after warm-up, per format |
| Facade | `DecodeAll` == concatenated `ReadFrames`; `leaveOpen` honoured |

Fixture provenance: `AssetSource/cartesia_tts_test.wav` (ours) is the WAV master; its FLAC and MP3 renderings are generated ONCE with
`ffmpeg` (if present on the dev machine — **Open Question Q1**) and committed under `tests/AN.Audio.Formats.Tests/Fixtures/` with a
`README.md` recording the exact command lines. Salamander files are NOT committed (license); a `[Trait("Category","Local")]` test decodes
`C:\PROJECTS\3P_SalamanderGrandPiano\Samples\A0v3.flac` when it exists.

## Phases

### Phase 1 — package skeleton + WAV done right (unblocks MusicStudio today)
- [ ] `src/AN.Audio.Common/` (D15): move `AudioFormat`/`SampleFormat` from `AN.Audio` (`git mv`), widen `SampleFormat`, add `AudioChannelMask`, `AudioBufferView`,
      `AudioSampleConvert` (moved from `AN.Audio/Internal/`); `AN.Audio` gets the `ProjectReference`; existing `AN.Audio.Tests` still pass; spec 30 D27 + overview amended
- [ ] `src/AN.Audio.Formats/` project, csproj per the Midi one (`IsPackable`, README/LICENSE pack items, `InternalsVisibleTo` tests); add to `AN.Audio.slnx`
- [ ] Generic tier: `IAudioDecoder` (incl. `NativeFormat`/`ReadFramesNative`, D16), `AudioDecoder_StreamInfo`, enums, exceptions, `AudioDecoder.Open/Sniff/DecodeAll`, `AudioDecoder_Pcm`
- [ ] `Internal/PeekableStream`, `Internal/BitReader`
- [ ] `Wav/` per §WAV including `smpl`/`cue`/`LIST` metadata and streaming reads; `ReadFramesNative` for PCM = a straight byte copy from the stream
- [ ] `tests/AN.Audio.Formats.Tests` with `Wav_TestWriter`, `ForwardOnlyStream`, the WAV rows + sniff + facade + allocation rows
- [ ] `_EXTERNAL_APIS/` notes; overview spec amendment (D14); README package list
- [ ] `cmd/publish-local` → LocalNuGet; bump `ANAudioVersion` in MusicStudio
- [ ] **MusicStudio**: `InstrumentSample.Decode` → `AudioDecoder.DecodeAll` + mono→stereo up-mix / >2ch→stereo down-mix in ONE adapter
      (`InstrumentSample.FromDecoded`), `SourceBitsPerSample` from `Info.SourceBitDepth`; delete the hand-rolled RIFF walker; `InstrumentSampleChecks`
      gain the odd-pad and EXTENSIBLE cases; `FormatDescription` gains the container (`FLAC · 48000 Hz · 24-bit …`). Verify `clap-808.wav` loads.

### Phase 2 — FLAC
- [x] Subsume `FlacDecoder.cs` → `Flac/Flac_ReferenceDecoder.cs` + `Flac/LICENSE-SimpleFlac.txt` (2026-09-08, upstream `dc149aa`)
- [ ] `// AN:` adaptations: namespace `AN.Audio.Formats.Flac`, `internal`, metadata-block loop parses STREAMINFO/VORBIS_COMMENT/SEEKTABLE and skips PICTURE/PADDING/APPLICATION/CUESHEET, `Options` defaults to no byte conversion / no MD5, interleaved `Span<int>`/`Span<float>` frame output (D16), tabs → spaces per repo style
- [ ] `Flac_Decoder` + `Flac_StreamInfo` + `Flac_Tags` (VORBIS_COMMENT) + `ReadFramesNative` (Int32) / `ReadFrames` (float)
- [ ] Tests: fixture bit-exactness vs WAV, MD5 verify, forward-only stream, Salamander local test
- [ ] 2b: `SeekToFrame` via SEEKTABLE / frame-header search
- [ ] MusicStudio: `+ samples…` file filter adds `*.flac`; verify `A0v3.flac` in a Sampler row

### Phase 3 — MP3
- [ ] `PackageReference NLayer 3.0.0`; `Mp3_Decoder` wrapper; `Mp3_Id3v2` parser; `GaplessInfo`
- [ ] Tests incl. forward-only stream (the 2.0.1 regression) and `TotalFrames == null` path
- [ ] MusicStudio filter adds `*.mp3`

### Phase 4 — later formats (each its own short addendum when started)
- [ ] AIFF/AIFC (`FORM`, big-endian PCM, `MARK`/`INST` loops — the other sampler-library staple)
- [ ] RF64/W64 (>4 GiB WAV), A-law/µ-law
- [ ] Ogg container: Vorbis via NVorbis, Opus via Concentus, FLAC-in-Ogg via our decoder
- [ ] Encoders (WAV/FLAC writers) if a consumer needs export

## Open questions

- **Q1** Is `ffmpeg` (or `flac`/`lame`) on the dev machine to render the committed FLAC/MP3 fixtures? If not, hand-pick a small
  permissively-licensed FLAC and MP3 and record their provenance.
- **Q2** NLayer 3.0.0 vs 2.0.1: 3.0.0 fixes the non-seekable read bug (D2 needs it); confirm on `nuget.org` that the `NLayer` 3.0.0 package
  itself still targets `net8.0` with no dependencies before pinning (the search result says so; verify at implementation time).
- **Q3** Should `AudioDecoder_Pcm` also carry the per-format metadata (`Wav_SamplerChunk`, `Flac_Tags`) as an optional `object? Metadata`,
  or must clients wanting metadata use the per-format tier? Current draft: per-format tier only (keeps the generic type dumb).