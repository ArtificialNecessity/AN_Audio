using System.Buffers.Binary;

namespace AN.Audio.Formats.Wav;

/// <summary><c>wFormatTag</c> values from <c>mmreg.h</c> (the ones a decoder meets in practice).</summary>
public enum Wav_FormatTag : ushort
{
    Unknown = 0x0000,
    Pcm = 0x0001,
    /// <summary>WAVE_FORMAT_ADPCM (Microsoft).</summary>
    Adpcm = 0x0002,
    IeeeFloat = 0x0003,
    Alaw = 0x0006,
    Mulaw = 0x0007,
    /// <summary>WAVE_FORMAT_DVI_ADPCM / IMA ADPCM.</summary>
    ImaAdpcm = 0x0011,
    /// <summary>WAVE_FORMAT_MPEG (layer 1/2).</summary>
    Mpeg = 0x0050,
    /// <summary>WAVE_FORMAT_MPEGLAYER3.</summary>
    MpegLayer3 = 0x0055,
    Extensible = 0xFFFE,
}

/// <summary>
/// The parsed <c>fmt </c> chunk (WAVEFORMATEX / WAVEFORMATEXTENSIBLE). <see cref="EffectiveTag"/> resolves EXTENSIBLE through
/// its SubFormat GUID so callers can switch on one value.
/// </summary>
public sealed record Wav_FormatChunk
{
    /// <summary>KSDATAFORMAT_SUBTYPE_PCM.</summary>
    public static readonly Guid SubFormatPcm = new("00000001-0000-0010-8000-00aa00389b71");
    /// <summary>KSDATAFORMAT_SUBTYPE_IEEE_FLOAT.</summary>
    public static readonly Guid SubFormatIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    public required Wav_FormatTag FormatTag { get; init; }
    public required int Channels { get; init; }
    public required int SampleRate { get; init; }
    public required uint AvgBytesPerSecond { get; init; }
    public required int BlockAlign { get; init; }
    /// <summary><c>wBitsPerSample</c>: the CONTAINER size per sample (8/16/24/32/64).</summary>
    public required int BitsPerSample { get; init; }
    /// <summary><c>cbSize</c> (0 for plain WAVEFORMATEX).</summary>
    public int ExtraSize { get; init; }

    // EXTENSIBLE only
    /// <summary>Significant bits (EXTENSIBLE <c>wValidBitsPerSample</c>); equals <see cref="BitsPerSample"/> otherwise.</summary>
    public required int ValidBitsPerSample { get; init; }
    public AudioChannelMask ChannelMask { get; init; }
    public Guid? SubFormat { get; init; }

    /// <summary><see cref="FormatTag"/> with EXTENSIBLE resolved via SubFormat (PCM / IEEE float / else Unknown).</summary>
    public Wav_FormatTag EffectiveTag =>
        FormatTag != Wav_FormatTag.Extensible ? FormatTag
        : SubFormat == SubFormatPcm ? Wav_FormatTag.Pcm
        : SubFormat == SubFormatIeeeFloat ? Wav_FormatTag.IeeeFloat
        : SubFormat is { } g && (g.ToByteArray()[4..] is var tail && tail.AsSpan().SequenceEqual(SubFormatPcm.ToByteArray().AsSpan(4)))
            ? (Wav_FormatTag)BinaryPrimitives.ReadUInt16LittleEndian(g.ToByteArray())   // generic KSDATAFORMAT_SUBTYPE_* wrapping a legacy tag
        : Wav_FormatTag.Unknown;

    /// <summary>Bytes per sample container: BlockAlign / Channels when consistent, else ceil(bits/8).</summary>
    public int ContainerBytes =>
        Channels > 0 && BlockAlign > 0 && BlockAlign % Channels == 0 && BlockAlign / Channels * 8 >= BitsPerSample
            ? BlockAlign / Channels
            : (BitsPerSample + 7) / 8;

    /// <summary>Parses a <c>fmt </c> chunk body (≥ 16 bytes).</summary>
    public static Wav_FormatChunk Parse(ReadOnlySpan<byte> body)
    {
        if (body.Length < 16)
            throw new AudioDecoder_FormatException($"fmt chunk is {body.Length} bytes, need at least 16", AudioDecoder_Container.Wav);
        var tag = (Wav_FormatTag)BinaryPrimitives.ReadUInt16LittleEndian(body);
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(2));
        int rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(4));
        uint avg = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(8));
        int align = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(12));
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(14));
        int cb = body.Length >= 18 ? BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(16)) : 0;

        int validBits = bits;
        AudioChannelMask mask = AudioChannelMask.None;
        Guid? sub = null;
        if (tag == Wav_FormatTag.Extensible)
        {
            if (body.Length < 40)
                throw new AudioDecoder_FormatException($"WAVE_FORMAT_EXTENSIBLE fmt chunk is {body.Length} bytes, need 40", AudioDecoder_Container.Wav);
            validBits = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(18));
            if (validBits == 0 || validBits > bits) validBits = bits;   // some writers put 0 here
            mask = (AudioChannelMask)BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(20));
            sub = new Guid(body.Slice(24, 16));
        }

        if (channels <= 0) throw new AudioDecoder_FormatException("fmt declares 0 channels", AudioDecoder_Container.Wav);
        if (rate <= 0) throw new AudioDecoder_FormatException("fmt declares sample rate 0", AudioDecoder_Container.Wav);

        return new Wav_FormatChunk
        {
            FormatTag = tag,
            Channels = channels,
            SampleRate = rate,
            AvgBytesPerSecond = avg,
            BlockAlign = align,
            BitsPerSample = bits,
            ExtraSize = cb,
            ValidBitsPerSample = validBits,
            ChannelMask = mask,
            SubFormat = sub,
        };
    }
}