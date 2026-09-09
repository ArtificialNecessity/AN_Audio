using System.Runtime.InteropServices;
using AN.Audio.Formats.Mp3;
using AN.Audio.Formats.Tests.Support;
using Xunit;

namespace AN.Audio.Formats.Tests;

/// <summary>§MP3 rows of the spec 50 test plan against the committed fixtures (see Fixtures/README.md).</summary>
public class Mp3DecoderTests
{
    private const int MasterFrames = 280_576;   // cartesia_tts_test.wav, mono 44100 Hz
    public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    public static IEnumerable<object[]> Modes() { yield return [true]; yield return [false]; }
    private static Stream Src(byte[] bytes, bool seekable) => seekable ? new MemoryStream(bytes) : new ForwardOnlyStream(bytes);
    private static byte[] Cbr() => File.ReadAllBytes(Fixture("cartesia_tts_test.mp3"));

    private static (long frames, float[] pcm) Drain(IAudioDecoder d, int chunk = 3001)
    {
        var all = new List<float>();
        var buf = new float[chunk * d.Info.Channels];
        long total = 0; int g;
        while ((g = d.ReadFrames(buf)) > 0) { total += g; all.AddRange(buf.AsSpan(0, g * d.Info.Channels)); }
        return (total, all.ToArray());
    }

    [Theory, MemberData(nameof(Modes))]
    public void Cbr_Info_And_FrameCount(bool seekable)
    {
        using var d = (Mp3_Decoder)AudioDecoder.Open(Src(Cbr(), seekable));
        Assert.Equal(AudioDecoder_Container.Mp3, d.Info.Container);
        Assert.Equal(AudioDecoder_SourceEncoding.MpegLayer3, d.Info.Encoding);
        Assert.Equal(44100, d.Info.SampleRate);
        Assert.Equal(1, d.Info.Channels);
        Assert.Equal(0, d.Info.SourceBitDepth);
        Assert.Equal(SampleFormat.Float32, d.NativeFormat.Format);
        Assert.Equal(seekable, d.Info.CanSeek);
        // LAME/Lavc writes an Info (CBR) block with the frame count and gapless data: TotalFrames is known even forward-only
        Assert.NotNull(d.Info.TotalFrames);
        Assert.False(d.StreamInfo.IsVbr);
        Assert.Equal(Mp3_Layer.Layer3, d.StreamInfo.Layer);
        Assert.Equal(Mp3_MpegVersion.Mpeg1, d.StreamInfo.Version);
        Assert.Equal(Mp3_ChannelMode.Mono, d.StreamInfo.ChannelMode);
        Assert.Equal(128_000, d.StreamInfo.BitRate);
        Assert.NotNull(d.StreamInfo.DeclaredFrameCount);
        Assert.True(d.Gapless.IsGapless, "LAME writes encoder delay/padding");
        Assert.True(d.StreamInfo.AudioStartOffset > 10, "audio starts after the ID3v2 tag");

        var (frames, _) = Drain(d);
        Assert.Equal(d.Info.TotalFrames!.Value, frames);
        Assert.Equal(frames, d.FramesRead);
        Assert.False(d.EndedEarly);
        // gapless trim: decoded length equals the master exactly
        Assert.Equal(MasterFrames, frames);
    }

    [Theory, MemberData(nameof(Modes))]
    public void Decoded_Audio_Resembles_WavMaster(bool seekable)
    {
        // Lossy: compare by normalised correlation rather than samples. With the LAME gapless rule (delay + 529 skipped) the decoded
        // audio must be TIME-ALIGNED with the master: same length, best lag 0, high correlation.
        var master = AudioDecoder.DecodeAll(Fixture("cartesia_tts_test.wav")).Interleaved;
        using var d = AudioDecoder.Open(Src(Cbr(), seekable));
        var (_, mp3) = Drain(d);
        Assert.Equal(master.Length, mp3.Length);   // frames × 1152 − delay − padding == the source length exactly
        double best = 0; int bestLag = int.MinValue;
        int n = Math.Min(master.Length, mp3.Length) - 2400;
        for (int lag = -1152; lag <= 1152; lag++)
        {
            double dot = 0, ea = 0, eb = 0;
            for (int i = 1200; i < n; i += 3)
            {
                int j = i + lag; if (j < 0 || j >= mp3.Length) continue;
                dot += master[i] * mp3[j]; ea += master[i] * master[i]; eb += mp3[j] * mp3[j];
            }
            double c = dot / Math.Sqrt(ea * eb);
            if (c > best) { best = c; bestLag = lag; }
        }
        Assert.True(best > 0.9, $"best correlation {best:F3}");
        Assert.Equal(0, bestLag);
    }

    [Theory, MemberData(nameof(Modes))]
    public void Native_Is_Same_Floats(bool seekable)
    {
        using var a = AudioDecoder.Open(Src(Cbr(), seekable));
        using var b = AudioDecoder.Open(Src(Cbr(), seekable));
        var (_, viaFloat) = Drain(a);
        var native = new byte[viaFloat.Length * 4];
        int total = 0; int g;
        while ((g = b.ReadFramesNative(native.AsSpan(total * 4, Math.Min(4096 * 4, native.Length - total * 4)))) > 0) total += g;
        Assert.Equal(viaFloat.Length, total);
        Assert.Equal(viaFloat, MemoryMarshal.Cast<byte, float>(native).ToArray());
        Assert.Throws<ArgumentException>(() => b.ReadFramesNative(new byte[3]));
    }

    [Theory, MemberData(nameof(Modes))]
    public void DecodeAll_Matches_ReadFrames(bool seekable)
    {
        using var d = AudioDecoder.Open(Src(Cbr(), seekable));
        var (frames, pcm) = Drain(d, 777);
        var all = AudioDecoder.DecodeAll(Src(Cbr(), seekable));
        Assert.Equal(frames, all.FrameCount);
        Assert.Equal(pcm, all.Interleaved);
    }

    [Theory, MemberData(nameof(Modes))]
    public void Vbr_Fixture_Decodes_Fully(bool seekable)
    {
        using var d = (Mp3_Decoder)AudioDecoder.Open(Src(File.ReadAllBytes(Fixture("cartesia_tts_test_vbr.mp3")), seekable));
        Assert.True(d.StreamInfo.IsVbr);
        Assert.Equal(Mp3_Id3Source.Id3v24, d.Tags!.Source);
        Assert.Equal("Cartesia TTS test VBR", d.Tags.Title);
        var (frames, _) = Drain(d);
        Assert.Equal(d.Info.TotalFrames, frames);
        Assert.InRange(frames, MasterFrames - 576, MasterFrames + 576);
        Assert.False(d.EndedEarly);
    }

    [Fact]
    public void NoXing_ForwardOnly_TotalFrames_Null_And_Id3v1_When_Seekable()
    {
        // ffmpeg did not honour -write_id3v1 for this file, so the 128-byte ID3v1.1 tag is synthesised here
        var v1 = new byte[128];
        "TAG"u8.CopyTo(v1);
        System.Text.Encoding.Latin1.GetBytes("Cartesia TTS test v1").CopyTo(v1, 3);
        System.Text.Encoding.Latin1.GetBytes("AN.Audio").CopyTo(v1, 33);
        System.Text.Encoding.Latin1.GetBytes("2026").CopyTo(v1, 93);
        v1[125] = 0; v1[126] = 7; v1[127] = 12;
        byte[] bytes = [.. File.ReadAllBytes(Fixture("cartesia_tts_test_noxing.mp3")), .. v1];
        using (var d = (Mp3_Decoder)AudioDecoder.Open(new ForwardOnlyStream(bytes)))
        {
            Assert.Null(d.Info.TotalFrames);   // D10: no header, cannot scan
            Assert.Null(d.Tags);               // ID3v1 lives at the end: unreachable forward-only
            var (frames, _) = Drain(d);
            Assert.InRange(frames, MasterFrames - 1152, MasterFrames + 2304);   // no gapless trimming without a LAME block
            Assert.False(d.EndedEarly);
        }
        using (var d = (Mp3_Decoder)AudioDecoder.Open(new MemoryStream(bytes)))
        {
            Assert.NotNull(d.Info.TotalFrames);   // seekable: NLayer scans the frames
            Assert.Equal(Mp3_Id3Source.Id3v1, d.Tags!.Source);
            Assert.Equal("Cartesia TTS test v1", d.Tags.Title);
            Assert.Equal("AN.Audio", d.Tags.Artist);
            Assert.Equal("7", d.Tags.Track);
            Assert.Equal("12", d.Tags.Genre);
            var (frames, _) = Drain(d);
            Assert.Equal(d.Info.TotalFrames, frames);
        }
    }

    [Theory, MemberData(nameof(Modes))]
    public void Id3v2_Tags(bool seekable)
    {
        using var d = (Mp3_Decoder)AudioDecoder.Open(Src(Cbr(), seekable));
        Assert.NotNull(d.Tags);
        Assert.Equal(Mp3_Id3Source.Id3v23, d.Tags!.Source);
        Assert.Equal("Cartesia TTS test", d.Tags.Title);
        Assert.Equal("AN.Audio", d.Tags.Artist);
        Assert.Equal("AN.Audio", d.Tags["TPE1"]);
        Assert.Equal(0, d.Tags.PictureCount);
        Assert.Contains("Lavf", d.Tags["TSSE"] ?? "");   // ffmpeg writes its encoder settings frame
        Assert.Equal(d.StreamInfo.AudioStartOffset, d.Tags.TagLength);
    }

    [Theory]
    [InlineData(3, false, false, false)]
    [InlineData(3, true, false, true)]
    [InlineData(4, false, true, false)]
    [InlineData(4, true, false, true)]
    [InlineData(2, false, false, false)]
    [InlineData(2, true, false, false)]
    public void Id3v2_Shapes_And_Picture_Callback(int major, bool unsync, bool footer, bool extendedHeader)
    {
        var png = new byte[3000];
        new Random(7).NextBytes(png);   // random bytes contain 0xFF 0x00 / 0xFF 0xEx pairs → exercises unsync
        png[0] = 0x89; png[1] = (byte)'P'; png[2] = (byte)'N'; png[3] = (byte)'G';
        var frames = new List<Mp3_TestTagWriter.Frame>
        {
            Mp3_TestTagWriter.Text("TIT2", "Título — 日本", encoding: (byte)(major == 2 ? 1 : 3)),
            Mp3_TestTagWriter.Text("TPE1", "Artist É", encoding: 0),
            Mp3_TestTagWriter.Text("TALB", "Album", encoding: 1),
            Mp3_TestTagWriter.Text("TRCK", "7/12", encoding: 2),
            Mp3_TestTagWriter.Text(major == 4 ? "TDRC" : "TYER", "2026"),
            Mp3_TestTagWriter.Comment("a comment"),
            Mp3_TestTagWriter.UserText("replaygain_track_gain", "-3.2 dB"),
            major == 2 ? Mp3_TestTagWriter.PictureV22("PNG", 3, "front", png) : Mp3_TestTagWriter.Picture("image/png", 3, "front", png),
            major == 2 ? Mp3_TestTagWriter.PictureV22("JPG", 4, "back", png[..100]) : Mp3_TestTagWriter.Picture("image/jpeg", 4, "back", png[..100]),
        };
        if (major == 2)
        {
            // v2.2 uses 3-char ids; the writer truncates 4-char ids, so supply the real ones
            frames = [Mp3_TestTagWriter.Text("TT2", "Título — 日本", 1), Mp3_TestTagWriter.Text("TP1", "Artist É", 0), Mp3_TestTagWriter.Text("TAL", "Album", 1),
                      Mp3_TestTagWriter.Text("TRK", "7/12", 0), Mp3_TestTagWriter.Text("TYE", "2026", 0),
                      Mp3_TestTagWriter.PictureV22("PNG", 3, "front", png), Mp3_TestTagWriter.PictureV22("JPG", 4, "back", png[..100])];
        }
        var tag = Mp3_TestTagWriter.Build(major, frames, padding: 64, unsync: unsync, footer: footer, extendedHeader: extendedHeader);
        var mp3 = Mp3_TestTagWriter.Retag(Cbr(), tag);

        foreach (bool seekable in new[] { true, false })
        {
            var received = new List<(AudioDecoder_PictureInfo info, byte[] bytes)>();
            using var d = new Mp3_Decoder(Src(mp3, seekable), new Mp3_DecoderOptions { OnPicture = (in info, span) => received.Add((info, span.ToArray())) });
            Assert.Equal("Título — 日本", d.Tags!.Title);
            Assert.Equal("Artist É", d.Tags.Artist);
            Assert.Equal("Album", d.Tags.Album);
            Assert.Equal("7/12", d.Tags.Track);
            Assert.Equal("2026", d.Tags.Year);
            if (major != 2)
            {
                Assert.Equal("a comment", d.Tags.Comment);
                Assert.Equal("-3.2 dB", d.Tags["TXXX:replaygain_track_gain"]);
            }
            Assert.Equal(2, d.Tags.PictureCount);
            Assert.Equal(2, received.Count);
            Assert.Equal(AudioDecoder_PictureType.FrontCover, received[0].info.Type);
            Assert.Equal("image/png", received[0].info.MimeType);
            Assert.Equal("front", received[0].info.Description);
            Assert.Equal(png.Length, received[0].info.ByteLength);
            Assert.Equal(png, received[0].bytes);
            Assert.Equal(AudioDecoder_PictureType.BackCover, received[1].info.Type);
            Assert.Equal("image/jpeg", received[1].info.MimeType);
            Assert.Equal(png[..100], received[1].bytes);
            Assert.Equal(tag.Length, d.Tags.TagLength);
            Assert.Equal(tag.Length, d.StreamInfo.AudioStartOffset);
            // audio unaffected by the tag
            var (decodedFrames, _) = Drain(d);
            Assert.Equal(d.Info.TotalFrames, decodedFrames);
        }

        // no callback: pictures counted, not delivered, audio identical
        using var noCb = (Mp3_Decoder)AudioDecoder.Open(new MemoryStream(mp3));
        Assert.Equal(2, noCb.Tags!.PictureCount);
    }

    [Fact]
    public void Garbage_Is_Unsupported_Or_FormatException()
    {
        var junk = new byte[5000];
        new Random(3).NextBytes(junk);
        junk[0] = 0; junk[1] = 0;   // make sure it is not an accidental sync
        Assert.ThrowsAny<Exception>(() => AudioDecoder.Open(new MemoryStream(junk), AudioDecoder_FormatHint.Mp3));
    }

    [Fact]
    public void Truncated_Sets_EndedEarly()
    {
        var full = Cbr();
        var bytes = full[..(full.Length * 2 / 3)];
        using var d = (Mp3_Decoder)AudioDecoder.Open(new ForwardOnlyStream(bytes));
        long declared = d.Info.TotalFrames!.Value;   // Info block still promises the full length
        var (frames, _) = Drain(d);
        Assert.True(frames > declared / 2 && frames < declared);
        Assert.True(d.EndedEarly);
    }

    [Fact]
    public void D13_ReadFrames_AllocatesNothing_InSteadyState()
    {
        // The framing is ours (reused frame buffer, reused IMpegFrame); NLayer's MpegFrameDecoder allocates nothing per frame either.
        using var d = AudioDecoder.Open(new MemoryStream(Cbr()));
        var buf = new float[1152];
        for (int i = 0; i < 20; i++) d.ReadFrames(buf);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int wrong = 0;
        for (int i = 0; i < 100; i++) if (d.ReadFrames(buf) != 1152) wrong++;
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, wrong);
        Assert.Equal(0, after - before);
    }

    // ── seeking (D11) ──

    [Fact]
    public void Seek_Equals_Linear()
    {
        using var lin = AudioDecoder.Open(new MemoryStream(Cbr()));
        var (total, linear) = Drain(lin);
        using var d = (Mp3_Decoder)AudioDecoder.Open(new MemoryStream(Cbr()));
        foreach (long target in new long[] { 100_000, 5, 1152, 1153, 200_001, total - 300, 0, total })
        {
            d.SeekToFrame(target);
            Assert.Equal(target, d.FramesRead);
            var got = new float[64];
            int n = d.ReadFrames(got);
            int expectedN = (int)Math.Min(64, total - target);
            Assert.Equal(expectedN, n);
            for (int i = 0; i < n; i++) Assert.Equal(linear[target + i], got[i]);
        }
        // read-through from a mid-point reaches the same end as the linear decode
        d.SeekToFrame(150_000);
        var (rest, tail) = Drain(d);
        Assert.Equal(total - 150_000, rest);
        Assert.Equal(total, d.FramesRead);
        Assert.False(d.EndedEarly);
        Assert.Equal(linear[^100..], tail[^100..]);
    }

    [Fact]
    public void Seek_ForwardOnly_Throws()
    {
        using var d = AudioDecoder.Open(new ForwardOnlyStream(Cbr()));
        Assert.Throws<NotSupportedException>(() => d.SeekToFrame(10));
    }
}