using System.Runtime.InteropServices;
using AN.Audio.Formats.Flac;
using AN.Audio.Formats.Tests.Support;
using AN.Audio.Formats.Wav;
using Xunit;

namespace AN.Audio.Formats.Tests;

/// <summary>§FLAC rows of the spec 50 test plan against the committed fixtures (see Fixtures/README.md).</summary>
public class FlacDecoderTests
{
    public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    public static IEnumerable<object[]> Modes() { yield return [true]; yield return [false]; }

    private static Stream Src(byte[] bytes, bool seekable) => seekable ? new MemoryStream(bytes) : new ForwardOnlyStream(bytes);

    /// <summary>The WAV master decoded to native ints (Int16 or Int24 → int, sign-extended, low bits).</summary>
    private static (int[] samples, AudioDecoder_StreamInfo info) WavMaster(string name)
    {
        using var wav = (Wav_Decoder)AudioDecoder.Open(Fixture(name));
        int frames = (int)wav.Info.TotalFrames!.Value;
        var native = new byte[frames * wav.NativeFormat.BytesPerFrame];
        int total = 0, bpf = wav.NativeFormat.BytesPerFrame;
        while (total < frames) { int g = wav.ReadFramesNative(native.AsSpan(total * bpf)); if (g == 0) break; total += g; }
        Assert.Equal(frames, total);
        var ints = new int[frames * wav.Info.Channels];
        AudioSampleConvert.ToInt32(native, wav.NativeFormat.Format, ints);
        // ToInt32 left-justifies; FLAC native is right-justified (low bits) → shift back
        int shift = 32 - wav.NativeFormat.BitsPerSample;
        for (int i = 0; i < ints.Length; i++) ints[i] >>= shift;
        return (ints, wav.Info);
    }

    [Theory]
    [InlineData("cartesia_tts_test.flac", "cartesia_tts_test.wav", 16, true)]
    [InlineData("cartesia_tts_test.flac", "cartesia_tts_test.wav", 16, false)]
    [InlineData("cartesia_tts_test_24.flac", "cartesia_tts_test_24.wav", 24, true)]
    [InlineData("cartesia_tts_test_24.flac", "cartesia_tts_test_24.wav", 24, false)]
    public void Native_BitExact_Against_WavMaster(string flac, string wav, int bits, bool seekable)
    {
        var (expected, wavInfo) = WavMaster(wav);
        var bytes = File.ReadAllBytes(Fixture(flac));
        using var d = (Flac_Decoder)AudioDecoder.Open(Src(bytes, seekable));
        Assert.Equal(AudioDecoder_Container.Flac, d.Info.Container);
        Assert.Equal(AudioDecoder_SourceEncoding.Flac, d.Info.Encoding);
        Assert.Equal(bits, d.Info.SourceBitDepth);
        Assert.Equal(wavInfo.SampleRate, d.Info.SampleRate);
        Assert.Equal(wavInfo.Channels, d.Info.Channels);
        Assert.Equal(wavInfo.TotalFrames, d.Info.TotalFrames);
        Assert.Equal(SampleFormat.Int32, d.NativeFormat.Format);
        Assert.Equal(seekable, d.Info.CanSeek);
        Assert.Equal(bits, d.StreamInfo.BitsPerSample);
        Assert.True(d.StreamInfo.IsFixedBlockSize);

        var actual = new int[expected.Length];
        var asBytes = MemoryMarshal.AsBytes(actual.AsSpan());
        int total = 0, ch = d.Info.Channels;
        // deliberately odd read sizes so frames straddle calls
        int[] sizes = [1, 7, 4095, 4096, 4097, 100_000];
        int k = 0;
        while (total < expected.Length / ch)
        {
            int want = Math.Min(sizes[k++ % sizes.Length], expected.Length / ch - total);
            int got = d.ReadFramesNative(asBytes.Slice(total * ch * 4, want * ch * 4));
            Assert.True(got > 0);
            total += got;
        }
        Assert.Equal(0, d.ReadFramesNative(new byte[4 * ch]));
        Assert.Equal(expected, actual);
        Assert.False(d.EndedEarly);
        Assert.Equal(expected.Length / ch, d.FramesRead);
    }

    [Theory, MemberData(nameof(Modes))]
    public void Float_Parity_With_AudioSampleConvert(bool seekable)
    {
        var (ints, _) = WavMaster("cartesia_tts_test_24.wav");
        var bytes = File.ReadAllBytes(Fixture("cartesia_tts_test_24.flac"));
        using var d = AudioDecoder.Open(Src(bytes, seekable));
        var floats = new float[ints.Length];
        int total = 0;
        while (total < floats.Length) { int g = d.ReadFrames(floats.AsSpan(total, Math.Min(3001, floats.Length - total))); if (g == 0) break; total += g; }
        Assert.Equal(floats.Length, total);
        // D4/D16: FLAC float == (sample << (32-bits)) as Int32 → Float32 via the ONE conversion
        var widened = new int[ints.Length];
        for (int i = 0; i < ints.Length; i++) widened[i] = ints[i] << 8;
        var viaConvert = new float[ints.Length];
        AudioSampleConvert.ToFloat32(MemoryMarshal.AsBytes(widened.AsSpan()), SampleFormat.Int32, viaConvert);
        for (int i = 0; i < floats.Length; i++) Assert.Equal(viaConvert[i], floats[i], 1e-7f);
    }

    [Theory, MemberData(nameof(Modes))]
    public void DecodeAll_Matches_Wav_DecodeAll(bool seekable)
    {
        var wav = AudioDecoder.DecodeAll(Fixture("cartesia_tts_test.wav"));
        var flac = AudioDecoder.DecodeAll(Src(File.ReadAllBytes(Fixture("cartesia_tts_test.flac")), seekable));
        Assert.Equal(wav.FrameCount, flac.FrameCount);
        Assert.Equal(wav.Interleaved, flac.Interleaved);
    }

    [Theory, MemberData(nameof(Modes))]
    public void VerifyMd5_On_Passes(bool seekable)
    {
        var bytes = File.ReadAllBytes(Fixture("cartesia_tts_test_24.flac"));
        using var d = new Flac_Decoder(Src(bytes, seekable), new Flac_DecoderOptions { VerifyMd5 = true });
        Assert.True(d.HasMd5);
        Assert.False(d.Md5Verified);
        var buf = new float[4096];
        while (d.ReadFrames(buf) > 0) { }
        Assert.True(d.Md5Verified);
        Assert.False(d.EndedEarly);
    }

    [Fact]
    public void VerifyMd5_DetectsCorruption()
    {
        var bytes = File.ReadAllBytes(Fixture("cartesia_tts_test.flac"));
        // flip a residual bit deep in the audio (past the metadata); the frame CRC-16 is not checked by the reference decoder,
        // so only MD5 catches it (or a FormatException if the corruption breaks the bitstream structure)
        bytes[bytes.Length / 2] ^= 0x10;
        using var d = new Flac_Decoder(new MemoryStream(bytes), new Flac_DecoderOptions { VerifyMd5 = true });
        var buf = new float[4096];
        Assert.ThrowsAny<Exception>(() => { while (d.ReadFrames(buf) > 0) { } });
    }

    [Theory, MemberData(nameof(Modes))]
    public void VorbisComment_Tags(bool seekable)
    {
        using var d = (Flac_Decoder)AudioDecoder.Open(Src(File.ReadAllBytes(Fixture("cartesia_tts_test.flac")), seekable));
        Assert.NotNull(d.Tags);
        Assert.Equal("Cartesia TTS test", d.Tags!.Title);
        Assert.Equal("AN.Audio", d.Tags.Artist);
        Assert.Equal("AN.Audio", d.Tags["artist"]);   // case-insensitive
        Assert.Contains("Lavf", d.Tags.Vendor);
        Assert.Contains(Flac_MetadataBlockType.StreamInfo, d.MetadataBlocksPresent);
        Assert.Contains(Flac_MetadataBlockType.VorbisComment, d.MetadataBlocksPresent);
    }

    [Theory, MemberData(nameof(Modes))]
    public void Truncated_EndedEarly(bool seekable)
    {
        var full = File.ReadAllBytes(Fixture("cartesia_tts_test.flac"));
        var bytes = full[..(full.Length * 2 / 3)];
        using var d = (Flac_Decoder)AudioDecoder.Open(Src(bytes, seekable));
        Assert.Equal(280576, d.Info.TotalFrames);   // header still promises the full length
        var buf = new float[4096];
        long total = 0; int g;
        while ((g = d.ReadFrames(buf)) > 0) total += g;
        Assert.True(total > 100_000 && total < 280576);
        Assert.True(d.EndedEarly);
    }

    [Fact]
    public void Corrupt_FrameSync_Is_FormatException()
    {
        var bytes = File.ReadAllBytes(Fixture("cartesia_tts_test.flac"));
        var (_, audioStart) = FlacFixtureTools.WalkMetadata(bytes);
        bytes[audioStart] = 0x00;   // destroy the first frame's sync code
        var ex = Assert.ThrowsAny<IOException>(() =>
        {
            using var d = AudioDecoder.Open(new MemoryStream(bytes));
            var buf = new float[4096];
            while (d.ReadFrames(buf) > 0) { }
        });
        Assert.IsType<AudioDecoder_FormatException>(ex);
    }

    [Fact]
    public void D13_ReadFrames_AllocatesNothing_InSteadyState()
    {
        var bytes = File.ReadAllBytes(Fixture("cartesia_tts_test.flac"));
        using var d = AudioDecoder.Open(new MemoryStream(bytes));
        var buf = new float[512];
        for (int i = 0; i < 20; i++) d.ReadFrames(buf);
        int wrong = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) if (d.ReadFrames(buf) != 512) wrong++;
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, wrong);
        Assert.Equal(0, after - before);
    }

    // ── seeking (2b) ──

    [Fact]
    public void Seek_WithoutSeekTable_FrameHeaderSearch()
    {
        var (expected, _) = WavMaster("cartesia_tts_test.wav");
        var bytes = File.ReadAllBytes(Fixture("cartesia_tts_test.flac"));
        using var d = (Flac_Decoder)AudioDecoder.Open(new MemoryStream(bytes));
        Assert.Null(d.SeekTable);   // ffmpeg writes none by default
        foreach (long target in new long[] { 200_000, 5, 123_457, 280_000, 0, 280_576 })
        {
            d.SeekToFrame(target);
            Assert.Equal(target, d.FramesRead);
            var got = new int[64];
            int n = d.ReadFramesNative(MemoryMarshal.AsBytes(got.AsSpan()));
            int expectedN = (int)Math.Min(64, 280_576 - target);
            Assert.Equal(expectedN, n);
            for (int i = 0; i < n; i++) Assert.Equal(expected[target + i], got[i]);
        }
        // after seeking back, a full read-through still yields the full stream
        d.SeekToFrame(0);
        var buf = new float[4096];
        long total = 0; int g;
        while ((g = d.ReadFrames(buf)) > 0) total += g;
        Assert.Equal(280_576, total);
        Assert.False(d.EndedEarly);
    }

    [Fact]
    public void Seek_ForwardOnly_Throws()
    {
        using var d = AudioDecoder.Open(new ForwardOnlyStream(File.ReadAllBytes(Fixture("cartesia_tts_test.flac"))));
        Assert.Throws<NotSupportedException>(() => d.SeekToFrame(10));
    }

    [Fact]
    public void Seek_WithSeekTable()
    {
        // Synthesise a SEEKTABLE: decode once recording frame offsets, splice a SEEKTABLE block after STREAMINFO
        var bytes = File.ReadAllBytes(Fixture("cartesia_tts_test.flac"));
        var (expected, _) = WavMaster("cartesia_tts_test.wav");
        var withTable = FlacFixtureTools.InsertSeekTable(bytes, everyNthFrame: 10);
        using var d = (Flac_Decoder)AudioDecoder.Open(new MemoryStream(withTable));
        Assert.NotNull(d.SeekTable);
        Assert.True(d.SeekTable!.Points.Count > 3);
        foreach (long target in new long[] { 150_000, 1, 279_999 })
        {
            d.SeekToFrame(target);
            var got = new int[16];
            int n = d.ReadFramesNative(MemoryMarshal.AsBytes(got.AsSpan()));
            Assert.Equal(16, n);
            for (int i = 0; i < n; i++) Assert.Equal(expected[target + i], got[i]);
        }
    }

    [Fact]
    public void TotalSamplesZero_Means_Unknown()
    {
        var bytes = File.ReadAllBytes(Fixture("cartesia_tts_test.flac"));
        FlacFixtureTools.ZeroTotalSamples(bytes);
        using var d = (Flac_Decoder)AudioDecoder.Open(new ForwardOnlyStream(bytes));
        Assert.Null(d.Info.TotalFrames);
        Assert.Null(d.StreamInfo.TotalSamplesOrNull);
        var pcm = AudioDecoder.DecodeAll(d);
        Assert.Equal(280_576, pcm.FrameCount);
        Assert.False(pcm.EndedEarly);
    }
}