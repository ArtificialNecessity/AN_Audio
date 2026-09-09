namespace AN.Audio.Formats;

/// <summary>
/// Malformed input: the bytes do not form a valid instance of the format they claim (D12).
/// Derives from <see cref="IOException"/> (the base of <see cref="InvalidDataException"/>, which is sealed and cannot be
/// subclassed) so existing <c>catch (IOException)</c> handlers around stream reads still see it.
/// </summary>
public class AudioDecoder_FormatException : IOException
{
    public AudioDecoder_Container? Container { get; }

    public AudioDecoder_FormatException(string message, AudioDecoder_Container? container = null, Exception? inner = null)
        : base(container is { } c ? $"{c}: {message}" : message, inner)
    {
        Container = container;
    }
}

/// <summary>Valid input we do not (yet) decode, e.g. WAV ADPCM, Ogg before Phase 4 (D12). Names the encoding.</summary>
public class AudioDecoder_UnsupportedException : NotSupportedException
{
    public AudioDecoder_Container? Container { get; }

    public AudioDecoder_UnsupportedException(string message, AudioDecoder_Container? container = null)
        : base(container is { } c ? $"{c}: {message}" : message)
    {
        Container = container;
    }
}