using System;

namespace AN.Audio.Internal;

/// <summary>
/// High-quality windowed-sinc resampler using a Kaiser-windowed polyphase FIR filter.
/// Supports any sample rate ratio (rational or irrational). Zero-allocation on the hot path.
/// Maintains state between calls for streaming use.
/// </summary>
internal sealed class SincResampler
{
    /// <summary>Number of taps on each side of the filter center (total kernel = 2 * HalfWidth).</summary>
    private const int KernelHalfWidth = 8;

    /// <summary>Total number of taps in the filter kernel.</summary>
    private const int KernelWidth = KernelHalfWidth * 2;

    /// <summary>Number of fractional sub-positions in the lookup table.</summary>
    private const int SubPositions = 256;

    /// <summary>Kaiser window beta parameter (~60dB stopband attenuation).</summary>
    private const double KaiserBeta = 6.0;

    /// <summary>Pre-computed filter coefficients: [subPosition * KernelWidth + tap].</summary>
    private static readonly float[] s_filterTable = BuildFilterTable();

    private readonly int _channels;
    private readonly double _ratio; // srcRate / dstRate

    // History buffer for carrying samples across callback boundaries.
    // Stores recent source frames so the kernel can look back across call boundaries.
    private const int HistoryCapacity = KernelWidth; // 16 frames (one full kernel width)
    private readonly float[] _history; // HistoryCapacity * channels
    private int _historyCount; // Number of valid frames in history (0..HistoryCapacity)

    // Fractional sub-sample position within the current source frame.
    // Always in the range [0, 1). Tracks the fractional part between integer source positions.
    private double _fracPosition;

    /// <summary>
    /// Creates a new SincResampler for the given rate conversion and channel count.
    /// </summary>
    public SincResampler(int srcRate, int dstRate, int channels)
    {
        _channels = channels;
        _ratio = (double)srcRate / dstRate;
        _history = new float[HistoryCapacity * channels];
        _historyCount = 0;
        _fracPosition = 0;
    }

    /// <summary>
    /// Reset the resampler state (history buffer and fractional position).
    /// Call on device switch or playback restart.
    /// </summary>
    public void Reset()
    {
        Array.Clear(_history);
        _historyCount = 0;
        _fracPosition = 0;
    }

    /// <summary>
    /// Process source frames and produce resampled output frames.
    /// Source and destination are interleaved float samples.
    /// The resampler reports how many source frames were actually consumed,
    /// which may be less than srcFrames. The caller must only advance its source
    /// pointer by srcConsumed.
    /// </summary>
    /// <param name="src">Source samples (interleaved float, srcFrames * channels).</param>
    /// <param name="srcFrames">Number of source frames available.</param>
    /// <param name="dst">Destination buffer (interleaved float, maxDstFrames * channels).</param>
    /// <param name="maxDstFrames">Maximum number of output frames to produce.</param>
    /// <param name="srcConsumed">Number of source frames actually consumed. The caller must
    /// only advance its source pointer by this amount.</param>
    /// <returns>Number of output frames actually produced.</returns>
    public int Process(ReadOnlySpan<float> src, int srcFrames, Span<float> dst, int maxDstFrames, out int srcConsumed)
    {
        int channels = _channels;
        double ratio = _ratio;
        int dstFrame = 0;

        // srcIndex tracks which integer source frame we're at.
        // frac tracks the fractional position between source frames.
        int srcIndex = 0;
        double frac = _fracPosition;

        while (dstFrame < maxDstFrames)
        {
            // The kernel needs source frames [srcIndex - KernelHalfWidth + 1 .. srcIndex + KernelHalfWidth - 1]
            int lastNeeded = srcIndex + KernelHalfWidth - 1;
            if (lastNeeded >= srcFrames)
                break; // Not enough source data

            // Select sub-filter from fractional position
            int subIdx = (int)(frac * SubPositions);
            if (subIdx >= SubPositions) subIdx = SubPositions - 1;
            int tableOffset = subIdx * KernelWidth;

            // Convolve kernel with source samples
            for (int ch = 0; ch < channels; ch++)
            {
                float sum = 0f;
                for (int tap = 0; tap < KernelWidth; tap++)
                {
                    int samplePos = srcIndex - KernelHalfWidth + 1 + tap;
                    float sample = GetSample(src, srcFrames, samplePos, ch, channels);
                    sum += sample * s_filterTable[tableOffset + tap];
                }
                dst[dstFrame * channels + ch] = sum;
            }

            dstFrame++;

            // Advance position by the ratio
            frac += ratio;
            int advance = (int)frac;
            frac -= advance;
            srcIndex += advance;
        }

        // Save fractional position for next call
        _fracPosition = frac;

        // Update history: save the last KernelHalfWidth frames from the consumed source.
        // The kernel looks back KernelHalfWidth-1 frames from center, so we need that many
        // frames from the end of consumed data for the next call's lookback.
        int consumed = srcIndex;
        srcConsumed = consumed;

        // Save the tail of consumed source into history
        int framesToSave = Math.Min(HistoryCapacity, consumed);
        if (framesToSave > 0)
        {
            int srcStart = consumed - framesToSave;
            // If srcStart is negative, we need to pull from old history
            if (srcStart >= 0)
            {
                for (int f = 0; f < framesToSave; f++)
                {
                    for (int ch = 0; ch < channels; ch++)
                    {
                        _history[f * channels + ch] = src[(srcStart + f) * channels + ch];
                    }
                }
                _historyCount = framesToSave;
            }
            else
            {
                // Some frames come from old history, some from src
                int fromHistory = -srcStart;
                int fromSrc = consumed;
                int oldHistStart = _historyCount - fromHistory;

                int writeIdx = 0;
                // Copy from old history tail
                for (int f = 0; f < fromHistory && oldHistStart + f >= 0; f++)
                {
                    for (int ch = 0; ch < channels; ch++)
                    {
                        _history[writeIdx * channels + ch] = _history[(oldHistStart + f) * channels + ch];
                    }
                    writeIdx++;
                }
                // Copy from src
                for (int f = 0; f < fromSrc; f++)
                {
                    for (int ch = 0; ch < channels; ch++)
                    {
                        _history[writeIdx * channels + ch] = src[f * channels + ch];
                    }
                    writeIdx++;
                }
                _historyCount = writeIdx;
            }
        }
        else if (consumed == 0)
        {
            // No frames consumed, history stays the same
        }

        return dstFrame;
    }

    /// <summary>
    /// Process overload without srcConsumed output (consumes all source frames).
    /// For use when the caller provides exactly the right amount of source data
    /// (e.g., in tests where source is pre-sliced to the exact consumed amount).
    /// </summary>
    public int Process(ReadOnlySpan<float> src, int srcFrames, Span<float> dst, int maxDstFrames)
    {
        return Process(src, srcFrames, dst, maxDstFrames, out _);
    }

    /// <summary>
    /// Get a sample at the given source position.
    /// Positive positions index into the current src buffer.
    /// Negative positions index into the history buffer (end-relative).
    /// </summary>
    private float GetSample(ReadOnlySpan<float> src, int srcFrames, int srcPos, int ch, int channels)
    {
        if (srcPos >= 0)
        {
            if (srcPos >= srcFrames)
                return 0f;
            return src[srcPos * channels + ch];
        }
        else
        {
            // Negative position: look in history buffer.
            // srcPos = -1 means the last frame in history (index histCount-1)
            int histIdx = _historyCount + srcPos;
            if (histIdx < 0)
                return 0f; // Before available history
            return _history[histIdx * channels + ch];
        }
    }

    /// <summary>
    /// Estimate how many source frames are needed to produce the given number of output frames.
    /// </summary>
    public int EstimateSourceFrames(int outputFrames)
    {
        // The resampler consumes approximately (outputFrames * ratio + currentFrac) integer frames.
        // Add 1 for rounding. The kernel looks into history for lookback so we don't need
        // to add kernel margin — just provide enough for the center positions to advance through.
        return (int)(outputFrames * _ratio + _fracPosition) + 2;
    }

    /// <summary>
    /// Build the static filter coefficient table.
    /// </summary>
    private static float[] BuildFilterTable()
    {
        float[] table = new float[SubPositions * KernelWidth];

        for (int sub = 0; sub < SubPositions; sub++)
        {
            double frac = sub / (double)SubPositions;
            double sum = 0;

            for (int tap = 0; tap < KernelWidth; tap++)
            {
                double x = (tap - KernelHalfWidth + 1) - frac;
                double sinc = (Math.Abs(x) < 1e-10) ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
                double window = Kaiser(x / KernelHalfWidth, KaiserBeta);
                double coeff = sinc * window;
                table[sub * KernelWidth + tap] = (float)coeff;
                sum += coeff;
            }

            // Normalize so coefficients sum to 1.0 (preserves DC gain)
            if (Math.Abs(sum) > 1e-10)
            {
                float invSum = (float)(1.0 / sum);
                for (int tap = 0; tap < KernelWidth; tap++)
                {
                    table[sub * KernelWidth + tap] *= invSum;
                }
            }
        }

        return table;
    }

    private static double Kaiser(double x, double beta)
    {
        if (Math.Abs(x) > 1.0)
            return 0.0;
        double term = 1.0 - x * x;
        return BesselI0(beta * Math.Sqrt(term)) / BesselI0(beta);
    }

    private static double BesselI0(double x)
    {
        double sum = 1.0;
        double term = 1.0;
        double halfX = x * 0.5;

        for (int k = 1; k <= 25; k++)
        {
            term *= (halfX / k);
            double termSquared = term * term;
            sum += termSquared;
            if (termSquared < sum * 1e-16)
                break;
        }

        return sum;
    }
}