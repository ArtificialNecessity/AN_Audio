# NLayer — `MpegFile` (ground truth, read 2026-09-08)

- Repo: https://github.com/naudio/NLayer — MIT. NuGet `NLayer` 2.0.1 and **3.0.0**: both `netstandard2.0` + `net8.0`, **no dependencies**. (`NLayer.NAudioSupport` 3.x is `net9.0` + `NAudio.Core` 3 — we do not use it.)
- Fully managed MPEG-1/2 audio decoder, layers I, II, III (port of JavaLayer). Output is float.

## Surface (from `NLayer/MpegFile.cs`)

```csharp
namespace NLayer;
public class MpegFile : IDisposable {
    public MpegFile(string fileName);          // opens + owns the FileStream
    public MpegFile(Stream stream);            // does NOT dispose the stream
    public int SampleRate { get; }
    public int Channels { get; }               // output channels (StereoMode aware)
    public bool CanSeek { get; }
    public long Length { get; }                // decoded PCM BYTES (float32 * channels), -1 when sample count unknown
    public TimeSpan Duration { get; }          // TimeSpan.Zero when unknown
    public long Position { get; set; }         // BYTES; setter seeks (throws if !CanSeek)
    public TimeSpan Time { get; set; }
    public void SetEQ(float[] eq);             // 32 bands, dB
    public StereoMode StereoMode { get; set; } // Both | LeftOnly | RightOnly | DownmixToMono
    public int ReadSamples(byte[] buffer, int index, int count);   // float32 written as bytes
    public int ReadSamples(float[] buffer, int index, int count);  // interleaved floats; returns SAMPLES (not frames) read
    // net8.0 asset (3.0.0): Span<float> / Span<byte> overloads of ReadSamples
}
```

## Behaviour notes

- Encoder delay/padding from the LAME/Xing tag are trimmed automatically (gapless); `Length`/`Duration` already exclude them.
- Sample count comes from the Xing/VBRI/LAME header or, on a seekable stream, a frame scan; on a non-seekable stream without a header it is unknown → `Length == -1`. Map to spec 50 `TotalFrames == null`.
- **2.0.1 bug**: reading from a NON-seekable stream throws `ArgumentOutOfRangeException` on the first read (end-of-stream trimming used `long.MaxValue`). Fixed in 3.0.0 release notes. Spec 50 pins 3.0.0 and tests a forward-only stream.
- Frames in ≈ 1152 samples/channel (layer III MPEG-1); `ReadSamples` may return fewer than requested at frame boundaries and at the end.