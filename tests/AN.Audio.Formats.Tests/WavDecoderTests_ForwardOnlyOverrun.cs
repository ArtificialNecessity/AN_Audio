using AN.Audio.Formats.Tests.Support;
using Xunit;

namespace AN.Audio.Formats.Tests;

public class WavDecoderTests_ForwardOnlyOverrun
{
    [Fact]
    public void Data_Overrun_UnknownRiff_ForwardOnly_DiscoversAtEof()
    {
        // RIFF size unknown AND data declared too long: only EOF can tell us
        var w = new Wav_TestWriter().Fmt(2, 48000, 16);
        var pcm = w.Pcm(300);
        var bytes = w.Data(pcm, declaredLength: (uint)pcm.Length * 3).Build(riffSizeOverride: 0);
        using var d = (Wav.Wav_Decoder)AudioDecoder.Open(new ForwardOnlyStream(bytes));
        Assert.Equal(900, d.Info.TotalFrames);   // declared, nothing contradicts it yet
        var f = new float[900 * 2];
        Assert.Equal(300, d.ReadFrames(f));
        Assert.Equal(0, d.ReadFrames(f));
        Assert.True(d.EndedEarly);
        for (int i = 0; i < 300; i++) Assert.Equal(w.ExpectedFloat(i, 1), f[i * 2 + 1]);
    }
}