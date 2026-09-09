namespace AN.Audio.Formats.Internal;

internal enum MpegFrameHeader_Version { Mpeg25 = 0, Reserved = 1, Mpeg2 = 2, Mpeg1 = 3 }
internal enum MpegFrameHeader_MpegLayer { Reserved = 0, Layer3 = 1, Layer2 = 2, Layer1 = 3 }
internal enum MpegFrameHeader_MpegChannelMode { Stereo = 0, JointStereo = 1, DualChannel = 2, Mono = 3 }

/// <summary>A decoded 4-byte MPEG audio frame header (ISO 11172-3 / 13818-3).</summary>
internal readonly record struct MpegFrameHeader_Info(
    MpegFrameHeader_Version Version,
    MpegFrameHeader_MpegLayer Layer,
    bool HasCrc,
    int BitRate,               // bit/s
    int SampleRate,            // Hz
    MpegFrameHeader_MpegChannelMode ChannelMode,
    int FrameLength,           // bytes incl. the header
    int SamplesPerFrame)       // per channel
{
    public int Channels => ChannelMode == MpegFrameHeader_MpegChannelMode.Mono ? 1 : 2;

    /// <summary>Layer III side-information size (bytes after the header/CRC); this is where a Xing/Info tag starts.</summary>
    public int SideInfoSize => Layer != MpegFrameHeader_MpegLayer.Layer3 ? 0
        : Version == MpegFrameHeader_Version.Mpeg1 ? (Channels == 1 ? 17 : 32)
        : (Channels == 1 ? 9 : 17);

    public AudioDecoder_SourceEncoding Encoding => Layer switch
    {
        MpegFrameHeader_MpegLayer.Layer1 => AudioDecoder_SourceEncoding.MpegLayer1,
        MpegFrameHeader_MpegLayer.Layer2 => AudioDecoder_SourceEncoding.MpegLayer2,
        _ => AudioDecoder_SourceEncoding.MpegLayer3,
    };
}

/// <summary>
/// MPEG audio frame-header parsing. Header layout <c>AAAAAAAA AAABBCCD EEEEFFGH IIJJKLMM</c> — A sync (11 bits), B version,
/// C layer, D protection, E bitrate, F sample rate, G padding, I channel mode. Used for sniffing (D8: "is that a frame header,
/// and does another one follow where it should?") and to describe the first frame (layer, side-info size for the Xing tag).
/// Decoding itself is NLayer's job.
/// </summary>
internal static class MpegFrameHeader
{
    // bitrate tables in kbit/s, index 1..14 (0 = free, 15 = bad)
    private static readonly int[] BitrateV1L1 = [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448, 0];
    private static readonly int[] BitrateV1L2 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 0];
    private static readonly int[] BitrateV1L3 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
    private static readonly int[] BitrateV2L1 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256, 0];
    private static readonly int[] BitrateV2L23 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0];
    private static readonly int[] SampleRateV1 = [44100, 48000, 32000, 0];
    private static readonly int[] SampleRateV2 = [22050, 24000, 16000, 0];
    private static readonly int[] SampleRateV25 = [11025, 12000, 8000, 0];

    /// <summary>Parses the header at the start of <paramref name="h"/>. False for anything that is not a valid, non-free-format header.</summary>
    public static bool TryParse(ReadOnlySpan<byte> h, out MpegFrameHeader_Info info)
    {
        info = default;
        if (h.Length < 4) return false;
        if (h[0] != 0xFF || (h[1] & 0xE0) != 0xE0) return false;
        var version = (MpegFrameHeader_Version)((h[1] >> 3) & 0x3);
        var layer = (MpegFrameHeader_MpegLayer)((h[1] >> 1) & 0x3);
        bool hasCrc = (h[1] & 0x1) == 0;
        int bitrateIndex = h[2] >> 4;
        int sampleRateIndex = (h[2] >> 2) & 0x3;
        int padding = (h[2] >> 1) & 0x1;
        var mode = (MpegFrameHeader_MpegChannelMode)(h[3] >> 6);
        if (version == MpegFrameHeader_Version.Reserved || layer == MpegFrameHeader_MpegLayer.Reserved) return false;
        if (bitrateIndex == 0 || bitrateIndex == 15) return false;   // free-format / bad: not sniffable
        if (sampleRateIndex == 3) return false;

        int[] bitrates = (version, layer) switch
        {
            (MpegFrameHeader_Version.Mpeg1, MpegFrameHeader_MpegLayer.Layer1) => BitrateV1L1,
            (MpegFrameHeader_Version.Mpeg1, MpegFrameHeader_MpegLayer.Layer2) => BitrateV1L2,
            (MpegFrameHeader_Version.Mpeg1, MpegFrameHeader_MpegLayer.Layer3) => BitrateV1L3,
            (_, MpegFrameHeader_MpegLayer.Layer1) => BitrateV2L1,
            _ => BitrateV2L23,
        };
        int[] rates = version switch { MpegFrameHeader_Version.Mpeg1 => SampleRateV1, MpegFrameHeader_Version.Mpeg2 => SampleRateV2, _ => SampleRateV25 };
        int bitrate = bitrates[bitrateIndex] * 1000;
        int sampleRate = rates[sampleRateIndex];

        int samplesPerFrame = layer == MpegFrameHeader_MpegLayer.Layer1 ? 384
            : layer == MpegFrameHeader_MpegLayer.Layer2 || version == MpegFrameHeader_Version.Mpeg1 ? 1152 : 576;
        int frameLength = layer == MpegFrameHeader_MpegLayer.Layer1
            ? (12 * bitrate / sampleRate + padding) * 4
            : samplesPerFrame / 8 * bitrate / sampleRate + padding;

        info = new MpegFrameHeader_Info(version, layer, hasCrc, bitrate, sampleRate, mode, frameLength, samplesPerFrame);
        return true;
    }

    /// <summary>Returns the frame length in bytes when <paramref name="h"/> starts with a valid header, else 0.</summary>
    public static int FrameLength(ReadOnlySpan<byte> h) => TryParse(h, out var info) ? info.FrameLength : 0;

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