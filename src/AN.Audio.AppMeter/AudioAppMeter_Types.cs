namespace AN.Audio.AppMeter;

// Public vocabulary of spec 80 (per-application output level metering). Everything in AudioAppMeter_Sample is blittable:
// no strings on the read path (D6/D12 — names go through IAudioAppMeter.LookupDisplayName).

/// <summary>What the backend on this OS can do. Spec 81 adds <c>Capture = 2</c> as a flag.</summary>
public enum AudioAppMeter_Capability
{
    None = 0,
    Metering = 1,
}

/// <summary>Why <see cref="AudioAppMeter_Capability.None"/> — D7: failure is reported, not retried.</summary>
public enum AudioAppMeter_UnavailableReason
{
    None = 0,
    UnsupportedOS = 1,
    OSTooOld = 2,
    NoAudioServer = 3,
    PermissionDenied = 4,
    NotImplemented = 5,
}

/// <summary>D4: which render endpoints are metered.</summary>
public enum AudioAppMeter_Scope
{
    /// <summary>Every active render endpoint (a game on headphones AND Spotify on speakers). Default.</summary>
    AllRenderEndpoints = 0,
    /// <summary>Only the current default render endpoint — the cheap opt-out.</summary>
    DefaultRenderEndpointOnly = 1,
}

/// <summary>Lifecycle state the OS reports for a session. Pulse: Active when not corked, Inactive when corked.</summary>
public enum AudioAppMeter_SessionState
{
    Active = 0,
    Inactive = 1,
    Expired = 2,
}

[Flags]
public enum AudioAppMeter_SessionFlags
{
    None = 0,
    /// <summary>The OS "system sounds" session (Windows <c>IsSystemSoundsSession</c>). Its pid is the host process, not the ding's author.</summary>
    SystemSounds = 1,
    /// <summary><see cref="AudioAppMeter_Sample.ProcessId"/> is 0: the OS could not attribute the session to one process.</summary>
    Unattributed = 2,
    Muted = 4,
}

/// <summary>Stable for the life of one session instance (Win: FNV-1a 64 of <c>GetSessionInstanceIdentifier</c>; Pulse: sink-input index; mac: process object id).</summary>
public readonly record struct AudioAppMeter_SessionId(ulong Value);

/// <summary>The render endpoint the session lives on (Win: FNV-1a 64 of the MMDevice id). D11: lets D4's two-rows-per-process be told apart.</summary>
public readonly record struct AudioAppMeter_EndpointId(ulong Value);

/// <summary>OS process id; 0 when unattributed.</summary>
public readonly record struct AudioAppMeter_ProcessId(int Value)
{
    public bool IsUnknown => Value == 0;
}

/// <summary>Peak sample amplitude 0..1 — D3: the MAX observed since the caller's previous <c>ReadSessions</c>, not the instantaneous OS value.</summary>
public readonly record struct AudioAppMeter_Peak(float Value)
{
    public bool IsSilent => Value <= 0f;
    public static readonly AudioAppMeter_Peak Silent = new(0f);
}

/// <summary>One row of <see cref="IAudioAppMeter.ReadSessions"/>. Blittable.</summary>
public readonly record struct AudioAppMeter_Sample(
    AudioAppMeter_SessionId SessionId,
    AudioAppMeter_EndpointId EndpointId,
    AudioAppMeter_ProcessId ProcessId,
    AudioAppMeter_Peak Peak,
    AudioAppMeter_SessionState State,
    AudioAppMeter_SessionFlags Flags);

public sealed record AudioAppMeter_Options
{
    public AudioAppMeter_Scope Scope { get; init; } = AudioAppMeter_Scope.AllRenderEndpoints;

    /// <summary>D8: how often the backend's own poll thread samples the OS meters and folds into the per-session max (Windows). Default 100 ms.</summary>
    public int PollIntervalMs { get; init; } = 100;

    /// <summary>D5: unconditional session re-enumeration cadence (catches endpoint add/remove and notification quirks). Default 5000 ms.</summary>
    public int ReenumerateIntervalMs { get; init; } = 5000;

    /// <summary>Linux only: <c>PA_STREAM_PEAK_DETECT</c> sample rate (server-side reduction). Ignored elsewhere.</summary>
    public int PulsePeakRateHz { get; init; } = 20;
}

/// <summary>Thrown by <see cref="AudioAppMeter.Create"/> when <see cref="AudioAppMeter.Capability"/> is <see cref="AudioAppMeter_Capability.None"/>.</summary>
public sealed class AudioAppMeter_UnavailableException : PlatformNotSupportedException
{
    public AudioAppMeter_UnavailableReason Reason { get; }

    public AudioAppMeter_UnavailableException(AudioAppMeter_UnavailableReason reason, string message) : base(message)
    {
        Reason = reason;
    }
}