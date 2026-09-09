using System.Runtime.InteropServices;
using AN.Audio.Formats.Internal;

namespace AN.Audio.Formats.Flac;

/// <summary>
/// FLAC decoder (spec 50 §FLAC): wraps the subsumed reference bitstream decoder (<see cref="FlacDecoder"/>, D6) and adds
/// the metadata tier (<see cref="StreamInfo"/>, <see cref="Tags"/>, <see cref="SeekTable"/>), the <see cref="IAudioDecoder"/>
/// contract with arbitrary-size reads carried across frame boundaries, <c>NativeFormat = Int32</c> (D16: lossless integers
/// sign-extended in the LOW bits; <c>Info.SourceBitDepth</c> says how many are significant), and seeking (D11) via
/// SEEKTABLE or a frame-header binary search when the stream seeks.
/// </summary>
public sealed class Flac_Decoder : IAudioDecoder
{
    private readonly PeekableStream _stream;
    private readonly FlacDecoder _ref;
    private readonly bool _canSeek;
    private readonly Flac_DecoderOptions _options;

    // The last decoded frame, interleaved, and how much of it has been handed out
    private readonly int[] _frame;
    private int _frameCount;      // frames in _frame
    private int _frameConsumed;   // frames already delivered
    private bool _eof;
    private bool _disposed;

    public AudioDecoder_StreamInfo Info { get; }
    public AudioFormat NativeFormat { get; }
    public long FramesRead { get; private set; }
    public bool EndedEarly { get; private set; }

    public Flac_StreamInfo StreamInfo => _ref.StreamInfo;
    public Flac_Tags? Tags => _ref.Tags;
    public Flac_SeekTable? SeekTable => _ref.SeekTable;
    /// <summary>Metadata block types seen in the header, in order (PICTURE etc. are recorded, not parsed).</summary>
    public IReadOnlyList<Flac_MetadataBlockType> MetadataBlocksPresent => _ref.MetadataBlocksPresent;
    /// <summary>True when the MD5 in STREAMINFO is non-zero (a zero signature means the encoder did not compute one).</summary>
    public bool HasMd5 => StreamInfo.HasMd5;
    /// <summary>Set once the whole stream has been decoded with <see cref="Flac_DecoderOptions.VerifyMd5"/> on and the hash matched.</summary>
    public bool Md5Verified { get; private set; }

    public Flac_Decoder(Stream source, Flac_DecoderOptions? options = null, bool leaveOpen = false)
        : this(new PeekableStream(source, leaveOpen), options) { }

    internal Flac_Decoder(PeekableStream stream, Flac_DecoderOptions? options = null)
    {
        _stream = stream;
        _canSeek = stream.CanSeek;
        _options = options ?? Flac_DecoderOptions.Default;
        try
        {
            var refOptions = new FlacDecoder.Options
            {
                ConvertOutputToBytes = _options.VerifyMd5,
                ValidateOutputHash = _options.VerifyMd5,
                AllowNonstandardByteOutput = true,   // MD5 is defined over the FLAC byte layout for every depth
            };
            _ref = Translate(() => new FlacDecoder(stream, refOptions));
        }
        catch
        {
            _stream.Dispose();
            throw;
        }

        var si = _ref.StreamInfo;
        if (si.BitsPerSample > 32)
            throw new AudioDecoder_UnsupportedException($"{si.BitsPerSample}-bit FLAC exceeds Int32", AudioDecoder_Container.Flac);
        NativeFormat = new AudioFormat(si.SampleRate, si.Channels, SampleFormat.Int32);
        Info = new AudioDecoder_StreamInfo(
            AudioDecoder_Container.Flac, AudioDecoder_SourceEncoding.Flac, si.SampleRate, si.Channels,
            SourceBitDepth: si.BitsPerSample, TotalFrames: si.TotalSamplesOrNull, CanSeek: _canSeek);
        _frame = new int[si.MaxBlockSize * si.Channels];
    }

    // ── error translation: the reference decoder speaks InvalidDataException/NotSupportedException (D12) ──

    private static T Translate<T>(Func<T> action)
    {
        try { return action(); }
        catch (InvalidDataException e) { throw new AudioDecoder_FormatException(e.Message, AudioDecoder_Container.Flac, e); }
        catch (NotSupportedException e) when (e is not AudioDecoder_UnsupportedException) { throw new AudioDecoder_UnsupportedException(e.Message, AudioDecoder_Container.Flac); }
    }

    /// <summary>Decodes the next frame into <see cref="_frame"/>; false at EOF (or truncation → <see cref="EndedEarly"/>).</summary>
    private bool NextFrame()
    {
        if (_eof) return false;
        bool ok;
        try
        {
            ok = _ref.DecodeFrame();
        }
        catch (EndOfStreamException)
        {
            EndedEarly = true;   // D12: stream ended mid-frame
            _eof = true;
            return false;
        }
        catch (InvalidDataException e) { throw new AudioDecoder_FormatException(e.Message, AudioDecoder_Container.Flac, e); }
        catch (NotSupportedException e) { throw new AudioDecoder_UnsupportedException(e.Message, AudioDecoder_Container.Flac); }

        if (!ok)
        {
            _eof = true;
            if (_ref.EndedShort) EndedEarly = true;
            else if (_options.VerifyMd5 && HasMd5) Md5Verified = true;
            return false;
        }
        _ref.CopyFrameInt32(_frame);
        _frameCount = _ref.BufferSampleCount;
        _frameConsumed = 0;
        return true;
    }

    // ── read path ──

    public int ReadFramesNative(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int bpf = NativeFormat.BytesPerFrame;
        if (destination.Length % bpf != 0)
            throw new ArgumentException($"destination length {destination.Length} is not a multiple of BytesPerFrame {bpf}", nameof(destination));
        return ReadInto(MemoryMarshal.Cast<byte, int>(destination));
    }

    public int ReadFramesNative(AudioBufferView view)
    {
        if (view.Format != NativeFormat)
            throw new ArgumentException($"view format {view.Format} != NativeFormat {NativeFormat}", nameof(view));
        return ReadFramesNative(view.Bytes);
    }

    private int ReadInto(Span<int> dst)
    {
        int ch = NativeFormat.Channels;
        int wanted = dst.Length / ch;
        int done = 0;
        while (done < wanted)
        {
            if (_frameConsumed == _frameCount && !NextFrame()) break;
            int take = Math.Min(wanted - done, _frameCount - _frameConsumed);
            _frame.AsSpan(_frameConsumed * ch, take * ch).CopyTo(dst.Slice(done * ch));
            _frameConsumed += take;
            done += take;
        }
        FramesRead += done;
        return done;
    }

    public int ReadFrames(Span<float> interleaved)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int ch = NativeFormat.Channels;
        if (interleaved.Length % ch != 0)
            throw new ArgumentException($"interleaved length {interleaved.Length} is not a multiple of Channels {ch}", nameof(interleaved));
        int wanted = interleaved.Length / ch;
        int done = 0;
        float scale = 1f / (1L << (StreamInfo.BitsPerSample - 1));   // D4, uniform with PCM (D16)
        while (done < wanted)
        {
            if (_frameConsumed == _frameCount && !NextFrame()) break;
            int take = Math.Min(wanted - done, _frameCount - _frameConsumed);
            var src = _frame.AsSpan(_frameConsumed * ch, take * ch);
            var dst = interleaved.Slice(done * ch, take * ch);
            for (int i = 0; i < src.Length; i++) dst[i] = src[i] * scale;
            _frameConsumed += take;
            done += take;
        }
        FramesRead += done;
        return done;
    }

    // ── seeking (D11, Phase 2b) ──

    public void SeekToFrame(long frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_canSeek) throw new NotSupportedException("the underlying stream does not support seeking");
        if (frame < 0) throw new ArgumentOutOfRangeException(nameof(frame));
        if (Info.TotalFrames is { } total && frame > total) frame = total;

        // 1. pick a starting byte offset at or before the target
        long startOffset = _ref.AudioStartOffset;
        long startSample = 0;
        if (SeekTable?.FindBefore(frame) is { } point)
        {
            startOffset = _ref.AudioStartOffset + point.StreamOffset;
            startSample = point.SampleNumber;
        }
        else if (Info.TotalFrames is { } totalFrames && totalFrames > 0 && frame > 0)
        {
            // no usable SEEKTABLE: binary search on frame headers, then fall back to the linear path from the estimate
            var found = BinarySearchFrameStart(frame, totalFrames);
            if (found is { } f) { startOffset = f.offset; startSample = f.firstSample; }
        }

        // 2. reseat the reference decoder there and decode forward to the frame containing the target
        _stream.Position = startOffset;
        _ref.ReseatAfterSeek(startOffset, startSample);
        _eof = false;
        EndedEarly = false;
        _frameCount = _frameConsumed = 0;
        FramesRead = startSample;

        while (true)
        {
            if (!NextFrame()) { _frameCount = _frameConsumed = 0; FramesRead = frame; return; }   // past the end: nothing more to deliver
            long first = _ref.BufferFirstSample;
            long last = first + _frameCount;
            if (frame < last)
            {
                _frameConsumed = (int)Math.Max(0, frame - first);
                FramesRead = frame;
                return;
            }
        }
    }

    /// <summary>
    /// Finds a frame header whose first sample is ≤ <paramref name="target"/> and reasonably close to it, by binary search
    /// over byte offsets with a forward sync scan (0xFFF8/0xFFF9 + CRC-8 validated header). Returns null when the scan
    /// cannot find a trustworthy header (caller then decodes linearly from the first frame).
    /// </summary>
    private (long offset, long firstSample)? BinarySearchFrameStart(long target, long totalFrames)
    {
        long lo = _ref.AudioStartOffset, hi = _stream.Length;
        (long offset, long firstSample)? best = null;
        // initial guess proportional to position; then bisect on the sample numbers we actually find
        for (int iter = 0; iter < 40 && hi - lo > 4096; iter++)
        {
            long guess = iter == 0 ? lo + (long)((hi - lo) * ((double)target / totalFrames)) : lo + (hi - lo) / 2;
            var hdr = ScanForFrameHeader(guess, hi);
            if (hdr is null) { hi = guess; continue; }
            if (hdr.Value.firstSample <= target) { best = hdr; lo = hdr.Value.offset + 16; if (target - hdr.Value.firstSample < StreamInfo.MaxBlockSize * 4L) break; }
            else hi = hdr.Value.offset;
        }
        return best;
    }

    /// <summary>Scans forward from <paramref name="from"/> for a frame header that passes CRC-8 and matches STREAMINFO.</summary>
    private (long offset, long firstSample)? ScanForFrameHeader(long from, long limit)
    {
        const int Window = 64 * 1024;
        var buf = new byte[Window + 32];
        long pos = from;
        while (pos < limit)
        {
            _stream.Position = pos;
            int n = _stream.ReadFully(buf);
            if (n < 16) return null;
            for (int i = 0; i + 16 <= n; i++)
            {
                if (buf[i] != 0xFF || (buf[i + 1] & 0xFE) != 0xF8) continue;
                if (TryParseFrameHeader(buf.AsSpan(i, Math.Min(16, n - i)), out long firstSample))
                    return (pos + i, firstSample);
            }
            pos += n - 16;   // keep a header's worth of overlap
            if (n < buf.Length) return null;
        }
        return null;
    }

    /// <summary>Parses a frame header at the start of <paramref name="h"/>; true only if the CRC-8 matches and the header agrees with STREAMINFO.</summary>
    private bool TryParseFrameHeader(ReadOnlySpan<byte> h, out long firstSample)
    {
        firstSample = 0;
        if (h.Length < 6) return false;
        bool variable = (h[1] & 0x01) != 0;
        int blockSizeCode = h[2] >> 4, sampleRateCode = h[2] & 0xF;
        int channelLayout = h[3] >> 4, bitDepthCode = (h[3] >> 1) & 0x7;
        if ((h[3] & 1) != 0 || blockSizeCode == 0 || sampleRateCode == 15) return false;
        int channels = channelLayout <= 7 ? channelLayout + 1 : channelLayout <= 10 ? 2 : -1;
        if (channels != StreamInfo.Channels) return false;
        int bits = bitDepthCode switch { 0 => StreamInfo.BitsPerSample, 1 => 8, 2 => 12, 4 => 16, 5 => 20, 6 => 24, 7 => 32, _ => -1 };
        if (bits != StreamInfo.BitsPerSample) return false;

        int p = 4;
        int lead = System.Numerics.BitOperations.LeadingZeroCount((uint)~(h[p] << 24));   // leading ones of the first byte
        if (lead > 6) return false;
        long coded = lead == 0 ? h[p] : h[p] & (0x7F >> lead);
        p++;
        for (int i = 1; i < lead; i++, p++)
        {
            if (p >= h.Length || (h[p] & 0xC0) != 0x80) return false;
            coded = (coded << 6) | (uint)(h[p] & 0x3F);
        }
        if (blockSizeCode == 6) p += 1; else if (blockSizeCode == 7) p += 2;
        if (sampleRateCode == 12) p += 1; else if (sampleRateCode is 13 or 14) p += 2;
        if (p >= h.Length) return false;
        if (FlacDecoder.Crc8(h.Slice(0, p)) != h[p]) return false;
        firstSample = variable ? coded : coded * StreamInfo.MaxBlockSize;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ref.Dispose();
        _stream.Dispose();
    }
}