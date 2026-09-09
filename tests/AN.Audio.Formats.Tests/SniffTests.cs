using System.Text;
using AN.Audio.Formats.Tests.Support;
using Xunit;

namespace AN.Audio.Formats.Tests;

public class SniffTests
{
    private static byte[] Bytes(string ascii) => Encoding.ASCII.GetBytes(ascii);

    [Fact] public void Riff_Wave() => Assert.Equal(AudioDecoder_Container.Wav, AudioDecoder.Sniff(Bytes("RIFF\0\0\0\0WAVEfmt ")));
    [Fact] public void Rf64_Wave() => Assert.Equal(AudioDecoder_Container.Wav, AudioDecoder.Sniff(Bytes("RF64\xFF\xFF\xFF\xFFWAVEds64")));
    [Fact] public void Flac() => Assert.Equal(AudioDecoder_Container.Flac, AudioDecoder.Sniff(Bytes("fLaC\0\0\0\x22")));
    [Fact] public void Ogg() => Assert.Equal(AudioDecoder_Container.Ogg, AudioDecoder.Sniff(Bytes("OggS\0\x02\0\0\0\0\0\0")));
    [Fact] public void Aiff() => Assert.Equal(AudioDecoder_Container.Aiff, AudioDecoder.Sniff(Bytes("FORM\0\0\0\0AIFFCOMM")));
    [Fact] public void Aifc() => Assert.Equal(AudioDecoder_Container.Aiff, AudioDecoder.Sniff(Bytes("FORM\0\0\0\0AIFCFVER")));

    [Fact]
    public void Riff_NotWave_IsNotWav()
    {
        Assert.Null(AudioDecoder.Sniff(Bytes("RIFF\0\0\0\0AVI LIST")));
    }

    [Fact]
    public void Garbage_IsNull_And_OpenThrowsUnsupported()
    {
        var junk = new byte[64];
        new Random(1).NextBytes(junk);
        junk[0] = 0x00;   // ensure no sync word at 0
        Assert.Null(AudioDecoder.Sniff(junk));
        var ex = Assert.Throws<AudioDecoder_UnsupportedException>(() => AudioDecoder.Open(new MemoryStream(junk)));
        Assert.Contains("unrecognised", ex.Message);
    }

    [Fact]
    public void Hint_Tiebreak_OnlyWhenNothingMatches()
    {
        var junk = new byte[16];
        Assert.Equal(AudioDecoder_Container.Mp3, AudioDecoder.Sniff(junk, AudioDecoder_FormatHint.Mp3));
        Assert.Equal(AudioDecoder_Container.Flac, AudioDecoder.Sniff(junk, AudioDecoder_FormatHint.Flac));
        // content beats hint
        Assert.Equal(AudioDecoder_Container.Wav, AudioDecoder.Sniff(Bytes("RIFF\0\0\0\0WAVEfmt "), AudioDecoder_FormatHint.Mp3));
    }

    private static byte[] MpegFrame(bool mpeg1 = true, int bitrateIndex = 9, int sampleRateIndex = 0, int padding = 0)
    {
        // MPEG-1 Layer III: FF FB (sync + v1 + L3 + no CRC), then bitrate/samplerate/padding, then mode
        var h = new byte[4];
        h[0] = 0xFF;
        h[1] = (byte)(0xE0 | ((mpeg1 ? 3 : 2) << 3) | (1 << 1) | 1);
        h[2] = (byte)((bitrateIndex << 4) | (sampleRateIndex << 2) | (padding << 1));
        h[3] = 0x00;
        int len = Internal.MpegFrameHeader.FrameLength(h);
        Assert.True(len > 4);
        var frame = new byte[len];
        h.CopyTo(frame, 0);
        return frame;
    }

    [Fact]
    public void Mp3_BareSync_NeedsTwoFrames()
    {
        var f = MpegFrame();
        // one frame only: not enough
        Assert.Null(AudioDecoder.Sniff(f));
        var two = f.Concat(MpegFrame()).ToArray();
        Assert.Equal(AudioDecoder_Container.Mp3, AudioDecoder.Sniff(two));
        // 128 kbit/s @ 44.1 kHz = 417 bytes/frame
        Assert.Equal(417, f.Length);
    }

    [Fact]
    public void Mp3_Id3v2Prefixed()
    {
        // ID3v2.3 header with a 100-byte tag, then a frame
        var id3 = new byte[10 + 100];
        "ID3"u8.CopyTo(id3);
        id3[3] = 3; id3[6] = 0; id3[7] = 0; id3[8] = 0; id3[9] = 100;
        var data = id3.Concat(MpegFrame()).ToArray();
        Assert.Equal(AudioDecoder_Container.Mp3, AudioDecoder.Sniff(data));
        // only the first 12 bytes visible (tag longer than the window) → still Mp3
        Assert.Equal(AudioDecoder_Container.Mp3, AudioDecoder.Sniff(data.AsSpan(0, 12)));
    }

    [Fact]
    public void Mp3_InvalidHeaderFields_Rejected()
    {
        var bad = new byte[] { 0xFF, 0xFB, 0xF0, 0x00 };   // bitrate index 15 = bad
        Assert.Equal(0, Internal.MpegFrameHeader.FrameLength(bad));
        bad = [0xFF, 0xFB, 0x9C, 0x00];                     // sample rate index 3 = reserved
        Assert.Equal(0, Internal.MpegFrameHeader.FrameLength(bad));
        bad = [0xFF, 0xE9, 0x90, 0x00];                     // version reserved (01)
        Assert.Equal(0, Internal.MpegFrameHeader.FrameLength(bad));
    }

    [Theory]
    [InlineData("x.wav", AudioDecoder_FormatHint.Wav)]
    [InlineData("X.FLAC", AudioDecoder_FormatHint.Flac)]
    [InlineData("a/b/c.Mp3", AudioDecoder_FormatHint.Mp3)]
    [InlineData(".aiff", AudioDecoder_FormatHint.Aiff)]
    [InlineData("noext", AudioDecoder_FormatHint.None)]
    public void HintFromExtension(string path, AudioDecoder_FormatHint expected)
        => Assert.Equal(expected, AudioDecoder.HintFromExtension(path));

    [Fact]
    public void Open_Flac_RoutesToFlacDecoder_And_RejectsBogusStreamInfo()
    {
        // all-zero STREAMINFO => sample rate 0 => malformed (proves the fLaC route reaches Flac_Decoder)
        var ex = Assert.Throws<AudioDecoder_FormatException>(() => AudioDecoder.Open(new MemoryStream(Bytes("fLaC\x80\0\0\x22" + new string('\0', 40)))));
        Assert.Equal(AudioDecoder_Container.Flac, ex.Container);
    }
}