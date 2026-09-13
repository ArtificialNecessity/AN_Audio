using System.Runtime.InteropServices;

namespace AN.Audio.Platforms.Windows.Asio;

/// <summary>
/// Spec 70 §4 — the ONE ASIO-specific hot-path stage: take interleaved Float32 frames (what <c>AudioFormatConverter</c> produced) and write
/// ONE channel of them into a driver-owned planar buffer in that channel's own <see cref="Asio_SampleType"/>. Allocation-free; scaling
/// rules come from <see cref="AudioSampleConvert"/> (the one place PCM scaling lives).
/// </summary>
internal static unsafe class Asio_PlanarWriter
{
    /// <summary>Sample types this writer (and therefore the backend) can render. Everything else is refused at open (D7).</summary>
    public static bool IsSupported(Asio_SampleType type) => type is Asio_SampleType.ASIOSTInt16LSB or Asio_SampleType.ASIOSTInt24LSB
        or Asio_SampleType.ASIOSTInt32LSB or Asio_SampleType.ASIOSTFloat32LSB or Asio_SampleType.ASIOSTFloat64LSB;

    public static int BytesPerSample(Asio_SampleType type) => type switch
    {
        Asio_SampleType.ASIOSTInt16LSB => 2,
        Asio_SampleType.ASIOSTInt24LSB => 3,
        Asio_SampleType.ASIOSTInt32LSB or Asio_SampleType.ASIOSTFloat32LSB => 4,
        Asio_SampleType.ASIOSTFloat64LSB => 8,
        _ => throw new NotSupportedException($"ASIO sample type {type} is not supported by AN.Audio (supported: Int16/Int24/Int32/Float32/Float64 LSB)"),
    };

    /// <summary>The <see cref="SampleFormat"/> a consumer would need to hit this type without conversion (reported as <c>DeviceFormat</c>).</summary>
    public static SampleFormat ToSampleFormat(Asio_SampleType type) => type switch
    {
        Asio_SampleType.ASIOSTInt16LSB => SampleFormat.Int16,
        Asio_SampleType.ASIOSTInt24LSB => SampleFormat.Int24,
        Asio_SampleType.ASIOSTInt32LSB => SampleFormat.Int32,
        Asio_SampleType.ASIOSTFloat32LSB => SampleFormat.Float32,
        Asio_SampleType.ASIOSTFloat64LSB => SampleFormat.Float64,
        _ => throw new NotSupportedException(type.ToString()),
    };

    /// <summary>Write <paramref name="frames"/> samples of channel <paramref name="channel"/> from <paramref name="interleaved"/> (stride <paramref name="channels"/>)
    /// into <paramref name="dst"/> as <paramref name="type"/>. Frames beyond <paramref name="frames"/> up to <paramref name="bufferFrames"/> are zeroed.</summary>
    public static void WriteChannel(ReadOnlySpan<float> interleaved, int frames, int channels, int channel, Asio_SampleType type, byte* dst, int bufferFrames)
    {
        int bps = BytesPerSample(type);
        switch (type)
        {
            case Asio_SampleType.ASIOSTInt32LSB:
            {
                int* d = (int*)dst;
                for (int f = 0, s = channel; f < frames; f++, s += channels) d[f] = AudioSampleConvert.FloatToInt32(interleaved[s]);
                break;
            }
            case Asio_SampleType.ASIOSTInt16LSB:
            {
                short* d = (short*)dst;
                for (int f = 0, s = channel; f < frames; f++, s += channels) d[f] = AudioSampleConvert.FloatToInt16(interleaved[s]);
                break;
            }
            case Asio_SampleType.ASIOSTInt24LSB:
            {
                byte* d = dst;
                for (int f = 0, s = channel; f < frames; f++, s += channels, d += 3)
                {
                    int v = AudioSampleConvert.FloatToInt24(interleaved[s]);
                    d[0] = (byte)v; d[1] = (byte)(v >> 8); d[2] = (byte)(v >> 16);
                }
                break;
            }
            case Asio_SampleType.ASIOSTFloat32LSB:
            {
                float* d = (float*)dst;
                for (int f = 0, s = channel; f < frames; f++, s += channels) d[f] = interleaved[s];
                break;
            }
            case Asio_SampleType.ASIOSTFloat64LSB:
            {
                double* d = (double*)dst;
                for (int f = 0, s = channel; f < frames; f++, s += channels) d[f] = interleaved[s];
                break;
            }
            default: throw new NotSupportedException(type.ToString());
        }
        if (frames < bufferFrames) new Span<byte>(dst + frames * bps, (bufferFrames - frames) * bps).Clear();
    }

    /// <summary>Silence a whole channel buffer.</summary>
    public static void Clear(Asio_SampleType type, byte* dst, int bufferFrames) => new Span<byte>(dst, bufferFrames * BytesPerSample(type)).Clear();
}