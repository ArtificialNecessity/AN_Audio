using AN.Audio.Internal;
using Xunit;
using Xunit.Abstractions;

namespace AN.Audio.Tests;

public class SincResamplerDiagnosticTests
{
    private readonly ITestOutputHelper _output;

    public SincResamplerDiagnosticTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Simulate the exact pattern AudioFormatConverter uses: provide estimated source,
    /// advance by srcConsumed, carry leftovers. Verify no discontinuities.
    /// </summary>
    [Fact]
    public void Diagnose_MultiCallback_WithSrcConsumed()
    {
        const int srcRate = 44100;
        const int dstRate = 48000;
        const int channels = 1;
        const double frequency = 1000.0;
        const double amplitude = 0.7;
        const int deviceFrames = 960;
        const int numCallbacks = 10;

        // Generate continuous source
        int totalSrc = numCallbacks * 900 + 500;
        float[] src = new float[totalSrc];
        for (int i = 0; i < totalSrc; i++)
        {
            double t = (double)i / srcRate;
            src[i] = (float)(amplitude * Math.Sin(2.0 * Math.PI * frequency * t));
        }

        var resampler = new SincResampler(srcRate, dstRate, channels);
        float[] allOutput = new float[numCallbacks * deviceFrames + 100];
        int totalOutput = 0;
        int srcOffset = 0;

        for (int cb = 0; cb < numCallbacks; cb++)
        {
            int srcNeeded = resampler.EstimateSourceFrames(deviceFrames);
            int srcAvail = Math.Min(srcNeeded, totalSrc - srcOffset);
            if (srcAvail <= 0) break;

            var srcSlice = src.AsSpan(srcOffset, srcAvail);
            var dstSlice = allOutput.AsSpan(totalOutput, deviceFrames);
            int produced = resampler.Process(srcSlice, srcAvail, dstSlice, deviceFrames, out int srcConsumed);

            _output.WriteLine($"CB{cb}: srcOffset={srcOffset} srcAvail={srcAvail} srcConsumed={srcConsumed} produced={produced}");

            totalOutput += produced;
            srcOffset += srcConsumed; // Only advance by consumed, not by provided!
        }

        // Check for discontinuities
        int discCount = 0;
        float maxJump = 0;
        for (int i = 1; i < totalOutput; i++)
        {
            float jump = Math.Abs(allOutput[i] - allOutput[i - 1]);
            if (jump > maxJump) maxJump = jump;
            if (jump > 0.3f)
            {
                discCount++;
                if (discCount <= 3)
                    _output.WriteLine($"  Disc at sample {i}: {allOutput[i-1]:F6} -> {allOutput[i]:F6} (jump={jump:F4})");
            }
        }
        _output.WriteLine($"Total discontinuities: {discCount}, max jump: {maxJump:F4}");

        Assert.True(discCount == 0, $"Found {discCount} discontinuities");
    }
}