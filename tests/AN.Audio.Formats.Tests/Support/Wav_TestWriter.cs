using System.Buffers.Binary;
using System.Text;
using AN.Audio.Formats.Wav;

namespace AN.Audio.Formats.Tests.Support;

/// <summary>
/// Synthesises WAV fixtures in memory, byte by byte, so every shape in the §WAV test plan is reproducible without
/// committed binaries. Each <c>Add*</c> appends a chunk; <see cref="Build"/> emits RIFF with the requested size field.
/// </summary>
public sealed class Wav_TestWriter
{
    private readonly List<ChunkSpec> _chunks = new();   // id + body (+ declared-length override, pad flag); emitted per layout at Build time

    public int Channels { get; private set; } = 2;
    public int SampleRate { get; private set; } = 48000;
    public int ContainerBits { get; private set; } = 16;
    public int ValidBits { get; private set; } = 16;
    public bool IsFloat { get; private set; }
    public bool Extensible { get; private set; }
    public AudioChannelMask Mask { get; private set; }
    public int BytesPerFrame => ContainerBits / 8 * Channels;

    // ── fmt ──

    public Wav_TestWriter Fmt(int channels, int sampleRate, int bits, bool isFloat = false, ushort? formatTagOverride = null)
    {
        Channels = channels; SampleRate = sampleRate; ContainerBits = bits; ValidBits = bits; IsFloat = isFloat; Extensible = false;
        var body = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(body, formatTagOverride ?? (ushort)(isFloat ? Wav_FormatTag.IeeeFloat : Wav_FormatTag.Pcm));
        FillCommon(body);
        _chunks.Add(Chunk("fmt ", body));
        return this;
    }

    public Wav_TestWriter FmtExtensible(int channels, int sampleRate, int containerBits, int validBits, AudioChannelMask mask, bool isFloat = false, Guid? subFormat = null)
    {
        Channels = channels; SampleRate = sampleRate; ContainerBits = containerBits; ValidBits = validBits; IsFloat = isFloat; Extensible = true; Mask = mask;
        var body = new byte[40];
        BinaryPrimitives.WriteUInt16LittleEndian(body, (ushort)Wav_FormatTag.Extensible);
        FillCommon(body);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(16), 22);   // cbSize
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(18), (ushort)validBits);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(20), (uint)mask);
        (subFormat ?? (isFloat ? Wav_FormatChunk.SubFormatIeeeFloat : Wav_FormatChunk.SubFormatPcm)).TryWriteBytes(body.AsSpan(24));
        _chunks.Add(Chunk("fmt ", body));
        return this;
    }

    private void FillCommon(Span<byte> body)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(2), (ushort)Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(4), (uint)SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(8), (uint)(SampleRate * BytesPerFrame));
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(12), (ushort)BytesPerFrame);
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(14), (ushort)ContainerBits);
    }

    // ── data ──

    /// <summary>Appends a data chunk with the given raw PCM bytes. <paramref name="declaredLength"/> overrides the size field (overrun / 0xFFFFFFFF tests).</summary>
    public Wav_TestWriter Data(byte[] pcm, uint? declaredLength = null, bool pad = true)
    {
        _chunks.Add(Chunk("data", pcm, declaredLength, pad));
        return this;
    }

    // ── metadata ──

    public Wav_TestWriter Smpl(int unityNote, uint pitchFraction, params (uint start, uint end, Wav_SampleLoopType type, uint playCount)[] loops)
    {
        var body = new byte[36 + loops.Length * 24];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), (uint)(1_000_000_000L / SampleRate));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), (uint)unityNote);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), pitchFraction);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(28), (uint)loops.Length);
        for (int i = 0; i < loops.Length; i++)
        {
            var l = body.AsSpan(36 + i * 24);
            BinaryPrimitives.WriteUInt32LittleEndian(l, (uint)i);
            BinaryPrimitives.WriteUInt32LittleEndian(l.Slice(4), (uint)loops[i].type);
            BinaryPrimitives.WriteUInt32LittleEndian(l.Slice(8), loops[i].start);
            BinaryPrimitives.WriteUInt32LittleEndian(l.Slice(12), loops[i].end);
            BinaryPrimitives.WriteUInt32LittleEndian(l.Slice(20), loops[i].playCount);
        }
        _chunks.Add(Chunk("smpl", body));
        return this;
    }

    public Wav_TestWriter Cue(params uint[] samplePositions)
    {
        var body = new byte[4 + samplePositions.Length * 24];
        BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)samplePositions.Length);
        for (int i = 0; i < samplePositions.Length; i++)
        {
            var c = body.AsSpan(4 + i * 24);
            BinaryPrimitives.WriteUInt32LittleEndian(c, (uint)(i + 1));
            BinaryPrimitives.WriteUInt32LittleEndian(c.Slice(4), samplePositions[i]);
            Wav_ChunkId.Data.WriteTo(c.Slice(8));
            BinaryPrimitives.WriteUInt32LittleEndian(c.Slice(20), samplePositions[i]);
        }
        _chunks.Add(Chunk("cue ", body));
        return this;
    }

    public Wav_TestWriter ListInfo(params (string id, string value)[] tags)
    {
        var ms = new MemoryStream();
        ms.Write("INFO"u8);
        Span<byte> len = stackalloc byte[4];
        foreach (var (id, value) in tags)
        {
            var text = Encoding.UTF8.GetBytes(value + "\0");
            ms.Write(Encoding.ASCII.GetBytes(id));
            BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)text.Length);
            ms.Write(len);
            ms.Write(text);
            if (text.Length % 2 == 1) ms.WriteByte(0);
        }
        _chunks.Add(Chunk("LIST", ms.ToArray()));
        return this;
    }

    public Wav_TestWriter Inst(byte unshiftedNote, sbyte fineTune, sbyte gain, byte lowNote, byte highNote, byte lowVel, byte highVel)
    {
        _chunks.Add(Chunk("inst", [unshiftedNote, (byte)fineTune, (byte)gain, lowNote, highNote, lowVel, highVel]));
        return this;
    }

    /// <summary>Any chunk. <paramref name="pad"/> false omits the RIFF pad byte for odd lengths (the clap-808 case).</summary>
    public Wav_TestWriter Raw(string id, byte[] body, bool pad = true, uint? declaredLength = null)
    {
        _chunks.Add(Chunk(id, body, declaredLength, pad));
        return this;
    }

    // ── build ──

    /// <summary>Emits the file. <paramref name="riffSizeOverride"/>: 0, 0xFFFFFFFF, or an oversize value for the D10 tests.</summary>
    public byte[] Build(uint? riffSizeOverride = null, int truncateToBytes = -1)
    {
        var ms = new MemoryStream();
        ms.Write("RIFF"u8);
        long total = 4 + _chunks.Sum(c => (long)RiffChunkBytes(c).Length);
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, riffSizeOverride ?? (uint)total);
        ms.Write(size);
        ms.Write("WAVE"u8);
        foreach (var c in _chunks) ms.Write(RiffChunkBytes(c));
        var bytes = ms.ToArray();
        if (truncateToBytes >= 0 && truncateToBytes < bytes.Length) Array.Resize(ref bytes, truncateToBytes);
        return bytes;
    }

    /// <summary>
    /// EBU 3306 RF64 (or <c>BW64</c>): 32-bit size fields written as -1 and resolved through a leading <c>ds64</c> chunk.
    /// <paramref name="ds64DataSize"/> overrides the data size ds64 declares (to simulate a &gt; stream declaration → clamp + EndedEarly).
    /// <paramref name="tableChunkIds"/>: other chunks whose 32-bit size is also written as -1 and resolved through the ds64 table.
    /// </summary>
    public byte[] BuildRf64(ulong? ds64DataSize = null, ulong? ds64SampleCount = null, bool bw64 = false, bool dataSizeMinusOne = true, params string[] tableChunkIds)
    {
        var body = new MemoryStream();
        var table = new List<(string id, ulong size)>();
        foreach (var c in _chunks)
        {
            bool minusOne = c.Id == "data" ? dataSizeMinusOne : tableChunkIds.Contains(c.Id);
            if (minusOne && c.Id != "data") table.Add((c.Id, (ulong)c.Body.Length));
            body.Write(RiffChunkBytes(c, minusOne ? 0xFFFFFFFFu : null));
        }
        var data = _chunks.First(c => c.Id == "data");
        var ds64 = new byte[28 + table.Count * 12];
        ulong riffSize = (ulong)(4 + 8 + ds64.Length + body.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(ds64, riffSize);
        BinaryPrimitives.WriteUInt64LittleEndian(ds64.AsSpan(8), ds64DataSize ?? (ulong)data.Body.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(ds64.AsSpan(16), ds64SampleCount ?? (ulong)(data.Body.Length / Math.Max(1, BytesPerFrame)));
        BinaryPrimitives.WriteUInt32LittleEndian(ds64.AsSpan(24), (uint)table.Count);
        for (int i = 0; i < table.Count; i++)
        {
            Encoding.ASCII.GetBytes(table[i].id, ds64.AsSpan(28 + i * 12, 4));
            BinaryPrimitives.WriteUInt64LittleEndian(ds64.AsSpan(32 + i * 12), table[i].size);
        }
        var ms = new MemoryStream();
        ms.Write(bw64 ? "BW64"u8 : "RF64"u8);
        ms.Write([0xFF, 0xFF, 0xFF, 0xFF]);
        ms.Write("WAVE"u8);
        ms.Write(RiffChunkBytes(new ChunkSpec("ds64", ds64, null, true)));
        body.Position = 0; body.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Sony Wave64: GUID chunk ids, u64 sizes INCLUDING the 24-byte header, 8-byte alignment (ffmpeg <c>w64.c</c> GUIDs).</summary>
    public byte[] BuildW64(ulong? totalSizeOverride = null)
    {
        var body = new MemoryStream();
        foreach (var c in _chunks)
        {
            var guid = new byte[16];
            Encoding.ASCII.GetBytes(c.Id, guid);
            Wav_ChunkId.W64ChunkGuidTail.CopyTo(guid.AsSpan(4));
            body.Write(guid);
            Span<byte> size = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(size, (ulong)(24 + c.Body.Length));
            body.Write(size);
            body.Write(c.Body);
            int pad = (int)((8 - (24 + c.Body.Length) % 8) % 8);
            if (c.Pad) body.Write(new byte[pad]);
        }
        var ms = new MemoryStream();
        var riff = new byte[16];
        "riff"u8.CopyTo(riff);
        Wav_ChunkId.W64RiffGuidTail.CopyTo(riff.AsSpan(4));
        ms.Write(riff);
        Span<byte> total = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(total, totalSizeOverride ?? (ulong)(40 + body.Length));
        ms.Write(total);
        var wave = new byte[16];
        "wave"u8.CopyTo(wave);
        Wav_ChunkId.W64ChunkGuidTail.CopyTo(wave.AsSpan(4));
        ms.Write(wave);
        body.Position = 0; body.CopyTo(ms);
        return ms.ToArray();
    }

    private sealed record ChunkSpec(string Id, byte[] Body, uint? DeclaredLength, bool Pad);

    private static ChunkSpec Chunk(string id, byte[] body, uint? declaredLength = null, bool pad = true) => new(id, body, declaredLength, pad);

    private static byte[] RiffChunkBytes(ChunkSpec c, uint? declaredOverride = null)
    {
        bool needPad = c.Pad && c.Body.Length % 2 == 1;
        var chunk = new byte[8 + c.Body.Length + (needPad ? 1 : 0)];
        Encoding.ASCII.GetBytes(c.Id, chunk);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), declaredOverride ?? c.DeclaredLength ?? (uint)c.Body.Length);
        c.Body.CopyTo(chunk, 8);
        return chunk;
    }

    // ── signal generators (raw little-endian PCM in the writer's current format) ──

    /// <summary>Deterministic per-sample integer values covering the full range; sample k of channel c = f(k, c).</summary>
    public static long IntSampleValue(int frame, int channel, int bits)
    {
        long max = (1L << (bits - 1)) - 1;
        long min = -(1L << (bits - 1));
        // sweep: frame 0 = min, 1 = max, 2 = 0, 3 = -1, then a hash
        return (frame + channel) switch
        {
            0 => min,
            1 => max,
            2 => 0,
            3 => -1,
            _ => (long)((((ulong)(frame * 2654435761u) ^ (ulong)(channel * 40503u)) % (ulong)(max - min + 1)) + (ulong)min)
        };
    }

    /// <summary>Raw PCM bytes for <paramref name="frames"/> frames in the writer's current fmt.</summary>
    public byte[] Pcm(int frames)
    {
        int bps = ContainerBits / 8;
        var data = new byte[frames * BytesPerFrame];
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < Channels; c++)
            {
                var s = data.AsSpan((f * Channels + c) * bps, bps);
                if (IsFloat)
                {
                    double v = FloatSampleValue(f, c);
                    if (bps == 4) BinaryPrimitives.WriteSingleLittleEndian(s, (float)v);
                    else BinaryPrimitives.WriteDoubleLittleEndian(s, v);
                }
                else
                {
                    // valid bits are left-justified in the container (EXTENSIBLE rule)
                    long v = IntSampleValue(f, c, ValidBits) << (ContainerBits - ValidBits);
                    if (bps == 1) s[0] = (byte)(v + 128);
                    else for (int i = 0; i < bps; i++) s[i] = (byte)(v >> (8 * i));
                }
            }
        return data;
    }

    public static double FloatSampleValue(int frame, int channel) => (frame + channel) switch
    {
        0 => -1.0, 1 => 1.0, 2 => 0.0, 3 => 1.25,   // 1.25: not clipped (D4)
        _ => Math.Sin((frame * 0.01 + channel) * Math.PI) * 0.9
    };

    /// <summary>The float32 value the decoder must produce for frame/channel in the current fmt (D4: / 2^(containerBits-1)).</summary>
    public float ExpectedFloat(int frame, int channel)
    {
        if (IsFloat) return (float)FloatSampleValue(frame, channel);
        long v = IntSampleValue(frame, channel, ValidBits) << (ContainerBits - ValidBits);
        return ContainerBits switch
        {
            8 => v / 128f,
            16 => v / 32768f,
            24 => v / 8388608f,
            32 => (float)(v / 2147483648.0),
            _ => throw new InvalidOperationException()
        };
    }
}