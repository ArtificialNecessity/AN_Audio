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
}