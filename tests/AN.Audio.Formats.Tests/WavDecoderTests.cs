using System.Runtime.InteropServices;
using AN.Audio.Formats.Tests.Support;
using AN.Audio.Formats.Wav;
using Xunit;

namespace AN.Audio.Formats.Tests;

/// <summary>§WAV rows of the spec 50 test plan. Every case runs seekable (MemoryStream) AND through ForwardOnlyStream.</summary>
public class WavDecoderTests
{
    public static IEnumerable<object[]> Modes() { yield return [true]; yield return [false]; }

    private static Stream Src(byte[] bytes, bool seekable) => seekable ? new MemoryStream(bytes) : new ForwardOnlyStream(bytes);

    private static Wav_Decoder OpenWav(byte[] bytes, bool seekable)
    {
        var d = AudioDecoder.Open(Src(bytes, seekable));
        return Assert.IsType<Wav_Decoder>(d);
    }

    /// <summary>Reads everything via ReadFrames with a deliberately awkward chunk size, asserts bit-exact D4 floats.</summary>
    private static void AssertFloatsExact(Wav_TestWriter w, IAudioDecoder d, int frames, int chunkFrames = 1000)
    {
        int ch = w.Channels;
        var all = new float[frames * ch];
        int total = 0;
        while (true)
        {
            int want = Math.Min(chunkFrames, frames - total);
            if (want == 0) { Assert.Equal(0, d.ReadFrames(new float[ch * 3])); break; }
            int got = d.ReadFrames(all.AsSpan(total * ch, want * ch));
            if (got == 0) break;
            total += got;
        }
        Assert.Equal(frames, total);
        Assert.Equal(frames, d.FramesRead);
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < ch; c++)
                Assert.Equal(w.ExpectedFloat(f, c), all[f * ch + c]);
    }

    // ── the PCM/float matrix ──

    public static IEnumerable<object[]> FormatMatrix()
    {
        foreach (bool seekable in new[] { true, false })
        {
            yield return [seekable, 8, 1, false, SampleFormat.UInt8];
            yield return [seekable, 16, 2, false, SampleFormat.Int16];
            yield return [seekable, 24, 2, false, SampleFormat.Int24];
            yield return [seekable, 32, 6, false, SampleFormat.Int32];
            yield return [seekable, 32, 1, true, SampleFormat.Float32];
            yield return [seekable, 64, 2, true, SampleFormat.Float64];
            yield return [seekable, 16, 6, false, SampleFormat.Int16];
        }
    }

    [Theory, MemberData(nameof(FormatMatrix))]
    public void Pcm_AllFormats_BitExact_Float_And_Native(bool seekable, int bits, int channels, bool isFloat, SampleFormat expectedNative)
    {
        const int frames = 3000;
        var w = new Wav_TestWriter().Fmt(channels, 44100, bits, isFloat);
        var pcm = w.Pcm(frames);
        var bytes = w.Data(pcm).Build();

        using (var d = OpenWav(bytes, seekable))
        {
            Assert.Equal(new AudioFormat(44100, channels, expectedNative), d.NativeFormat);
            Assert.Equal(frames, d.Info.TotalFrames);
            Assert.Equal(bits, d.Info.SourceBitDepth);
            Assert.Equal(isFloat ? AudioDecoder_SourceEncoding.PcmFloat : AudioDecoder_SourceEncoding.PcmInt, d.Info.Encoding);
            Assert.Equal(seekable, d.Info.CanSeek);
            AssertFloatsExact(w, d, frames, chunkFrames: 777);
            Assert.False(d.EndedEarly);
        }

        // native path = straight byte copy
        using (var d = OpenWav(bytes, seekable))
        {
            var native = new byte[pcm.Length];
            int total = 0, bpf = d.NativeFormat.BytesPerFrame;
            while (total < frames)
            {
                int want = Math.Min(513, frames - total);
                int got = d.ReadFramesNative(native.AsSpan(total * bpf, want * bpf));
                Assert.True(got > 0);
                total += got;
            }
            Assert.Equal(0, d.ReadFramesNative(new byte[bpf]));
            Assert.Equal(pcm, native);
        }
    }

    [Theory, MemberData(nameof(Modes))]
    public void Extensible_24in32_ValidBits20_Mask(bool seekable)
    {
        var w = new Wav_TestWriter().FmtExtensible(2, 96000, containerBits: 24, validBits: 20, AudioChannelMask.Stereo);
        var bytes = w.Data(w.Pcm(500)).Build();
        using var d = OpenWav(bytes, seekable);
        Assert.Equal(Wav_FormatTag.Extensible, d.Format.FormatTag);
        Assert.Equal(Wav_FormatTag.Pcm, d.Format.EffectiveTag);
        Assert.Equal(20, d.Format.ValidBitsPerSample);
        Assert.Equal(20, d.Info.SourceBitDepth);
        Assert.Equal(AudioChannelMask.Stereo, d.Format.ChannelMask);
        Assert.Equal(SampleFormat.Int24, d.NativeFormat.Format);
        AssertFloatsExact(w, d, 500);
    }

    [Theory, MemberData(nameof(Modes))]
    public void Extensible_Float32_And_SixChannel_Mask(bool seekable)
    {
        var w = new Wav_TestWriter().FmtExtensible(6, 48000, 32, 32, AudioChannelMask.FivePointOne, isFloat: true);
        var bytes = w.Data(w.Pcm(200)).Build();
        using var d = OpenWav(bytes, seekable);
        Assert.Equal(Wav_FormatTag.IeeeFloat, d.Format.EffectiveTag);
        Assert.Equal(SampleFormat.Float32, d.NativeFormat.Format);
        Assert.Equal(AudioChannelMask.FivePointOne, d.Format.ChannelMask);
        Assert.Equal(6, d.Format.ChannelMask.PositionCount());
        AssertFloatsExact(w, d, 200);
    }

    [Theory, MemberData(nameof(Modes))]
    public void Extensible_20in32(bool seekable)
    {
        var w = new Wav_TestWriter().FmtExtensible(1, 48000, containerBits: 32, validBits: 20, AudioChannelMask.Mono);
        var bytes = w.Data(w.Pcm(100)).Build();
        using var d = OpenWav(bytes, seekable);
        Assert.Equal(SampleFormat.Int32, d.NativeFormat.Format);
        Assert.Equal(20, d.Info.SourceBitDepth);
        AssertFloatsExact(w, d, 100);
    }

    // ── tolerance cases ──

    [Theory, MemberData(nameof(Modes))]
    public void OddChunk_WithoutPadByte_AtEnd_Accepted(bool seekable)
    {
        // the clap-808 shape: trailing odd-length 'id3 ' chunk, writer omitted the pad byte
        var w = new Wav_TestWriter().Fmt(2, 44100, 16);
        var pcm = w.Pcm(100);
        var bytes = w.Data(pcm).Raw("SAUR", new byte[10]).Raw("id3 ", new byte[215], pad: false).Build();
        Assert.Equal(1, bytes.Length % 2);

        using var d = OpenWav(bytes, seekable);
        Assert.Equal(100, d.Info.TotalFrames);
        var f = new float[200];
        Assert.Equal(100, d.ReadFrames(f));
        Assert.Equal(0, d.ReadFrames(f));
        Assert.False(d.EndedEarly);
        Assert.True(d.MetadataComplete);
        var ids = d.Chunks.Select(c => c.Id.ToString()).ToArray();
        Assert.Equal(new[] { "fmt ", "data", "SAUR", "id3 " }, ids);
        var id3 = d.Chunks.Last();
        Assert.Equal(215u, id3.DeclaredLength);
        Assert.False(id3.PadBytePresent);
        Assert.Equal(215, id3.RawBytes!.Length);
    }

    [Theory, MemberData(nameof(Modes))]
    public void OddChunk_WithPad_BeforeData(bool seekable)
    {
        var w = new Wav_TestWriter().Fmt(1, 8000, 8);
        var pcm = w.Pcm(11);   // odd data length too
        var bytes = w.Raw("junk", new byte[7]).Data(pcm).Raw("tail", new byte[3]).Build();
        using var d = OpenWav(bytes, seekable);
        Assert.Equal(11, d.Info.TotalFrames);
        AssertFloatsExact(w, d, 11);
        Assert.Contains(d.Chunks, c => c.Id.ToString() == "junk" && c.PadBytePresent);
        Assert.Contains(d.Chunks, c => c.Id.ToString() == "tail" && c.PadBytePresent);
    }

    [Theory]
    [InlineData(true, 0u)]
    [InlineData(false, 0u)]
    [InlineData(true, 0xFFFFFFFFu)]
    [InlineData(false, 0xFFFFFFFFu)]
    [InlineData(true, 0x7FFFFFF0u)]   // oversize (larger than the stream) → unknown
    public void RiffSize_Unknown_StillDecodes(bool seekable, uint riffSize)
    {
        var w = new Wav_TestWriter().Fmt(2, 48000, 16);
        var bytes = w.Data(w.Pcm(300)).Build(riffSizeOverride: riffSize);
        using var d = OpenWav(bytes, seekable);
        Assert.False(d.RiffLengthKnown);
        Assert.Equal(300, d.Info.TotalFrames);   // data length itself is declared
        AssertFloatsExact(w, d, 300);
    }

    [Theory, MemberData(nameof(Modes))]
    public void DataLength_Unknown_0xFFFFFFFF_ReadsToEof(bool seekable)
    {
        var w = new Wav_TestWriter().Fmt(2, 48000, 16);
        var bytes = w.Data(w.Pcm(300), declaredLength: 0xFFFFFFFF).Build(riffSizeOverride: 0);
        using var d = OpenWav(bytes, seekable);
        Assert.Null(d.Info.TotalFrames);
        Assert.Null(d.Info.Duration);
        AssertFloatsExact(w, d, 300);
        Assert.False(d.EndedEarly);
    }

    [Theory, MemberData(nameof(Modes))]
    public void Data_Overrun_Clamped_EndedEarly(bool seekable)
    {
        var w = new Wav_TestWriter().Fmt(2, 48000, 16);
        var pcm = w.Pcm(300);
        var bytes = w.Data(pcm, declaredLength: (uint)pcm.Length * 3).Build();   // declares 900 frames, has 300
        using var d = OpenWav(bytes, seekable);
        // clamped at open in both modes: the (correct) RIFF size bounds the data chunk even on a forward-only stream
        Assert.Equal(300, d.Info.TotalFrames);
        var f = new float[900 * 2];
        int got = d.ReadFrames(f);
        Assert.Equal(300, got);
        Assert.Equal(0, d.ReadFrames(f));
        Assert.True(d.EndedEarly);   // clamped at open (seekable) or discovered at EOF (forward-only)
        for (int i = 0; i < 300; i++) Assert.Equal(w.ExpectedFloat(i, 0), f[i * 2]);
    }

    [Theory, MemberData(nameof(Modes))]
    public void Truncated_MidFrame_EndedEarly(bool seekable)
    {
        var w = new Wav_TestWriter().Fmt(2, 48000, 24);
        var full = w.Data(w.Pcm(100)).Build();
        var bytes = full[..(full.Length - 7)];   // lose one frame and one byte
        using var d = OpenWav(bytes, seekable);
        var f = new float[200];
        Assert.Equal(98, d.ReadFrames(f));
        Assert.Equal(0, d.ReadFrames(f));
        Assert.True(d.EndedEarly);
    }

    [Theory, MemberData(nameof(Modes))]
    public void Metadata_Smpl_Cue_List_Inst_BeforeData(bool seekable)
    {
        var w = new Wav_TestWriter().Fmt(1, 44100, 16);
        var bytes = w
            .Smpl(60, 0x80000000u, (10, 90, Wav_SampleLoopType.Forward, 0), (20, 30, Wav_SampleLoopType.PingPong, 3))
            .Cue(5, 50)
            .ListInfo(("INAM", "Clap"), ("IART", "99Sounds"), ("ISFT", "Lavf"), ("ICMT", "café"))
            .Inst(60, -5, 3, 48, 72, 1, 127)
            .Data(w.Pcm(100)).Build();
        using var d = OpenWav(bytes, seekable);
        AssertMetadata(d);
        Assert.Equal(seekable, d.MetadataComplete);   // forward-only: chunks might still follow data
        AssertFloatsExact(w, d, 100);
        Assert.True(d.MetadataComplete);
    }

    [Theory, MemberData(nameof(Modes))]
    public void Metadata_AfterData_EagerWhenSeekable_LazyOtherwise(bool seekable)
    {
        var w = new Wav_TestWriter().Fmt(1, 44100, 16);
        var bytes = w.Data(w.Pcm(100))
            .Smpl(60, 0x80000000u, (10, 90, Wav_SampleLoopType.Forward, 0), (20, 30, Wav_SampleLoopType.PingPong, 3))
            .Cue(5, 50)
            .ListInfo(("INAM", "Clap"), ("IART", "99Sounds"), ("ISFT", "Lavf"), ("ICMT", "café"))
            .Inst(60, -5, 3, 48, 72, 1, 127)
            .Build();
        using var d = OpenWav(bytes, seekable);
        if (seekable)
        {
            Assert.True(d.MetadataComplete);
            AssertMetadata(d);
        }
        else
        {
            Assert.False(d.MetadataComplete);
            Assert.Null(d.Sampler);
        }
        AssertFloatsExact(w, d, 100);   // ends with ReadFrames == 0 → trailing walk
        Assert.True(d.MetadataComplete);
        AssertMetadata(d);
    }

    private static void AssertMetadata(Wav_Decoder d)
    {
        var s = d.Sampler!;
        Assert.Equal(60, s.MidiUnityNote);
        Assert.Equal(50.0, s.MidiPitchFractionCents, 6);
        Assert.Equal(2, s.Loops.Length);
        Assert.Equal(new Wav_SampleLoop(0, Wav_SampleLoopType.Forward, 10, 90, 0, 0), s.Loops[0]);
        Assert.True(s.Loops[0].IsInfinite);
        Assert.Equal(Wav_SampleLoopType.PingPong, s.Loops[1].Type);
        Assert.Equal(3u, s.Loops[1].PlayCount);

        Assert.Equal(2, d.CuePoints!.Length);
        Assert.Equal(50u, d.CuePoints[1].SampleOffset);
        Assert.Equal(Wav_ChunkId.Data, d.CuePoints[1].DataChunkId);

        Assert.Equal("Clap", d.InfoTags!.Title);
        Assert.Equal("99Sounds", d.InfoTags.ArtistName);
        Assert.Equal("Lavf", d.InfoTags["ISFT"]);
        Assert.Equal("café", d.InfoTags.CommentText);
        Assert.Equal(4, d.InfoTags.Count);

        var i = d.Instrument!.Value;
        Assert.Equal(60, i.UnshiftedNote);
        Assert.Equal(-5, i.FineTuneCents);
        Assert.Equal(127, i.HighVelocity);
    }

    [Fact]
    public void Fmt_AfterData_Seekable_Ok()
    {
        var w = new Wav_TestWriter().Fmt(2, 48000, 16);
        var pcm = w.Pcm(50);
        // build manually: data first, then fmt
        var w2 = new Wav_TestWriter();
        var fmtOnly = new Wav_TestWriter().Fmt(2, 48000, 16).Build();
        var fmtChunk = fmtOnly[12..];   // 8 + 16 bytes
        var bytes = w2.Data(pcm).Raw("fmt ", fmtChunk[8..]).Build();
        using var d = OpenWav(bytes, seekable: true);
        Assert.Equal(50, d.Info.TotalFrames);
        AssertFloatsExact(w, d, 50);
    }

    [Fact]
    public void Fmt_AfterData_ForwardOnly_Throws()
    {
        var w = new Wav_TestWriter().Fmt(2, 48000, 16);
        var pcm = w.Pcm(50);
        var fmtOnly = new Wav_TestWriter().Fmt(2, 48000, 16).Build();
        var bytes = new Wav_TestWriter().Data(pcm).Raw("fmt ", fmtOnly[20..]).Build();
        var ex = Assert.Throws<AudioDecoder_FormatException>(() => AudioDecoder.Open(new ForwardOnlyStream(bytes)));
        Assert.Contains("fmt chunk after data", ex.Message);
        Assert.IsAssignableFrom<IOException>(ex);
    }

    [Theory]
    [InlineData(0x0002, "Adpcm")]
    [InlineData(0x0011, "ImaAdpcm")]
    [InlineData(0x0055, "MpegLayer3")]
    [InlineData(0x0006, "Alaw")]
    [InlineData(0x0007, "Mulaw")]
    [InlineData(0x1234, "0x1234")]
    public void Unsupported_Encodings_NameTheTag(int tag, string expectedInMessage)
    {
        var w = new Wav_TestWriter().Fmt(1, 8000, 16, formatTagOverride: (ushort)tag);
        var bytes = w.Data(new byte[64]).Build();
        var ex = Assert.Throws<AudioDecoder_UnsupportedException>(() => AudioDecoder.Open(new MemoryStream(bytes)));
        Assert.Contains(expectedInMessage, ex.Message);
        Assert.Equal(AudioDecoder_Container.Wav, ex.Container);
    }

    [Fact]
    public void Malformed_NoFmt_NoData_ShortHeader()
    {
        var noFmt = new Wav_TestWriter().Data(new byte[16]).Build();
        Assert.Throws<AudioDecoder_FormatException>(() => AudioDecoder.Open(new MemoryStream(noFmt)));
        var noData = new Wav_TestWriter().Fmt(1, 8000, 16).Build();
        Assert.Throws<AudioDecoder_FormatException>(() => AudioDecoder.Open(new MemoryStream(noData)));
        // sniff says WAV but the header is cut
        Assert.Throws<AudioDecoder_FormatException>(() => new Wav_Decoder(new MemoryStream("RIFF\0\0\0\0WAVEfm"u8.ToArray())));
    }

    // ── facade ──

    [Theory, MemberData(nameof(Modes))]
    public void DecodeAll_Equals_ConcatReadFrames_And_Grows(bool seekable)
    {
        var w = new Wav_TestWriter().Fmt(2, 48000, 16);
        var pcm = w.Pcm(10_000);
        // unknown length forces the growable path
        var bytes = w.Data(pcm, declaredLength: 0xFFFFFFFF).Build(riffSizeOverride: 0);
        var pcmResult = AudioDecoder.DecodeAll(Src(bytes, seekable));
        Assert.Equal(10_000, pcmResult.FrameCount);
        Assert.Equal(20_000, pcmResult.Interleaved.Length);
        Assert.False(pcmResult.EndedEarly);
        Assert.Equal(AudioDecoder_Container.Wav, pcmResult.Info.Container);

        using var d = OpenWav(bytes, seekable);
        var manual = new List<float>();
        var buf = new float[2 * 333];
        int got;
        while ((got = d.ReadFrames(buf)) > 0) manual.AddRange(buf.Take(got * 2));
        Assert.Equal(manual.ToArray(), pcmResult.Interleaved);
    }

    [Fact]
    public void LeaveOpen_Honoured()
    {
        var w = new Wav_TestWriter().Fmt(1, 8000, 16);
        var ms = new MemoryStream(w.Data(w.Pcm(10)).Build());
        AudioDecoder.Open(ms, leaveOpen: true).Dispose();
        Assert.True(ms.CanRead);
        ms.Position = 0;
        AudioDecoder.Open(ms, leaveOpen: false).Dispose();
        Assert.False(ms.CanRead);
    }

    [Fact]
    public void Seek_Works_Seekable_Throws_ForwardOnly()
    {
        var w = new Wav_TestWriter().Fmt(2, 48000, 16);
        var bytes = w.Data(w.Pcm(1000)).Build();
        using (var d = OpenWav(bytes, true))
        {
            d.SeekToFrame(500);
            Assert.Equal(500, d.FramesRead);
            var f = new float[2];
            Assert.Equal(1, d.ReadFrames(f));
            Assert.Equal(w.ExpectedFloat(500, 0), f[0]);
            Assert.Equal(w.ExpectedFloat(500, 1), f[1]);
            d.SeekToFrame(5000);   // clamps
            Assert.Equal(0, d.ReadFrames(f));
            d.SeekToFrame(0);
            Assert.Equal(1, d.ReadFrames(f));
            Assert.Equal(w.ExpectedFloat(0, 0), f[0]);
        }
        using (var d = OpenWav(bytes, false))
            Assert.Throws<NotSupportedException>(() => d.SeekToFrame(1));
    }

    [Fact]
    public void ReadFramesNative_View_RejectsWrongFormat()
    {
        var w = new Wav_TestWriter().Fmt(2, 48000, 16);
        using var d = OpenWav(w.Data(w.Pcm(10)).Build(), true);
        var bytes = new byte[16];
        Assert.Throws<ArgumentException>(() => d.ReadFramesNative(new AudioBufferView(bytes, new AudioFormat(48000, 2, SampleFormat.Float32))));
        Assert.Equal(4, d.ReadFramesNative(new AudioBufferView(bytes, d.NativeFormat)));
        Assert.Throws<ArgumentException>(() => d.ReadFramesNative(new byte[5]));
        Assert.Throws<ArgumentException>(() => d.ReadFrames(new float[3]));
    }

    [Theory]
    [InlineData(16, false)]
    [InlineData(24, false)]
    [InlineData(32, true)]
    public void D13_ReadFrames_AllocatesNothing_InSteadyState(int bits, bool isFloat)
    {
        var w = new Wav_TestWriter().Fmt(2, 48000, bits, isFloat);
        var bytes = w.Data(w.Pcm(200 * 512)).Build();
        using var d = OpenWav(bytes, true);
        var buf = new float[2 * 512];
        // warm-up
        for (int i = 0; i < 10; i++) d.ReadFrames(buf);
        int wrong = 0;   // xunit's Assert.Equal<T> allocates a comparer per call, so count and assert afterwards
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) if (d.ReadFrames(buf) != 512) wrong++;
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, wrong);
        Assert.Equal(0, after - before);
    }
}