using System.Buffers.Binary;
using AN.Audio.Formats.Internal;
using AN.Audio.Formats.Wav;

namespace AN.Audio.Formats;

/// <summary>
/// Facade of the generic tier (D3): sniffs the container by content (D8) and returns the per-format decoder as
/// <see cref="IAudioDecoder"/>. <see cref="DecodeAll(Stream, AudioDecoder_FormatHint, bool)"/> is a loop over the streaming path (D2).
/// </summary>
public static class AudioDecoder
{
    /// <summary>Bytes <see cref="Open(Stream, AudioDecoder_FormatHint, bool)"/> peeks before deciding. Enough for an ID3v2 header + one MPEG frame in most cases.</summary>
    internal const int SniffBytes = 12;
    internal const int SniffBytesMp3 = 8192;

    /// <summary>
    /// Sniffs the leading bytes through an <see cref="PeekableStream"/> so non-seekable sources work, then returns the
    /// per-format decoder. <paramref name="leaveOpen"/> semantics as in <see cref="StreamReader"/>.
    /// </summary>
    public static IAudioDecoder Open(Stream source, AudioDecoder_FormatHint hint = default, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        var peek = new PeekableStream(source, leaveOpen);
        try
        {
            var container = Sniff(peek.Peek(SniffBytes), hint);
            if (container is null && hint == AudioDecoder_FormatHint.None || container is AudioDecoder_Container.Mp3)
            {
                // Weak signature: look deeper for an MPEG sync pair before deciding
                var deeper = Sniff(peek.Peek(SniffBytesMp3), hint);
                container = deeper ?? container;
            }
            return container switch
            {
                AudioDecoder_Container.Wav => new Wav_Decoder(peek),
                AudioDecoder_Container.Flac => throw new AudioDecoder_UnsupportedException("FLAC decoding arrives in spec 50 Phase 2", AudioDecoder_Container.Flac),
                AudioDecoder_Container.Mp3 => throw new AudioDecoder_UnsupportedException("MP3 decoding arrives in spec 50 Phase 3", AudioDecoder_Container.Mp3),
                AudioDecoder_Container.Aiff => throw new AudioDecoder_UnsupportedException("AIFF is not yet supported (Phase 4)", AudioDecoder_Container.Aiff),
                AudioDecoder_Container.Ogg => throw new AudioDecoder_UnsupportedException("Ogg is not yet supported (Phase 4)", AudioDecoder_Container.Ogg),
                _ => throw new AudioDecoder_UnsupportedException("unrecognised audio container"),
            };
        }
        catch
        {
            peek.Dispose();
            throw;
        }
    }

    /// <summary>Opens a file with a 64 KiB sequential-scan <see cref="FileStream"/>; the extension supplies the hint.</summary>
    public static IAudioDecoder Open(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        try { return Open(fs, HintFromExtension(path)); }
        catch { fs.Dispose(); throw; }
    }

    /// <summary>Convenience over the streaming path: decodes everything into one buffer (capacity hint = TotalFrames when known).</summary>
    public static AudioDecoder_Pcm DecodeAll(Stream source, AudioDecoder_FormatHint hint = default, bool leaveOpen = false)
    {
        using var dec = Open(source, hint, leaveOpen);
        return DecodeAll(dec);
    }

    public static AudioDecoder_Pcm DecodeAll(string path)
    {
        using var dec = Open(path);
        return DecodeAll(dec);
    }

    /// <summary>Drains an already-open decoder into one buffer. Does not dispose it.</summary>
    public static AudioDecoder_Pcm DecodeAll(IAudioDecoder dec)
    {
        int channels = dec.Info.Channels;
        const int BlockFrames = 4096;
        long capacityFrames = dec.Info.TotalFrames is { } t && t > 0 && t < int.MaxValue / channels ? t : BlockFrames * 4;
        var buffer = new float[capacityFrames * channels];
        long frames = 0;
        while (true)
        {
            long free = buffer.Length / channels - frames;
            if (free < BlockFrames)
            {
                long newFrames = Math.Max(buffer.Length / channels * 2L, frames + BlockFrames);
                if (newFrames * channels > int.MaxValue) throw new AudioDecoder_UnsupportedException("stream too large for DecodeAll; use ReadFrames");
                Array.Resize(ref buffer, (int)(newFrames * channels));
                free = buffer.Length / channels - frames;
            }
            int want = (int)Math.Min(free, BlockFrames);
            int got = dec.ReadFrames(buffer.AsSpan((int)(frames * channels), want * channels));
            if (got == 0) break;
            frames += got;
        }
        if (buffer.Length != frames * channels)
            Array.Resize(ref buffer, (int)(frames * channels));
        return new AudioDecoder_Pcm(dec.Info, buffer, (int)frames, dec.EndedEarly);
    }

    /// <summary>
    /// Content-based container detection (D8). Returns null when nothing matches and <paramref name="hint"/> is None.
    /// Give at least 12 bytes; give more (a few KiB) to detect bare MPEG streams reliably (two consecutive valid frame headers).
    /// </summary>
    public static AudioDecoder_Container? Sniff(ReadOnlySpan<byte> leadingBytes, AudioDecoder_FormatHint hint = default)
    {
        if (leadingBytes.Length >= 12)
        {
            uint tag0 = BinaryPrimitives.ReadUInt32BigEndian(leadingBytes);
            uint tag8 = BinaryPrimitives.ReadUInt32BigEndian(leadingBytes.Slice(8));
            if ((tag0 == 0x52494646u /* RIFF */ || tag0 == 0x52463634u /* RF64 */) && tag8 == 0x57415645u /* WAVE */)
                return AudioDecoder_Container.Wav;
            if (tag0 == 0x464F524Du /* FORM */ && (tag8 == 0x41494646u /* AIFF */ || tag8 == 0x41494643u /* AIFC */))
                return AudioDecoder_Container.Aiff;
        }
        if (leadingBytes.Length >= 4)
        {
            uint tag0 = BinaryPrimitives.ReadUInt32BigEndian(leadingBytes);
            if (tag0 == 0x664C6143u /* fLaC */) return AudioDecoder_Container.Flac;
            if (tag0 == 0x4F676753u /* OggS */) return AudioDecoder_Container.Ogg;
        }

        // MPEG: optional ID3v2 tag, then a sync word with a valid header and a second valid header at the computed offset
        int offset = 0;
        if (leadingBytes.Length >= 10 && leadingBytes[0] == (byte)'I' && leadingBytes[1] == (byte)'D' && leadingBytes[2] == (byte)'3')
        {
            int size = ((leadingBytes[6] & 0x7F) << 21) | ((leadingBytes[7] & 0x7F) << 14) | ((leadingBytes[8] & 0x7F) << 7) | (leadingBytes[9] & 0x7F);
            bool footer = (leadingBytes[5] & 0x10) != 0;
            offset = 10 + size + (footer ? 10 : 0);
            if (offset >= leadingBytes.Length)
                return AudioDecoder_Container.Mp3;   // tag is longer than what we can see: ID3v2 essentially means MPEG audio
        }
        if (MpegFrameHeader.LooksLikeMpegAudio(leadingBytes.Slice(offset), requireSecondFrame: offset == 0))
            return AudioDecoder_Container.Mp3;

        return hint switch
        {
            AudioDecoder_FormatHint.Wav => AudioDecoder_Container.Wav,
            AudioDecoder_FormatHint.Flac => AudioDecoder_Container.Flac,
            AudioDecoder_FormatHint.Mp3 => AudioDecoder_Container.Mp3,
            AudioDecoder_FormatHint.Aiff => AudioDecoder_Container.Aiff,
            AudioDecoder_FormatHint.Ogg => AudioDecoder_Container.Ogg,
            _ => null,
        };
    }

    /// <summary>Maps a file extension to a hint (case-insensitive).</summary>
    public static AudioDecoder_FormatHint HintFromExtension(string pathOrExtension)
    {
        string ext = Path.GetExtension(pathOrExtension);
        if (ext.Length == 0) ext = pathOrExtension;
        return ext.ToLowerInvariant() switch
        {
            ".wav" or ".wave" or ".bwf" or ".rf64" => AudioDecoder_FormatHint.Wav,
            ".flac" or ".fla" => AudioDecoder_FormatHint.Flac,
            ".mp3" or ".mp2" or ".mpga" => AudioDecoder_FormatHint.Mp3,
            ".aif" or ".aiff" or ".aifc" => AudioDecoder_FormatHint.Aiff,
            ".ogg" or ".oga" or ".opus" => AudioDecoder_FormatHint.Ogg,
            _ => AudioDecoder_FormatHint.None,
        };
    }
}