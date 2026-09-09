# 50 — AN.Audio.Formats: managed audio FORMAT decoding (WAV, FLAC, MP3, …)

- **Status:** v2 (2026-09-08) — D1–D16 agreed; **Phase 1 built** (Common + Formats skeleton + WAV, published as 0.260908.234621; MusicStudio adapter 1d done 2026-09-09); **Phase 2 built** (FLAC incl. seeking, published as 0.260908.235755); Phase 3 (MP3) not started
- **Package:** `ArtificialNecessity.Audio.Formats` (`AN.Audio.Formats.dll`), `src/AN.Audio.Formats/`, namespace `AN.Audio.Formats`
- **Depends only on** `ArtificialNecessity.Audio.Common` (D15: the shared PCM vocabulary) and, from Phase 3, `NLayer`. Never on
  `ArtificialNecessity.Audio` itself. Pure managed, `AnyCPU`, NativeAOT-safe.
- **Consumers:** MusicStudio (Sampler rows, samples rail — `Nodes/Source/Sampler/InstrumentSample.cs` now calls `AudioDecoder.DecodeAll`; the hand-rolled WAV walker is gone, Phase 1d), Mirica (TTS playback), Arcane Siege (SFX/music).
- **Implementation checklist / as-built deviations:** `50_Audio_Formats_IMPL.md` (table "As built").

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
| D12 | **Errors**: malformed input → `AudioDecoder_FormatException : IOException` (the spec first said `InvalidDataException`, which is sealed in .NET; `IOException` is its base) with a format-specific message; unsupported-but-valid input (e.g. WAV ADPCM, Ogg until Phase 4) → `AudioDecoder_UnsupportedException : NotSupportedException` naming the encoding. Truncated **trailing** data (stream ended mid-frame) is NOT an error: the decoder returns the frames it has and reports `EndedEarly = true`. | Half-downloaded files should play what exists. |
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
    public int BitsPerSample => BytesPerSample * 8;   // container bits
}

[Flags] public enum AudioChannelMask : uint { None = 0, FrontLeft = 0x1, FrontRight = 0x2, FrontCenter = 0x4, LowFrequency = 0x8, /* … all 18 SPEAKER_* bits, Reserved, All */
                                              Mono, Stereo, Quad, Surround, FivePointOne, FivePointOneSurround, SevenPointOne, SevenPointOneSurround /* KSAUDIO_SPEAKER_* layouts */ }
public static class AudioChannelMaskExtensions { public static int PositionCount(this AudioChannelMask mask); }

/// A typed window over interleaved PCM bytes: the ONE way a buffer + its format travel together.
public readonly ref struct AudioBufferView(Span<byte> Bytes, AudioFormat Format)   // ctor throws unless Bytes is whole frames and Channels > 0
{
    public int FrameCount => Bytes.Length / Format.BytesPerFrame;
    public Span<float>  AsFloat32();  public Span<short> AsInt16();   // throw InvalidOperationException unless Format matches
    public Span<int>    AsInt32();    public Span<double> AsFloat64();
    public AudioBufferView SliceFrames(int frameCount);  public AudioBufferView SliceFrames(int frameStart, int frameCount);
}

/// Span-based conversions between any two SampleFormats; the ONE place the `/ 2^(bits-1)` and `* 2^(bits-1)` rules live.
/// float→int is symmetric (* 2^(bits-1), rounded) and SATURATES (+1.0 → max, never wraps); int→int shifts; float→float casts, never clips.
public static class AudioSampleConvert
{
    public static int Convert(ReadOnlySpan<byte> src, AudioFormat from, Span<byte> dst, AudioFormat to);   // same rate & channels; returns frames converted (min of both buffers)
    public static int ToFloat32(ReadOnlySpan<byte> src, SampleFormat from, Span<float> dst);   // likewise ToFloat64 / ToInt16 / ToInt32
    public static short FloatToInt16(float v);  public static int FloatToInt24(float v);  public static int FloatToInt32(double v);  public static byte FloatToUInt8(float v);
    public static float Int16ToFloat(short v);  public static int ReadInt24(ReadOnlySpan<byte> src, int sampleIndex);  public static void WriteInt24(Span<byte> dst, int sampleIndex, int value);
}
```

`AudioSampleConvert` is new in Common. `AN.Audio/Internal/AudioFormatConverter` (device matching: resampler glue, channel mapping, ArrayPool)
STAYS in `AN.Audio` and delegates its Int16↔float scalars to `AudioSampleConvert` — that changed the device write path from `* 32767` to the
symmetric saturating `* 32768` (≤ 1 LSB, agreed 2026-09-08). It also now throws `NotSupportedException` for any `SampleFormat` other than
Int16/Float32. The sinc resampler does NOT move (it is device-side policy).

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
    public static AudioDecoder_FormatHint HintFromExtension(string pathOrExtension);   // .wav/.flac/.mp3/.aif*/.ogg → hint, else None

    /// Convenience over the streaming path: decodes everything into one buffer (capacity hint = TotalFrames when known).
    public static AudioDecoder_Pcm DecodeAll(Stream source, AudioDecoder_FormatHint hint = default, bool leaveOpen = false);
    public static AudioDecoder_Pcm DecodeAll(string path);
    public static AudioDecoder_Pcm DecodeAll(IAudioDecoder open);   // drains an already-open per-format decoder (metadata first, then audio); does not dispose it
}

public sealed class AudioDecoder_Pcm
{
    public AudioDecoder_StreamInfo Info { get; }
    public float[] Interleaved { get; }            // exactly FrameCount * Channels
    public int FrameCount { get; }
    public bool EndedEarly { get; }
    public TimeSpan Duration { get; }
}

// Both exceptions carry `AudioDecoder_Container? Container` and prefix the message with it ("Wav: …", "Flac: …").
```

**Sniff table** (`Open` peeks 12 bytes; if nothing matches, or the match is MP3, it peeks up to 8 KiB so the two-frame MPEG rule can run):

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
  is `data`, decode what exists and set `EndedEarly`. Unknown chunk ids are skipped but recorded in `Wav_Decoder.Chunks` as `Wav_ChunkInfo`
  (id, offset, declared length, clamped length, `PadBytePresent`, and the raw body for non-`data` chunks ≤ 1 MiB). `data` length `0xFFFFFFFF`
  (or `0` with unknown RIFF size) → unknown, read to EOF. `RF64` → `Unsupported` until Phase 4. `RiffLengthKnown` reports the header's honesty.
- **`fmt `**: `Wav_FormatTag { Pcm = 1, IeeeFloat = 3, Alaw = 6, Mulaw = 7, Extensible = 0xFFFE, … }` as an enum (overview rule 1).
  `Extensible` resolves through the 16-byte SubFormat GUID (`KSDATAFORMAT_SUBTYPE_PCM` / `_IEEE_FLOAT`) and exposes `ValidBitsPerSample` +
  `ChannelMask` (`AudioChannelMask` from Common, D15 — the earlier `Wav_ChannelMask` name is superseded); `Wav_FormatChunk.EffectiveTag` gives the
  resolved tag, `ContainerBytes` the per-sample container (from `BlockAlign / Channels`, else `ceil(bits/8)`). Supported now: PCM 8 (unsigned), 16, 24, 32
  (incl. 20/24 valid bits in 24/32 containers; `Info.SourceBitDepth` = valid bits, `NativeFormat` = container), IEEE float 32/64, any channel count.
  A-law/µ-law: Phase 4 (tables are trivial). ADPCM/MP3-in-WAV: `Unsupported`, naming the tag.
- **`data` before `fmt `** (seen in the wild) → on a seekable stream the walk skips the body, finds `fmt`, then seeks back; otherwise
  `FormatException` ("fmt chunk after data on a non-seekable stream").
- **Metadata surfaced** (all optional, read only when present, never required for audio): `smpl` → `Wav_SamplerChunk { MidiUnityNote,
  MidiPitchFraction, Loops: Wav_SampleLoop[] { Start, End, Type, Fraction, PlayCount } }` (the Sampler's free root note + loop points);
  `cue ` → `Wav_CuePoint[]`; `LIST/INFO` → `Wav_InfoTags` (INAM, IART, ICMT, ISFT…); `inst` → `Wav_InstrumentChunk`; `bext` → left as
  raw bytes in `Chunks`. Chunks that follow `data` are read lazily after the audio on non-seekable streams (so `smpl` at the end of an HTTP
  stream is available once `ReadFrames` returns 0); `Wav_Decoder.MetadataComplete` says whether that has happened (always true after open on a
  seekable stream, where the whole chunk list is walked eagerly).
- Streaming: the decoder reads through `Internal/PeekableStream`; PCM is converted frame-block by frame-block (4096 frames) straight
  from the byte stream into the caller's span (Float32 sources go straight into the caller's `Span<float>` with no block at all). No whole-file buffer ever exists.
- Seek (D11): `SeekToFrame` is a byte-offset computation (`dataStart + frame * BytesPerFrame`), clamped to `TotalFrames`; throws `NotSupportedException` when the stream cannot seek.

### `Flac/` — `Flac_Decoder : IAudioDecoder`

- `Flac/Flac_ReferenceDecoder.cs` (D6, built) is the bitstream decoder, `internal sealed`. Our public `Flac_Decoder` adds: `Flac_StreamInfo`
  (min/max block size, min/max frame size, sample rate, channels, bits, total samples, MD5), `Flac_Tags` from `VORBIS_COMMENT`
  (case-insensitive multimap; `TITLE`, `ARTIST`, …; `Vendor`), `Flac_SeekTable`, `MetadataBlocksPresent` (PICTURE etc. recorded, not parsed), and
  `NativeFormat = Int32` (D16: lossless integers, sign-extended in the low bits) via `ReadFramesNative`. The reference decoder skips
  non-STREAMINFO blocks byte by byte — the `// AN:` adaptation replaces that loop with one that parses the blocks we surface.
  All types live in `Flac/Flac_MetadataTypes.cs`. Constructor: `Flac_Decoder(Stream, Flac_DecoderOptions?, leaveOpen)`.
- Streaming: the reference decoder decodes ONE frame per `DecodeFrame()` into `long[channel][sample]`; the `// AN:` output path
  interleaves that block straight into the caller's `Span<int>`/`Span<float>` (no intermediate `byte[]`; the upstream `ConvertOutputToBytes`
  path stays only for MD5 verification). `Flac_Decoder` carries the decoded frame across `ReadFrames` calls of any size. Works on
  non-seekable streams as-is; the reference `BitReader` reads through a 64 KiB buffer (not `Stream.ReadByte`). MD5 is OFF by default;
  `Flac_DecoderOptions.VerifyMd5` turns it on (synchronous hash per frame) — `HasMd5` is false and verification is skipped when STREAMINFO carries
  an all-zero signature ("not computed"; the Salamander set does this); `Md5Verified` is set at end of stream when it matched.
- Truncation (D12): `EndOfStreamException` mid-frame or fewer samples than STREAMINFO declared → `EndedEarly`, no throw. Malformed → the reference
  decoder's `InvalidDataException` is translated to `AudioDecoder_FormatException`; its `NotSupportedException` (property change mid-stream, >32-bit) to `AudioDecoder_UnsupportedException`.
  The frame-header CRC-8 is verified (upstream skipped it) so both the decoder and the seek scanner reject false syncs.
- Seek (D11, built): SEEKTABLE when present (`FindBefore(target)`); otherwise a binary search over byte offsets, each probe doing a forward
  scan for a sync (`0xFFF8`/`0xFFF9`) whose header passes CRC-8 and agrees with STREAMINFO (channels, bits); then the decoder is re-seated
  at that frame and decodes forward into the frame containing the target. `SeekToFrame` on a non-seekable stream throws `NotSupportedException`.
- Bit depths: 16/24 (Salamander) and 8/12/20/32 all decode; the float conversion uses STREAMINFO bits (`/ 2^(bits-1)`). The reference decoder's
  `AllowNonstandardByteOutput` is set true so the MD5 byte path works for every depth (MD5 is defined over the FLAC byte layout).

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
  through; `CanSeek` mirrors the inner stream; `Position` is correct in both modes; `Peek(n)`, `Skip(n)`, `ReadFully`, `ReadExactlyOrThrow`. THE reason non-seekable sources work.
- `ForwardOnlyStream` (tests only) — a `Stream` over a `byte[]` that reports `CanSeek = false`, `Length` throws, and hands out
  bytes in small random-sized chunks (1..97 bytes) to simulate a network read. Every format's tests run once seekable, once forward-only.
- Sample-format conversion is NOT here: it is `AN.Audio.AudioSampleConvert` in `AN.Audio.Common` (D15), shared with the device layer.
- `BitReader` — little/big-endian header field reads for WAV/AIFF; FLAC keeps the reference decoder's own (adapted) bit reader.
- `MpegFrameHeader` — MPEG-1/2/2.5 layer I–III frame-header validation and frame length (bitrate/sample-rate tables) for the sniff table's "two consecutive valid frames" rule. Decoding itself is NLayer's (Phase 3).

## Repository changes

```
src/AN.Audio.Common/               (D15) AudioFormat, SampleFormat, AudioChannelMask, AudioBufferView, AudioSampleConvert — nothing else
  AN.Audio.Common.csproj           net8.0;net9.0;net10.0, PackageId ArtificialNecessity.Audio.Common, no dependencies
src/AN.Audio/                      gains ProjectReference → AN.Audio.Common; AudioFormat.cs MOVED out (git mv); Internal/AudioFormatConverter STAYS
                                   (device-side: resampler glue, channel mapping) and delegates its Int16<->float scalars to AudioSampleConvert
src/AN.Audio.Formats/
  AN.Audio.Formats.csproj        net8.0;net9.0;net10.0, PackageId ArtificialNecessity.Audio.Formats, ProjectReference AN.Audio.Common, PackageReference NLayer (Phase 3)
  AudioDecoder.cs                Open / Sniff / DecodeAll / HintFromExtension
  IAudioDecoder.cs  AudioDecoder_StreamInfo.cs  AudioDecoder_Pcm.cs  AudioDecoder_Exceptions.cs  AudioDecoder_Enums.cs
  Internal/                      PeekableStream, BitReader, MpegFrameHeader
  Wav/                           Wav_Decoder.cs, Wav_FormatChunk.cs (Wav_FormatTag + Wav_FormatChunk), Wav_ChunkId.cs (Wav_ChunkId + Wav_ChunkInfo),
                                 Wav_Metadata.cs (Wav_SamplerChunk, Wav_SampleLoop(+Type), Wav_CuePoint, Wav_InstrumentChunk, Wav_InfoTags)
  Flac/                          Flac_ReferenceDecoder.cs (subsumed SimpleFlac, MIT notice inside, // AN: adaptations) + LICENSE-SimpleFlac.txt (packed),
                                 Flac_Decoder.cs, Flac_MetadataTypes.cs (Flac_MetadataBlockType, Flac_StreamInfo, Flac_SeekPoint, Flac_SeekTable, Flac_Tags, Flac_DecoderOptions)
  Mp3/                           (Phase 3) Mp3_Decoder, Mp3_Id3v2, Mp3_Id3Tags
tests/AN.Audio.Common.Tests/     xunit: AudioFormat, AudioChannelMask, AudioBufferView, AudioSampleConvert (21 tests)
tests/AN.Audio.Formats.Tests/    xunit (110 tests); Support/ ForwardOnlyStream, Wav_TestWriter, FlacFixtureTools; Fixtures/ (README.md + cartesia_tts_test{,_24}.{wav,flac});
                                 SniffTests, PeekableStreamTests, WavDecoderTests(+_ForwardOnlyOverrun), FlacDecoderTests, LocalFileTests
cmd/publish-local.cs             packs the solution (4 packages); projects added to AN.Audio.slnx
_EXTERNAL_APIS/SimpleFlac_FlacDecoder.md, NLayer_MpegFile.md, WaveFormatExtensible_ChannelMask.md
```

`AN.Audio.Build.props` is imported as in the other projects (timestamp versioning, analyzers, `artifacts/`). `<AllowUnsafeBlocks>false</AllowUnsafeBlocks>`
— nothing here needs it.

## Test plan (every phase adds its rows; all hardware-free)

| Area | Test |
|---|---|
| Sniff ✅ | each magic → container; ID3-prefixed MP3 (also when the tag exceeds the peek window); bare sync needs two valid frames; invalid bitrate/rate/version rejected; garbage → Unsupported; hint tiebreak only when content says nothing; `HintFromExtension` |
| PeekableStream ✅ | peek/replay across the buffer boundary, skip from buffer + inner, short peek at EOF, seek fast path inside the buffer, forward-only throws on seek, `leaveOpen` |
| WAV ✅ | synthesized fixtures written by a test-side `Wav_TestWriter`: 8/16/24/32 PCM, float32/64, 1/2/6 ch, EXTENSIBLE with masks (20-in-24, 20-in-32, float 5.1), odd chunk WITHOUT pad (the 99Sounds case), odd chunks with pad, RIFF size 0 / 0xFFFFFFFF / oversize, `data` length 0xFFFFFFFF, `data` overrun (RIFF-bounded and EOF-discovered), mid-frame truncation, `smpl`+`cue`+`LIST`+`inst` before and after `data`, `fmt` after `data`, unsupported tags named, malformed headers; bit-exact float AND native expectations; seek; `ReadFramesNative(AudioBufferView)` guards |
| FLAC ✅ | fixture FLAC vs its WAV rendering: bit-exact int32 and float (16- and 24-bit); MD5 verify on (pass + detected corruption); tags; truncation → `EndedEarly`; corrupt sync → `FormatException`; total-samples = 0 → `TotalFrames == null`; seek with SEEKTABLE (synthesised) and via frame-header search; forward-only seek throws |
| MP3 | fixture decodes to expected frame count ± 1 granule, sample rate/channels, ID3v2 title; `TotalFrames` null on forward-only stream |
| Streaming ✅ | EVERY fixture through `ForwardOnlyStream`; results identical to the seekable run; `EndedEarly` on a truncated copy |
| Allocation ✅ | D13: zero bytes allocated across 100 `ReadFrames` after warm-up, per format (WAV 16/24/float, FLAC). Note: xunit's `Assert.Equal<T>` allocates — count inside the loop, assert after |
| Facade ✅ | `DecodeAll` == concatenated `ReadFrames` (growable path with unknown length); `leaveOpen` honoured |
| Local ✅ | `[Trait("Category","Local")]`, skipped when the file is absent: `clap-808.wav` (24-bit mono, un-padded trailing `id3 `), Salamander `A0v3.flac` (24-bit stereo, zero MD5, seek == linear) |

Fixture provenance: `AssetSource/cartesia_tts_test.wav` (ours) is the WAV master; its FLAC and MP3 renderings are generated ONCE with
`ffmpeg` (if present on the dev machine — **Open Question Q1**) and committed under `tests/AN.Audio.Formats.Tests/Fixtures/` with a
`README.md` recording the exact command lines. Salamander files are NOT committed (license); a `[Trait("Category","Local")]` test decodes
`C:\PROJECTS\3P_SalamanderGrandPiano\Samples\A0v3.flac` when it exists.

## Phases

### Phase 1 — package skeleton + WAV done right (unblocks MusicStudio today)
Built 2026-09-08, commits `81d92b7` (Common) and `9584806` (Formats skeleton + WAV, one commit because the facade references `Wav_Decoder`). Published `0.260908.234621`.
- [x] `src/AN.Audio.Common/` (D15): move `AudioFormat`/`SampleFormat` from `AN.Audio` (`git mv`), widen `SampleFormat`, add `AudioChannelMask`, `AudioBufferView`,
      `AudioSampleConvert` (new; `AN.Audio/Internal/AudioFormatConverter` stays and delegates its two per-sample helpers to it); `AN.Audio` gets the `ProjectReference`; existing `AN.Audio.Tests` still pass; spec 30 D27 + overview amended
- [x] `src/AN.Audio.Formats/` project, csproj per the Midi one (`IsPackable`, README/LICENSE pack items, `InternalsVisibleTo` tests); add to `AN.Audio.slnx`
- [x] Generic tier: `IAudioDecoder` (incl. `NativeFormat`/`ReadFramesNative`, D16), `AudioDecoder_StreamInfo`, enums, exceptions, `AudioDecoder.Open/Sniff/DecodeAll`, `AudioDecoder_Pcm`
- [x] `Internal/PeekableStream`, `Internal/BitReader`, `Internal/MpegFrameHeader` (sniff-time MPEG header validation)
- [x] `Wav/` per §WAV including `smpl`/`cue`/`LIST` metadata and streaming reads; `ReadFramesNative` for PCM = a straight byte copy from the stream
- [x] `tests/AN.Audio.Formats.Tests` with `Wav_TestWriter`, `ForwardOnlyStream`, the WAV rows + sniff + facade + allocation rows (88 tests at the end of Phase 1); Local test on `clap-808.wav` passes seekable and forward-only
- [x] `_EXTERNAL_APIS/` notes; overview spec amendment (D14); README package list
- [x] `cmd/publish-local` → LocalNuGet (`0.260908.234621`, superseded by `0.260908.235755` after Phase 2)
- [x] **MusicStudio** (done 2026-09-09 in `C:\PROJECTS\AN_MusicStudio`; details in the IMPL Phase 1d list — `InstrumentSample.cs` now lives at `Nodes/Source/Sampler/`, >2 ch takes the front pair, 321/321 library WAVs decode): bump `ANAudioVersion` to `0.260908.235755` and reference `ArtificialNecessity.Audio.Formats`;
      `InstrumentSample.Decode` → `AudioDecoder.DecodeAll` + mono→stereo up-mix / >2ch→stereo down-mix in ONE adapter
      (`InstrumentSample.FromDecoded`), `SourceBitsPerSample` from `Info.SourceBitDepth`; delete the hand-rolled RIFF walker; `InstrumentSampleChecks`
      gain the odd-pad and EXTENSIBLE cases; `FormatDescription` gains the container (`FLAC · 48000 Hz · 24-bit …`). Verify `clap-808.wav` loads.

### Phase 2 — FLAC
Built 2026-09-08, commit `f31655c`. Published `0.260908.235755`. 110 Formats tests green; Local test on Salamander `A0v3.flac` (24-bit stereo, 1079560 frames, all-zero MD5) decodes fully and seek == linear decode.
- [x] Subsume `FlacDecoder.cs` → `Flac/Flac_ReferenceDecoder.cs` + `Flac/LICENSE-SimpleFlac.txt` (2026-09-08, upstream `dc149aa`)
- [x] `// AN:` adaptations: namespace `AN.Audio.Formats.Flac`, `internal sealed`, metadata-block loop parses STREAMINFO/VORBIS_COMMENT/SEEKTABLE and records PICTURE/PADDING/APPLICATION/CUESHEET as present, `Options` defaults to no byte conversion / no MD5, interleaved `Span<int>`/`Span<float>` frame output (D16), tabs → spaces per repo style; plus buffered re-seatable `BitReader`, frame-header CRC-8 check, coded frame number → `BufferFirstSample`, `EndedShort` instead of throwing, MD5 skipped when STREAMINFO's signature is all zero
- [x] `Flac_Decoder` + `Flac_MetadataTypes.cs` (`Flac_StreamInfo`, `Flac_Tags`, `Flac_SeekTable`/`Flac_SeekPoint`, `Flac_MetadataBlockType`, `Flac_DecoderOptions`) + `ReadFramesNative` (Int32) / `ReadFrames` (float); `AudioDecoder.Open` routes `fLaC`
- [x] Tests (22): fixture bit-exactness vs WAV (16/24-bit, seekable + forward-only, odd read sizes), float parity via `AudioSampleConvert`, `DecodeAll` parity, MD5 pass + corruption, tags, truncation → `EndedEarly`, corrupt sync → `FormatException`, zero allocation, total = 0 → `TotalFrames == null`, Salamander local test
- [x] 2b: `SeekToFrame` via SEEKTABLE when present, else byte-offset binary search with a forward sync scan validated by CRC-8 + STREAMINFO agreement; tested with a synthesised SEEKTABLE (`FlacFixtureTools`) and without
- [x] MusicStudio: file pickers offer `wav` + `flac`; `A0v3.flac` verified (2026-09-09)

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

- **Q1** ANSWERED 2026-09-08: `ffmpeg` is on the dev machine (chocolatey). FLAC fixtures (16- and 24-bit) rendered from
  `AssetSource/cartesia_tts_test.wav`; command lines in `tests/AN.Audio.Formats.Tests/Fixtures/README.md`. MP3 fixture: Phase 3, same tool.
- **Q2** NLayer 3.0.0 vs 2.0.1: 3.0.0 fixes the non-seekable read bug (D2 needs it); confirm on `nuget.org` that the `NLayer` 3.0.0 package
  itself still targets `net8.0` with no dependencies before pinning (the search result says so; verify at implementation time).
- **Q3** Should `AudioDecoder_Pcm` also carry the per-format metadata (`Wav_SamplerChunk`, `Flac_Tags`) as an optional `object? Metadata`,
  or must clients wanting metadata use the per-format tier? Built: per-format tier only (keeps the generic type dumb); the
  `AudioDecoder.DecodeAll(IAudioDecoder)` overload lets a caller open `Wav_Decoder`/`Flac_Decoder`, read metadata, then drain the audio.