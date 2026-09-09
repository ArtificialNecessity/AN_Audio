namespace AN.Audio;

/// <summary>How aggressively the backend asks the OS for a small callback period (spec 60 D1). The default is today's behaviour, unchanged.</summary>
public enum AudioOutput_LatencyMode
{
    /// <summary>The OS default shared path with <see cref="AudioOutputOptions.BufferSizeMs"/> of buffering (Windows: ~10 ms engine period + buffer).</summary>
    Default = 0,
    /// <summary>Ask the OS for its MINIMUM shared-mode period (Windows: <c>IAudioClient3::InitializeSharedAudioStream</c>; macOS: AUHAL min buffer;
    /// Linux: hw_params min period + SCHED_FIFO). <see cref="AudioOutputOptions.BufferSizeMs"/> is ignored. Falls back to <see cref="Default"/>
    /// when the OS refuses — see <see cref="IAudioOutput.LatencyModeActual"/> / <see cref="IAudioOutput.LatencyFallbackReason"/>.</summary>
    LowLatency = 1,
    /// <summary>Windows: WASAPI EXCLUSIVE mode, event-driven, at the driver's minimum device period (typically 2\u20133 ms) \u2014 the only path under 10 ms
    /// when the driver exposes no small SHARED period (Realtek UAD, NVIDIA HDMI, virtual devices all report a single 10 ms period). Costs: no other
    /// application can play through the endpoint while we hold it, and system effects are bypassed regardless of <see cref="AudioOutputOptions.Processing"/>.
    /// Falls back to <see cref="LowLatency"/>, then <see cref="Default"/>. macOS/Linux: treated as <see cref="LowLatency"/>.</summary>
    Exclusive = 2,
}

/// <summary>Why <see cref="IAudioOutput.LatencyModeActual"/> or <see cref="IAudioOutput.StreamProcessingActual"/> differs from what was requested (spec 60 D3).</summary>
public enum AudioOutput_LatencyFallbackReason
{
    None = 0,
    /// <summary>The OS lacks the interface (Windows &lt; 10 1607: no <c>IAudioClient3</c>).</summary>
    OsTooOld = 1,
    /// <summary>The driver/engine rejected the low-latency initialisation; the default path was used.</summary>
    DriverRefused = 2,
    /// <summary>Linux: <c>SCHED_FIFO</c> denied (no rtprio rights). The small period is still in effect; expect underruns under load.</summary>
    RealtimeRightsMissing = 3,
    /// <summary>Informational: another stream had already locked the engine to a different small period, which we adopted.</summary>
    EnginePeriodLocked = 4,
    /// <summary>RAW processing was requested but the endpoint does not support it (<c>AUDCLNT_E_RAW_MODE_UNSUPPORTED</c>); system effects stay on.</summary>
    RawModeUnsupported = 5,
    /// <summary>Exclusive mode refused: another application holds the endpoint, the user disabled exclusive access in Sound settings, or no exclusive-capable
    /// format was found (<c>AUDCLNT_E_DEVICE_IN_USE</c> / <c>AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED</c> / <c>AUDCLNT_E_UNSUPPORTED_FORMAT</c>). Fell back to shared.</summary>
    ExclusiveRefused = 6,
    Unknown = 99,
}

/// <summary>Whether the OS's per-endpoint effects chain (Windows APOs: loudness equalisation, "enhancements", spatial sound) processes our stream.</summary>
public enum AudioOutput_StreamProcessing
{
    /// <summary>Normal: the OEM/user effects apply, as for every other app.</summary>
    SystemEffects = 0,
    /// <summary>Windows <c>AUDCLNT_STREAMOPTIONS_RAW</c>: bypass the endpoint effects chain — lower processing latency, the sound is exactly what we
    /// rendered. The speakers may sound different from other apps. Not every endpoint supports it (falls back, see
    /// <see cref="AudioOutput_LatencyFallbackReason.RawModeUnsupported"/>). No effect on macOS/Linux today.</summary>
    Raw = 1,
}