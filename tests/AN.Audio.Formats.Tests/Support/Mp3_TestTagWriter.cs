using System.Buffers.Binary;
using System.Text;

namespace AN.Audio.Formats.Tests.Support;

/// <summary>Synthesises ID3v2 tags (v2.2 / 2.3 / 2.4) to splice in front of an MP3 fixture, so tag shapes need no committed files.</summary>
public static class Mp3_TestTagWriter
{
    public sealed record Frame(string Id, byte[] Body);

    public static Frame Text(string id, string value, byte encoding = 3)
    {
        byte[] text = encoding switch
        {
            0 => Encoding.Latin1.GetBytes(value),
            1 => [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(value)],
            2 => new UnicodeEncoding(true, false).GetBytes(value),
            _ => Encoding.UTF8.GetBytes(value),
        };
        return new Frame(id, [encoding, .. text]);
    }

    public static Frame UserText(string description, string value)
        => new("TXXX", [3, .. Encoding.UTF8.GetBytes(description), 0, .. Encoding.UTF8.GetBytes(value)]);

    public static Frame Comment(string text, string lang = "eng")
        => new("COMM", [3, .. Encoding.Latin1.GetBytes(lang), 0, .. Encoding.UTF8.GetBytes(text)]);

    public static Frame Picture(string mime, byte pictureType, string description, byte[] image)
        => new("APIC", [0, .. Encoding.Latin1.GetBytes(mime), 0, pictureType, .. Encoding.Latin1.GetBytes(description), 0, .. image]);

    /// <summary>v2.2 picture frame (3-char format instead of MIME).</summary>
    public static Frame PictureV22(string format3, byte pictureType, string description, byte[] image)
        => new("PIC", [0, .. Encoding.Latin1.GetBytes(format3), pictureType, .. Encoding.Latin1.GetBytes(description), 0, .. image]);

    /// <summary>Builds a complete tag. <paramref name="unsync"/> applies whole-tag unsynchronisation (v2.2/2.3 semantics) or per-frame (v2.4).</summary>
    public static byte[] Build(int major, IEnumerable<Frame> frames, int padding = 0, bool unsync = false, bool footer = false, bool extendedHeader = false)
    {
        var body = new MemoryStream();
        if (extendedHeader)
        {
            if (major == 3) { body.Write([0, 0, 0, 6, 0, 0, 0, 0, 0, 0]); }            // size(4, excl.)=6, flags(2), padding size(4)
            else if (major == 4) { body.Write([0, 0, 0, 6, 1, 0]); }                     // syncsafe size(4, incl.)=6, count=1, flags=0
        }
        foreach (var f in frames)
        {
            byte[] data = f.Body;
            if (major == 2)
            {
                body.Write(Encoding.Latin1.GetBytes(f.Id.Length == 3 ? f.Id : f.Id[..3]));
                body.Write([(byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length]);
                body.Write(data);
            }
            else
            {
                body.Write(Encoding.Latin1.GetBytes(f.Id));
                var size = new byte[4];
                byte flags1 = 0;
                if (major == 4 && unsync) { data = Unsync(data); flags1 = 0x02; }
                if (major == 3) BinaryPrimitives.WriteUInt32BigEndian(size, (uint)data.Length);
                else WriteSyncSafe(size, data.Length);
                body.Write(size);
                body.Write([0, flags1]);
                body.Write(data);
            }
        }
        body.Write(new byte[padding]);
        byte[] bodyBytes = body.ToArray();
        byte tagFlags = 0;
        if (unsync && major < 4) { bodyBytes = Unsync(bodyBytes); tagFlags |= 0x80; }
        if (unsync && major == 4) tagFlags |= 0x80;   // v2.4 sets the tag flag too when any frame is unsynchronised
        if (extendedHeader && major >= 3) tagFlags |= 0x40;
        if (footer && major == 4) tagFlags |= 0x10;

        var header = new byte[10];
        header[0] = (byte)'I'; header[1] = (byte)'D'; header[2] = (byte)'3';
        header[3] = (byte)major; header[4] = 0; header[5] = tagFlags;
        WriteSyncSafe(header.AsSpan(6), bodyBytes.Length);
        var tag = new List<byte>(header);
        tag.AddRange(bodyBytes);
        if (footer && major == 4)
        {
            var foot = (byte[])header.Clone();
            foot[0] = (byte)'3'; foot[1] = (byte)'D'; foot[2] = (byte)'I';
            tag.AddRange(foot);
        }
        return tag.ToArray();
    }

    /// <summary>Replaces the existing ID3v2 tag (if any) at the head of <paramref name="mp3"/> with <paramref name="tag"/>.</summary>
    public static byte[] Retag(byte[] mp3, byte[] tag)
    {
        int existing = 0;
        if (mp3.Length >= 10 && mp3[0] == 'I' && mp3[1] == 'D' && mp3[2] == '3')
        {
            int size = ((mp3[6] & 0x7F) << 21) | ((mp3[7] & 0x7F) << 14) | ((mp3[8] & 0x7F) << 7) | (mp3[9] & 0x7F);
            existing = 10 + size + ((mp3[5] & 0x10) != 0 ? 10 : 0);
        }
        return [.. tag, .. mp3.AsSpan(existing)];
    }

    public static byte[] StripTag(byte[] mp3) => Retag(mp3, []);

    private static void WriteSyncSafe(Span<byte> dst, int v)
    {
        dst[0] = (byte)((v >> 21) & 0x7F); dst[1] = (byte)((v >> 14) & 0x7F); dst[2] = (byte)((v >> 7) & 0x7F); dst[3] = (byte)(v & 0x7F);
    }

    private static byte[] Unsync(byte[] src)
    {
        var dst = new List<byte>(src.Length + 16);
        for (int i = 0; i < src.Length; i++)
        {
            dst.Add(src[i]);
            if (src[i] == 0xFF && (i + 1 == src.Length || src[i + 1] == 0x00 || (src[i + 1] & 0xE0) == 0xE0)) dst.Add(0x00);
        }
        return dst.ToArray();
    }
}