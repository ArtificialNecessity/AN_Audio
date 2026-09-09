using AN.Audio.Formats.Internal;
using NLayer;

namespace AN.Audio.Formats.Mp3;

/// <summary>
/// Our <c>IMpegFrame</c>: one MPEG frame's bytes plus the sequential bit reader NLayer's layer decoders pull from.
/// Reused for every frame (no per-frame allocation, D13).
/// </summary>
internal sealed class Mp3_Frame : IMpegFrame
{
    private byte[] _buf = new byte[4096];
    private int _len;
    private int _readOffset, _bitsRead;
    private ulong _bitBucket;
    private MpegFrameHeader_Info _h;

    public MpegFrameHeader_Info Header => _h;

    public void Set(in MpegFrameHeader_Info header, ReadOnlySpan<byte> bytes)
    {
        _h = header;
        if (_buf.Length < bytes.Length) _buf = new byte[Math.Max(bytes.Length, _buf.Length * 2)];
        bytes.CopyTo(_buf);
        _len = bytes.Length;
        Reset();
    }

    // ── IMpegFrame ──
    public int SampleRate => _h.SampleRate;
    public int SampleRateIndex => _h.SampleRate switch { 44100 or 22050 or 11025 => 0, 48000 or 24000 or 12000 => 1, _ => 2 };
    public int FrameLength => _len;
    public int BitRate => _h.BitRate;
    public MpegVersion Version => _h.Version switch { MpegFrameHeader_Version.Mpeg1 => MpegVersion.Version1, MpegFrameHeader_Version.Mpeg2 => MpegVersion.Version2, _ => MpegVersion.Version25 };
    public MpegLayer Layer => (MpegLayer)(4 - (int)_h.Layer);                      // NLayer: LayerI=1..LayerIII=3; header code: Layer3=1..Layer1=3
    public MpegChannelMode ChannelMode => (MpegChannelMode)(int)_h.ChannelMode;    // same numbering (Stereo, JointStereo, DualChannel, Mono)
    public int ChannelModeExtension => (_buf[3] >> 4) & 0x3;
    public int SampleCount => _h.SamplesPerFrame;
    public int BitRateIndex => _buf[2] >> 4;
    public bool IsCopyrighted => (_buf[3] & 0x8) != 0;
    public bool HasCrc => _h.HasCrc;
    public bool IsCorrupted => false;   // CRC is not verified; NLayer's own reader mutes CRC failures, we let the bitstream speak

    public void Reset()
    {
        _readOffset = 4 + (_h.HasCrc ? 2 : 0);
        _bitBucket = 0;
        _bitsRead = 0;
    }

    public int ReadBits(int bitCount)
    {
        if (bitCount < 1 || bitCount > 32) throw new ArgumentOutOfRangeException(nameof(bitCount));
        while (_bitsRead < bitCount)
        {
            if (_readOffset >= _len) return -1;   // end of frame
            _bitBucket = (_bitBucket << 8) | _buf[_readOffset++];
            _bitsRead += 8;
        }
        int v = (int)((_bitBucket >> (_bitsRead - bitCount)) & ((1UL << bitCount) - 1));
        _bitsRead -= bitCount;
        return v;
    }
}

/// <summary>
/// Sequential MPEG frame reader over a <see cref="PeekableStream"/>: the framing half of the MP3 decoder (NLayer only decodes
/// the frames we hand it). Skips ID3v2 tags wherever they appear, resynchronises through garbage (a header is trusted only
/// when the next header agrees with it), reports a truncated final frame as <see cref="EndedShort"/>. Header-only mode
/// (<c>readBody = false</c>) walks frames without copying them — used to build the seek index and to count frames.
/// </summary>
internal sealed class Mp3_FrameReader
{
    private const int ResyncWindow = 64 * 1024;
    private readonly PeekableStream _stream;
    private bool _needConfirm = true;   // after (re)positioning, require the second header to agree before trusting a sync

    public Mp3_Frame Frame { get; } = new();
    public MpegFrameHeader_Info Header { get; private set; }
    /// <summary>Absolute byte offset of the current frame's header.</summary>
    public long Offset { get; private set; }
    /// <summary>Set when the stream ended inside a frame (D12: not an error, but the caller reports <c>EndedEarly</c>).</summary>
    public bool EndedShort { get; private set; }

    public Mp3_FrameReader(PeekableStream stream) => _stream = stream;

    /// <summary>Moves to <paramref name="byteOffset"/> (seekable streams only) and forgets sync.</summary>
    public void Reposition(long byteOffset)
    {
        _stream.Position = byteOffset;
        _needConfirm = true;
        EndedShort = false;
    }

    /// <summary>Advances to the next frame. False at end of stream (or truncation → <see cref="EndedShort"/>).</summary>
    public bool Next(bool readBody)
    {
        while (true)
        {
            var head = _stream.Peek(10);
            if (head.Length < 4) return false;

            int tag = Mp3_Id3v2.TagLength(head);
            if (tag > 0) { _stream.Skip(tag); _needConfirm = true; continue; }
            if (head.Length >= 3 && head[0] == (byte)'T' && head[1] == (byte)'A' && head[2] == (byte)'G' && _stream.Peek(129).Length == 128)
                return false;   // ID3v1 tag is the last 128 bytes: end of audio

            if (!MpegFrameHeader.TryParse(head, out var h) || _needConfirm && !SecondHeaderAgrees(h))
            {
                if (!Resync()) return false;
                continue;
            }
            _needConfirm = false;
            Header = h;
            Offset = _stream.Position;
            if (readBody)
            {
                var body = _stream.Peek(h.FrameLength);
                if (body.Length < h.FrameLength) { EndedShort = true; _stream.Skip(body.Length); return false; }
                Frame.Set(h, body);
                _stream.Skip(h.FrameLength);
            }
            else if (_stream.Skip(h.FrameLength) < h.FrameLength) { EndedShort = true; return false; }
            return true;
        }
    }

    private bool SecondHeaderAgrees(in MpegFrameHeader_Info h)
    {
        var span = _stream.Peek(h.FrameLength + 4);
        if (span.Length < h.FrameLength + 4) return true;   // cannot see it (end of stream): accept the frame
        return MpegFrameHeader.TryParse(span.Slice(h.FrameLength), out var h2) && h2.SampleRate == h.SampleRate && h2.Layer == h.Layer && h2.Version == h.Version
            || Mp3_Id3v2.TagLength(span.Slice(h.FrameLength)) > 0
            || span[h.FrameLength] == (byte)'T' && span[h.FrameLength + 1] == (byte)'A' && span[h.FrameLength + 2] == (byte)'G';
    }

    /// <summary>Skips forward to the next confirmed frame header; false when none is found before end of stream.</summary>
    private bool Resync()
    {
        _needConfirm = true;
        while (true)
        {
            var span = _stream.Peek(ResyncWindow);
            if (span.Length < 4) { _stream.Skip(span.Length); return false; }
            for (int i = 1; i + 4 <= span.Length; i++)
            {
                if (span[i] != 0xFF || (span[i + 1] & 0xE0) != 0xE0)
                {
                    if (span[i] == (byte)'I' && i + 10 <= span.Length && Mp3_Id3v2.TagLength(span.Slice(i)) > 0) { _stream.Skip(i); return true; }
                    continue;
                }
                if (!MpegFrameHeader.TryParse(span.Slice(i), out var h)) continue;
                int next = i + h.FrameLength;
                if (next + 4 <= span.Length && !(MpegFrameHeader.TryParse(span.Slice(next), out var h2) && h2.SampleRate == h.SampleRate && h2.Layer == h.Layer)) continue;
                _stream.Skip(i);
                return true;
            }
            // nothing in this window: drop it (keep 3 bytes so a header straddling the boundary is still found)
            _stream.Skip(Math.Max(1, span.Length - 3));
            if (span.Length < ResyncWindow) return false;
        }
    }
}