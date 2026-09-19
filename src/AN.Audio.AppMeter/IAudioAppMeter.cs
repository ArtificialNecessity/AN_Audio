namespace AN.Audio.AppMeter;

/// <summary>
/// Per-application output level meter (spec 80): which processes are emitting audio right now, and how loud (peak 0..1) each one is.
/// The backend owns a fast poll loop (D3) so the consumer may read at any cadence and still see transients.
/// </summary>
public interface IAudioAppMeter : IDisposable
{
    AudioAppMeter_Capability Capability { get; }
    AudioAppMeter_UnavailableReason UnavailableReason { get; }

    /// <summary>
    /// Fills <paramref name="into"/> with one row per live session and returns the row count — or, if <paramref name="into"/> is too small,
    /// the needed count with nothing written. Each row's <c>Peak</c> is the max since the previous call (D3) and is reset by this call.
    /// Control plane: may allocate, may call the OS. Never blocks on audio I/O. Safe from any thread; calls are serialised internally.
    /// </summary>
    int ReadSessions(Span<AudioAppMeter_Sample> into);

    /// <summary>Master peak (0..1) of the default render endpoint (Windows/macOS) or default sink (Pulse). 0 when unknown.</summary>
    float ReadMasterPeak();

    /// <summary>D6/D12: the OS display name for a session, raw (Windows <c>GetDisplayName</c>, usually empty). Allocates. Null when the session is gone.</summary>
    string? LookupDisplayName(AudioAppMeter_SessionId sessionId);

    /// <summary>Raised on a background thread (consumer marshals, I5) when a session appears or disappears. Coalesced; carries no data.</summary>
    event Action? SessionsChanged;
}