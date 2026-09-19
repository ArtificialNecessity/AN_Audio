namespace AN.Audio.AppMeter.Internal;

/// <summary><see cref="AudioAppMeter_Capability.None"/> meter: reports the reason, meters nothing. Returned where no backend exists or a backend refused to initialise (D7).</summary>
internal sealed class NullAppMeter : IAudioAppMeter
{
    public NullAppMeter(AudioAppMeter_UnavailableReason reason) => UnavailableReason = reason;

    public AudioAppMeter_Capability Capability => AudioAppMeter_Capability.None;
    public AudioAppMeter_UnavailableReason UnavailableReason { get; }

    public int ReadSessions(Span<AudioAppMeter_Sample> into) => 0;
    public float ReadMasterPeak() => 0f;
    public string? LookupDisplayName(AudioAppMeter_SessionId sessionId) => null;

    public event Action? SessionsChanged { add { } remove { } }

    public void Dispose() { }
}