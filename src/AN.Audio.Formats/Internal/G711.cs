namespace AN.Audio.Formats.Internal;

/// <summary>
/// ITU-T G.711 companded 8-bit → linear 16-bit expansion tables (formulas as in Sun's reference <c>g711.c</c>: µ-law ±32124,
/// A-law ±32256 on the 16-bit scale). Used by WAV <c>WAVE_FORMAT_MULAW</c>/<c>WAVE_FORMAT_ALAW</c> (and AIFF later).
/// </summary>
internal static class G711
{
    public static readonly short[] MuLawToInt16 = BuildMuLaw();
    public static readonly short[] ALawToInt16 = BuildALaw();

    private static short[] BuildMuLaw()
    {
        var t = new short[256];
        for (int i = 0; i < 256; i++)
        {
            int u = ~i & 0xFF;                       // µ-law bytes are stored inverted
            int v = ((u & 0x0F) << 3) + 0x84;        // mantissa + bias (132)
            v <<= (u >> 4) & 0x07;                   // segment
            t[i] = (short)((u & 0x80) != 0 ? 0x84 - v : v - 0x84);
        }
        return t;
    }

    private static short[] BuildALaw()
    {
        var t = new short[256];
        for (int i = 0; i < 256; i++)
        {
            int a = i ^ 0x55;                        // even bits are inverted on the wire
            int seg = (a & 0x70) >> 4;
            int v = (a & 0x0F) << 4;
            v += seg == 0 ? 0x008 : 0x108;
            if (seg > 1) v <<= seg - 1;
            t[i] = (short)((a & 0x80) != 0 ? v : -v);   // A-law: sign bit SET means positive
        }
        return t;
    }

    /// <summary>Expands <paramref name="src"/> companded bytes into <paramref name="dst"/> (same sample count).</summary>
    public static void Expand(ReadOnlySpan<byte> src, Span<short> dst, short[] table)
    {
        for (int i = 0; i < src.Length; i++) dst[i] = table[src[i]];
    }
}