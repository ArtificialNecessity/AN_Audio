using System.Runtime.InteropServices;
using AN.Audio.Formats.Internal;
using NLayer;

namespace AN.Audio.Formats.Mp3;

/// <summary>
/// MPEG-1/2/2.5 layer I–III decoder (spec 50 §MP3, D7). The framing is ours (<see cref="Mp3_FrameReader"/>: sync, ID3 skipping,
/// frame index, gapless trim, seeking); NLayer's <c>MpegFrameDecoder</c> decodes the frames we hand it (the package's only external
/// dependency; no NLayer type appears in any public signature). Adds the metadata tier (<see cref="Tags"/>, <see cref="StreamInfo"/>,
/// <see cref="Gapless"/>), pictures by callback (<see cref="Mp3_DecoderOptions.OnPicture"/>) and the <see cref="IAudioDecoder"/>
/// contract. <c>NativeFormat = Float32</c>: MPEG decodes to floats, so the native path IS the float path.
/// </summary>
public sealed class Mp3_Decoder : IAudioDecoder
{
    /// <summary>MDCT/filterbank decoder delay every MPEG layer III decoder adds; LAME's gapless rule (as ffmpeg applies it) folds it into the start skip.</summary>
    private const int DecoderDelaySamples = 529;
    private const int MaxSamplesPerFrame = 1152;

    private readonly PeekableStream _stream;
    private readonly Mp3_FrameReader _reader;
    private readonly MpegFrameDecoder _nlayer;
    private readonly bool _canSeek;
    private readonly int _channels;
    private readonly int _spf;                  // samples per frame per channel
    private readonly long _startSkip;           // raw samples dropped at the start (gapless)
    private readonly long _audioStartOffset;    // byte offset of the first AUDIO frame (after the Xing/Info frame when present)
    private readonly int _preroll;              // frames decoded and discarded before a seek target

    // decoded frame carried across ReadFrames calls of any size
    private readonly float[] _frame;
    private int _frameCount;                    // frames (per channel) in _frame
    private int _frameConsumed;
    private long _rawPos;                       // raw sample index (start skip NOT subtracted) of the next undelivered sample
    private bool _eof;
    private bool _disposed;

    // seek index (seekable streams): byte offset of audio frame i; grows lazily by header-only scanning
    private readonly List<long>? _frameOffsets;
    private long _indexScanPos;                 // byte offset just after the last indexed frame
    private bool _indexComplete;

    public AudioDecoder_StreamInfo Info { get; }
    public AudioFormat NativeFormat { get; }
    public long FramesRead { get; private set; }
    public bool EndedEarly { get; private set; }

    /// <summary>ID3v2 (or ID3v1) metadata; null when the stream carries neither.</summary>
    public Mp3_Id3Tags? Tags { get; }
    public Mp3_StreamInfo StreamInfo { get; }
    public Mp3_GaplessInfo Gapless { get; }
    /// <summary>Frames that decoded to no samples (bit reservoir starved = damaged stream) and were emitted as silence to keep the timeline exact.</summary>
    public int CorruptFrames { get; private set; }

    public Mp3_Decoder(Stream source, Mp3_DecoderOptions? options = null, bool leaveOpen = false)
        : this(new PeekableStream(source, leaveOpen), options) { }

    internal Mp3_Decoder(PeekableStream stream, Mp3_DecoderOptions? options = null)
    {
        _stream = stream;
        _canSeek = stream.CanSeek;
        options ??= Mp3_DecoderOptions.Default;
        try
        {
            // 1. ID3v2 from the look-ahead (the reader skips the tag itself); ID3v1 from the tail when seekable and no v2 tag
            int tagLength = Mp3_Id3v2.TagLength(_stream.Peek(10));
            if (tagLength > 0)
                Tags = Mp3_Id3v2.Parse(_stream.Peek(tagLength), options.OnPicture);
            else if (_canSeek && options.ReadId3v1 && _stream.Length >= 128)
            {
                var tail = new byte[128];
                _stream.Position = _stream.Length - 128;
                _stream.ReadFully(tail);
                _stream.Position = 0;
                Tags = Mp3_Id3v2.ParseV1(tail);
            }

            // 2. first frame header (+ optional Xing/Info/VBRI block)
            var (header, xing, firstOffset) = FindFirstFrame(tagLength);
            _channels = header.Channels;
            _spf = header.SamplesPerFrame;
            _audioStartOffset = firstOffset + (xing is not null ? header.FrameLength : 0);
            _preroll = Math.Max(4, 512 / Math.Max(1, header.FrameLength) + 2);   // the reservoir reaches back ≤ 511 bytes

            Gapless = new Mp3_GaplessInfo(xing?.EncoderDelay ?? 0, xing?.EncoderPadding ?? 0);
            StreamInfo = new Mp3_StreamInfo(
                header.Version switch { MpegFrameHeader_Version.Mpeg1 => Mp3_MpegVersion.Mpeg1, MpegFrameHeader_Version.Mpeg2 => Mp3_MpegVersion.Mpeg2, _ => Mp3_MpegVersion.Mpeg25 },
                (Mp3_Layer)(4 - (int)header.Layer),
                (Mp3_ChannelMode)(int)header.ChannelMode,
                header.SampleRate, header.BitRate, header.HasCrc, _spf, firstOffset,
                IsVbr: xing?.IsVbr ?? false, DeclaredFrameCount: xing?.FrameCount, DeclaredByteCount: xing?.ByteCount, Encoder: xing?.Encoder);

            // 3. gapless trim (LAME rule as ffmpeg applies it): skip delay + 529 at the start, padding - 529 at the end
            long endSkip = 0;
            if (Gapless.IsGapless)
            {
                _startSkip = Gapless.EncoderDelay + DecoderDelaySamples;
                endSkip = Math.Max(0, Gapless.EncoderPadding - DecoderDelaySamples);
            }

            // 4. framing: the reader starts at byte 0 (skips the ID3 tag itself); the Xing frame is skipped below
            _reader = new Mp3_FrameReader(_stream);
            _nlayer = new MpegFrameDecoder { StereoMode = StereoMode.Both };
            _frame = new float[MaxSamplesPerFrame * _channels];
            long? rawFrames = xing?.FrameCount is { } fc ? (long)fc * _spf : null;
            if (_canSeek)
            {
                _frameOffsets = new List<long>(rawFrames is { } rf ? (int)Math.Min(rf / _spf + 1, 1 << 20) : 1024);
                _indexScanPos = _audioStartOffset;
                if (rawFrames is null)
                {
                    EnsureIndexed(long.MaxValue);   // no Xing block: one header-only pass gives the frame count (and the whole index)
                    rawFrames = (long)_frameOffsets.Count * _spf;
                }
                _reader.Reposition(0);
            }
            long? totalFrames = rawFrames is { } r ? Math.Max(0, r - _startSkip - endSkip) : null;

            NativeFormat = new AudioFormat(header.SampleRate, _channels, SampleFormat.Float32);
            Info = new AudioDecoder_StreamInfo(AudioDecoder_Container.Mp3, header.Encoding, header.SampleRate, _channels,
                SourceBitDepth: 0, TotalFrames: totalFrames, CanSeek: _canSeek);
        }
        catch
        {
            _stream.Dispose();
            throw;
        }
    }

    /// <summary>Scans the look-ahead after the tag for a frame header confirmed by a second header (or a Xing block).</summary>
    private (MpegFrameHeader_Info header, MpegXingHeader_Info? xing, long offset) FindFirstFrame(int tagLength)
    {
        const int Window = 64 * 1024;
        var span = _stream.Peek(tagLength + Window);
        for (int i = tagLength; i + 4 <= span.Length; i++)
        {
            if (span[i] != 0xFF || (span[i + 1] & 0xE0) != 0xE0) continue;
            if (!MpegFrameHeader.TryParse(span.Slice(i), out var h))
            {
                // valid-looking sync with bitrate index 0 = free-format: legal MPEG we do not decode
                if (((span[i + 1] >> 3) & 3) != 1 && ((span[i + 1] >> 1) & 3) != 0 && (span[i + 2] >> 4) == 0 && ((span[i + 2] >> 2) & 3) != 3)
                    throw new AudioDecoder_UnsupportedException("free-format (bitrate index 0) MPEG audio", AudioDecoder_Container.Mp3);
                continue;
            }
            var frame = span.Slice(i, Math.Min(h.FrameLength, span.Length - i));
            bool hasXing = MpegXingHeader.TryParse(frame, h, out var x);
            int next = i + h.FrameLength;
            bool secondOk = next + 4 > span.Length   // cannot see it: trust the first
                            || MpegFrameHeader.TryParse(span.Slice(next), out var h2) && h2.SampleRate == h.SampleRate && h2.Layer == h.Layer;
            if (!hasXing && !secondOk) continue;
            return (h, hasXing ? x : null, i);
        }
        throw new AudioDecoder_FormatException("no MPEG audio frame found" + (tagLength > 0 ? " after the ID3 tag" : ""), AudioDecoder_Container.Mp3);
    }

    // ── framing ──

    /// <summary>Reads and decodes the next AUDIO frame into <see cref="_frame"/>; false at end of stream.</summary>
    private bool NextFrame()
    {
        if (_eof) return false;
        while (true)
        {
            if (!_reader.Next(readBody: true))
            {
                _eof = true;
                if (_reader.EndedShort) EndedEarly = true;
                return false;
            }
            if (_reader.Offset < _audioStartOffset) continue;   // the Xing/Info frame carries no audio
            var h = _reader.Header;
            if (h.SampleRate != NativeFormat.SampleRate || h.Channels != _channels)
                throw new AudioDecoder_UnsupportedException($"stream properties change mid-stream at byte {_reader.Offset} ({h.SampleRate} Hz, {h.Channels} ch)", AudioDecoder_Container.Mp3);
            if (_canSeek) IndexCurrentFrame();
            int samples = Decode(_frame);
            if (samples == 0)
            {
                // reservoir starved after a full pre-roll (or at stream start) = damaged frame: silence keeps the timeline exact
                CorruptFrames++;
                _frame.AsSpan(0, _spf * _channels).Clear();
                samples = _spf * _channels;
            }
            _frameCount = samples / _channels;
            _frameConsumed = 0;
            return true;
        }
    }

    private int Decode(Span<float> dst)
    {
        try { return _nlayer.DecodeFrame(_reader.Frame, dst); }
        catch (InvalidDataException) { return 0; }   // NLayer's own reader also treats a bad frame as "skip"; we keep the timeline
    }

    private void IndexCurrentFrame()
    {
        // Linear reads extend the index only at its frontier (offsets are strictly increasing)
        if (_frameOffsets!.Count == 0 || _reader.Offset > _frameOffsets[^1])
        {
            if (_reader.Offset >= _indexScanPos) { _frameOffsets.Add(_reader.Offset); _indexScanPos = _reader.Offset + _reader.Header.FrameLength; }
        }
    }

    /// <summary>Header-only scan until audio frame <paramref name="frameIndex"/> is indexed or the stream ends (then <see cref="_indexComplete"/>).</summary>
    private void EnsureIndexed(long frameIndex)
    {
        if (_indexComplete || frameIndex < _frameOffsets!.Count) return;
        _reader.Reposition(_indexScanPos);
        while (_frameOffsets.Count <= frameIndex && _reader.Next(readBody: false))
        {
            if (_reader.Offset < _audioStartOffset) continue;
            _frameOffsets.Add(_reader.Offset);
            _indexScanPos = _reader.Offset + _reader.Header.FrameLength;
        }
        if (_frameOffsets.Count <= frameIndex) _indexComplete = true;
    }

    // ── read path ──

    public int ReadFrames(Span<float> interleaved)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (interleaved.Length % _channels != 0)
            throw new ArgumentException($"interleaved length {interleaved.Length} is not a multiple of Channels {_channels}", nameof(interleaved));
        int wanted = interleaved.Length / _channels;
        if (Info.TotalFrames is { } total) wanted = (int)Math.Min(wanted, Math.Max(0, total - FramesRead));   // end trim
        int done = 0;
        while (done < wanted)
        {
            if (_frameConsumed == _frameCount && !NextFrame()) break;
            // start trim: drop raw samples before _startSkip
            if (_rawPos < _startSkip)
            {
                int drop = (int)Math.Min(_startSkip - _rawPos, _frameCount - _frameConsumed);
                _frameConsumed += drop; _rawPos += drop;
                continue;
            }
            int take = Math.Min(wanted - done, _frameCount - _frameConsumed);
            _frame.AsSpan(_frameConsumed * _channels, take * _channels).CopyTo(interleaved.Slice(done * _channels));
            _frameConsumed += take; _rawPos += take; done += take;
        }
        FramesRead += done;
        if (Info.TotalFrames is { } t)
        {
            if (FramesRead >= t) _eof = true;
            else if (_eof) EndedEarly = true;
        }
        return done;
    }

    public int ReadFramesNative(Span<byte> destination)
    {
        int bpf = NativeFormat.BytesPerFrame;
        if (destination.Length % bpf != 0)
            throw new ArgumentException($"destination length {destination.Length} is not a multiple of BytesPerFrame {bpf}", nameof(destination));
        return ReadFrames(MemoryMarshal.Cast<byte, float>(destination));
    }

    public int ReadFramesNative(AudioBufferView view)
    {
        if (view.Format != NativeFormat)
            throw new ArgumentException($"view format {view.Format} != NativeFormat {NativeFormat}", nameof(view));
        return ReadFramesNative(view.Bytes);
    }

    // ── seeking (D11) ──

    public void SeekToFrame(long frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_canSeek) throw new NotSupportedException("the underlying stream does not support seeking");
        if (frame < 0) throw new ArgumentOutOfRangeException(nameof(frame));
        if (Info.TotalFrames is { } total && frame > total) frame = total;
        _eof = false;
        EndedEarly = false;
        _frameCount = _frameConsumed = 0;

        long rawTarget = frame + _startSkip;
        long targetFrame = rawTarget / _spf;
        EnsureIndexed(targetFrame);
        if (targetFrame >= _frameOffsets!.Count)
        {
            // beyond the last frame: nothing more to deliver
            _eof = true;
            _rawPos = rawTarget;
            FramesRead = frame;
            return;
        }

        // Re-prime: decode (and discard) the frames before the target so the bit reservoir, overlap-add and synthesis history match a linear decode
        long first = Math.Max(0, targetFrame - _preroll);
        _reader.Reposition(_frameOffsets[(int)first]);
        _nlayer.Reset();
        for (long i = first; i < targetFrame; i++)
        {
            if (!_reader.Next(readBody: true)) { _eof = true; FramesRead = frame; _rawPos = rawTarget; return; }
            Decode(_frame);
        }
        _rawPos = targetFrame * _spf;
        if (!NextFrame()) { FramesRead = frame; _rawPos = rawTarget; return; }
        _frameConsumed = (int)(rawTarget - _rawPos);
        _rawPos = rawTarget;
        FramesRead = frame;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Dispose();
    }
}