using System.Buffers.Binary;
using System.Text;

namespace AN.Audio.Formats.Internal;

/// <summary>
/// The VBR/gapless information block encoders write into the FIRST MPEG frame ("Xing"/"Info" from LAME and friends,
/// "VBRI" from Fraunhofer). Ground truth: NLayer <c>MpegFrame.ParseXing/ParseVBRI</c> (3.0.0) and the LAME tag layout.
/// NLayer keeps its copy private; we parse the same bytes from the peek buffer so <c>Mp3_Decoder</c> can expose them.
/// </summary>
internal readonly record struct MpegXingHeader_Info(
    bool IsVbr,                // "Xing" (VBR) or "VBRI" vs "Info" (CBR written by LAME)
    int? FrameCount,           // MPEG frames in the stream, excluding this header frame
    int? ByteCount,            // bytes of audio, excluding this header frame
    int EncoderDelay,          // samples LAME asks the decoder to drop at the start (0 when no LAME block)
    int EncoderPadding,        // samples to drop at the end
    string? Encoder)           // e.g. "LAME3.100" or "Lavc61.19"
{
    /// <summary>Samples per channel after gapless trimming, when the frame count is known.</summary>
    public long? TotalFramesAfterTrim(int samplesPerFrame)
        => FrameCount is { } f ? Math.Max(0, (long)f * samplesPerFrame - EncoderDelay - EncoderPadding) : null;
}

internal static class MpegXingHeader
{
    /// <summary>
    /// Looks for a Xing/Info or VBRI block inside the frame that starts at <paramref name="frame"/> (header included;
    /// the span may be shorter than the frame). Returns false when the frame is ordinary audio.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> frame, in MpegFrameHeader_Info header, out MpegXingHeader_Info info)
    {
        info = default;
        int xingOffset = 4 + (header.HasCrc ? 2 : 0) + header.SideInfoSize;
        if (frame.Length >= xingOffset + 8 && IsTag(frame.Slice(xingOffset), "Xing", "Info"))
            return TryParseXing(frame.Slice(xingOffset), out info);
        const int VbriOffset = 4 + 32;   // VBRI always sits 32 bytes after the header
        if (frame.Length >= VbriOffset + 26 && IsTag(frame.Slice(VbriOffset), "VBRI"))
            return TryParseVbri(frame.Slice(VbriOffset), out info);
        return false;
    }

    private static bool IsTag(ReadOnlySpan<byte> s, string a, string? b = null)
        => s.Length >= 4 && (s[0] == a[0] && s[1] == a[1] && s[2] == a[2] && s[3] == a[3]
                             || b is not null && s[0] == b[0] && s[1] == b[1] && s[2] == b[2] && s[3] == b[3]);

    private static bool TryParseXing(ReadOnlySpan<byte> x, out MpegXingHeader_Info info)
    {
        info = default;
        bool isVbr = x[0] == (byte)'X';
        int p = 4;
        if (x.Length < p + 4) return false;
        uint flags = BinaryPrimitives.ReadUInt32BigEndian(x.Slice(p)); p += 4;
        int? frames = null, bytes = null;
        if ((flags & 0x1) != 0) { if (x.Length < p + 4) return false; frames = (int)BinaryPrimitives.ReadUInt32BigEndian(x.Slice(p)); p += 4; }
        if ((flags & 0x2) != 0) { if (x.Length < p + 4) return false; bytes = (int)BinaryPrimitives.ReadUInt32BigEndian(x.Slice(p)); p += 4; }
        if ((flags & 0x4) != 0) { p += 100; }   // TOC: 100 seek-table bytes we do not use
        if ((flags & 0x8) != 0) { p += 4; }     // quality indicator

        // LAME extension: 9-byte encoder string, then revision/method, lowpass, replay gain (8), flags, ABR rate, delay/padding (3), ...
        int delay = 0, padding = 0;
        string? encoder = null;
        if (x.Length >= p + 24)
        {
            var enc = x.Slice(p, 9);
            bool printable = true;
            foreach (byte c in enc) if (c != 0 && (c < 0x20 || c > 0x7E)) { printable = false; break; }
            if (printable && enc[0] != 0)
            {
                encoder = Encoding.ASCII.GetString(enc).TrimEnd('\0', ' ');
                // Only LAME-compatible writers (LAME, Lavc, Lavf) fill the gapless fields; revision nibble must be 0 or 1
                bool lameLayout = enc.StartsWith("LAME"u8) || enc.StartsWith("Lavc"u8) || enc.StartsWith("Lavf"u8);
                if (lameLayout && (x[p + 9] & 0xF0) <= 0x10)
                {
                    delay = (x[p + 21] << 4) | (x[p + 22] >> 4);
                    padding = ((x[p + 22] & 0x0F) << 8) | x[p + 23];
                }
            }
        }
        info = new MpegXingHeader_Info(isVbr, frames, bytes, delay, padding, encoder);
        return true;
    }

    private static bool TryParseVbri(ReadOnlySpan<byte> v, out MpegXingHeader_Info info)
    {
        // "VBRI" version(2) delay(2) quality(2) bytes(4) frames(4) tocEntries(2) scale(2) entrySize(2) framesPerEntry(2)
        int bytes = (int)BinaryPrimitives.ReadUInt32BigEndian(v.Slice(10));
        int frames = (int)BinaryPrimitives.ReadUInt32BigEndian(v.Slice(14));
        info = new MpegXingHeader_Info(IsVbr: true, frames, bytes, EncoderDelay: 0, EncoderPadding: 0, Encoder: "VBRI");
        return true;
    }
}