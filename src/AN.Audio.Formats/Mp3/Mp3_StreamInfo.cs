namespace AN.Audio.Formats.Mp3;

public enum Mp3_MpegVersion { Mpeg1, Mpeg2, Mpeg25 }
public enum Mp3_Layer { Layer1 = 1, Layer2 = 2, Layer3 = 3 }
public enum Mp3_ChannelMode { Stereo, JointStereo, DualChannel, Mono }

/// <summary>What the first MPEG frame header and the optional Xing/Info/VBRI block say about the stream.</summary>
public readonly record struct Mp3_StreamInfo(
    Mp3_MpegVersion Version,
    Mp3_Layer Layer,
    Mp3_ChannelMode ChannelMode,
    int SampleRate,
    /// <summary>Bit rate of the FIRST audio frame; nominal for CBR, meaningless for VBR (see <see cref="IsVbr"/>).</summary>
    int BitRate,
    bool HasCrc,
    int SamplesPerFrame,
    /// <summary>Byte offset of the first MPEG frame (after any ID3v2 tag).</summary>
    long AudioStartOffset,
    /// <summary>True when a Xing (VBR) or VBRI block was present; false for Info (CBR) or none.</summary>
    bool IsVbr,
    /// <summary>MPEG frame count declared by the Xing/VBRI block (excludes the header frame); null when absent.</summary>
    int? DeclaredFrameCount,
    /// <summary>Audio byte count declared by the Xing/VBRI block; null when absent.</summary>
    int? DeclaredByteCount,
    /// <summary>Encoder string from the LAME extension (e.g. "LAME3.100", "Lavc61.19"); null when absent.</summary>
    string? Encoder);

/// <summary>LAME gapless data: samples to drop at the start and end. NLayer applies these; they are exposed for information.</summary>
public readonly record struct Mp3_GaplessInfo(int EncoderDelay, int EncoderPadding)
{
    public bool IsGapless => EncoderDelay > 0 || EncoderPadding > 0;
}

public sealed class Mp3_DecoderOptions
{
    public static readonly Mp3_DecoderOptions Default = new();

    /// <summary>Receives each embedded picture during construction (spec 50 §MP3). Null = picture bodies are skipped, never buffered.</summary>
    public AudioDecoder_PictureCallback? OnPicture { get; init; }

    /// <summary>On a seekable stream WITHOUT an ID3v2 tag, look for an ID3v1 tag in the last 128 bytes. Default true.</summary>
    public bool ReadId3v1 { get; init; } = true;
}