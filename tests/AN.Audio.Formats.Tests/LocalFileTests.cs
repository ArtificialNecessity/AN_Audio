using AN.Audio.Formats.Tests.Support;
using AN.Audio.Formats.Wav;
using Xunit;

namespace AN.Audio.Formats.Tests;

/// <summary>
/// Tests against real files that live on the dev machine and are NOT committed (licence). Each skips when its file is
/// absent, so CI stays green. Filter with <c>dotnet test --filter Category=Local</c>.
/// </summary>
[Trait("Category", "Local")]
public class LocalFileTests
{
    // fmt: PCM, mono, 44100 Hz, blockAlign 3, 24-bit; data 142080 bytes; trailing 'id3 ' 215 bytes, no pad byte (file length odd)
    public static readonly string Clap808 = @"C:\Users\david\Documents\AudioSamples\Instruments\NoRedistribution\99Sounds_Drum_Samples\clap-808.wav";

    [Fact]
    public void Clap808_TrailingOddId3WithoutPad_Decodes()
    {
        if (!File.Exists(Clap808)) return;   // skipped when absent

        var bytes = File.ReadAllBytes(Clap808);
        Assert.Equal(1, bytes.Length % 2);   // the whole file is odd: the last chunk's pad byte is missing

        foreach (bool seekable in new[] { true, false })
        {
            using var d = (Wav_Decoder)AudioDecoder.Open(seekable ? new MemoryStream(bytes) : new ForwardOnlyStream(bytes));
            Assert.Equal(24, d.Info.SourceBitDepth);
            Assert.Equal(SampleFormat.Int24, d.NativeFormat.Format);
            Assert.Equal(1, d.Info.Channels);
            Assert.Equal(44100, d.Info.SampleRate);
            Assert.Equal(142080 / 3, d.Info.TotalFrames);

            var pcm = AudioDecoder.DecodeAll(d);
            Assert.Equal(d.Info.TotalFrames, pcm.FrameCount);
            Assert.False(pcm.EndedEarly);
            Assert.True(d.MetadataComplete);

            var ids = d.Chunks.Select(c => c.Id.ToString()).ToList();
            Assert.Contains("SAUR", ids);
            Assert.Contains("LIST", ids);
            Assert.Contains("id3 ", ids);
            var id3 = d.Chunks.Last();
            Assert.Equal("id3 ", id3.Id.ToString());
            Assert.Equal(215u, id3.DeclaredLength);
            Assert.False(id3.PadBytePresent);
        }

        // and through the path overload
        var viaPath = AudioDecoder.DecodeAll(Clap808);
        Assert.Equal(142080 / 3, viaPath.FrameCount);
    }

    public static readonly string SalamanderA0v3 = @"C:\PROJECTS\3P_SalamanderGrandPiano\Samples\A0v3.flac";

    [Fact]
    public void Salamander_A0v3_24bitStereo_DecodesFully_Md5Verifies()
    {
        if (!File.Exists(SalamanderA0v3)) return;   // skipped when absent

        using var fs = new FileStream(SalamanderA0v3, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        using var d = new Flac.Flac_Decoder(fs, new Flac.Flac_DecoderOptions { VerifyMd5 = true });
        Assert.Equal(24, d.Info.SourceBitDepth);
        Assert.Equal(2, d.Info.Channels);
        Assert.Equal(48000, d.Info.SampleRate);
        Assert.Equal(1079560, d.Info.TotalFrames);   // ffprobe duration_ts
        // Salamander's encoder wrote an all-zero MD5 (legal: "not computed"); verification is only meaningful when present

        var buf = new float[4096 * 2];
        long total = 0; int g;
        while ((g = d.ReadFrames(buf)) > 0) total += g;
        Assert.Equal(d.Info.TotalFrames, total);
        Assert.False(d.EndedEarly);
        Assert.Equal(d.HasMd5, d.Md5Verified);

        // Seek round-trip on a real 24-bit stereo file (no SEEKTABLE expected from most encoders)
        long target = total / 2;
        d.SeekToFrame(target);
        var a = new int[2 * 32];
        Assert.Equal(32, d.ReadFramesNative(System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan())));
        // compare with a linear decode to the same position
        using var lin = (Flac.Flac_Decoder)AudioDecoder.Open(SalamanderA0v3);
        var skip = new int[2 * 4096];
        long pos = 0;
        while (pos < target) { int want = (int)Math.Min(4096, target - pos); pos += lin.ReadFramesNative(System.Runtime.InteropServices.MemoryMarshal.AsBytes(skip.AsSpan(0, want * 2))); }
        var b = new int[2 * 32];
        Assert.Equal(32, lin.ReadFramesNative(System.Runtime.InteropServices.MemoryMarshal.AsBytes(b.AsSpan())));
        Assert.Equal(b, a);
    }
}