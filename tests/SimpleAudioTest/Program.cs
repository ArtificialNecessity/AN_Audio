using AN.Audio;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SimpleAudioTest;

/// <summary>
/// Standalone command-line audio test.
/// Loads a WAV file, plays it through AN.Audio's platform backend.
/// Zero allocation in the audio callback path.
/// </summary>
internal static class Program
{
    static int Main(string[] args)
    {
        // Parse args: [wavPath] [--duration <seconds>]
        string wavPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "AssetSource", "cartesia_tts_test.wav");
        double? durationSeconds = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--duration" && i + 1 < args.Length)
            {
                durationSeconds = double.Parse(args[++i]);
            }
            else if (!args[i].StartsWith("--"))
            {
                wavPath = args[i];
            }
        }

        wavPath = Path.GetFullPath(wavPath);

        if (!File.Exists(wavPath))
        {
            Console.Error.WriteLine($"WAV file not found: {wavPath}");
            return 1;
        }

        Console.WriteLine($"Loading: {wavPath}");

        // Parse WAV and load all PCM data into memory (pre-allocated)
        var wav = WavReader.Load(wavPath);
        Console.WriteLine($"WAV: {wav.SampleRate}Hz, {wav.Channels}ch, {wav.BitsPerSample}bit, {wav.PcmData.Length} bytes ({wav.DurationSeconds:F2}s)");

        // Create audio output matching the endpoint's preferred format
        // The callback will convert from WAV format -> endpoint format
        var format = new AudioFormat(wav.SampleRate, wav.Channels, SampleFormat.Int16);
        Console.WriteLine($"Requesting format: {format.SampleRate}Hz, {format.Channels}ch, {format.Format}");

        using var output = AudioOutput.Create(format, bufferSizeMs: 20);
        Console.WriteLine($"Endpoint format: {output.Format.SampleRate}Hz, {output.Format.Channels}ch, {output.Format.Format}");
        Console.WriteLine($"Latency: {output.LatencyMs:F1}ms");

        // Playback state — accessed only from the audio thread (no lock needed)
        bool looping = durationSeconds.HasValue;
        var state = new PlaybackState(wav, looping);

        if (durationSeconds.HasValue)
            Console.WriteLine($"Playing (looping for {durationSeconds.Value}s)... press Enter to stop");
        else
            Console.WriteLine("Playing... (press Enter to stop)");
        Console.WriteLine();

        output.Start(state.FillBuffer);

        // Wait for playback to finish, duration to elapse, or user to press Enter
        bool consoleAvailable = true;
        try { _ = Console.KeyAvailable; }
        catch (InvalidOperationException) { consoleAvailable = false; }

        var stopwatch = Stopwatch.StartNew();
        bool timedOut = false;

        while (!state.Finished && !timedOut)
        {
            if (durationSeconds.HasValue && stopwatch.Elapsed.TotalSeconds >= durationSeconds.Value)
            {
                timedOut = true;
                break;
            }
            if (consoleAvailable)
            {
                try
                {
                    if (Console.KeyAvailable)
                    {
                        Console.ReadKey(intercept: true);
                        break;
                    }
                }
                catch (InvalidOperationException) { consoleAvailable = false; }
            }
            Thread.Sleep(50);
        }

        output.Stop();

        double elapsed = stopwatch.Elapsed.TotalSeconds;
        Console.WriteLine();
        if (timedOut)
            Console.WriteLine($"Duration test complete. Played for {elapsed:F1}s with {state.LoopCount} loops.");
        else if (state.Finished)
            Console.WriteLine("Playback complete.");
        else
            Console.WriteLine("Stopped by user.");
        return 0;
    }
}

/// <summary>
/// Holds the PCM data and read position. The FillBuffer method is the audio callback.
/// All fields are pre-allocated. No GC pressure in the hot path.
/// </summary>
internal sealed class PlaybackState
{
    private readonly WavData _wav;
    private readonly bool _looping;
    private int _position; // byte offset into PCM data
    private int _loopCount;

    public bool Finished { get; private set; }
    public int LoopCount => _loopCount;

    public PlaybackState(WavData wav, bool looping)
    {
        _wav = wav;
        _looping = looping;
        _position = 0;
    }

    /// <summary>
    /// Audio callback. Called on the audio thread. Zero allocations.
    /// Handles format conversion from source WAV (int16/mono/44100) to endpoint format.
    /// </summary>
    public int FillBuffer(Span<byte> buffer, int frameCount, AudioFormat outputFormat)
    {
        if (Finished)
        {
            buffer.Clear();
            return 0;
        }

        int framesWritten = 0;
        var pcm = _wav.PcmData;
        int srcBytesPerSample = _wav.BitsPerSample / 8;
        int srcChannels = _wav.Channels;
        int srcBytesPerFrame = srcBytesPerSample * srcChannels;

        int dstChannels = outputFormat.Channels;
        int dstBytesPerFrame = outputFormat.BytesPerFrame;
        bool dstIsFloat = outputFormat.Format == SampleFormat.Float32;

        // Fast path: same format (int16, same channels, same rate)
        if (!dstIsFloat && srcChannels == dstChannels && _wav.SampleRate == outputFormat.SampleRate)
        {
            int bytesNeeded = frameCount * srcBytesPerFrame;
            int written = 0;
            while (written < bytesNeeded)
            {
                int bytesAvailable = pcm.Length - _position;
                int bytesToCopy = Math.Min(bytesNeeded - written, bytesAvailable);

                pcm.AsSpan(_position, bytesToCopy).CopyTo(buffer.Slice(written));
                _position += bytesToCopy;
                written += bytesToCopy;

                if (_position >= pcm.Length)
                {
                    if (_looping)
                    {
                        _position = 0;
                        _loopCount++;
                    }
                    else
                    {
                        Finished = true;
                        break;
                    }
                }
            }
            framesWritten = written / srcBytesPerFrame;
        }
        else
        {
            // Conversion path: int16 source -> float32 output, with channel upmix
            // No allocation: we work sample-by-sample writing directly into the output buffer
            for (int f = 0; f < frameCount; f++)
            {
                if (_position >= pcm.Length)
                {
                    if (_looping)
                    {
                        _position = 0;
                        _loopCount++;
                    }
                    else
                    {
                        Finished = true;
                        break;
                    }
                }

                // Read source sample(s) for this frame
                for (int outCh = 0; outCh < dstChannels; outCh++)
                {
                    // Map output channel to source channel (mono->stereo = duplicate)
                    int srcCh = (outCh < srcChannels) ? outCh : srcChannels - 1;
                    int srcOffset = _position + srcCh * srcBytesPerSample;

                    float sample;
                    if (srcBytesPerSample == 2 && srcOffset + 1 < pcm.Length)
                    {
                        short s = (short)(pcm[srcOffset] | (pcm[srcOffset + 1] << 8));
                        sample = s / 32768f;
                    }
                    else
                    {
                        sample = 0f;
                    }

                    // Write to output
                    int dstOffset = (f * dstChannels + outCh) * outputFormat.BytesPerSample;
                    if (dstIsFloat)
                    {
                        MemoryMarshal.Write(buffer.Slice(dstOffset), in sample);
                    }
                    else
                    {
                        short pcmOut = (short)(sample * 32767f);
                        MemoryMarshal.Write(buffer.Slice(dstOffset), in pcmOut);
                    }
                }

                _position += srcBytesPerFrame;
                framesWritten++;
            }
        }

        return framesWritten;
    }
}

/// <summary>
/// Immutable container for loaded WAV data.
/// </summary>
internal sealed class WavData
{
    public int SampleRate { get; init; }
    public int Channels { get; init; }
    public int BitsPerSample { get; init; }
    public byte[] PcmData { get; init; } = Array.Empty<byte>();

    public double DurationSeconds =>
        PcmData.Length / (double)(SampleRate * Channels * (BitsPerSample / 8));
}

/// <summary>
/// Minimal WAV file parser. Handles standard PCM WAV files.
/// One-shot load into a byte[] — no streaming, no allocation during playback.
/// </summary>
internal static class WavReader
{
    public static WavData Load(string path)
    {
        using var fs = File.OpenRead(path);
        using var reader = new BinaryReader(fs);

        // RIFF header
        uint riffId = reader.ReadUInt32(); // 'RIFF'
        if (riffId != 0x46464952) throw new InvalidDataException("Not a RIFF file");

        uint fileSize = reader.ReadUInt32();
        uint waveId = reader.ReadUInt32(); // 'WAVE'
        if (waveId != 0x45564157) throw new InvalidDataException("Not a WAVE file");

        int sampleRate = 0;
        short channels = 0;
        short bitsPerSample = 0;
        byte[]? pcmData = null;

        // Read chunks
        while (fs.Position < fs.Length)
        {
            uint chunkId = reader.ReadUInt32();
            uint chunkSize = reader.ReadUInt32();
            long chunkEnd = fs.Position + chunkSize;

            switch (chunkId)
            {
                case 0x20746D66: // 'fmt '
                    short formatTag = reader.ReadInt16();
                    channels = reader.ReadInt16();
                    sampleRate = reader.ReadInt32();
                    int byteRate = reader.ReadInt32();
                    short blockAlign = reader.ReadInt16();
                    bitsPerSample = reader.ReadInt16();
                    // Skip any extra fmt bytes
                    break;

                case 0x61746164: // 'data'
                    pcmData = reader.ReadBytes((int)chunkSize);
                    break;
            }

            // Seek to next chunk (handles padding/extra bytes)
            fs.Position = chunkEnd;
            // Chunks are word-aligned
            if (chunkSize % 2 != 0 && fs.Position < fs.Length)
                fs.Position++;
        }

        if (pcmData == null)
            throw new InvalidDataException("No data chunk found in WAV file");

        return new WavData
        {
            SampleRate = sampleRate,
            Channels = channels,
            BitsPerSample = bitsPerSample,
            PcmData = pcmData
        };
    }
}