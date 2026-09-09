using System.Runtime.InteropServices;
using AN.Audio.Formats.Tests.Support;
using AN.Audio.Formats.Wav;
using Xunit;

namespace AN.Audio.Formats.Tests;

/// <summary>Spec 50 Phase 4a: WAVE_FORMAT_ALAW / WAVE_FORMAT_MULAW (ITU-T G.711) in <see cref="Wav_Decoder"/>.</summary>
public class WavG711Tests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (bool alaw in new[] { true, false })
            foreach (bool seekable in new[] { true, false })
                foreach (bool extensible in new[] { false, true })
                    yield return [alaw, seekable, extensible];
    }

    /// <summary>Reference expansion written from the G.711 tables' definition (ITU-T G.711 Tables 1a/2a), independently of Internal/G711.</summary>
    private static short RefMuLaw(byte b)
    {
        int u = ~b & 0xFF;
        int sign = u & 0x80, seg = (u >> 4) & 7, q = u & 0xF;
        int mag = (((q << 1) + 33) << seg) - 33;   // 2*(q + 16.5) * 2^seg - 33  (== ((q<<3)+132)<<seg >> 2 ... on the 14-bit scale)
        mag <<= 2;                                 // 14-bit → 16-bit scale
        return (short)(sign != 0 ? -mag : mag);
    }

    private static short RefALaw(byte b)
    {
        int a = b ^ 0x55;
        int sign = a & 0x80, seg = (a >> 4) & 7, q = a & 0xF;
        int mag = seg == 0 ? (q << 1) + 1 : ((q << 1) + 33) << (seg - 1);   // 13-bit scale
        mag <<= 3;                                                             // 13-bit → 16-bit scale
        return (short)(sign != 0 ? mag : -mag);
    }

    private static byte[] Companded(int frames, int channels)
    {
        // every code appears, in every channel position
        var bytes = new byte[frames * channels];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 7 + 3);
        return bytes;
    }

    private static byte[] Build(bool alaw, bool extensible, int channels, int frames, out byte[] wire)
    {
        var w = new Wav_TestWriter();
        ushort tag = (ushort)(alaw ? Wav_FormatTag.Alaw : Wav_FormatTag.Mulaw);
        if (extensible)
        {
            // KSDATAFORMAT_SUBTYPE_ALAW / _MULAW = the legacy tag in the first two bytes of the generic KS GUID
            var guidBytes = Wav_FormatChunk.SubFormatPcm.ToByteArray();
            guidBytes[0] = (byte)tag; guidBytes[1] = (byte)(tag >> 8);
            w.FmtExtensible(channels, 8000, 8, 8, channels == 1 ? AudioChannelMask.Mono : AudioChannelMask.Stereo, subFormat: new Guid(guidBytes));
        }
        else w.Fmt(channels, 8000, 8, formatTagOverride: tag);
        wire = Companded(frames, channels);
        return w.Data(wire).Smpl(60, 0, (10u, 20u, Wav_SampleLoopType.Forward, 0u)).Build();
    }

    [Theory, MemberData(nameof(Cases))]
    public void Expands_To_Int16_BitExact(bool alaw, bool seekable, bool extensible)
    {
        const int frames = 1000, channels = 2;
        var file = Build(alaw, extensible, channels, frames, out var wire);
        Stream src = seekable ? new MemoryStream(file) : new ForwardOnlyStream(file);
        using var d = (Wav_Decoder)AudioDecoder.Open(src);

        Assert.Equal(alaw ? AudioDecoder_SourceEncoding.Alaw : AudioDecoder_SourceEncoding.Mulaw, d.Info.Encoding);
        Assert.Equal(SampleFormat.Int16, d.NativeFormat.Format);
        Assert.Equal(8, d.Info.SourceBitDepth);
        Assert.Equal(8000, d.Info.SampleRate);
        Assert.Equal(channels, d.Info.Channels);
        Assert.Equal(frames, d.Info.TotalFrames);
        Assert.Equal(alaw ? Wav_FormatTag.Alaw : Wav_FormatTag.Mulaw, d.Format.EffectiveTag);

        // native (Int16) in odd-sized reads
        var native = new short[frames * channels];
        var asBytes = MemoryMarshal.AsBytes(native.AsSpan());
        int total = 0; int[] sizes = [1, 3, 4095, 4096, 4097, 100];
        for (int k = 0; total < frames; k++)
        {
            int want = Math.Min(sizes[k % sizes.Length], frames - total);
            int got = d.ReadFramesNative(asBytes.Slice(total * channels * 2, want * channels * 2));
            Assert.True(got > 0);
            total += got;
        }
        Assert.Equal(0, d.ReadFramesNative(new byte[2 * channels]));
        Assert.False(d.EndedEarly);
        Assert.Equal(frames, d.FramesRead);
        for (int i = 0; i < wire.Length; i++)
            Assert.Equal(alaw ? RefALaw(wire[i]) : RefMuLaw(wire[i]), native[i]);
        Assert.NotNull(d.Sampler);   // metadata after data still read (forward-only: lazily, once the audio is exhausted)
        Assert.Equal(60, d.Sampler!.MidiUnityNote);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Float_Is_Int16_Over_32768(bool alaw)
    {
        var file = Build(alaw, extensible: false, channels: 1, frames: 512, out var wire);
        using var d = AudioDecoder.Open(new ForwardOnlyStream(file));
        var floats = new float[512];
        int total = 0, g;
        while ((g = d.ReadFrames(floats.AsSpan(total, Math.Min(77, 512 - total)))) > 0) total += g;
        Assert.Equal(512, total);
        for (int i = 0; i < 512; i++)
            Assert.Equal((alaw ? RefALaw(wire[i]) : RefMuLaw(wire[i])) / 32768f, floats[i]);
    }

    [Fact]
    public void Seek_Uses_Wire_Bytes_Not_Native_Bytes()
    {
        var file = Build(alaw: false, extensible: false, channels: 2, frames: 300, out var wire);
        using var d = AudioDecoder.Open(new MemoryStream(file));
        d.SeekToFrame(123);
        Assert.Equal(123, d.FramesRead);
        var got = new short[2 * 4];
        Assert.Equal(4, d.ReadFramesNative(MemoryMarshal.AsBytes(got.AsSpan())));
        for (int i = 0; i < got.Length; i++) Assert.Equal(RefMuLaw(wire[123 * 2 + i]), got[i]);
        d.SeekToFrame(300);
        Assert.Equal(0, d.ReadFramesNative(MemoryMarshal.AsBytes(got.AsSpan())));
    }

    [Fact]
    public void Truncated_Wire_Sets_EndedEarly()
    {
        var file = Build(alaw: true, extensible: false, channels: 1, frames: 400, out _);
        var cut = file[..(file.Length - 250)];   // inside data
        using var d = AudioDecoder.Open(new ForwardOnlyStream(cut));
        var pcm = AudioDecoder.DecodeAll(d);
        Assert.True(pcm.FrameCount < 400 && pcm.FrameCount > 100);
        Assert.True(pcm.EndedEarly);
    }

    [Fact]
    public void G711_With_16Bit_Container_Is_Unsupported()
    {
        var w = new Wav_TestWriter().Fmt(1, 8000, 16, formatTagOverride: (ushort)Wav_FormatTag.Mulaw).Data(new byte[64]);
        var ex = Assert.Throws<AudioDecoder_UnsupportedException>(() => AudioDecoder.Open(new MemoryStream(w.Build())));
        Assert.Contains("Mulaw", ex.Message);
    }

    [Fact]
    public void Tables_Match_Reference_For_All_256_Codes()
    {
        for (int i = 0; i < 256; i++)
        {
            Assert.Equal(RefMuLaw((byte)i), Internal.G711.MuLawToInt16[i]);
            Assert.Equal(RefALaw((byte)i), Internal.G711.ALawToInt16[i]);
        }
        // well-known anchors: µ-law 0xFF = +0, 0x7F = -0 (→ 0), 0x80 = +32124 (max), 0x00 = -32124
        Assert.Equal(0, Internal.G711.MuLawToInt16[0xFF]);
        Assert.Equal(0, Internal.G711.MuLawToInt16[0x7F]);
        Assert.Equal(32124, Internal.G711.MuLawToInt16[0x80]);
        Assert.Equal(-32124, Internal.G711.MuLawToInt16[0x00]);
        // A-law 0xD5 = +8 (smallest positive), 0x55 = -8, 0xAA = +32256 (max), 0x2A = -32256
        Assert.Equal(8, Internal.G711.ALawToInt16[0xD5]);
        Assert.Equal(-8, Internal.G711.ALawToInt16[0x55]);
        Assert.Equal(32256, Internal.G711.ALawToInt16[0xAA]);
        Assert.Equal(-32256, Internal.G711.ALawToInt16[0x2A]);
    }
}