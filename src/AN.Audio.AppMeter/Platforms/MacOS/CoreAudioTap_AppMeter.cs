using AN.Audio.AppMeter.Internal;

namespace AN.Audio.AppMeter.Platforms.MacOS;

/// <summary>
/// Spec 80 Phase 3 STUB (approved 2026-09-19): macOS has no per-process meter without tapping PCM (process taps, 14.2+), so metering
/// IS capture there and is implemented once in spec 81. Until then: <see cref="AudioAppMeter_Capability.None"/> / <see cref="AudioAppMeter_UnavailableReason.NotImplemented"/>.
/// Design for the real thing is recorded in spec 80 §4 (macOS).
/// </summary>
internal static class CoreAudioTap_AppMeter
{
    public static AudioAppMeter_UnavailableReason ProbeReason => AudioAppMeter_UnavailableReason.NotImplemented;

    public static IAudioAppMeter Create(AudioAppMeter_Options options) => new NullAppMeter(ProbeReason);
}