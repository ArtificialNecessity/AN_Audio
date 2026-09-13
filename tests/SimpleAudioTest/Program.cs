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
        // Parse args: [wavPath] [--duration <seconds>] [--low-latency] [--raw]   (spec 60 §4 / §8)
        //             [--asio] [--asio-driver <name|asio:{CLSID}>] [--asio-offset N] [--asio-probe] [--tone]   (spec 70 §5)
        string wavPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "AssetSource", "cartesia_tts_test.wav");
        double? durationSeconds = null;
        bool lowLatency = false, exclusive = false, raw = false, probeAll = false, asio = false, asioProbe = false, tone = false;
        string? deviceId = null, asioDriver = null; int asioOffset = 0;
        float volume = 0.25f; // --volume 0..1; the fixture is LOUD at unity

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--duration" && i + 1 < args.Length)
            {
                durationSeconds = double.Parse(args[++i]);
            }
            else if (args[i] == "--volume" && i + 1 < args.Length) volume = Math.Clamp(float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture), 0f, 1f);
            else if (args[i] == "--low-latency") lowLatency = true;
            else if (args[i] == "--exclusive") exclusive = true;
            else if (args[i] == "--raw") raw = true;
            else if (args[i] == "--probe-all") probeAll = true; // open EVERY render endpoint in LowLatency and report the period it grants
            else if (args[i] == "--device" && i + 1 < args.Length) deviceId = args[++i];
            else if (args[i] == "--asio") asio = true;
            else if (args[i] == "--asio-driver" && i + 1 < args.Length) { asio = true; asioDriver = args[++i]; }
            else if (args[i] == "--asio-offset" && i + 1 < args.Length) asioOffset = int.Parse(args[++i]);
            else if (args[i] == "--asio-probe") { asio = true; asioProbe = true; } // list drivers, open the chosen one, print its facts, exit
            else if (args[i] == "--tone") tone = true; // 440 Hz sine instead of the WAV (clean measurement signal)
            else if (!args[i].StartsWith("--"))
            {
                wavPath = args[i];
            }
        }

        wavPath = Path.GetFullPath(wavPath);

        if (!tone && !asioProbe && !File.Exists(wavPath))
        {
            Console.Error.WriteLine($"WAV file not found: {wavPath}");
            return 1;
        }

        if (asio)
        {
            var asioManager = AudioOutput.GetDeviceManager(AudioOutput_Backend.Asio);
            var drivers = asioManager?.GetOutputDevices() ?? [];
            Console.WriteLine($"ASIO drivers ({drivers.Count}):");
            foreach (var d in drivers) Console.WriteLine($"  {d.DisplayName,-40} {d.Id}");
            if (drivers.Count == 0) { Console.Error.WriteLine("No ASIO driver registered for this process bitness."); return 1; }
            var chosen = asioDriver is null ? drivers[0]
                : drivers.FirstOrDefault(d => string.Equals(d.Id, asioDriver, StringComparison.OrdinalIgnoreCase) || d.DisplayName.Contains(asioDriver, StringComparison.OrdinalIgnoreCase))
                  ?? throw new ArgumentException($"no ASIO driver matches '{asioDriver}'");
            deviceId = chosen.Id;
            Console.WriteLine($"Using: {chosen.DisplayName} ({chosen.Id}), output offset {asioOffset}");
        }

        if (asioProbe)
        {
            using var probe = AudioOutput.Create(new AudioFormat(48000, 2, SampleFormat.Float32),
                new AudioOutputOptions { Backend = AudioOutput_Backend.Asio, PreferredDevices = [deviceId!], Asio_OutputChannelOffset = new(asioOffset) });
            Console.WriteLine($"Device format: {probe.DeviceFormat.SampleRate} Hz, {probe.DeviceFormat.Channels} ch, {probe.DeviceFormat.Format}");
            Console.WriteLine($"Period: {probe.PeriodFrames} frames = {probe.PeriodFrames * 1000.0 / probe.DeviceFormat.SampleRate:F2} ms   LatencyMs (outputLatency): {probe.LatencyMs:F2}");
            Console.WriteLine($"Mode: {probe.LatencyModeActual}/{probe.StreamProcessingActual}   fallback: {probe.LatencyFallbackReason}   device: {probe.CurrentDevice}");
            return 0;
        }

        if (!tone) Console.WriteLine($"Loading: {wavPath}");

        if (probeAll)
        {
            var manager = AudioOutput.GetDeviceManager();
            if (manager == null) { Console.Error.WriteLine("No device manager on this platform"); return 1; }
            foreach (var device in manager.GetOutputDevices())
            {
                try
                {
                    using var probe = AudioOutput.Create(new AudioFormat(48000, 2, SampleFormat.Float32), new AudioOutputOptions { Latency = AudioOutput_LatencyMode.LowLatency, SwitchPolicy = AudioSwitchPolicy.PreferenceList, PreferredDevices = [device.Id] });
                    Console.WriteLine($"{device.DisplayName,-52} period {probe.PeriodFrames,5} frames = {probe.PeriodFrames * 1000.0 / probe.DeviceFormat.SampleRate,6:F2} ms @ {probe.DeviceFormat.SampleRate} Hz   {probe.LatencyModeActual}/{probe.LatencyFallbackReason}   id={device.Id}");
                }
                catch (Exception ex) { Console.WriteLine($"{device.DisplayName,-52} FAILED: {ex.Message}"); }
            }
            return 0;
        }

        // Parse WAV and load all PCM data into memory (pre-allocated)
        var wav = tone ? ToneGenerator.Sine(48000, 2, 440.0, seconds: 1.0) : WavReader.Load(wavPath);
        Console.WriteLine(tone ? "Signal: 440 Hz sine, 48000 Hz stereo Int16 (1 s loop)" : $"WAV: {wav.SampleRate}Hz, {wav.Channels}ch, {wav.BitsPerSample}bit, {wav.PcmData.Length} bytes ({wav.DurationSeconds:F2}s)");

        // Create audio output matching the endpoint's preferred format
        // The callback will convert from WAV format -> endpoint format
        var format = new AudioFormat(wav.SampleRate, wav.Channels, SampleFormat.Int16);
        Console.WriteLine($"Requesting format: {format.SampleRate}Hz, {format.Channels}ch, {format.Format}");

        var options = new AudioOutputOptions
        {
            BufferSizeMs = 20,
            Latency = exclusive ? AudioOutput_LatencyMode.Exclusive : lowLatency ? AudioOutput_LatencyMode.LowLatency : AudioOutput_LatencyMode.Default,
            Processing = raw ? AudioOutput_StreamProcessing.Raw : AudioOutput_StreamProcessing.SystemEffects,
            SwitchPolicy = deviceId != null ? AudioSwitchPolicy.PreferenceList : AudioSwitchPolicy.FollowDefault, PreferredDevices = deviceId != null ? [deviceId] : null,
            Backend = asio ? AudioOutput_Backend.Asio : AudioOutput_Backend.Auto,
            Asio_OutputChannelOffset = new(asioOffset),
        };
        Console.WriteLine($"Requested: backend {options.Backend}, latency {options.Latency}, processing {options.Processing}");
        using var output = AudioOutput.Create(format, options);
        Console.WriteLine($"Consumer format: {output.Format.SampleRate}Hz, {output.Format.Channels}ch, {output.Format.Format}");
        Console.WriteLine($"Device format: {output.DeviceFormat.SampleRate}Hz, {output.DeviceFormat.Channels}ch, {output.DeviceFormat.Format}");
        Console.WriteLine($"Latency: {output.LatencyMs:F1}ms");
        Console.WriteLine($"Actual: latency {output.LatencyModeActual}, processing {output.StreamProcessingActual}, fallback {output.LatencyFallbackReason}, period {output.PeriodFrames} frames = {output.PeriodFrames * 1000.0 / output.DeviceFormat.SampleRate:F2} ms");
        Console.WriteLine($"Switch policy: {output.SwitchPolicy}");
        Console.WriteLine($"Current device: {output.CurrentDevice}");

        // Subscribe to device management events for diagnostics
        output.DeviceSwitched += device =>
            Console.WriteLine($"\n  >> DEVICE SWITCHED: {device}");
        output.DeviceFormatChanged += newFormat =>
            Console.WriteLine($"\n  >> DEVICE FORMAT CHANGED: {newFormat.SampleRate}Hz, {newFormat.Channels}ch, {newFormat.Format}");
        output.DeviceLost += reason =>
            Console.WriteLine($"\n  >> DEVICE LOST: {reason}");

        // Playback state — accessed only from the audio thread (no lock needed)
        bool looping = true; // Always loop — duration controls when we stop
        var state = new PlaybackState(wav, looping, volume);

        // Default to 30 seconds if no duration specified (use --duration N to override)
        if (!durationSeconds.HasValue)
            durationSeconds = 30;

        Console.WriteLine($"Playing (looping for {durationSeconds.Value}s)... press Enter to stop");
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
        Console.WriteLine($"Underruns: {output.UnderrunCount}   (period {output.PeriodFrames} frames, {output.LatencyModeActual}/{output.StreamProcessingActual})");
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
    private readonly float _volume;
    private int _position; // byte offset into PCM data
    private int _loopCount;

    public bool Finished { get; private set; }
    public int LoopCount => _loopCount;

    public PlaybackState(WavData wav, bool looping, float volume = 1f)
    {
        _wav = wav;
        _looping = looping;
        _volume = volume;
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

        // Gain (in place, allocation-free) — the fixture is mastered hot; default --volume 0.3
        if (_volume < 1f && framesWritten > 0)
        {
            if (dstIsFloat)
            {
                var f32 = MemoryMarshal.Cast<byte, float>(buffer.Slice(0, framesWritten * dstBytesPerFrame));
                for (int i = 0; i < f32.Length; i++) f32[i] *= _volume;
            }
            else
            {
                var s16 = MemoryMarshal.Cast<byte, short>(buffer.Slice(0, framesWritten * dstBytesPerFrame));
                for (int i = 0; i < s16.Length; i++) s16[i] = (short)(s16[i] * _volume);
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

/// <summary>Synthesises a looping sine as Int16 PCM so the ASIO/WASAPI measurements use a clean, known signal (--tone).</summary>
internal static class ToneGenerator
{
    public static WavData Sine(int sampleRate, int channels, double frequencyHz, double seconds)
    {
        int frames = (int)(sampleRate * seconds);
        var pcm = new byte[frames * channels * 2];
        for (int f = 0; f < frames; f++)
        {
            short s = (short)(Math.Sin(2 * Math.PI * frequencyHz * f / sampleRate) * 32767 * 0.25);
            for (int c = 0; c < channels; c++) { int o = (f * channels + c) * 2; pcm[o] = (byte)s; pcm[o + 1] = (byte)(s >> 8); }
        }
        return new WavData { SampleRate = sampleRate, Channels = channels, BitsPerSample = 16, PcmData = pcm };
    }
}