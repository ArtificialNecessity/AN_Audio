using System.Runtime.InteropServices;

namespace AN.Audio.Internal;

/// <summary>
/// Converts audio between consumer format and device format.
/// Handles: sample format (Int16 ↔ Float32), sample rate (linear interpolation),
/// and channel count (mono → stereo upmix, stereo → mono downmix).
/// Zero-allocation on the hot path.
/// </summary>
internal sealed class AudioFormatConverter
{
    private readonly AudioFormat _consumerFormat;
    private AudioFormat _deviceFormat;

    // Resampling state
    private double _resampleFrac;
    private double _resampleStep; // consumer_rate / device_rate

    // Cached for fast path check
    private bool _isPassthrough;

    public AudioFormatConverter(AudioFormat consumerFormat, AudioFormat deviceFormat)
    {
        _consumerFormat = consumerFormat;
        _deviceFormat = deviceFormat;
        RecalculateState();
    }

    /// <summary>
    /// True if consumer format exactly matches device format (no conversion needed).
    /// </summary>
    public bool IsPassthrough => _isPassthrough;

    /// <summary>
    /// Update the device format (e.g., after a device switch). Resets resampler state.
    /// </summary>
    public void UpdateDeviceFormat(AudioFormat newDeviceFormat)
    {
        _deviceFormat = newDeviceFormat;
        _resampleFrac = 0;
        RecalculateState();
    }

    /// <summary>
    /// Reset resampling state (e.g., when playback restarts).
    /// </summary>
    public void Reset()
    {
        _resampleFrac = 0;
    }

    /// <summary>
    /// Fill the device buffer by pulling samples from the consumer callback.
    /// The callback is invoked with the consumer format; output is written in device format.
    /// Returns the number of device frames actually filled.
    /// </summary>
    /// <param name="deviceBuffer">Raw device buffer to fill (in device format).</param>
    /// <param name="deviceFrameCount">Number of device frames requested.</param>
    /// <param name="callback">The consumer's audio callback.</param>
    /// <returns>Number of device frames written.</returns>
    public unsafe int FillDeviceBuffer(Span<byte> deviceBuffer, int deviceFrameCount, AudioCallback callback)
    {
        if (_isPassthrough)
        {
            // Fast path: formats match exactly, just call through
            return callback(deviceBuffer, deviceFrameCount, _consumerFormat);
        }

        // Slow path: need conversion
        return ConvertAndFill(deviceBuffer, deviceFrameCount, callback);
    }

    private unsafe int ConvertAndFill(Span<byte> deviceBuffer, int deviceFrameCount, AudioCallback callback)
    {
        // Strategy: request consumer frames, then convert to device format.
        // For resampling, we need to figure out how many consumer frames
        // will produce the requested device frames.
        int consumerFramesNeeded = EstimateConsumerFrames(deviceFrameCount);

        // Rent a temporary buffer for consumer data
        int consumerBytes = consumerFramesNeeded * _consumerFormat.BytesPerFrame;
        byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(consumerBytes);
        Span<byte> consumerBuffer = rented.AsSpan(0, consumerBytes);

        try
        {
            // Get consumer data
            int consumerFramesWritten = callback(consumerBuffer, consumerFramesNeeded, _consumerFormat);
            if (consumerFramesWritten == 0)
            {
                deviceBuffer.Slice(0, deviceFrameCount * _deviceFormat.BytesPerFrame).Clear();
                return 0;
            }

            // Convert consumer frames to device frames
            int deviceFramesProduced = ConvertFrames(
                consumerBuffer, consumerFramesWritten,
                deviceBuffer, deviceFrameCount);

            // Zero any remaining device frames
            if (deviceFramesProduced < deviceFrameCount)
            {
                int filledBytes = deviceFramesProduced * _deviceFormat.BytesPerFrame;
                deviceBuffer.Slice(filledBytes, (deviceFrameCount - deviceFramesProduced) * _deviceFormat.BytesPerFrame).Clear();
            }

            return deviceFramesProduced;
        }
        finally
        {
            if (rented != null)
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Convert consumer frames to device frames.
    /// Handles sample rate, sample format, and channel count differences.
    /// </summary>
    private int ConvertFrames(Span<byte> srcBuf, int srcFrames, Span<byte> dstBuf, int maxDstFrames)
    {
        // Work in float internally for all conversions
        int srcChannels = _consumerFormat.Channels;
        int dstChannels = _deviceFormat.Channels;
        bool needResample = _consumerFormat.SampleRate != _deviceFormat.SampleRate;

        int dstFrame = 0;

        if (needResample)
        {
            // Resampling with linear interpolation
            while (dstFrame < maxDstFrames)
            {
                // Determine the fractional position in the source
                int srcIdx = (int)_resampleFrac;
                double frac = _resampleFrac - srcIdx;

                if (srcIdx + 1 >= srcFrames)
                    break; // Not enough source data

                // Interpolate each source channel, then write to dest channels
                for (int dstCh = 0; dstCh < dstChannels; dstCh++)
                {
                    int srcCh = MapChannel(dstCh, srcChannels, dstChannels);
                    float s0 = ReadSampleAsFloat(srcBuf, srcIdx, srcCh, srcChannels, _consumerFormat);
                    float s1 = ReadSampleAsFloat(srcBuf, srcIdx + 1, srcCh, srcChannels, _consumerFormat);
                    float interpolated = (float)(s0 + (s1 - s0) * frac);
                    WriteSampleFromFloat(dstBuf, dstFrame, dstCh, dstChannels, _deviceFormat, interpolated);
                }

                dstFrame++;
                _resampleFrac += _resampleStep;
            }

            // Adjust _resampleFrac to account for consumed source frames
            _resampleFrac -= srcFrames;
            if (_resampleFrac < 0) _resampleFrac = 0;
        }
        else
        {
            // No resampling needed — just format/channel conversion
            int framesToConvert = Math.Min(srcFrames, maxDstFrames);
            for (dstFrame = 0; dstFrame < framesToConvert; dstFrame++)
            {
                for (int dstCh = 0; dstCh < dstChannels; dstCh++)
                {
                    int srcCh = MapChannel(dstCh, srcChannels, dstChannels);
                    float sample = ReadSampleAsFloat(srcBuf, dstFrame, srcCh, srcChannels, _consumerFormat);
                    WriteSampleFromFloat(dstBuf, dstFrame, dstCh, dstChannels, _deviceFormat, sample);
                }
            }
        }

        return dstFrame;
    }

    /// <summary>
    /// Read a sample from the buffer as a float in [-1, 1] range.
    /// </summary>
    private static float ReadSampleAsFloat(Span<byte> buf, int frame, int channel, int channelCount, AudioFormat fmt)
    {
        int sampleIndex = frame * channelCount + channel;
        if (fmt.Format == SampleFormat.Float32)
        {
            var floats = MemoryMarshal.Cast<byte, float>(buf);
            return floats[sampleIndex];
        }
        else // Int16
        {
            var shorts = MemoryMarshal.Cast<byte, short>(buf);
            return shorts[sampleIndex] / 32768f;
        }
    }

    /// <summary>
    /// Write a float sample [-1, 1] to the buffer in the target format.
    /// </summary>
    private static void WriteSampleFromFloat(Span<byte> buf, int frame, int channel, int channelCount, AudioFormat fmt, float value)
    {
        int sampleIndex = frame * channelCount + channel;
        if (fmt.Format == SampleFormat.Float32)
        {
            var floats = MemoryMarshal.Cast<byte, float>(buf);
            floats[sampleIndex] = value;
        }
        else // Int16
        {
            var shorts = MemoryMarshal.Cast<byte, short>(buf);
            shorts[sampleIndex] = (short)(value * 32767f);
        }
    }

    /// <summary>
    /// Map destination channel to source channel.
    /// Mono → Stereo: both dest channels read from source channel 0.
    /// Stereo → Mono: dest channel 0 reads from source channel 0 (simple, not averaged).
    /// Same channel count: 1:1 mapping.
    /// </summary>
    private static int MapChannel(int dstChannel, int srcChannels, int dstChannels)
    {
        if (srcChannels == dstChannels)
            return dstChannel;
        if (srcChannels == 1)
            return 0; // mono source: all dest channels read from channel 0
        // Multi-channel source, fewer dest channels: just take the first N
        return dstChannel < srcChannels ? dstChannel : 0;
    }

    /// <summary>
    /// Estimate how many consumer frames are needed to produce the given device frames.
    /// </summary>
    private int EstimateConsumerFrames(int deviceFrames)
    {
        if (_consumerFormat.SampleRate == _deviceFormat.SampleRate)
            return deviceFrames;

        // Add a small margin for resampling (need src+1 for interpolation)
        return (int)(deviceFrames * _resampleStep) + 2;
    }

    private void RecalculateState()
    {
        _isPassthrough = _consumerFormat.SampleRate == _deviceFormat.SampleRate
                      && _consumerFormat.Channels == _deviceFormat.Channels
                      && _consumerFormat.Format == _deviceFormat.Format;

        _resampleStep = (double)_consumerFormat.SampleRate / _deviceFormat.SampleRate;
    }
}