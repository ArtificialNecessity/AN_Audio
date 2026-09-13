namespace AN.Audio;

/// <summary>Which OS audio stack carries the stream (spec 70 D2). Windows only; macOS/Linux ignore it.</summary>
public enum AudioOutput_Backend
{
    /// <summary>WASAPI, unless the resolved device id is an ASIO id (<c>asio:{CLSID}</c>, see <see cref="AudioOutput.GetDeviceManager(AudioOutput_Backend)"/>).
    /// <c>Auto</c> never picks ASIO on its own (spec 70 §8: it would silently change the device's global sample rate).</summary>
    Auto = 0,
    /// <summary>WASAPI shared/exclusive per <see cref="AudioOutputOptions.Latency"/> (spec 60).</summary>
    Wasapi = 1,
    /// <summary>The user's installed ASIO driver (spec 70). No driver registered → falls back to WASAPI with
    /// <see cref="AudioOutput_LatencyFallbackReason.BackendUnavailable"/>.</summary>
    Asio = 2,
}

/// <summary>A native top-level window handle (Windows HWND) a consumer may lend as dialog owner for driver control panels (spec 70 D4).</summary>
public readonly record struct AudioOutput_NativeWindowHandle(nint Value);

/// <summary>Zero-based hardware output channel index on an ASIO driver (spec 70 D8: `Out 1` is index 0).</summary>
public readonly record struct Asio_ChannelIndex(int Value);

/// <summary>Spec 70 D6 (amended 2026-09-13): what to do when the consumer's sample rate differs from the ASIO device's current rate.</summary>
public enum Asio_SampleRatePolicy
{
    /// <summary>Default. Leave the device's clock alone and resample in AN.Audio (sinc). Reported as <see cref="AudioOutput_LatencyFallbackReason.DriverRateAdopted"/>.
    /// Measured on the MOTU M4: switching the device rate mutes its outputs for 1–2 s while the clock relocks — the head of the stream is lost.</summary>
    AdoptDriverRate = 0,
    /// <summary>Ask the driver to switch to the consumer's rate (<c>setSampleRate</c>). Affects every application using the device; expect a
    /// relock mute at start. Falls back to <see cref="AdoptDriverRate"/> if the driver refuses (external clock, unsupported rate).</summary>
    SetDeviceRate = 1,
}