# AN.Audio: Upgrade Resampler from Linear Interpolation to Windowed-Sinc

## Problem

The current `AudioFormatConverter` in `AN.Audio/Internal/AudioFormatConverter.cs` uses **simple two-point linear interpolation** for sample rate conversion. This produces audible artifacts:

- **Constant grittiness/noise floor** — aliasing from insufficient band-limiting
- **High-frequency distortion** — particularly noticeable on speech (sibilants, plosives)
- **Lo-fi quality** — compared to browser Web Audio API which uses high-quality sinc resampling

The most common conversion path is **44100Hz → 48000Hz** (Cartesia TTS → WASAPI device). The ratio 44100/48000 = 0.91875 is irrational, meaning every single output sample requires interpolation. With linear interp, every sample carries aliasing artifacts, producing a constant gritty texture.

### Evidence

- Same Cartesia TTS audio played through Web Audio API (which uses sinc resampling) sounds crystal clear
- Same audio played through AN.Audio sounds "dirty, gritty, poppy"
- The only difference is the resampling stage

## Current Implementation

```csharp
// AudioFormatConverter.cs, lines 130-158 (resampling path)
while (dstFrame < maxDstFrames)
{
    int srcIdx = (int)_resampleFrac;
    double frac = _resampleFrac - srcIdx;

    // Only TWO source samples used — this is the problem
    float s0 = ReadSampleAsFloat(srcBuf, srcIdx, srcCh, srcChannels, _consumerFormat);
    float s1 = ReadSampleAsFloat(srcBuf, srcIdx + 1, srcCh, srcChannels, _consumerFormat);
    float interpolated = (float)(s0 + (s1 - s0) * frac);  // Linear interp = poor low-pass
    ...
}
```

Linear interpolation is equivalent to convolving with a triangle function — its frequency response rolls off quickly and doesn't properly suppress frequencies above Nyquist/2, causing aliasing.

## Fix: Upgrade to Windowed-Sinc Interpolation (In-Place)

Replace the two-point linear interpolation with a **windowed-sinc** filter. This is the industry standard for high-quality sample rate conversion. The upgrade is internal to `AudioFormatConverter` — no public API changes.

### Approach

This is a **drop-in algorithm upgrade**. The existing `AudioFormatConverter` architecture stays the same:
- `FillDeviceBuffer()` unchanged
- Passthrough fast-path unchanged
- Channel mapping unchanged
- Format conversion (Int16↔Float32) unchanged
- Only the resampling inner loop changes

### Design

**Filter parameters:**
- Kernel half-width: **8 taps** (16-point sinc). Excellent quality for speech/music at minimal CPU cost.
- Window function: **Kaiser window** (β ≈ 6.0) — good stopband attenuation (~60dB)
- Pre-computed lookup table: sinc×window values for 256 sub-positions between each integer sample

**Algorithm (per output sample):**
```
1. Determine fractional position in source: pos = outputIdx * (srcRate / dstRate)
2. Integer part = center of the filter kernel
3. Fractional part selects which pre-computed sub-filter to use
4. Convolve 16 source samples (center ± 8) with the windowed-sinc coefficients
5. Sum = output sample
```

**Ring buffer for cross-boundary samples:**
The sinc kernel needs ±8 samples around each interpolation center. When a callback boundary falls mid-kernel, the last ~8 samples from the previous call must be available. A ring buffer (size = kernel half-width × channels) stores these between calls.

### Implementation Plan

1. **New file: `Internal/SincResampler.cs`**
   - Pre-computes the Kaiser-windowed sinc lookup table in constructor
   - Maintains a ring buffer of recent source samples (size = 16 frames × channels)
   - Method: `int Process(ReadOnlySpan<float> src, int srcFrames, Span<float> dst, int maxDstFrames, int channels)`
   - Tracks fractional position state between calls (for streaming)
   - `Reset()` method clears ring buffer and fractional state

2. **Modify `AudioFormatConverter.cs`**
   - Add a `SincResampler` field, instantiate in constructor
   - Replace the inline linear-interp loop (lines 130–158) with a call to `SincResampler.Process()`
   - Update `EstimateConsumerFrames()`: change `+2` margin to `+8` (kernel half-width)
   - Update `Reset()` to also reset the `SincResampler`
   - Update `UpdateDeviceFormat()` to recreate/reset the `SincResampler`

3. **Edge cases:**
   - First call: ring buffer is zero (produces clean fade-in, inaudible)
   - Device switch / `Reset()`: clears ring buffer
   - Consumer callback asked for ~8 extra frames per fill (acceptable overhead)

### Performance Budget

At 48000Hz stereo output:
- 48000 output samples/sec × 16 taps × 2 channels = 1.5M multiply-adds/sec
- This is trivial for any modern CPU (< 0.1% of one core)
- The existing linear interp already does per-sample work; this increases from 1 multiply to 16 per sample

### Filter Table Pre-computation

```csharp
const int KernelHalfWidth = 8;      // 8 taps each side = 16-point filter
const int SubPositions = 256;        // fractional resolution
const double KaiserBeta = 6.0;

float[,] table = new float[SubPositions, KernelHalfWidth * 2];

for (int sub = 0; sub < SubPositions; sub++)
{
    double frac = sub / (double)SubPositions;
    for (int tap = -KernelHalfWidth; tap < KernelHalfWidth; tap++)
    {
        double x = tap - frac;
        double sinc = (x == 0) ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
        double kaiser = KaiserWindow(x / KernelHalfWidth, KaiserBeta);
        table[sub, tap + KernelHalfWidth] = (float)(sinc * kaiser);
    }
}
```

### Testing

- Unit test: resample a known sine wave 44100→48000, verify output frequency and amplitude are preserved
- Unit test: resample white noise, verify no spectral content above new Nyquist
- A/B listening test: compare linear vs sinc on Cartesia TTS output

## Files Changed

| File | Change |
|------|--------|
| `src/AN.Audio/Internal/SincResampler.cs` | **New** — sinc filter table, ring buffer, convolution loop |
| `src/AN.Audio/Internal/AudioFormatConverter.cs` | Replace linear interp with `SincResampler` call |

## Alternative Quick Fix (for testing theory)

Change Cartesia to request `sample_rate: 48000` and update `AudioStreamPlayer.SourceSampleRate` to 48000. If the WASAPI device is 48000Hz, the `AudioFormatConverter` will hit its passthrough path (no resampling at all) and the grittiness should disappear completely.

This confirms the resampler is the culprit but doesn't fix it properly — it just avoids it for one specific device rate.