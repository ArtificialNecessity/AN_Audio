// Flac_ReferenceDecoder.cs — the FLAC bitstream decoder for AN.Audio.Formats (spec _SPECS/50_Audio_Formats.md, D6).
//
// ORIGIN: subsumed from jdpurcell/SimpleFlac `FlacDecoder.cs`, upstream commit dc149aa4f685eb854ba0b99abe701b4a8bf8b539
// (2025-08-16, https://github.com/jdpurcell/SimpleFlac), itself a C# port of Project Nayuki's "Simple FLAC decoder"
// (https://www.nayuki.io/page/simple-flac-implementation). Upstream is a finished single-file project (5 commits, one week);
// we own this copy: bug fixes and features (metadata blocks, seeking, span output) are made HERE and are marked `// AN:`.
// The original MIT licence and copyright notices below apply to the portions derived from that work and MUST be kept.
//
// Modifications (C) David Jeske 2026, Apache 2.0 (see repository LICENSE.txt) — MIT-derived portions remain MIT.
//
// ---- Original notice (verbatim) ----
// License: MIT
//
// Copyright (c) J.D. Purcell (C# port and enhancements)
// Copyright (c) Project Nayuki (Simple FLAC decoder in Java)
// https://www.nayuki.io/page/simple-flac-implementation
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of
// this software and associated documentation files (the "Software"), to deal in
// the Software without restriction, including without limitation the rights to
// use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
// of the Software, and to permit persons to whom the Software is furnished to do
// so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

// AN: summary of adaptations to the upstream body (each site is also marked inline):
// AN:  - tabs → 4 spaces (repo style); namespace AN.Audio.Formats.Flac; class internal sealed.
// AN:  - BitReader does NOT own/dispose the stream (Flac_Decoder owns the PeekableStream) and reads through a 64 KiB
// AN:    byte buffer instead of Stream.ReadByte per byte; it tracks the absolute byte position and can be re-seated
// AN:    after the owner seeks the stream (ReseatAfterSeek).
// AN:  - Metadata loop parses STREAMINFO (→ Flac_StreamInfo), VORBIS_COMMENT (→ Flac_Tags), SEEKTABLE (→ Flac_SeekTable) and
// AN:    records PADDING/APPLICATION/CUESHEET/PICTURE as present; AudioStartOffset = first byte after the last block.
// AN:  - Frame header: the coded (sample|frame) number is decoded and exposed as BufferFirstSample (needed for seeking);
// AN:    frame-header CRC-8 is verified (FrameHeaderCrcMismatch → InvalidDataException) so the seek scanner can trust it.
// AN:  - Options defaults: ConvertOutputToBytes=false, ValidateOutputHash=false. MD5 hashing is synchronous (no Task).
// AN:  - End of stream with fewer samples than STREAMINFO declared sets EndedShort instead of throwing (D12).
// AN:  - Interleaved span output: CopyFrameInt32(Span<int>) and CopyFrameFloat(Span<float>) — no intermediate byte[].

using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;

#nullable enable

namespace AN.Audio.Formats.Flac;   // AN: was SimpleFlac

internal sealed class FlacDecoder : IDisposable {   // AN: was public; wrapped by Flac_Decoder
    private readonly Options _options;
    private readonly BitReader _reader;
    private readonly IncrementalHash? _outputHasher;
    private byte[] _expectedOutputHash = new byte[16];

    public long? StreamSampleCount { get; private set; }
    public int SampleRate { get; private set; }
    public int ChannelCount { get; private set; }
    public int BitsPerSample { get; private set; }
    public int BytesPerSample { get; private set; }
    public int MaxSamplesPerFrame { get; private set; }

    public long[][] BufferSamples { get; private set; } = [[]];
    public byte[] BufferBytes { get; private set; } = [];
    public int BufferSampleCount { get; private set; }
    public int BufferByteCount { get; private set; }
    public long RunningSampleCount { get; private set; }

    public int BlockAlign => BytesPerSample * ChannelCount;

    // AN: metadata surfaced for Flac_Decoder
    public Flac_StreamInfo StreamInfo { get; private set; } = null!;
    public Flac_Tags? Tags { get; private set; }
    public Flac_SeekTable? SeekTable { get; private set; }
    public List<Flac_MetadataBlockType> MetadataBlocksPresent { get; } = new();
    /// <summary>AN: absolute stream offset of the first frame (SEEKTABLE offsets are relative to this).</summary>
    public long AudioStartOffset { get; private set; }
    /// <summary>AN: sample number of the first sample in BufferSamples (from the frame header).</summary>
    public long BufferFirstSample { get; private set; }
    /// <summary>AN: absolute stream offset where the last decoded frame began.</summary>
    public long BufferFrameOffset { get; private set; }
    /// <summary>AN: D12 — the stream ended before STREAMINFO's declared total (set by DecodeFrame returning false).</summary>
    public bool EndedShort { get; private set; }
    /// <summary>AN: bytes consumed so far (absolute), at byte granularity between frames.</summary>
    public long BytePosition => _reader.BytePosition;

    public FlacDecoder(Stream input, Options? options = null) {
        _options = options ?? new Options();
        _reader = new BitReader(input);   // AN: reader no longer owns the stream
        ValidateOptions();
        ReadMetadata();
        _outputHasher = _options.ValidateOutputHash ? IncrementalHash.CreateHash(HashAlgorithmName.MD5) : null;
    }

    // AN: the string-path constructor was removed (Flac_Decoder / AudioDecoder.Open own file access)

    public void Dispose() {
        _outputHasher?.Dispose();   // AN: stream is owned by the caller
    }

    private void ValidateOptions() {
        if (_options.ValidateOutputHash && !_options.ConvertOutputToBytes)
            throw new ArgumentException("Output hash validation requires conversion to bytes.");
    }

    private void ReadMetadata() {
        if (_reader.Read(32) != 0x664C6143)
            throw new InvalidDataException("FLAC stream marker not found.");

        bool foundLastMetadataBlock;
        bool streamInfoSeen = false;
        do {
            foundLastMetadataBlock = _reader.Read(1) != 0;
            int type = (int)_reader.Read(7);
            int length = (int)_reader.Read(24);
            var blockType = (Flac_MetadataBlockType)type;   // AN: parse the blocks we surface, skip the rest
            MetadataBlocksPresent.Add(blockType);
            switch (blockType) {
                case Flac_MetadataBlockType.StreamInfo: {
                    var body = _reader.ReadBytes(length);
                    StreamInfo = Flac_StreamInfo.Parse(body);
                    ApplyStreamInfo();
                    streamInfoSeen = true;
                    break;
                }
                case Flac_MetadataBlockType.VorbisComment:
                    Tags = Flac_Tags.Parse(_reader.ReadBytes(length));
                    break;
                case Flac_MetadataBlockType.SeekTable:
                    SeekTable = Flac_SeekTable.Parse(_reader.ReadBytes(length));
                    break;
                default:
                    _reader.SkipBytes(length);   // PADDING / APPLICATION / CUESHEET / PICTURE: recorded as present only
                    break;
            }
        }
        while (!foundLastMetadataBlock);

        if (!streamInfoSeen)
            throw new InvalidDataException("Stream info metadata block not found.");
        AudioStartOffset = _reader.BytePosition;
    }

    private void ApplyStreamInfo() {   // AN: was ReadStreaminfoBlock reading fields directly from the bit reader
        var si = StreamInfo;
        MaxSamplesPerFrame = si.MaxBlockSize;
        SampleRate = si.SampleRate;
        ChannelCount = si.Channels;
        BitsPerSample = si.BitsPerSample;
        _expectedOutputHash = si.Md5Signature;
        StreamSampleCount = si.TotalSamplesOrNull;
        BytesPerSample = (BitsPerSample + 7) / 8;
        BufferSamples = new long[ChannelCount][];
        for (int ch = 0; ch < ChannelCount; ch++) {
            BufferSamples[ch] = new long[MaxSamplesPerFrame];
        }
        if (_options.ConvertOutputToBytes) {
            BufferBytes = new byte[MaxSamplesPerFrame * BlockAlign];
        }
    }

    /// <summary>AN: after the owner repositioned the stream to a frame boundary, discard buffered bits and resume there.</summary>
    public void ReseatAfterSeek(long absoluteStreamPosition, long runningSampleCount) {
        _reader.Reseat(absoluteStreamPosition);
        RunningSampleCount = runningSampleCount;
        EndedShort = false;
    }

    public bool DecodeFrame() {
        if (_reader.HasReachedEnd) {
            if (StreamSampleCount is not null && RunningSampleCount != StreamSampleCount) {
                EndedShort = true;   // AN: was throw InvalidDataException("Stream sample count is incorrect.") — D12
            }

            if (_options.ValidateOutputHash && !EndedShort && StreamInfo.HasMd5) {   // AN: an all-zero MD5 means "not computed"
                Span<byte> actualHash = stackalloc byte[16];
                _outputHasher!.GetCurrentHash(actualHash);

                if (!actualHash.SequenceEqual(_expectedOutputHash))
                    throw new InvalidDataException("Output hash is incorrect.");
            }

            return false;
        }

        BufferFrameOffset = _reader.BytePosition;   // AN
        _reader.BeginCrc8();                        // AN: frame header CRC-8 covers everything up to the CRC byte
        if (_reader.Read(15) != 0x7FFC)
            throw new InvalidDataException("Invalid frame sync code.");

        bool variableBlockSize = _reader.Read(1) != 0;   // AN: was Skip(1); needed to interpret the coded number
        int blockSizeCode = (int)_reader.Read(4);
        int sampleRateCode = (int)_reader.Read(4);
        int channelLayout = (int)_reader.Read(4);
        int bitDepthCode = (int)_reader.Read(3);
        _reader.Skip(1); // Reserved bit

        // Coded number (sample or frame number) — AN: decoded (UTF-8-like) instead of skipped
        ulong first = _reader.Read(8);
        int codedNumberLeadingOnes = BitOperations.LeadingZeroCount(~(first << 56));
        long codedNumber;
        if (codedNumberLeadingOnes == 0) {
            codedNumber = (long)first;
        }
        else {
            if (codedNumberLeadingOnes > 6) throw new InvalidDataException("Invalid coded number.");
            codedNumber = (long)(first & (0x7FUL >> codedNumberLeadingOnes));
            for (int i = 1; i < codedNumberLeadingOnes; i++) {
                ulong b = _reader.Read(8);
                if ((b & 0xC0) != 0x80) throw new InvalidDataException("Invalid coded number continuation.");
                codedNumber = (codedNumber << 6) | (long)(b & 0x3F);
            }
        }

        int frameSampleCount = blockSizeCode switch {
            1 => 192,
            >= 2 and <= 5 => 576 << (blockSizeCode - 2),
            6 => (int)_reader.Read(8) + 1,
            7 => (int)_reader.Read(16) + 1,
            >= 8 and <= 15 => 256 << (blockSizeCode - 8),
            _ => throw new InvalidDataException("Reserved block size.")
        };

        int frameSampleRate = sampleRateCode switch {
            0 => SampleRate,
            >= 1 and <= 11 => SampleRateCodes[sampleRateCode],
            12 => (int)_reader.Read(8) * 1000,
            13 => (int)_reader.Read(16),
            14 => (int)_reader.Read(16) * 10,
            _ => throw new InvalidDataException("Reserved sample rate.")
        };

        int frameBitsPerSample = bitDepthCode switch {
            0 => BitsPerSample,
            >= 1 and <= 2 => 8 + ((bitDepthCode - 1) * 4),
            >= 4 and <= 6 => 16 + ((bitDepthCode - 4) * 4),
            7 => 32,
            _ => throw new InvalidDataException("Reserved bit depth.")
        };

        int frameChannelCount = channelLayout switch {
            >= 0 and <= 7 => channelLayout + 1,
            >= 8 and <= 10 => 2,
            _ => throw new InvalidDataException("Reserved channel layout.")
        };

        if (frameSampleCount > MaxSamplesPerFrame)
            throw new InvalidDataException("Frame sample count exceeds maximum.");

        if (frameSampleRate != SampleRate || frameBitsPerSample != BitsPerSample || frameChannelCount != ChannelCount)
            throw new NotSupportedException("Unsupported audio property change.");

        byte expectedCrc8 = _reader.EndCrc8();   // AN: CRC-8 of the header so far
        int headerCrc = (int)_reader.Read(8);
        if (headerCrc != expectedCrc8)
            throw new InvalidDataException("Frame header CRC-8 mismatch.");   // AN

        BufferFirstSample = variableBlockSize ? codedNumber : codedNumber * MaxSamplesPerFrame;   // AN
        BufferSampleCount = frameSampleCount;
        RunningSampleCount += frameSampleCount;
        DecodeSubframes(_reader, BitsPerSample, channelLayout, BufferSamples, BufferSampleCount);
        _reader.AlignToByte();
        _reader.Skip(16); // Whole frame CRC

        if (_options.ConvertOutputToBytes) {
            ConvertOutputToBytes(BitsPerSample, ChannelCount, BufferSamples, BufferSampleCount, BufferBytes, _options.AllowNonstandardByteOutput);
            BufferByteCount = BufferSampleCount * BlockAlign;
        }

        if (_options.ValidateOutputHash) {
            _outputHasher!.AppendData(BufferBytes.AsSpan(0, BufferByteCount));   // AN: synchronous (was Task.Run)
        }

        return true;
    }

    // AN: interleaved span output (D16) — samples sign-extended in the low bits of an int, exactly as decoded
    public void CopyFrameInt32(Span<int> destination) {
        int n = BufferSampleCount, ch = ChannelCount;
        for (int c = 0; c < ch; c++) {
            long[] src = BufferSamples[c];
            for (int i = 0, o = c; i < n; i++, o += ch) destination[o] = (int)src[i];
        }
    }

    public void CopyFrameFloat(Span<float> destination) {
        int n = BufferSampleCount, ch = ChannelCount;
        float scale = 1f / (1L << (BitsPerSample - 1));   // D4: / 2^(bits-1)
        for (int c = 0; c < ch; c++) {
            long[] src = BufferSamples[c];
            for (int i = 0, o = c; i < n; i++, o += ch) destination[o] = src[i] * scale;
        }
    }

    private static void DecodeSubframes(BitReader reader, int bitsPerSample, int channelLayout, long[][] result, int blockSize) {
        if (channelLayout >= 0 && channelLayout <= 7) {
            for (int ch = 0; ch < result.Length; ch++) {
                DecodeSubframe(reader, bitsPerSample, result[ch].AsSpan(0, blockSize));
            }
        }
        else if (channelLayout >= 8 && channelLayout <= 10) {
            DecodeSubframe(reader, bitsPerSample + (channelLayout == 9 ? 1 : 0), result[0].AsSpan(0, blockSize));
            DecodeSubframe(reader, bitsPerSample + (channelLayout == 9 ? 0 : 1), result[1].AsSpan(0, blockSize));
            if (channelLayout == 8) {
                for (int i = 0; i < blockSize; i++) {
                    result[1][i] = result[0][i] - result[1][i];
                }
            }
            else if (channelLayout == 9) {
                for (int i = 0; i < blockSize; i++) {
                    result[0][i] += result[1][i];
                }
            }
            else if (channelLayout == 10) {
                for (int i = 0; i < blockSize; i++) {
                    long side = result[1][i];
                    long right = result[0][i] - (side >> 1);
                    result[1][i] = right;
                    result[0][i] = right + side;
                }
            }
        }
        else {
            throw new ArgumentOutOfRangeException(nameof(channelLayout));
        }
    }

    private static void DecodeSubframe(BitReader reader, int bitsPerSample, Span<long> result) {
        if (reader.Read(1) != 0)
            throw new InvalidDataException("Invalid subframe padding.");

        int type = (int)reader.Read(6);
        int shift = (int)reader.Read(1);
        if (shift == 1) {
            while (reader.Read(1) == 0) {
                shift++;
            }
        }
        bitsPerSample -= shift;

        if (type == 0) { // Constant coding
            long v = reader.ReadSigned(bitsPerSample);
            for (int i = 0; i < result.Length; i++) {
                result[i] = v;
            }
        }
        else if (type == 1) { // Verbatim coding
            for (int i = 0; i < result.Length; i++) {
                result[i] = reader.ReadSigned(bitsPerSample);
            }
        }
        else if (type >= 8 && type <= 12) {
            DecodeFixedPredictionSubframe(reader, type - 8, bitsPerSample, result);
        }
        else if (type >= 32 && type <= 63) {
            DecodeLinearPredictiveCodingSubframe(reader, type - 31, bitsPerSample, result);
        }
        else {
            throw new InvalidDataException("Reserved subframe type.");
        }

        if (shift != 0) {
            for (int i = 0; i < result.Length; i++) {
                result[i] <<= shift;
            }
        }
    }

    private static void DecodeFixedPredictionSubframe(BitReader reader, int predOrder, int bitsPerSample, Span<long> result) {
        for (int i = 0; i < predOrder; i++) {
            result[i] = reader.ReadSigned(bitsPerSample);
        }
        DecodeResiduals(reader, predOrder, result);
        if (predOrder != 0) {
            RestoreLinearPrediction(result, FixedPredictionCoefficients[predOrder], 0);
        }
    }

    private static void DecodeLinearPredictiveCodingSubframe(BitReader reader, int lpcOrder, int bitsPerSample, Span<long> result) {
        for (int i = 0; i < lpcOrder; i++) {
            result[i] = reader.ReadSigned(bitsPerSample);
        }
        int precision = (int)reader.Read(4) + 1;
        int shift = (int)reader.ReadSigned(5);
        Span<long> coefs = stackalloc long[lpcOrder];
        for (int i = coefs.Length - 1; i >= 0; i--) {
            coefs[i] = reader.ReadSigned(precision);
        }
        DecodeResiduals(reader, lpcOrder, result);
        RestoreLinearPrediction(result, coefs, shift);
    }

    private static void DecodeResiduals(BitReader reader, int warmup, Span<long> result) {
        int method = (int)reader.Read(2);
        if (method >= 2)
            throw new InvalidDataException("Reserved residual coding method.");
        int paramBits = method == 0 ? 4 : 5;
        int escapeParam = method == 0 ? 15 : 31;

        int partitionOrder = (int)reader.Read(4);
        int numPartitions = 1 << partitionOrder;
        if (result.Length % numPartitions != 0)
            throw new InvalidDataException("Block size not divisible by number of Rice partitions.");
        int partitionSize = result.Length / numPartitions;

        for (int i = 0; i < numPartitions; i++) {
            int start = i * partitionSize + (i == 0 ? warmup : 0);
            int end = (i + 1) * partitionSize;

            int param = (int)reader.Read(paramBits);
            if (param != escapeParam) {
                for (int j = start; j < end; j++) {
                    result[j] = DecodeRice(reader, param);
                }
            }
            else {
                int numBits = (int)reader.Read(5);
                for (int j = start; j < end; j++) {
                    result[j] = numBits != 0 ? reader.ReadSigned(numBits) : 0;
                }
            }
        }
    }

    private static void RestoreLinearPrediction(Span<long> result, ReadOnlySpan<long> coefs, int shift) {
        for (int i = 0; i < result.Length - coefs.Length; i++) {
            long sum = 0;
            for (int j = 0; j < coefs.Length; j++) {
                sum += result[i + j] * coefs[j];
            }
            result[i + coefs.Length] += sum >> shift;
        }
    }

    private static long DecodeRice(BitReader reader, int k) {
        ulong data = reader.RawBuffer;
        int leadingZeroCount = BitOperations.LeadingZeroCount(data);
        int quotientBitCount = leadingZeroCount + 1;
        int fullBitCount = quotientBitCount + k;
        if (fullBitCount > BitReader.BitsAvailableWorstCase) {
            return DecodeRiceFallback(reader, k);
        }
        ulong v = (ulong)leadingZeroCount << k;
        if (k != 0) {
            v |= (data << quotientBitCount) >> (64 - k);
        }
        reader.Skip(fullBitCount);
        // Apply sign from LSB
        return (int)(v >> 1) ^ -(int)(v & 1);
    }

    private static long DecodeRiceFallback(BitReader reader, int k) {
        int leadingZeroCount = 0;
        while (reader.Read(1) == 0) {
            leadingZeroCount++;
        }
        ulong v = (ulong)leadingZeroCount << k;
        if (k != 0) {
            v |= reader.Read(k);
        }
        // Apply sign from LSB
        return (int)(v >> 1) ^ -(int)(v & 1);
    }

    private static void ConvertOutputToBytes(int bitsPerSample, int channelCount, long[][] samples, int sampleCount, byte[] bytes, bool allowNonstandard) {
        if (!allowNonstandard && (bitsPerSample % 8 != 0 || bitsPerSample == 8)) {
            // Not allowed by default because the output produced here, which targets the byte format
            // specified by FLAC to calculate its MD5 signature, differs from the byte format used in
            // PCM WAV files. For non-whole-byte bit depths, WAV expects the samples to be shifted such
            // that the padding is in the LSBs, and for 8-bit, WAV expects the samples to be unsigned.
            throw new NotSupportedException("Unsupported bit depth.");
        }
        int bytesPerSample = (bitsPerSample + 7) / 8;
        int blockAlign = bytesPerSample * channelCount;
        for (int ch = 0; ch < channelCount; ch++) {
            long[] src = samples[ch];
            int offset = ch * bytesPerSample;
            if (bytesPerSample == 1) {
                for (int i = 0; i < sampleCount; i++) {
                    bytes[offset] = (byte)src[i];
                    offset += blockAlign;
                }
            }
            else if (bytesPerSample == 2) {
                Span<byte> byteSpan = bytes.AsSpan();
                for (int i = 0; i < sampleCount; i++) {
                    BinaryPrimitives.WriteInt16LittleEndian(byteSpan.Slice(offset, 2), (short)src[i]);
                    offset += blockAlign;
                }
            }
            else if (bytesPerSample == 3) {
                for (int i = 0; i < sampleCount; i++) {
                    long s = src[i];
                    bytes[offset    ] = (byte)s;
                    bytes[offset + 1] = (byte)(s >> 8);
                    bytes[offset + 2] = (byte)(s >> 16);
                    offset += blockAlign;
                }
            }
            else if (bytesPerSample == 4) {
                Span<byte> byteSpan = bytes.AsSpan();
                for (int i = 0; i < sampleCount; i++) {
                    BinaryPrimitives.WriteInt32LittleEndian(byteSpan.Slice(offset, 4), (int)src[i]);
                    offset += blockAlign;
                }
            }
            else {
                throw new NotSupportedException("Unsupported bit depth.");
            }
        }
    }

    private static readonly int[] SampleRateCodes = [
        0, 88200, 176400, 192000, 8000, 16000, 22050, 24000, 32000, 44100, 48000, 96000
    ];

    private static readonly long[][] FixedPredictionCoefficients = [
        [],
        [1],
        [-1, 2],
        [1, -3, 3],
        [-1, 4, -6, 4]
    ];

    // AN: static helper for the seek scanner (Flac_Decoder) — CRC-8 poly 0x07, init 0, as in the FLAC frame header
    public static byte Crc8(ReadOnlySpan<byte> data) {
        byte crc = 0;
        foreach (byte b in data) crc = Crc8Table[crc ^ b];
        return crc;
    }

    private static readonly byte[] Crc8Table = BuildCrc8Table();
    private static byte[] BuildCrc8Table() {
        var t = new byte[256];
        for (int i = 0; i < 256; i++) {
            int c = i;
            for (int b = 0; b < 8; b++) c = (c & 0x80) != 0 ? ((c << 1) ^ 0x07) & 0xFF : (c << 1) & 0xFF;
            t[i] = (byte)c;
        }
        return t;
    }

    private sealed class BitReader {   // AN: no longer IDisposable; buffered; position-tracking; re-seatable
        // Buffer replenish logic ensures that a full byte isn't missing
        public const int BitsAvailableWorstCase = 57;
        private const int ByteBufferSize = 64 * 1024;   // AN

        private readonly Stream _stream;
        private ulong _buffer;
        private int _bufferDeficitBits;
        private int _streamOverreadBytes;

        // AN: byte buffer between the stream and the 64-bit bit buffer
        private readonly byte[] _bytes = new byte[ByteBufferSize];
        private int _bytesHead, _bytesTail;
        private long _bytesFedToBitBuffer;   // total bytes ever moved into _buffer since the last (re)seat
        private long _seatOffset;            // absolute stream offset of byte 0 of the current seating
        private bool _crcActive;             // AN: CRC-8 accumulation over bytes as they LEAVE the bit buffer
        private byte _crc;
        // (CRC accumulator fields live next to CrcConsume)

        public BitReader(Stream stream) {
            _stream = stream;
            _seatOffset = stream.CanSeek ? stream.Position : 0;
            _bufferDeficitBits = 64;
            ReplenishBuffer();
        }

        public bool HasReachedEnd =>
            _streamOverreadBytes >= 8;

        public ulong RawBuffer =>
            _buffer;

        /// <summary>AN: absolute byte offset of the next unread bit's byte (exact when byte-aligned).</summary>
        public long BytePosition => _seatOffset + ConsumedBits / 8;

        private long ConsumedBits => _bytesFedToBitBuffer * 8 - (64 - _bufferDeficitBits);

        /// <summary>AN: the owner moved the stream to <paramref name="absolutePosition"/>; forget everything buffered.</summary>
        public void Reseat(long absolutePosition) {
            _seatOffset = absolutePosition;
            _bytesHead = _bytesTail = 0;
            _bytesFedToBitBuffer = 0;
            _buffer = 0;
            _bufferDeficitBits = 64;
            _streamOverreadBytes = 0;
            _crcActive = false;
            ReplenishBuffer();
        }

        private int NextByte() {   // AN: buffered replacement for _stream.ReadByte()
            if (_bytesHead == _bytesTail) {
                _bytesHead = 0;
                _bytesTail = _stream.Read(_bytes, 0, _bytes.Length);
                if (_bytesTail <= 0) { _bytesTail = 0; return -1; }
            }
            return _bytes[_bytesHead++];
        }

        private void ReplenishBuffer() {
            while (_bufferDeficitBits >= 8) {
                int b = NextByte();
                if (b == -1) {
                    _streamOverreadBytes++;
                    if (HasReachedEnd) {
                        if (_bufferDeficitBits == 8) {
                            // End was exactly reached; leave deficit so subsequent reads will throw
                            return;
                        }
                        throw new EndOfStreamException();
                    }
                }
                else {
                    _buffer |= (ulong)b << (_bufferDeficitBits - 8);
                    _bytesFedToBitBuffer++;
                }
                _bufferDeficitBits -= 8;
            }
        }

        public void Skip(int numBits) {
            if (numBits < 1 || numBits > BitsAvailableWorstCase)
                throw new ArgumentOutOfRangeException(nameof(numBits));
            if (_crcActive) CrcConsume(numBits);   // AN
            _buffer <<= numBits;
            _bufferDeficitBits += numBits;
            ReplenishBuffer();
        }

        public ulong Read(int numBits) {
            ulong x = _buffer >> (64 - numBits);
            Skip(numBits);
            return x;
        }

        public long ReadSigned(int numBits) {
            ulong x = Read(numBits);
            int shift = 64 - numBits;
            return (long)(x << shift) >> shift;
        }

        public void AlignToByte() {
            if (_bufferDeficitBits != 0) {
                Skip(8 - _bufferDeficitBits);
            }
        }

        // AN: byte-granular metadata helpers (only valid when byte-aligned, which metadata always is)
        public byte[] ReadBytes(int count) {
            var result = new byte[count];
            for (int i = 0; i < count; i++) result[i] = (byte)Read(8);
            return result;
        }

        public void SkipBytes(int count) {
            for (int i = 0; i < count; i++) Skip(8);
        }

        // AN: CRC-8 over whole bytes consumed between BeginCrc8 and EndCrc8 (frame header is byte-aligned at both ends)
        public void BeginCrc8() { _crcActive = true; _crc = 0; _crcAcc = 0; _crcAccBits = 0; }
        public byte EndCrc8() { _crcActive = false; return _crc; }
        private ulong _crcAcc;      // bits consumed but not yet a whole byte (< 8 of them)
        private int _crcAccBits;
        private void CrcConsume(int numBits) {
            // the bits about to leave the top of _buffer, appended to the pending partial byte
            ulong value = _buffer >> (64 - numBits);
            _crcAcc = (_crcAcc << numBits) | value;   // _crcAccBits < 8 and numBits <= 57 => fits in 64 bits
            _crcAccBits += numBits;
            while (_crcAccBits >= 8) {
                _crcAccBits -= 8;
                _crc = Crc8Table[_crc ^ (byte)(_crcAcc >> _crcAccBits)];
            }
            _crcAcc &= (1UL << _crcAccBits) - 1;
        }
    }

    public sealed class Options {
        public bool ConvertOutputToBytes { get; set; } = false;   // AN: was true
        public bool ValidateOutputHash { get; set; } = false;     // AN: was true
        public bool AllowNonstandardByteOutput { get; set; } = false;
    }
}