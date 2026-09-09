# SimpleFlac — `FlacDecoder.cs` (ground truth, read 2026-09-08)

- Repo: https://github.com/jdpurcell/SimpleFlac — MIT (J.D. Purcell C# port; Project Nayuki original Java). Single file `FlacDecoder.cs`, 541 lines, `namespace SimpleFlac`, .NET 8+, no `unsafe`, no dependencies (uses `System.Buffers.Binary`, `System.Numerics`, `System.Security.Cryptography` for MD5, `System.Threading.Tasks`).
- Not on NuGet. Vendored per spec 50 D6.

## Surface

```csharp
public class FlacDecoder : IDisposable {
    public FlacDecoder(Stream input, Options? options = null);   // reads metadata in ctor; disposes reader on throw
    public FlacDecoder(string path, Options? options = null);    // FileStream 64 KiB SequentialScan
    public long? StreamSampleCount { get; }   // null when STREAMINFO total = 0
    public int SampleRate, ChannelCount, BitsPerSample, BytesPerSample, MaxSamplesPerFrame { get; }
    public long[][] BufferSamples { get; }    // [channel][sample] of the LAST decoded frame
    public byte[] BufferBytes { get; }        // little-endian interleaved PCM of the last frame (when ConvertOutputToBytes)
    public int BufferSampleCount, BufferByteCount { get; }
    public long RunningSampleCount { get; }
    public int BlockAlign => BytesPerSample * ChannelCount;
    public bool DecodeFrame();                // false at end of stream
    public void Dispose();
    public class Options { bool ConvertOutputToBytes; bool ValidateOutputHash /* MD5, requires bytes */; bool AllowNonstandardByteOutput /* 8-bit / non-whole-byte depths in the BYTE path */; }
}
```

## Behaviour notes

- Ctor: checks `fLaC` marker, walks metadata blocks; parses STREAMINFO (type 0) only, **skips every other block byte-by-byte** (`_reader.Skip(8)` per byte). Spec 50 replaces this loop to parse VORBIS_COMMENT / SEEKTABLE.
- Pull model: one FLAC frame per `DecodeFrame()`; samples land in `BufferSamples` as `long`. Perfect for streaming on a non-seekable stream.
- No seek API. Seeking (spec 50 D11) is ours to add.
- Nayuki's decoder is a reference implementation (readable, correct for all subframe types CONSTANT/VERBATIM/FIXED/LPC, stereo decorrelation, CRC); performance is "optimised while readable" — fine for sample loading, measure before using it for hundreds of files at once.