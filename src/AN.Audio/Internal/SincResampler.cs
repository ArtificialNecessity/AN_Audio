using System;

namespace AN.Audio.Internal;

/// <summary>
/// High-quality windowed-sinc resampler using a Kaiser-windowed polyphase FIR filter.
/// Supports any sample rate ratio (rational or irrational). Zero-allocation on the hot path.
/// Maintains state between calls for streaming use.
///
/// IMPORTANT: The resampler always consumes ALL provided source frames. Unconsumed
/// frames are stored in the internal history buffer for the next call's kernel lookback.
/// This means the caller can advance its source pointer by the full srcFrames amount.
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

    // History buffer: stores the tail of ALL source frames provided (not just consumed).
    // This ensures that when the next call's kernel looks back, it sees the correct data.
    // Size must accommodate the maximum lookback the kernel needs.
    private const int HistoryCapacity = KernelWidth; // 16 frames
    private readonly float[] _history; // HistoryCapacity * channels
    private int _historyCount; // Number of valid frames in history (0..HistoryCapacity)

    // Tracks the fractional sub-sample position between calls.
    // srcIndex (integer part) is reset each call; only the fraction carries over.
    private double _fracPosition;

    // Tracks how many source frames were provided but not yet "reached" by the
    // output position. This is the distance from where we stopped to the end of src.
    // On the next call, this becomes a negative start offset (we begin in history).
    private int _pendingSourceFrames;

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
        _pendingSourceFrames = 0;
    }

    /// <summary>
    /// Reset the resampler state. Call on device switch or playback restart.
    /// </summary>
    public void Reset()
    {
        Array.Clear(_history);
        _historyCount = 0;
        _fracPosition = 0;
        _pendingSourceFrames = 0;
    }

    /// <summary>
    /// Process source frames and produce resampled output frames.
    /// ALL source frames are consumed (stored in history if not directly used).
    /// The caller should advance its source pointer by the full srcFrames.
    /// </summary>
    /// <param name="src">Source samples (interleaved float, srcFrames * channels).</param>
    /// <param name="srcFrames">Number of source frames provided.</param>
    /// <param name="dst">Destination buffer (interleaved float, maxDstFrames * channels).</param>
    /// <param name="maxDstFrames">Maximum number of output frames to produce.</param>
    /// <returns>Number of output frames actually produced.</returns>
    public int Process(ReadOnlySpan<float> src, int srcFrames, Span<float> dst, int maxDstFrames)
    {
        int channels = _channels;
        double ratio = _ratio;
        int dstFrame = 0;

        // srcIndex starts negative if we have pending frames from last call
        // (meaning the output position is still "in" the previous source data,
        // which is now in our history buffer).
        int srcIndex = -_pendingSourceFrames;
        double frac = _fracPosition;

        while (dstFrame < maxDstFrames)
        {
            // The kernel accesses [srcIndex - KernelHalfWidth + 1 .. srcIndex + KernelHalfWidth - 1]
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

            // Advance position
            frac += ratio;
            int advance = (int)frac;
            frac -= advance;
            srcIndex += advance;
        }

        // Save fractional position
        _fracPosition = frac;

        // How many source frames are "ahead" of where we stopped?
        // These frames were provided but the output hasn't caught up to them yet.
        // They'll be in history for the next call's kernel to access.
        _pendingSourceFrames = srcFrames - srcIndex;
        if (_pendingSourceFrames < 0) _pendingSourceFrames = 0;

        // Update history: save the tail of the source buffer.
        // The kernel needs up to KernelHalfWidth frames of lookback.
        // We save up to HistoryCapacity frames from the end of the provided source.
        int framesToSave = Math.Min(HistoryCapacity, srcFrames);
        int saveStart = srcFrames - framesToSave;
        for (int f = 0; f < framesToSave; f++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                _history[f * channels + ch] = src[(saveStart + f) * channels + ch];
            }
        }
        _historyCount = framesToSave;

        return dstFrame;
    }

    /// <summary>
    /// Process overload that also reports srcConsumed (for compatibility with tests).
    /// Since ALL source frames are always consumed, srcConsumed always equals srcFrames.
    /// </summary>
    public int Process(ReadOnlySpan<float> src, int srcFrames, Span<float> dst, int maxDstFrames, out int srcConsumed)
    {
        int result = Process(src, srcFrames, dst, maxDstFrames);
        srcConsumed = srcFrames; // Always consumes all provided frames
        return result;
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
            // srcPos = -1 means the last frame in history (index histCount-1)
            int histIdx = _historyCount + srcPos;
            if (histIdx < 0)
                return 0f; // Before available history — zero pad
            return _history[histIdx * channels + ch];
        }
    }

    /// <summary>
    /// Estimate how many source frames are needed to produce the given number of output frames.
    /// Provides enough so the resampler can fill the full output without running short.
    /// </summary>
    public int EstimateSourceFrames(int outputFrames)
    {
        // The resampler starts at srcIndex = -_pendingSourceFrames, advances by ~ratio per output.
        // After outputFrames outputs, srcIndex ≈ -_pendingSourceFrames + outputFrames * ratio + frac.
        // We need lastNeeded = srcIndex + KernelHalfWidth - 1 < srcFrames.
        // So: srcFrames > outputFrames * ratio - _pendingSourceFrames + frac + KernelHalfWidth - 1
        int needed = (int)(outputFrames * _ratio + _fracPosition) - _pendingSourceFrames + KernelHalfWidth + 1;
        return Math.Max(needed, 0);
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