namespace AN.Audio;

/// <summary>
/// Speaker-position bit mask, values and order exactly as <c>SPEAKER_*</c> in <c>ksmedia.h</c>
/// (WAVEFORMATEXTENSIBLE.dwChannelMask). Interleaved channels appear in ascending bit order.
/// See <c>_EXTERNAL_APIS/WaveFormatExtensible_ChannelMask.md</c>.
/// </summary>
[Flags]
public enum AudioChannelMask : uint
{
    /// <summary>No positions declared (mono/stereo WAVEFORMATEX, or a writer that left it 0).</summary>
    None = 0,
    FrontLeft = 0x1,
    FrontRight = 0x2,
    FrontCenter = 0x4,
    LowFrequency = 0x8,
    BackLeft = 0x10,
    BackRight = 0x20,
    FrontLeftOfCenter = 0x40,
    FrontRightOfCenter = 0x80,
    BackCenter = 0x100,
    SideLeft = 0x200,
    SideRight = 0x400,
    TopCenter = 0x800,
    TopFrontLeft = 0x1000,
    TopFrontCenter = 0x2000,
    TopFrontRight = 0x4000,
    TopBackLeft = 0x8000,
    TopBackCenter = 0x10000,
    TopBackRight = 0x20000,

    /// <summary>SPEAKER_RESERVED: bits 18..30.</summary>
    Reserved = 0x7FFC0000,
    /// <summary>SPEAKER_ALL: any possible permutation of channels.</summary>
    All = 0x80000000,

    // Common layouts (KSAUDIO_SPEAKER_*)
    Mono = FrontCenter,
    Stereo = FrontLeft | FrontRight,
    Quad = FrontLeft | FrontRight | BackLeft | BackRight,
    Surround = FrontLeft | FrontRight | FrontCenter | BackCenter,
    FivePointOne = FrontLeft | FrontRight | FrontCenter | LowFrequency | BackLeft | BackRight,
    FivePointOneSurround = FrontLeft | FrontRight | FrontCenter | LowFrequency | SideLeft | SideRight,
    SevenPointOne = FivePointOne | FrontLeftOfCenter | FrontRightOfCenter,
    SevenPointOneSurround = FivePointOne | SideLeft | SideRight,
}

public static class AudioChannelMaskExtensions
{
    /// <summary>Number of speaker positions set (excludes <see cref="AudioChannelMask.All"/>).</summary>
    public static int PositionCount(this AudioChannelMask mask)
        => System.Numerics.BitOperations.PopCount((uint)(mask & ~AudioChannelMask.All));
}