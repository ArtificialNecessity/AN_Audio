using System.Buffers.Binary;
using System.Text;
using AN.Audio.Formats.Internal;

namespace AN.Audio.Formats.Mp3;

/// <summary>
/// ID3v2.2 / 2.3 / 2.4 tag parser (id3.org informal standards). Reads the tag from the <see cref="PeekableStream"/> look-ahead
/// WITHOUT consuming it: NLayer addresses the stream by absolute offset from byte 0 and skips the tag itself. Text frames land
/// in <see cref="Mp3_Id3Tags"/>; pictures go to the callback (span over the tag buffer, valid for the call only) or are skipped.
/// Tolerant: any malformed frame ends the walk with what was read so far — tags never make audio undecodable.
/// </summary>
internal static class Mp3_Id3v2
{
    private static readonly Encoding Latin1 = Encoding.Latin1;
    private static readonly Encoding Utf16Be = new UnicodeEncoding(bigEndian: true, byteOrderMark: false);

    /// <summary>Total length of the ID3v2 tag at the head of <paramref name="head"/> (header + body + footer), or 0 when there is none.</summary>
    public static int TagLength(ReadOnlySpan<byte> head)
    {
        if (head.Length < 10 || head[0] != (byte)'I' || head[1] != (byte)'D' || head[2] != (byte)'3') return 0;
        if (head[3] is < 2 or > 4 || head[4] == 0xFF) return 0;
        if (((head[6] | head[7] | head[8] | head[9]) & 0x80) != 0) return 0;   // syncsafe bytes must have bit 7 clear
        int size = SyncSafe(head.Slice(6));
        bool footer = (head[5] & 0x10) != 0;
        return 10 + size + (footer ? 10 : 0);
    }

    /// <summary>Parses the whole tag (<paramref name="tag"/> must be exactly <see cref="TagLength"/> bytes, or shorter if the stream was truncated).</summary>
    public static Mp3_Id3Tags Parse(ReadOnlySpan<byte> tag, AudioDecoder_PictureCallback? onPicture)
    {
        var tags = new Mp3_Id3Tags { TagLength = tag.Length };
        if (tag.Length < 10) return tags;
        int major = tag[3];
        byte flags = tag[5];
        tags.Source = major switch { 2 => Mp3_Id3Source.Id3v22, 3 => Mp3_Id3Source.Id3v23, _ => Mp3_Id3Source.Id3v24 };
        int declared = SyncSafe(tag.Slice(6));
        ReadOnlySpan<byte> body = tag.Slice(10, Math.Min(declared, tag.Length - 10));

        // v2.2/2.3: unsynchronisation applies to the whole tag body; v2.4 applies it per frame
        byte[]? unsynced = null;
        if ((flags & 0x80) != 0 && major < 4) { unsynced = Deunsync(body); body = unsynced; }

        // extended header (v2.3: size excludes its own 4 bytes; v2.4: syncsafe size includes itself)
        if ((flags & 0x40) != 0 && major >= 3)
        {
            if (body.Length < 4) return tags;
            int ext = major == 3 ? (int)BinaryPrimitives.ReadUInt32BigEndian(body) + 4 : SyncSafe(body);
            if (ext < 4 || ext > body.Length) return tags;
            body = body.Slice(ext);
        }

        int idLen = major == 2 ? 3 : 4, headerLen = major == 2 ? 6 : 10;
        while (body.Length >= headerLen)
        {
            if (body[0] == 0) break;   // padding
            string rawId = Latin1.GetString(body.Slice(0, idLen));
            int size = major switch
            {
                2 => (body[3] << 16) | (body[4] << 8) | body[5],
                3 => (int)BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4)),
                _ => SyncSafe(body.Slice(4)),
            };
            int frameFlags = major == 2 ? 0 : body[9];
            body = body.Slice(headerLen);
            if (size < 0 || size > body.Length) break;   // malformed / truncated: keep what we have
            ReadOnlySpan<byte> data = body.Slice(0, size);
            body = body.Slice(size);

            if (!IsValidId(rawId)) break;
            // format flags we cannot honour: compression / encryption → skip the frame
            bool compressed = major == 3 ? (frameFlags & 0x80) != 0 : (frameFlags & 0x08) != 0;
            bool encrypted = major == 3 ? (frameFlags & 0x40) != 0 : (frameFlags & 0x04) != 0;
            if (compressed || encrypted) continue;
            bool grouped = major == 3 ? (frameFlags & 0x20) != 0 : (frameFlags & 0x40) != 0;
            if (grouped) { if (data.Length < 1) continue; data = data.Slice(1); }
            byte[]? frameUnsynced = null;
            if (major == 4)
            {
                if ((frameFlags & 0x01) != 0) { if (data.Length < 4) continue; data = data.Slice(4); }   // data length indicator
                if ((frameFlags & 0x02) != 0) { frameUnsynced = Deunsync(data); data = frameUnsynced; }
            }

            var id = new Mp3_Id3FrameId(major == 2 ? MapV22(rawId) : rawId);
            if (id == Mp3_Id3FrameId.Picture) { tags.PictureCount++; if (onPicture is not null) DeliverPicture(data, major, onPicture); }
            else if (id == Mp3_Id3FrameId.Comment) ParseComment(data, tags);
            else if (id == Mp3_Id3FrameId.UserText) ParseUserText(data, tags);
            else if (id.Value[0] == 'T' && data.Length >= 1) tags.Add(id, ReadText(data.Slice(1), data[0], terminated: false, out _));
            // everything else (USLT, PRIV, GEOB, UFID, …) is skipped
        }
        return tags;
    }

    /// <summary>ID3v1: the last 128 bytes of the file, "TAG" + fixed-width Latin-1 fields.</summary>
    public static Mp3_Id3Tags? ParseV1(ReadOnlySpan<byte> tail)
    {
        if (tail.Length != 128 || tail[0] != (byte)'T' || tail[1] != (byte)'A' || tail[2] != (byte)'G') return null;
        var tags = new Mp3_Id3Tags { Source = Mp3_Id3Source.Id3v1 };
        tags.Add(Mp3_Id3FrameId.Title, Fixed(tail.Slice(3, 30)));
        tags.Add(Mp3_Id3FrameId.Artist, Fixed(tail.Slice(33, 30)));
        tags.Add(Mp3_Id3FrameId.Album, Fixed(tail.Slice(63, 30)));
        tags.Add(Mp3_Id3FrameId.Year, Fixed(tail.Slice(93, 4)));
        if (tail[125] == 0 && tail[126] != 0)   // ID3v1.1 track number
        {
            tags.Add(Mp3_Id3FrameId.Comment, Fixed(tail.Slice(97, 28)));
            tags.Add(Mp3_Id3FrameId.Track, tail[126].ToString());
        }
        else tags.Add(Mp3_Id3FrameId.Comment, Fixed(tail.Slice(97, 30)));
        if (tail[127] != 255) tags.Add(Mp3_Id3FrameId.Genre, tail[127].ToString());
        return tags;

        static string Fixed(ReadOnlySpan<byte> f) => Latin1.GetString(f).TrimEnd('\0', ' ');
    }

    // ── frame bodies ──

    private static void DeliverPicture(ReadOnlySpan<byte> d, int major, AudioDecoder_PictureCallback cb)
    {
        // APIC: enc(1) mime(latin1, 0-terminated) type(1) description(enc, terminated) data
        // PIC (v2.2): enc(1) format(3: "PNG"/"JPG") type(1) description data
        if (d.Length < 2) return;
        byte enc = d[0]; d = d.Slice(1);
        string mime;
        if (major == 2)
        {
            if (d.Length < 3) return;
            string fmt = Latin1.GetString(d.Slice(0, 3)); d = d.Slice(3);
            mime = fmt.Equals("PNG", StringComparison.OrdinalIgnoreCase) ? "image/png" : fmt.Equals("JPG", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" : "image/" + fmt.ToLowerInvariant();
        }
        else
        {
            mime = ReadText(d, 0, terminated: true, out int used); d = d.Slice(used);
            if (mime.Length == 0) mime = "image/";   // spec: omitted means "image/"
        }
        if (d.Length < 1) return;
        var type = (AudioDecoder_PictureType)Math.Min((int)d[0], 20); d = d.Slice(1);
        string description = ReadText(d, enc, terminated: true, out int descUsed); d = d.Slice(descUsed);
        cb(new AudioDecoder_PictureInfo(type, mime, description, d.Length), d);
    }

    private static void ParseComment(ReadOnlySpan<byte> d, Mp3_Id3Tags tags)
    {
        // COMM: enc(1) language(3) short-description(enc, terminated) text(enc)
        if (d.Length < 4) return;
        byte enc = d[0]; d = d.Slice(4);
        ReadText(d, enc, terminated: true, out int used); d = d.Slice(used);
        tags.Add(Mp3_Id3FrameId.Comment, ReadText(d, enc, terminated: false, out _));
    }

    private static void ParseUserText(ReadOnlySpan<byte> d, Mp3_Id3Tags tags)
    {
        // TXXX: enc(1) description(enc, terminated) value(enc)
        if (d.Length < 1) return;
        byte enc = d[0]; d = d.Slice(1);
        string desc = ReadText(d, enc, terminated: true, out int used); d = d.Slice(used);
        tags.Add(new Mp3_Id3FrameId("TXXX:" + desc), ReadText(d, enc, terminated: false, out _));
    }

    /// <summary>Decodes text in ID3 encoding <paramref name="enc"/> (0 Latin-1, 1 UTF-16 w/ BOM, 2 UTF-16BE, 3 UTF-8), stopping at the terminator when <paramref name="terminated"/>.</summary>
    private static string ReadText(ReadOnlySpan<byte> d, byte enc, bool terminated, out int bytesUsed)
    {
        bool wide = enc is 1 or 2;
        int end = d.Length;
        if (terminated)
        {
            end = -1;
            if (wide) { for (int i = 0; i + 1 < d.Length; i += 2) if (d[i] == 0 && d[i + 1] == 0) { end = i; break; } }
            else end = d.IndexOf((byte)0);
            if (end < 0) { end = d.Length; bytesUsed = d.Length; }
            else bytesUsed = end + (wide ? 2 : 1);
        }
        else bytesUsed = d.Length;
        var text = d.Slice(0, end);
        string s;
        switch (enc)
        {
            case 1:
                if (text.Length >= 2 && text[0] == 0xFF && text[1] == 0xFE) s = Encoding.Unicode.GetString(text.Slice(2));
                else if (text.Length >= 2 && text[0] == 0xFE && text[1] == 0xFF) s = Utf16Be.GetString(text.Slice(2));
                else s = Encoding.Unicode.GetString(text);   // no BOM: LE is what Windows writers emit
                break;
            case 2: s = Utf16Be.GetString(text); break;
            case 3: s = Encoding.UTF8.GetString(text); break;
            default: s = Latin1.GetString(text); break;
        }
        // v2.4 allows multiple null-separated values in one text frame; present them joined
        return s.TrimEnd('\0').Replace('\0', '/');
    }

    // ── helpers ──

    private static int SyncSafe(ReadOnlySpan<byte> b) => ((b[0] & 0x7F) << 21) | ((b[1] & 0x7F) << 14) | ((b[2] & 0x7F) << 7) | (b[3] & 0x7F);

    /// <summary>Reverses unsynchronisation: every <c>FF 00</c> becomes <c>FF</c>.</summary>
    private static byte[] Deunsync(ReadOnlySpan<byte> src)
    {
        var dst = new byte[src.Length];
        int n = 0;
        for (int i = 0; i < src.Length; i++)
        {
            dst[n++] = src[i];
            if (src[i] == 0xFF && i + 1 < src.Length && src[i + 1] == 0x00) i++;
        }
        return dst[..n];
    }

    private static bool IsValidId(string id)
    {
        foreach (char c in id) if (!(c is >= 'A' and <= 'Z' or >= '0' and <= '9')) return false;
        return true;
    }

    private static string MapV22(string id) => id switch
    {
        "TT2" => "TIT2", "TP1" => "TPE1", "TP2" => "TPE2", "TAL" => "TALB", "TRK" => "TRCK", "TYE" => "TYER",
        "TCO" => "TCON", "COM" => "COMM", "PIC" => "APIC", "TXX" => "TXXX", "TT1" => "TIT1", "TT3" => "TIT3",
        "TP3" => "TPE3", "TP4" => "TPE4", "TCM" => "TCOM", "TEN" => "TENC", "TBP" => "TBPM", "TPA" => "TPOS",
        _ => id,   // unknown 3-char id stays as-is (still starts with 'T' → kept as text)
    };
}