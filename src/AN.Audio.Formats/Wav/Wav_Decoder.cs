using System.Runtime.InteropServices;
using System.Buffers.Binary;
using AN.Audio.Formats.Internal;

namespace AN.Audio.Formats.Wav;

/// <summary>
/// RIFF/WAVE decoder (spec 50 §WAV): correct per the RIFF spec, tolerant where writers are known to be wrong.
/// <list type="bullet">
/// <item>RIFF size 0 / 0xFFFFFFFF / larger than the stream → length unknown, walk to EOF (D10).</item>
/// <item>Odd chunk with a missing final pad byte → accepted (the 99Sounds <c>clap-808.wav</c> case).</item>
/// <item><c>data</c> that overruns the stream → clamped; decode what exists and set <see cref="EndedEarly"/> (D12).</item>
/// <item>Unknown chunks skipped but recorded in <see cref="Chunks"/>; <c>smpl</c>/<c>cue </c>/<c>LIST/INFO</c>/<c>inst</c> parsed.</item>
/// <item><c>fmt </c> after <c>data</c> → handled when the stream seeks, else <see cref="AudioDecoder_FormatException"/>.</item>
/// <item>Chunks after <c>data</c> are read eagerly when seekable, lazily (after the audio) on forward-only streams.</item>
/// </list>
/// PCM is read in 4096-frame blocks straight from the stream (D16: <see cref="ReadFramesNative(Span{byte})"/> is a byte copy).
/// </summary>
public sealed class Wav_Decoder : IAudioDecoder
{
    /// <summary>Non-<c>data</c> chunk bodies up to this size are retained in <see cref="Wav_ChunkInfo.RawBytes"/>.</summary>
    public static readonly int MaxRetainedChunkBytes = 1 << 20;

    internal const int BlockFrames = 4096;

    private readonly PeekableStream _stream;
    private readonly BitReader _reader;
    private readonly List<Wav_ChunkInfo> _chunks = new();
    private readonly bool _canSeek;

    private long _riffEnd;                 // absolute offset one past the RIFF body, or long.MaxValue when unknown
    private bool _riffLengthKnown;
    private long _dataStart;               // absolute offset of the first audio byte
    private long? _dataLength;             // clamped data bytes; null = unknown (read to EOF)
    private long _dataDeclaredLength;      // body length as declared (ds64-resolved for RF64; header-less for Wave64)
    private bool _dataLengthUnknown;       // the size field was a placeholder (0xFFFFFFFF, or 0 with unknown RIFF size)
    private long _dataBytesRead;
    private bool _dataSeen;
    private bool _trailingChunksRead;
    private byte[]? _nativeBlock;          // ReadFrames(float) on non-float sources
    private byte[]? _wireBlock;            // G.711: companded bytes read from the stream before expansion
    private short[]? _g711;                // A-law / µ-law expansion table when the wire format is companded (Phase 4a)
    private int _sourceBytesPerFrame;      // bytes per frame ON THE WIRE (== NativeFormat.BytesPerFrame except for G.711: 1 byte/sample → Int16)
    private bool _disposed;

    public Wav_FormatChunk Format { get; private set; } = null!;
    public AudioDecoder_StreamInfo Info { get; private set; }
    public AudioFormat NativeFormat { get; private set; }
    public long FramesRead { get; private set; }
    public bool EndedEarly { get; private set; }

    /// <summary>Every chunk seen so far (see <see cref="MetadataComplete"/>).</summary>
    public IReadOnlyList<Wav_ChunkInfo> Chunks => _chunks;
    /// <summary>False on a forward-only stream until the audio has been fully read (chunks after <c>data</c> are still ahead).</summary>
    public bool MetadataComplete => _trailingChunksRead;
    public Wav_SamplerChunk? Sampler { get; private set; }
    public Wav_CuePoint[]? CuePoints { get; private set; }
    public Wav_InfoTags? InfoTags { get; private set; }
    public Wav_InstrumentChunk? Instrument { get; private set; }
    /// <summary>True when the RIFF header declared a usable size (false for 0 / 0xFFFFFFFF / oversize).</summary>
    public bool RiffLengthKnown => _riffLengthKnown;
    /// <summary>RIFF, RF64/BW64 or Wave64 (Phase 4c).</summary>
    public Wav_ContainerLayout Layout { get; private set; }
    /// <summary>The RF64 <c>ds64</c> chunk when present.</summary>
    public Wav_DataSize64Chunk? DataSize64 { get; private set; }

    /// <summary>Opens a WAV stream. The stream may be forward-only.</summary>
    public Wav_Decoder(Stream source, bool leaveOpen = false)
        : this(new PeekableStream(source, leaveOpen)) { }

    internal Wav_Decoder(PeekableStream stream)
    {
        _stream = stream;
        _reader = new BitReader(stream);
        _canSeek = stream.CanSeek;
        try
        {
            ReadHeaderAndWalk();
        }
        catch
        {
            _stream.Dispose();
            throw;
        }
    }

    // ── header + chunk walk ──

    private static AudioDecoder_FormatException Malformed(string message, Exception? inner = null)
        => new(message, AudioDecoder_Container.Wav, inner);

    private void ReadHeaderAndWalk()
    {
        long start = _stream.Position;
        var head = _stream.Peek(40);
        if (head.Length < 12) throw Malformed("stream shorter than a RIFF header");
        var riff = Wav_ChunkId.FromBytes(head);
        ulong riffSize;
        bool riffSizeIsPlaceholder;   // 0 / 0xFFFFFFFF in the 32-bit field
        if (riff == Wav_ChunkId.W64Riff)
        {
            // Sony Wave64: riff GUID (16) + u64 total size incl. this header + wave GUID (16)
            if (head.Length < 40 || !head.Slice(4, 12).SequenceEqual(Wav_ChunkId.W64RiffGuidTail))
                throw Malformed("not a Wave64 stream (riff GUID tail mismatch)");
            if (Wav_ChunkId.FromBytes(head.Slice(24)) != Wav_ChunkId.W64Wave || !head.Slice(28, 12).SequenceEqual(Wav_ChunkId.W64ChunkGuidTail))
                throw Malformed("not a Wave64 stream (wave GUID missing)");
            Layout = Wav_ContainerLayout.Wave64;
            ulong total = BinaryPrimitives.ReadUInt64LittleEndian(head.Slice(16));
            riffSize = total >= 40 ? total - 24 : 0;   // express as "body after the 24-byte riff header" like RIFF's size field
            riffSizeIsPlaceholder = total == 0 || total == ulong.MaxValue;
            _stream.Skip(40);
        }
        else
        {
            var wave = Wav_ChunkId.FromBytes(head.Slice(8));
            bool rf64 = riff == Wav_ChunkId.Rf64 || riff == Wav_ChunkId.Bw64;
            if ((riff != Wav_ChunkId.Riff && !rf64) || wave != Wav_ChunkId.Wave)
                throw Malformed($"not a RIFF/WAVE stream (got '{riff}' … '{wave}')");
            Layout = rf64 ? Wav_ContainerLayout.Rf64 : Wav_ContainerLayout.Riff;
            uint size32 = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(4));
            riffSize = size32;
            riffSizeIsPlaceholder = size32 == 0 || size32 == 0xFFFFFFFF;
            _stream.Skip(12);
            if (rf64)
            {
                // EBU 3306: ds64 MUST be the first chunk; it carries the 64-bit sizes the 32-bit fields cannot
                ReadDs64();
                if (DataSize64 is { } ds && ds.RiffSize > 0) { riffSize = ds.RiffSize; riffSizeIsPlaceholder = false; }
            }
        }
        long headerBytes = Layout == Wav_ContainerLayout.Wave64 ? 24 : 8;
        _riffLengthKnown = !riffSizeIsPlaceholder && riffSize < long.MaxValue - 64;
        if (_riffLengthKnown && _canSeek && start + headerBytes + (long)riffSize > _stream.Length)
            _riffLengthKnown = false;   // oversize: treat as unknown (§WAV)
        _riffEnd = _riffLengthKnown ? start + headerBytes + (long)riffSize : long.MaxValue;

        // Walk until data (forward-only) or to the end (seekable)
        bool fmtSeen = false;
        while (true)
        {
            var step = WalkOneChunk(ref fmtSeen);
            if (step == WalkStep.End) break;
            if (step == WalkStep.Data)
            {
                if (!_canSeek)
                {
                    if (!fmtSeen) throw Malformed("fmt chunk after data on a non-seekable stream");
                    break;   // trailing chunks are read lazily after the audio
                }
                // seekable: skip the data body (+pad) and keep walking for trailing metadata
                long skipTo = _dataStart + (_dataLength ?? (_stream.Length - _dataStart));
                skipTo = Math.Min(_stream.Length, skipTo + PadBytesFor(_dataDeclaredLength));
                if (skipTo >= _stream.Length) { _trailingChunksRead = true; break; }
                _stream.Position = skipTo;
            }
        }
        if (_canSeek) _trailingChunksRead = true;

        if (!fmtSeen) throw Malformed("no fmt chunk");
        if (!_dataSeen) throw Malformed("no data chunk");
        if (_canSeek) _stream.Position = _dataStart;

        ParseMetadata();
        ResolveFormat();
    }

    /// <summary>Alignment padding after a chunk body: RIFF/RF64 pad odd lengths to 2, Wave64 pads header+body to 8.</summary>
    private int PadBytesFor(long bodyLength) => Layout == Wav_ContainerLayout.Wave64
        ? (int)((8 - (24 + bodyLength) % 8) % 8)
        : (int)(bodyLength & 1);

    private int ChunkHeaderBytes => Layout == Wav_ContainerLayout.Wave64 ? 24 : 8;

    /// <summary>Reads the RF64 <c>ds64</c> chunk (must directly follow <c>WAVE</c>); tolerates its absence (then sizes come from the 32-bit fields).</summary>
    private void ReadDs64()
    {
        var hdr = _stream.Peek(8);
        if (hdr.Length < 8 || Wav_ChunkId.FromBytes(hdr) != Wav_ChunkId.Ds64) return;
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(4));
        if (size < 28) throw Malformed($"ds64 chunk is {size} bytes, need at least 28");
        long headerOffset = _stream.Position;
        _stream.Skip(8);
        var body = new byte[size];
        if (_stream.ReadFully(body) < size) throw Malformed("stream ends inside the ds64 chunk");
        _stream.Skip(size & 1);
        ulong riffSize = BinaryPrimitives.ReadUInt64LittleEndian(body);
        ulong dataSize = BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(8));
        ulong sampleCount = BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(16));
        uint tableLength = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(24));
        var table = new List<Wav_DataSize64Chunk.Entry>();
        for (int i = 0; i < tableLength && 28 + (i + 1) * 12 <= body.Length; i++)
        {
            var e = body.AsSpan(28 + i * 12);
            table.Add(new(Wav_ChunkId.FromBytes(e), BinaryPrimitives.ReadUInt64LittleEndian(e.Slice(4))));
        }
        DataSize64 = new Wav_DataSize64Chunk(riffSize, dataSize, sampleCount, table);
        _chunks.Add(new Wav_ChunkInfo(Wav_ChunkId.Ds64, headerOffset, size, size, true, body));
    }

    /// <summary>Reads one chunk header in the current layout; false at EOF / short header. <paramref name="declared"/> is the BODY length.</summary>
    private bool TryReadChunkHeader(out Wav_ChunkId id, out long declared, out bool sizeIsPlaceholder)
    {
        id = default; declared = 0; sizeIsPlaceholder = false;
        int hdrLen = ChunkHeaderBytes;
        var hdr = _stream.Peek(hdrLen);
        if (hdr.Length < hdrLen) return false;
        id = Wav_ChunkId.FromBytes(hdr);
        if (Layout == Wav_ContainerLayout.Wave64)
        {
            // GUID (fourcc + 12-byte tail) + u64 size INCLUDING the 24-byte header
            ulong total = BinaryPrimitives.ReadUInt64LittleEndian(hdr.Slice(16));
            if (total < 24) return false;
            declared = (long)Math.Min(total - 24, long.MaxValue / 2);
        }
        else
        {
            uint size32 = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(4));
            declared = size32;
            sizeIsPlaceholder = size32 == 0xFFFFFFFF;
            if (sizeIsPlaceholder && DataSize64?.SizeFor(id) is { } size64)
            {
                declared = (long)Math.Min(size64, long.MaxValue / 2);
                sizeIsPlaceholder = false;   // resolved through ds64
            }
        }
        _stream.Skip(hdrLen);
        return true;
    }

    private enum WalkStep { Continue, Data, End }

    private WalkStep WalkOneChunk(ref bool fmtSeen)
    {
        long headerOffset = _stream.Position;
        if (headerOffset + ChunkHeaderBytes > _riffEnd) return WalkStep.End;
        if (!TryReadChunkHeader(out var id, out long declared, out bool sizeIsPlaceholder)) return WalkStep.End;   // trailing junk shorter than a header, or clean EOF
        long bodyStart = _stream.Position;

        // Clamp against what can exist
        long clamped = declared;
        if (_riffLengthKnown && bodyStart + clamped > _riffEnd) clamped = Math.Max(0, _riffEnd - bodyStart);
        if (_canSeek && bodyStart + clamped > _stream.Length) clamped = Math.Max(0, _stream.Length - bodyStart);

        if (id == Wav_ChunkId.Data)
        {
            if (_dataSeen) { /* second data chunk: ignore, skip it */ }
            else
            {
                _dataSeen = true;
                _dataStart = bodyStart;
                _dataDeclaredLength = declared;
                bool unknown = sizeIsPlaceholder || (declared == 0 && !_riffLengthKnown);
                _dataLengthUnknown = unknown;
                _dataLength = unknown ? null : clamped;
                if (!unknown && !_canSeek && !_riffLengthKnown) _dataLength = declared;   // clamp happens at read time via EndedEarly
                _chunks.Add(new Wav_ChunkInfo(id, headerOffset, declared, _dataLength ?? -1, true, null));
                return WalkStep.Data;
            }
        }

        byte[]? raw = null;
        long skipped;
        if (clamped <= MaxRetainedChunkBytes)
        {
            raw = new byte[clamped];
            int got = _stream.ReadFully(raw);
            if (got < raw.Length) { Array.Resize(ref raw, got); clamped = got; }
            skipped = got;
        }
        else
        {
            skipped = _stream.Skip(clamped);
            clamped = skipped;
        }

        bool padPresent = true;
        int pad = PadBytesFor(declared);
        if (pad > 0 && clamped == declared)
        {
            // alignment padding — accept its absence at EOF (clap-808)
            padPresent = _stream.Skip(pad) == pad;
        }

        if (id == Wav_ChunkId.Fmt)
        {
            if (raw is null) throw Malformed("fmt chunk absurdly large");
            Format = Wav_FormatChunk.Parse(raw);
            fmtSeen = true;
        }
        _chunks.Add(new Wav_ChunkInfo(id, headerOffset, declared, clamped, padPresent, raw));
        return skipped < declared && clamped < declared ? WalkStep.End : WalkStep.Continue;
    }

    private void ParseMetadata()
    {
        foreach (var c in _chunks)
        {
            if (c.RawBytes is null) continue;
            if (c.Id == Wav_ChunkId.Smpl) Sampler ??= Wav_SamplerChunk.TryParse(c.RawBytes);
            else if (c.Id == Wav_ChunkId.Cue) CuePoints ??= Wav_CuePoint.TryParse(c.RawBytes);
            else if (c.Id == Wav_ChunkId.Inst) Instrument ??= Wav_InstrumentChunk.TryParse(c.RawBytes);
            else if (c.Id == Wav_ChunkId.List && c.RawBytes.Length >= 4 && Wav_ChunkId.FromBytes(c.RawBytes) == Wav_ChunkId.Info)
                InfoTags ??= Wav_InfoTags.Parse(c.RawBytes.AsSpan(4));
        }
    }

    private void ResolveFormat()
    {
        var f = Format;
        var tag = f.EffectiveTag;
        int container = f.ContainerBytes;
        SampleFormat sf;
        AudioDecoder_SourceEncoding enc;
        int sourceBitDepth = f.ValidBitsPerSample;
        switch (tag)
        {
            case Wav_FormatTag.Pcm:
                enc = AudioDecoder_SourceEncoding.PcmInt;
                sf = container switch
                {
                    1 => SampleFormat.UInt8,
                    2 => SampleFormat.Int16,
                    3 => SampleFormat.Int24,
                    4 => SampleFormat.Int32,
                    _ => throw new AudioDecoder_UnsupportedException($"PCM with {f.BitsPerSample} bits in a {container}-byte container", AudioDecoder_Container.Wav),
                };
                break;
            case Wav_FormatTag.IeeeFloat:
                enc = AudioDecoder_SourceEncoding.PcmFloat;
                sf = container switch
                {
                    4 => SampleFormat.Float32,
                    8 => SampleFormat.Float64,
                    _ => throw new AudioDecoder_UnsupportedException($"IEEE float with {f.BitsPerSample} bits", AudioDecoder_Container.Wav),
                };
                break;
            case Wav_FormatTag.Alaw:
            case Wav_FormatTag.Mulaw:
                // G.711 (Phase 4a): 8-bit companded on the wire, expanded to Int16 (the only lossless linear representation)
                if (container != 1)
                    throw new AudioDecoder_UnsupportedException($"{tag} with {f.BitsPerSample} bits per sample (must be 8)", AudioDecoder_Container.Wav);
                enc = tag == Wav_FormatTag.Alaw ? AudioDecoder_SourceEncoding.Alaw : AudioDecoder_SourceEncoding.Mulaw;
                _g711 = tag == Wav_FormatTag.Alaw ? G711.ALawToInt16 : G711.MuLawToInt16;
                sf = SampleFormat.Int16;
                sourceBitDepth = 8;
                break;
            case Wav_FormatTag.Adpcm:
            case Wav_FormatTag.ImaAdpcm:
            case Wav_FormatTag.Mpeg:
            case Wav_FormatTag.MpegLayer3:
                throw new AudioDecoder_UnsupportedException($"{tag} (WAVE_FORMAT 0x{(ushort)tag:X4}) is not supported", AudioDecoder_Container.Wav);
            default:
                throw new AudioDecoder_UnsupportedException(f.FormatTag == Wav_FormatTag.Extensible
                    ? $"WAVE_FORMAT_EXTENSIBLE with SubFormat {f.SubFormat} is not supported"
                    : $"WAVE_FORMAT 0x{(ushort)f.FormatTag:X4} is not supported", AudioDecoder_Container.Wav);
        }

        NativeFormat = new AudioFormat(f.SampleRate, f.Channels, sf);
        _sourceBytesPerFrame = _g711 is null ? NativeFormat.BytesPerFrame : f.Channels;
        long? totalFrames = _dataLength is { } len ? len / _sourceBytesPerFrame : null;
        Info = new AudioDecoder_StreamInfo(
            AudioDecoder_Container.Wav, enc, f.SampleRate, f.Channels,
            SourceBitDepth: sourceBitDepth, TotalFrames: totalFrames, CanSeek: _canSeek);
    }

    // ── read path ──

    public int ReadFramesNative(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int bpf = NativeFormat.BytesPerFrame;
        if (destination.Length % bpf != 0)
            throw new ArgumentException($"destination length {destination.Length} is not a multiple of BytesPerFrame {bpf}", nameof(destination));
        if (_g711 is null) return ReadSourceFrames(destination);

        // G.711: one companded byte per sample on the wire, Int16 in the caller's buffer (NativeFormat = Int16)
        int channels = NativeFormat.Channels;
        _wireBlock ??= new byte[BlockFrames * _sourceBytesPerFrame];   // NOT _nativeBlock: ReadFrames(float) may be reading into that one
        var dst = MemoryMarshal.Cast<byte, short>(destination);
        int framesWanted = destination.Length / bpf, total = 0;
        while (total < framesWanted)
        {
            int block = Math.Min(BlockFrames, framesWanted - total);
            int got = ReadSourceFrames(_wireBlock.AsSpan(0, block * _sourceBytesPerFrame));
            if (got == 0) break;
            G711.Expand(_wireBlock.AsSpan(0, got * channels), dst.Slice(total * channels, got * channels), _g711);
            total += got;
            if (got < block) break;
        }
        return total;
    }

    /// <summary>Reads whole frames of the WIRE layout (<see cref=\"_sourceBytesPerFrame\"/> bytes each) straight from the stream; returns frames.</summary>
    private int ReadSourceFrames(Span<byte> destination)
    {
        int bpf = _sourceBytesPerFrame;
        long remaining = _dataLength is { } len ? len - _dataBytesRead : long.MaxValue;
        int want = (int)Math.Min(destination.Length, remaining);
        want -= want % bpf;
        if (want == 0) { OnAudioEnd(); return 0; }

        int got = _stream.ReadFully(destination.Slice(0, want));
        if (got < want)
        {
            // Stream ended before the declared data length (or unknown length: clean EOF)
            if (_dataLength is not null) EndedEarly = true;
            else if (got % bpf != 0) EndedEarly = true;   // partial trailing frame
            got -= got % bpf;
        }
        _dataBytesRead += got;
        int frames = got / bpf;
        FramesRead += frames;
        if (frames == 0) OnAudioEnd();
        return frames;
    }

    public int ReadFramesNative(AudioBufferView view)
    {
        if (view.Format != NativeFormat)
            throw new ArgumentException($"view format {view.Format} != NativeFormat {NativeFormat}", nameof(view));
        return ReadFramesNative(view.Bytes);
    }

    public int ReadFrames(Span<float> interleaved)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int channels = NativeFormat.Channels;
        if (interleaved.Length % channels != 0)
            throw new ArgumentException($"interleaved length {interleaved.Length} is not a multiple of Channels {channels}", nameof(interleaved));

        if (NativeFormat.Format == SampleFormat.Float32)
            return ReadFramesNative(MemoryMarshal.AsBytes(interleaved));   // zero-copy: the native layout IS float32

        int bpf = NativeFormat.BytesPerFrame;
        _nativeBlock ??= new byte[BlockFrames * bpf];
        int totalFrames = 0;
        int framesWanted = interleaved.Length / channels;
        while (totalFrames < framesWanted)
        {
            int block = Math.Min(BlockFrames, framesWanted - totalFrames);
            int got = ReadFramesNative(_nativeBlock.AsSpan(0, block * bpf));
            if (got == 0) break;
            AudioSampleConvert.ToFloat32(_nativeBlock.AsSpan(0, got * bpf), NativeFormat.Format,
                interleaved.Slice(totalFrames * channels, got * channels));
            totalFrames += got;
            if (got < block) break;   // EOF / EndedEarly
        }
        return totalFrames;
    }

    /// <summary>On forward-only streams, chunks after <c>data</c> become readable once the audio is exhausted.</summary>
    private void OnAudioEnd()
    {
        // A data chunk clamped at open (declared longer than the stream) or ending on a partial frame is a truncation (D12)
        if (_dataLength is { } known && !_dataLengthUnknown && (known < _dataDeclaredLength || known % _sourceBytesPerFrame != 0))
            EndedEarly = true;

        if (_trailingChunksRead) return;
        _trailingChunksRead = true;
        if (_dataLength is null) return;   // data ran to EOF: nothing can follow
        if (EndedEarly) return;
        // consume the data alignment padding, then walk
        _stream.Skip(PadBytesFor(_dataDeclaredLength));
        bool fmtSeen = true;
        try
        {
            while (WalkOneChunk(ref fmtSeen) == WalkStep.Continue) { }
        }
        catch (AudioDecoder_FormatException) { /* trailing junk must never break a finished decode */ }
        ParseMetadata();
    }

    public void SeekToFrame(long frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_canSeek) throw new NotSupportedException("the underlying stream does not support seeking");
        if (frame < 0) throw new ArgumentOutOfRangeException(nameof(frame));
        if (Info.TotalFrames is { } total && frame > total) frame = total;
        long bytes = frame * _sourceBytesPerFrame;
        _stream.Position = _dataStart + bytes;
        _dataBytesRead = bytes;
        FramesRead = frame;
        EndedEarly = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Dispose();
    }
}