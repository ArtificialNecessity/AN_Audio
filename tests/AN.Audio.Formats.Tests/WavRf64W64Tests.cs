using System.Runtime.InteropServices;
using AN.Audio.Formats.Tests.Support;
using AN.Audio.Formats.Wav;
using Xunit;

namespace AN.Audio.Formats.Tests;

/// <summary>Spec 50 Phase 4c: RF64/BW64 (EBU 3306 <c>ds64</c>) and Sony Wave64 layouts in <see cref="Wav_Decoder"/>. All fixtures synthesised.</summary>
public class WavRf64W64Tests
{
    public static IEnumerable<object[]> Modes() { yield return [true]; yield return [false]; }
    private static Stream Src(byte[] b, bool seekable) => seekable ? new MemoryStream(b) : new ForwardOnlyStream(b);

    private static Wav_TestWriter Typical(int frames, out byte[] pcm)
    {
        var w = new Wav_TestWriter().Fmt(2, 48000, 16);
        pcm = w.Pcm(frames);
        w.Data(pcm).Smpl(69, 0, (100u, 200u, Wav_SampleLoopType.Forward, 0u)).Raw("odd ", [1, 2, 3, 4, 5]).ListInfo(("INAM", "RF64 test"));
        return w;
    }

    private static void AssertAudio(Wav_Decoder d, byte[] pcm, int frames)
    {
        Assert.Equal(frames, d.Info.TotalFrames);
        var native = new byte[pcm.Length];
        int total = 0, bpf = d.NativeFormat.BytesPerFrame; int g;
        while (total < frames && (g = d.ReadFramesNative(native.AsSpan(total * bpf, Math.Min(1000, frames - total) * bpf))) > 0) total += g;
        Assert.Equal(frames, total);
        Assert.Equal(0, d.ReadFramesNative(new byte[bpf]));
        Assert.Equal(pcm, native);
        Assert.False(d.EndedEarly);
        Assert.NotNull(d.Sampler);
        Assert.Equal(69, d.Sampler!.MidiUnityNote);
        Assert.Equal("RF64 test", d.InfoTags?.Title);
        Assert.True(d.MetadataComplete);
    }

    [Theory, MemberData(nameof(Modes))]
    public void Rf64_DataSize_From_Ds64(bool seekable)
    {
        var w = Typical(3000, out var pcm);
        var file = w.BuildRf64();
        using var d = (Wav_Decoder)AudioDecoder.Open(Src(file, seekable));
        Assert.Equal(Wav_ContainerLayout.Rf64, d.Layout);
        Assert.NotNull(d.DataSize64);
        Assert.Equal((ulong)pcm.Length, d.DataSize64!.DataSize);
        Assert.Equal(3000ul, d.DataSize64.SampleCount);
        Assert.True(d.RiffLengthKnown);
        Assert.Equal(Wav_ChunkId.Ds64, d.Chunks[0].Id);
        var data = d.Chunks.First(c => c.Id == Wav_ChunkId.Data);
        Assert.Equal(pcm.Length, data.DeclaredLength);   // resolved through ds64, not 0xFFFFFFFF
        AssertAudio(d, pcm, 3000);
    }

    [Theory, MemberData(nameof(Modes))]
    public void Rf64_Table_Resolves_Other_MinusOne_Chunks(bool seekable)
    {
        var w = Typical(500, out var pcm);
        var file = w.BuildRf64(tableChunkIds: ["smpl", "odd "]);
        using var d = (Wav_Decoder)AudioDecoder.Open(Src(file, seekable));
        Assert.Equal(2, d.DataSize64!.Table.Count);
        Assert.Equal(5ul, d.DataSize64.SizeFor(Wav_ChunkId.FromString("odd ")));
        AssertAudio(d, pcm, 500);
        var odd = d.Chunks.First(c => c.Id == Wav_ChunkId.FromString("odd "));
        Assert.Equal(5, odd.DeclaredLength);
        Assert.True(odd.PadBytePresent);
    }

    [Fact]
    public void Bw64_Fourcc_Is_Rf64_Layout()
    {
        var w = Typical(100, out var pcm);
        var file = w.BuildRf64(bw64: true);
        Assert.Equal(AudioDecoder_Container.Wav, AudioDecoder.Sniff(file.AsSpan(0, 12)));
        using var d = (Wav_Decoder)AudioDecoder.Open(new MemoryStream(file));
        Assert.Equal(Wav_ContainerLayout.Rf64, d.Layout);
        AssertAudio(d, pcm, 100);
    }

    [Theory, MemberData(nameof(Modes))]
    public void Rf64_Declared_Beyond_Stream_Clamps_And_EndsEarly(bool seekable)
    {
        // a "> 4 GiB" declaration on a file that is not: decode what exists, flag EndedEarly (D12)
        var w = new Wav_TestWriter().Fmt(1, 44100, 16);
        var pcm = w.Pcm(1000);
        w.Data(pcm);
        var file = w.BuildRf64(ds64DataSize: 5_000_000_000ul, ds64SampleCount: 2_500_000_000ul);
        using var d = (Wav_Decoder)AudioDecoder.Open(Src(file, seekable));
        var all = AudioDecoder.DecodeAll(d);
        Assert.Equal(1000, all.FrameCount);
        Assert.True(all.EndedEarly);
        if (seekable) Assert.Equal(1000, d.Info.TotalFrames);   // clamped at open against the stream length
    }

    [Fact]
    public void Rf64_Data_With_Real_32bit_Size_Still_Works()
    {
        var w = Typical(200, out var pcm);
        var file = w.BuildRf64(dataSizeMinusOne: false);
        using var d = (Wav_Decoder)AudioDecoder.Open(new MemoryStream(file));
        AssertAudio(d, pcm, 200);
    }

    [Theory, MemberData(nameof(Modes))]
    public void Wave64_Layout(bool seekable)
    {
        var w = Typical(2500, out var pcm);
        var file = w.BuildW64();
        Assert.Equal(AudioDecoder_Container.Wav, AudioDecoder.Sniff(file.AsSpan(0, 40)));
        Assert.Null(AudioDecoder.Sniff(file.AsSpan(0, 12)));   // too short to see the wave GUID: content says nothing
        using var d = (Wav_Decoder)AudioDecoder.Open(Src(file, seekable));
        Assert.Equal(Wav_ContainerLayout.Wave64, d.Layout);
        Assert.Null(d.DataSize64);
        Assert.True(d.RiffLengthKnown);
        AssertAudio(d, pcm, 2500);
        // the 5-byte chunk is padded to an 8-byte boundary (24 + 5 → 32): the walk must land on the next header
        var odd = d.Chunks.First(c => c.Id == Wav_ChunkId.FromString("odd "));
        Assert.Equal(5, odd.DeclaredLength);
        Assert.True(odd.PadBytePresent);
        Assert.Equal([1, 2, 3, 4, 5], odd.RawBytes!);
        Assert.Contains(d.Chunks, c => c.Id == Wav_ChunkId.List);
    }

    [Fact]
    public void Wave64_Seek_And_Truncation()
    {
        var w = new Wav_TestWriter().Fmt(2, 48000, 24);
        var pcm = w.Pcm(700);
        w.Data(pcm);
        var file = w.BuildW64();
        using (var d = AudioDecoder.Open(new MemoryStream(file)))
        {
            d.SeekToFrame(321);
            var got = new byte[6 * 3];
            Assert.Equal(3, d.ReadFramesNative(got));
            Assert.Equal(pcm.AsSpan(321 * 6, 18).ToArray(), got);
        }
        var cut = file[..(file.Length - 500)];
        using (var d = AudioDecoder.Open(new ForwardOnlyStream(cut)))
        {
            var all = AudioDecoder.DecodeAll(d);
            Assert.True(all.EndedEarly);
            Assert.True(all.FrameCount < 700);
        }
    }

    [Fact]
    public void Wave64_Bad_Guid_Tail_Is_FormatException()
    {
        var w = Typical(10, out _);
        var file = w.BuildW64();
        file[9] ^= 0xFF;   // riff GUID tail
        Assert.Throws<AudioDecoder_FormatException>(() => new Wav_Decoder(new MemoryStream(file)));
    }

    [Fact]
    public void Riff_Layout_Unchanged()
    {
        var w = Typical(10, out var pcm);
        using var d = (Wav_Decoder)AudioDecoder.Open(new MemoryStream(w.Build()));
        Assert.Equal(Wav_ContainerLayout.Riff, d.Layout);
        Assert.Null(d.DataSize64);
        AssertAudio(d, pcm, 10);
    }
}