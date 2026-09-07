namespace AN.Audio.Midi.Internal;

/// <summary>
/// MIDI 2.0 Bit Scaling (M2-115-U §3 Min-Center-Max; ground truth _EXTERNAL_APIS/UMP_MIDI2_Format.md).
/// Upscaling preserves min, centre and max exactly and is reversible by truncation, which is what makes it
/// safe to expose MIDI 1.0 data through MIDI 2.0-sized accessors (SPEC-30 D25). Pure, allocation-free.
/// </summary>
internal static class Midi_BitScaling
{
    /// <summary>Min-Center-Max upscale of <paramref name="value"/> with <paramref name="srcBits"/> significant bits to <paramref name="dstBits"/>.</summary>
    public static uint Upscale(uint value, int srcBits, int dstBits)
    {
        if (srcBits >= dstBits) return value >> (srcBits - dstBits);
        int scaleBits = dstBits - srcBits;
        uint srcCenter = 1u << (srcBits - 1);
        if (value <= srcCenter) return value << scaleBits;   // lower half: plain shift (0 → 0, centre → centre)

        // Upper half: shift, then replicate the low bits downward so max → all ones.
        int repeat = srcBits - 1;
        uint bitShifted = (value - srcCenter) << scaleBits;
        uint result = bitShifted | (1u << (dstBits - 1));
        for (int fill = scaleBits; fill > 0; fill -= repeat)
        {
            bitShifted >>= repeat;
            result |= bitShifted;
        }
        return result;
    }

    /// <summary>Truncating downscale. Downscale(Upscale(v)) == v.</summary>
    public static uint Downscale(uint value, int srcBits, int dstBits) => srcBits <= dstBits ? value << (dstBits - srcBits) : value >> (srcBits - dstBits);

    public static ushort Up7To16(byte v) => (ushort)Upscale(v, 7, 16);
    public static uint Up7To32(byte v) => Upscale(v, 7, 32);
    public static uint Up14To32(int v) => Upscale((uint)v, 14, 32);
    public static byte Down16To7(ushort v) => (byte)(v >> 9);
    public static byte Down32To7(uint v) => (byte)(v >> 25);
    public static int Down32To14(uint v) => (int)(v >> 18);
}