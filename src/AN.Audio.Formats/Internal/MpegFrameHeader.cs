namespace AN.Audio.Formats.Internal;

/// <summary>
/// Minimal MPEG audio frame-header validation for sniffing (D8). ISO 11172-3 / 13818-3 header layout:
/// <c>AAAAAAAA AAABBCCD EEEEFFGH IIJJKLMM</c> — A sync (11 bits), B version, C layer, D protection, E bitrate, F sample rate,
/// G padding, I channel mode. Decoding itself is NLayer's job (Phase 3); this only answers "is that a frame header, and does
/// another one follow where it should?".
/// </summary>
internal static class MpegFrameHeader
{
    private enum Version { Mpeg25 = 0, Reserved = 1, Mpeg2 = 2, Mpeg1 = 3 }
    private enum Layer { Reserved = 0, Layer3 = 1, Layer2 = 2, Layer1 = 3 }

    // bitrate tables in kbit/s, index 1..14 (0 = free, 15 = bad)
    private static readonly int[] BitrateV1L1 = [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448, 0];
    private static readonly int[] BitrateV1L2 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 0];
    private static readonly int[] BitrateV1L3 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
    private static readonly int[] BitrateV2L1 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256, 0];
    private static readonly int[] BitrateV2L23 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0];
    private static readonly int[] SampleRateV1 = [44100, 48000, 32000, 0];
    private static readonly int[] SampleRateV2 = [22050, 24000, 16000, 0];
    private static readonly int[] SampleRateV25 = [11025, 12000, 8000, 0];

    /// <summary>Returns the frame length in bytes when <paramref name="h"/> starts with a valid header, else 0.</summary>
    public static int FrameLength(ReadOnlySpan<byte> h)
    {
        if (h.Length < 4) return 0;
        if (h[0] != 0xFF || (h[1] & 0xE0) != 0xE0) return 0;
        var version = (Version)((h[1] >> 3) & 0x3);
        var layer = (Layer)((h[1] >> 1) & 0x3);
        int bitrateIndex = h[2] >> 4;
        int sampleRateIndex = (h[2] >> 2) & 0x3;
        int padding = (h[2] >> 1) & 0x1;
        if (version == Version.Reserved || layer == Layer.Reserved) return 0;
        if (bitrateIndex == 0 || bitrateIndex == 15) return 0;   // free-format / bad: not sniffable
        if (sampleRateIndex == 3) return 0;

        int[] bitrates = (version, layer) switch
        {
            (Version.Mpeg1, Layer.Layer1) => BitrateV1L1,
            (Version.Mpeg1, Layer.Layer2) => BitrateV1L2,
            (Version.Mpeg1, Layer.Layer3) => BitrateV1L3,
            (_, Layer.Layer1) => BitrateV2L1,
            _ => BitrateV2L23,
        };
        int[] rates = version switch { Version.Mpeg1 => SampleRateV1, Version.Mpeg2 => SampleRateV2, _ => SampleRateV25 };
        int bitrate = bitrates[bitrateIndex] * 1000;
        int sampleRate = rates[sampleRateIndex];

        if (layer == Layer.Layer1)
            return (12 * bitrate / sampleRate + padding) * 4;
        int samplesPerFrame = layer == Layer.Layer2 || version == Version.Mpeg1 ? 1152 : 576;
        return samplesPerFrame / 8 * bitrate / sampleRate + padding;
    }

    /// <summary>
    /// True when <paramref name="data"/> begins with a valid frame header and (when <paramref name="requireSecondFrame"/>)
    /// another valid header sits exactly one frame later. If the data is too short to see the second header, the first
    /// alone is accepted only when not required.
    /// </summary>
    public static bool LooksLikeMpegAudio(ReadOnlySpan<byte> data, bool requireSecondFrame)
    {
        int len = FrameLength(data);
        if (len == 0) return false;
        if (!requireSecondFrame) return true;
        if (data.Length < len + 4) return false;
        return FrameLength(data.Slice(len)) != 0;
    }
}