namespace AN.Audio.Formats;

/// <summary>Result of <see cref="AudioDecoder.DecodeAll(Stream, AudioDecoder_FormatHint, bool)"/>: the whole stream as interleaved float (D4).</summary>
public sealed class AudioDecoder_Pcm
{
    public AudioDecoder_StreamInfo Info { get; }

    /// <summary>Exactly <c>FrameCount * Info.Channels</c> samples.</summary>
    public float[] Interleaved { get; }

    public int FrameCount { get; }

    /// <summary>D12: the source ended before its declared length.</summary>
    public bool EndedEarly { get; }

    public TimeSpan Duration => TimeSpan.FromSeconds((double)FrameCount / Info.SampleRate);

    internal AudioDecoder_Pcm(AudioDecoder_StreamInfo info, float[] interleaved, int frameCount, bool endedEarly)
    {
        Info = info;
        Interleaved = interleaved;
        FrameCount = frameCount;
        EndedEarly = endedEarly;
    }
}