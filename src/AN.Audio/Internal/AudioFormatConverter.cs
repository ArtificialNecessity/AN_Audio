using System.Runtime.InteropServices;

namespace AN.Audio.Internal;

/// <summary>
/// Converts audio between consumer format and device format.
/// Handles: sample format (Int16 ↔ Float32), sample rate (windowed-sinc resampling),
/// and channel count (mono → stereo upmix, stereo → mono downmix).
/// Zero-allocation on the hot path (uses ArrayPool rentals).
/// </summary>
internal sealed class AudioFormatConverter
{
    private readonly AudioFormat _consumerFormat;
    private SincResampler? _sincResampler;
    private AudioFormat _deviceFormat;

    // Resampling state (kept for linear interp fallback)
    private double _resampleFrac;
    private double _resampleStep; // consumer_rate / device_rate

    // Cached for fast path check
    private bool _isPassthrough;

    public AudioFormatConverter(AudioFormat consumerFormat, AudioFormat deviceFormat)
    {
        _consumerFormat = consumerFormat;
        _deviceFormat = deviceFormat;
        _sincResampler = CreateResamplerIfNeeded();
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
        _sincResampler = CreateResamplerIfNeeded();
        RecalculateState();
    }

    /// <summary>
    /// Reset resampling state (e.g., when playback restarts).
    /// </summary>
    public void Reset()
    {
        _resampleFrac = 0;
        _sincResampler?.Reset();
    }

    /// <summary>
    /// Fill the device buffer by pulling samples from the consumer callback.
    /// The callback is invoked with the consumer format; output is written in device format.
    /// Returns the number of device frames actually filled.
    /// </summary>
    public unsafe int FillDeviceBuffer(Span<byte> deviceBuffer, int deviceFrameCount, AudioCallback callback)
    {
        if (_isPassthrough)
        {
            return callback(deviceBuffer, deviceFrameCount, _consumerFormat);
        }

        return ConvertAndFill(deviceBuffer, deviceFrameCount, callback);
    }

    private unsafe int ConvertAndFill(Span<byte> deviceBuffer, int deviceFrameCount, AudioCallback callback)
    {
        int consumerFramesNeeded = _sincResampler != null
            ? _sincResampler.EstimateSourceFrames(deviceFrameCount)
            : EstimateConsumerFrames(deviceFrameCount);

        int consumerBytes = consumerFramesNeeded * _consumerFormat.BytesPerFrame;
        byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(consumerBytes);
        Span<byte> consumerBuffer = rented.AsSpan(0, consumerBytes);

        try
        {
            int consumerFramesWritten = callback(consumerBuffer, consumerFramesNeeded, _consumerFormat);
            if (consumerFramesWritten == 0)
            {
                deviceBuffer.Slice(0, deviceFrameCount * _deviceFormat.BytesPerFrame).Clear();
                return 0;
            }

            int deviceFramesProduced = ConvertFrames(
                consumerBuffer, consumerFramesWritten,
                deviceBuffer, deviceFrameCount);

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

    private int ConvertFrames(Span<byte> srcBuf, int srcFrames, Span<byte> dstBuf, int maxDstFrames)
    {
        int srcChannels = _consumerFormat.Channels;
        int dstChannels = _deviceFormat.Channels;
        bool needResample = _consumerFormat.SampleRate != _deviceFormat.SampleRate;

        int dstFrame = 0;

        if (needResample)
        {
            if (_sincResampler != null)
            {
                // High-quality sinc resampling path
                int srcSampleCount = srcFrames * srcChannels;
                float[] srcFloatRented = System.Buffers.ArrayPool<float>.Shared.Rent(srcSampleCount);
                Span<float> srcFloat = srcFloatRented.AsSpan(0, srcSampleCount);

                try
                {
                    // Convert source bytes to float
                    for (int f = 0; f < srcFrames; f++)
                    {
                        for (int ch = 0; ch < srcChannels; ch++)
                        {
                            srcFloat[f * srcChannels + ch] = ReadSampleAsFloat(srcBuf, f, ch, srcChannels, _consumerFormat);
                        }
                    }

                    // Resample (consumes ALL srcFrames, stores unconsumed tail in history)
                    int dstSampleCount = maxDstFrames * srcChannels;
                    float[] dstFloatRented = System.Buffers.ArrayPool<float>.Shared.Rent(dstSampleCount);
                    Span<float> dstFloat = dstFloatRented.AsSpan(0, dstSampleCount);

                    try
                    {
                        int resampledFrames = _sincResampler.Process(srcFloat, srcFrames, dstFloat, maxDstFrames);

                        // Write resampled output with channel mapping and format conversion
                        for (int f = 0; f < resampledFrames; f++)
                        {
                            for (int dstCh = 0; dstCh < dstChannels; dstCh++)
                            {
                                int srcCh = MapChannel(dstCh, srcChannels, dstChannels);
                                float sample = dstFloat[f * srcChannels + srcCh];
                                WriteSampleFromFloat(dstBuf, f, dstCh, dstChannels, _deviceFormat, sample);
                            }
                        }
                        dstFrame = resampledFrames;
                    }
                    finally
                    {
                        System.Buffers.ArrayPool<float>.Shared.Return(dstFloatRented);
                    }
                }
                finally
                {
                    System.Buffers.ArrayPool<float>.Shared.Return(srcFloatRented);
                }
            }
            else
            {
                // Fallback: linear interpolation
                while (dstFrame < maxDstFrames)
                {
                    int srcIdx = (int)_resampleFrac;
                    double frac = _resampleFrac - srcIdx;

                    if (srcIdx + 1 >= srcFrames)
                        break;

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

                _resampleFrac -= srcFrames;
                if (_resampleFrac < 0) _resampleFrac = 0;
            }
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

    private static float ReadSampleAsFloat(Span<byte> buf, int frame, int channel, int channelCount, AudioFormat fmt)
    {
        int sampleIndex = frame * channelCount + channel;
        if (fmt.Format == SampleFormat.Float32)
        {
            var floats = MemoryMarshal.Cast<byte, float>(buf);
            return floats[sampleIndex];
        }
        else
        {
            var shorts = MemoryMarshal.Cast<byte, short>(buf);
            return shorts[sampleIndex] / 32768f;
        }
    }

    private static void WriteSampleFromFloat(Span<byte> buf, int frame, int channel, int channelCount, AudioFormat fmt, float value)
    {
        int sampleIndex = frame * channelCount + channel;
        if (fmt.Format == SampleFormat.Float32)
        {
            var floats = MemoryMarshal.Cast<byte, float>(buf);
            floats[sampleIndex] = value;
        }
        else
        {
            var shorts = MemoryMarshal.Cast<byte, short>(buf);
            shorts[sampleIndex] = (short)(value * 32767f);
        }
    }

    private static int MapChannel(int dstChannel, int srcChannels, int dstChannels)
    {
        if (srcChannels == dstChannels)
            return dstChannel;
        if (srcChannels == 1)
            return 0;
        return dstChannel < srcChannels ? dstChannel : 0;
    }

    private int EstimateConsumerFrames(int deviceFrames)
    {
        if (_consumerFormat.SampleRate == _deviceFormat.SampleRate)
            return deviceFrames;
        return (int)(deviceFrames * _resampleStep) + 2;
    }

    private void RecalculateState()
    {
        _isPassthrough = _consumerFormat.SampleRate == _deviceFormat.SampleRate
                      && _consumerFormat.Channels == _deviceFormat.Channels
                      && _consumerFormat.Format == _deviceFormat.Format;

        _resampleStep = (double)_consumerFormat.SampleRate / _deviceFormat.SampleRate;
    }

    private SincResampler? CreateResamplerIfNeeded()
    {
        if (_consumerFormat.SampleRate == _deviceFormat.SampleRate)
            return null;

        return new SincResampler(_consumerFormat.SampleRate, _deviceFormat.SampleRate, _consumerFormat.Channels);
    }
}