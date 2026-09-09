using System.Buffers.Binary;
using AN.Audio.Formats.Flac;

namespace AN.Audio.Formats.Tests.Support;

/// <summary>Byte-level surgery on FLAC fixtures for cases ffmpeg does not produce (SEEKTABLE, unknown total).</summary>
public static class FlacFixtureTools
{
    /// <summary>Walks the metadata blocks; returns (offset of each block header, type, length, isLast) and the audio start.</summary>
    public static (List<(int headerOffset, Flac_MetadataBlockType type, int length, bool last)> blocks, int audioStart) WalkMetadata(byte[] flac)
    {
        if (flac[0] != 'f' || flac[1] != 'L' || flac[2] != 'a' || flac[3] != 'C') throw new InvalidDataException("not FLAC");
        var blocks = new List<(int, Flac_MetadataBlockType, int, bool)>();
        int pos = 4;
        while (true)
        {
            bool last = (flac[pos] & 0x80) != 0;
            var type = (Flac_MetadataBlockType)(flac[pos] & 0x7F);
            int len = (flac[pos + 1] << 16) | (flac[pos + 2] << 8) | flac[pos + 3];
            blocks.Add((pos, type, len, last));
            pos += 4 + len;
            if (last) break;
        }
        return (blocks, pos);
    }

    /// <summary>Sets STREAMINFO total-samples (36 bits) to 0 in place → D10 unknown length.</summary>
    public static void ZeroTotalSamples(byte[] flac)
    {
        var (blocks, _) = WalkMetadata(flac);
        var si = blocks.First(b => b.type == Flac_MetadataBlockType.StreamInfo);
        int body = si.headerOffset + 4;
        // bytes 13..17 of STREAMINFO: low 4 bits of byte 13 + bytes 14..17 = total samples
        flac[body + 13] &= 0xF0;
        flac[body + 14] = flac[body + 15] = flac[body + 16] = flac[body + 17] = 0;
    }

    /// <summary>
    /// Decodes the file once to learn every frame's (firstSample, offset, size), then rebuilds the file with a SEEKTABLE
    /// block (one point every <paramref name="everyNthFrame"/> frames) inserted after STREAMINFO.
    /// </summary>
    public static byte[] InsertSeekTable(byte[] flac, int everyNthFrame)
    {
        var (blocks, audioStart) = WalkMetadata(flac);
        var points = new List<(long sample, long offset, int count)>();
        using (var d = new Flac_Decoder(new MemoryStream(flac)))
        {
            // Use the public seek scanner indirectly: decode frame by frame through the reference decoder's positions.
            // Flac_Decoder does not expose per-frame offsets, so scan frame headers ourselves via the sync + CRC-8 rule.
            int frameIndex = 0;
            long pos = audioStart;
            while (pos < flac.Length - 6)
            {
                if (flac[pos] == 0xFF && (flac[pos + 1] & 0xFE) == 0xF8 && TryHeaderFirstSample(flac, (int)pos, d.StreamInfo, out long first, out int frameSamples))
                {
                    if (frameIndex % everyNthFrame == 0) points.Add((first, pos - audioStart, frameSamples));
                    frameIndex++;
                    pos += 16;   // skip past the header so sync bytes inside it are not re-matched
                }
                else pos++;
            }
        }
        if (points.Count < 2) throw new InvalidOperationException("frame scan found too few frames");

        var table = new byte[points.Count * 18];
        for (int i = 0; i < points.Count; i++)
        {
            BinaryPrimitives.WriteUInt64BigEndian(table.AsSpan(i * 18), (ulong)points[i].sample);
            BinaryPrimitives.WriteUInt64BigEndian(table.AsSpan(i * 18 + 8), (ulong)points[i].offset);
            BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(i * 18 + 16), (ushort)points[i].count);
        }

        var si = blocks[0];
        int insertAt = si.headerOffset + 4 + si.length;
        var result = new byte[flac.Length + 4 + table.Length];
        flac.AsSpan(0, insertAt).CopyTo(result);
        result[si.headerOffset] &= 0x7F;   // STREAMINFO is no longer last (if it was)
        result[insertAt] = (byte)Flac_MetadataBlockType.SeekTable;   // not last: the original blocks follow
        result[insertAt + 1] = (byte)(table.Length >> 16); result[insertAt + 2] = (byte)(table.Length >> 8); result[insertAt + 3] = (byte)table.Length;
        table.CopyTo(result, insertAt + 4);
        flac.AsSpan(insertAt).CopyTo(result.AsSpan(insertAt + 4 + table.Length));
        return result;
    }

    private static bool TryHeaderFirstSample(byte[] h, int at, Flac_StreamInfo si, out long firstSample, out int frameSamples)
    {
        firstSample = 0; frameSamples = 0;
        if (at + 16 > h.Length) return false;
        bool variable = (h[at + 1] & 1) != 0;
        int bsCode = h[at + 2] >> 4, srCode = h[at + 2] & 0xF;
        int layout = h[at + 3] >> 4, bdCode = (h[at + 3] >> 1) & 7;
        if ((h[at + 3] & 1) != 0 || bsCode == 0 || srCode == 15) return false;
        int channels = layout <= 7 ? layout + 1 : layout <= 10 ? 2 : -1;
        if (channels != si.Channels) return false;
        int bits = bdCode switch { 0 => si.BitsPerSample, 1 => 8, 2 => 12, 4 => 16, 5 => 20, 6 => 24, 7 => 32, _ => -1 };
        if (bits != si.BitsPerSample) return false;
        int p = at + 4;
        int lead = System.Numerics.BitOperations.LeadingZeroCount((uint)~(h[p] << 24));
        if (lead > 6) return false;
        long coded = lead == 0 ? h[p] : h[p] & (0x7F >> lead);
        p++;
        for (int i = 1; i < lead; i++, p++) { if ((h[p] & 0xC0) != 0x80) return false; coded = (coded << 6) | (uint)(h[p] & 0x3F); }
        int blockSize = bsCode switch { 1 => 192, >= 2 and <= 5 => 576 << (bsCode - 2), 6 => h[p] + 1, 7 => (h[p] << 8 | h[p + 1]) + 1, _ => 256 << (bsCode - 8) };
        if (bsCode == 6) p += 1; else if (bsCode == 7) p += 2;
        if (srCode == 12) p += 1; else if (srCode is 13 or 14) p += 2;
        // CRC-8 over header bytes [at, p)
        byte crc = 0;
        for (int i = at; i < p; i++) { crc ^= h[i]; for (int b = 0; b < 8; b++) crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1); }
        if (crc != h[p]) return false;
        firstSample = variable ? coded : coded * si.MaxBlockSize;
        frameSamples = blockSize;
        return true;
    }
}