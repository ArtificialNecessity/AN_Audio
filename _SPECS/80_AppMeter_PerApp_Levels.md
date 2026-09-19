# 80 — AN.Audio.AppMeter: per-application output level metering

- **Status:** BUILT Phase 0 + Phase 1 (Windows, live-validated) + Phase 3 stub (2026-09-19). Phase 2 (Linux) open. See §8 build-time decisions and §9 as-built notes.
- **Package:** `ArtificialNecessity.Audio.AppMeter` (`src/AN.Audio.AppMeter/`, namespace `AN.Audio.AppMeter`). Independent feature-area package per spec 00 packaging rule; no reference to `AN.Audio` or `Common` (needs no PCM type).
- **Consumers:** AN.Monitor (`C:\PROJECTS\AN_Monitor`) — an `audio` stat + "what dinged recently" sort. Future: MusicStudio level strip, Mirica "who is talking" indicator.
- **References:** `_EXTERNAL_APIS/WASAPI_AudioSessions_Metering.md` (Windows ground truth, SDK 10.0.26100.0); PulseAudio `stream.h` / "Writing Volume Control UIs" (freedesktop wiki); Apple `AudioHardware.h` process taps (macOS 14.2+). Sibling tier-2 spec: `81_AppCapture_PerApp_PCM.md` (TBD).

- [x] Phase 0 — project + public contract + `Capability.None` factory on every OS, layout/constant tests
- [x] Phase 1 — Windows WASAPI sessions backend (Monitor's need)
- [ ] Phase 2 — Linux libpulse backend (pipewire-pulse compatible)
- [x] Phase 3 — macOS: `Capability.None, Reason = NotImplemented` STUB (approved 2026-09-19); process-tap implementation deferred to spec 81 since on macOS metering IS capture

## 1. Goal

Answer, on every OS through one API shape: **which processes are emitting audio right now, and how loud (peak 0..1) is each one** — cheaply enough
to poll 10–20×/s forever from a resident monitor, without copying PCM where the OS offers a meter.

Non-goals here: capturing the PCM (tier 2, spec 81), controlling per-app volume/mute, input (microphone) metering.

## 2. What each OS offers (why the API is shaped the way it is)

| | Windows | Linux (PulseAudio protocol; PipeWire via `pipewire-pulse`) | macOS |
|---|---|---|---|
| Enumerate producers with pid | `IAudioSessionManager2::GetSessionEnumerator` → `IAudioSessionControl2::GetProcessId` | `pa_context_get_sink_input_info_list` → proplist `application.process.id` | `kAudioHardwarePropertyProcessObjectList` + `kAudioProcessPropertyPID` (14.0+) |
| Per-app meter WITHOUT PCM | ✅ `IAudioMeterInformation::GetPeakValue` per session | ✅ record stream on the sink's monitor source, `PA_STREAM_PEAK_DETECT` + `pa_stream_set_monitor_stream(sink_input_idx)`; server pushes one peak per period | ❌ none — must tap PCM (`CATapDescription` → `AudioHardwareCreateProcessTap` → tap-only aggregate device → IOProc), 14.2+, TCC "System Audio Recording" prompt, not sandboxed |
| Master meter | ✅ `IMMDevice::Activate(IID_IAudioMeterInformation)` | ✅ monitor source of the sink, no `set_monitor_stream` | same tap, exclusive-of-nothing |
| Producer arrival/departure events | `IAudioSessionNotification::OnSessionCreated` (+ state `Expired` on poll) | `pa_context_subscribe(PA_SUBSCRIPTION_MASK_SINK_INPUT)` | `kAudioHardwarePropertyProcessObjectList` listener |
| Value semantics | peak AFTER session volume/mute, BEFORE master volume | peak of the stream's PCM UNTOUCHED by hardware volume (docs) | raw tapped PCM |

**D1 — meter semantics are "what the app emits", not "what the user hears".** Master/hardware volume never enters a per-app value. Windows' session mute/volume does (a
muted app reads 0 there) and we document that as a platform note rather than fight it.

**D2 — metering and capture are one operation on macOS and two on Windows/Linux**, so the library exposes them as two interfaces (`IAudioAppMeter` here,
`IAudioAppCapture` in spec 81) and a macOS backend that implements the meter BY capturing. Consumers that only meter never pay for PCM on Windows/Linux.

## 3. Public contract

```csharp
namespace AN.Audio.AppMeter;

public static class AudioAppMeter
{
    public static bool IsAvailable { get; }                        // a backend exists AND reports Capability != None
    public static AudioAppMeter_Capability Capability { get; }     // cheap, no OS handles opened
    public static IAudioAppMeter Create(AudioAppMeter_Options? options = null);   // throws AudioAppMeter_UnavailableException when Capability == None
}

public enum AudioAppMeter_Capability { None = 0, Metering = 1 }   // spec 81 adds Capture = 2 as a flag
public enum AudioAppMeter_UnavailableReason { None, UnsupportedOS, OSTooOld, NoAudioServer, PermissionDenied, NotImplemented }

public sealed record AudioAppMeter_Options
{
    public AudioAppMeter_Scope Scope { get; init; } = AudioAppMeter_Scope.AllRenderEndpoints;   // or DefaultRenderEndpointOnly
    public int PulsePeakRateHz { get; init; } = 20;      // Linux only: PA_STREAM_PEAK_DETECT sample rate (server-side reduction). Ignored elsewhere.
}

public interface IAudioAppMeter : IDisposable
{
    AudioAppMeter_Capability Capability { get; }
    AudioAppMeter_UnavailableReason UnavailableReason { get; }

    /// Fills `into` with one row per live session and returns the row count (or the needed count if `into` is too small; nothing written).
    /// Control plane: may allocate, may call the OS. Never blocks on audio I/O. Safe from any thread; calls are serialised internally.
    int ReadSessions(Span<AudioAppMeter_Sample> into);

    /// Master peak (0..1) of the default render endpoint (Windows/macOS) or default sink (Pulse). 0 when unknown.
    float ReadMasterPeak();

    /// Raised (any thread — consumer marshals, I5) when a session appears or disappears. Coalesced; never carries data.
    event Action? SessionsChanged;
}

/// One row of ReadSessions. Blittable; no strings on the hot-ish path (names are fetched via LookupDisplayName on demand).
public readonly record struct AudioAppMeter_Sample(
    AudioAppMeter_SessionId SessionId,    // stable for the life of the session (Win: SessionInstanceIdentifier hash; Pulse: sink_input index; mac: process object id)
    AudioAppMeter_ProcessId ProcessId,    // 0 when the OS cannot attribute (Windows AUDCLNT_S_NO_SINGLE_PROCESS, Pulse stream without the proplist key)
    AudioAppMeter_Peak Peak,              // 0..1 peak since the previous ReadSessions for THIS session (see D3)
    AudioAppMeter_SessionState State,     // Active | Inactive | Expired (Pulse: Active when not corked, Inactive when corked)
    AudioAppMeter_SessionFlags Flags);    // SystemSounds | Unattributed | Muted

public readonly record struct AudioAppMeter_SessionId(ulong Value);
public readonly record struct AudioAppMeter_ProcessId(int Value);
public readonly record struct AudioAppMeter_Peak(float Value) { public bool IsSilent => Value <= 0f; }
public enum AudioAppMeter_SessionState { Active, Inactive, Expired }
[Flags] public enum AudioAppMeter_SessionFlags { None = 0, SystemSounds = 1, Unattributed = 2, Muted = 4 }
```

**D3 — `Peak` is the MAX observed since the caller's previous `ReadSessions`**, not the instantaneous OS value. The backend runs its own
poll thread (Windows: 50 ms `GetPeakValue` per session; Pulse: server pushes at `PulsePeakRateHz`) and folds into a per-session max that
`ReadSessions` reads-and-resets. Rationale: Windows' meter is the last ~10 ms engine period; a consumer polling at 2 s (Monitor's tick)
would miss every ding. The library owns the fast loop so the consumer can poll at any cadence and still see transients. Poll thread is
control plane (may allocate), one per meter instance, `ThreadPriority.BelowNormal`, stops on Dispose.

**D4 — scope = all active render endpoints by default.** A game on headphones and Spotify on speakers are both "apps making sound". Windows:
`EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE)` → one `IAudioSessionManager2` per endpoint; a process with sessions on two endpoints appears
twice (distinct `SessionId`, same `ProcessId`) — consumers aggregate by pid. Pulse: every sink's monitor source. `DefaultRenderEndpointOnly` is the cheap opt-out.

**D5 — session-list refresh = event + safety re-enumerate.** Windows: `IAudioSessionNotification` (we implement the COM vtable, rule 4:
`[UnmanagedCallersOnly]` statics + GCHandle) triggers a re-enumerate on the poll thread; PLUS an unconditional re-enumerate every 5 s (endpoint
added/removed, notification quirks). Sessions seen `Expired` on two consecutive polls are dropped and `SessionsChanged` fires. Pulse: subscribe
`SINK_INPUT` new/remove; the safety re-list is the same 5 s.

**D6 — names are not in the sample.** `string? LookupDisplayName(AudioAppMeter_SessionId)` is a separate, allocating call (Windows `GetDisplayName`
is usually empty anyway; consumers already know the pid → exe). Keeps `AudioAppMeter_Sample` blittable.

**D7 — failure is reported, not retried (rule 8).** Backend init failure → `Capability.None` + reason; a per-endpoint COM error mid-run drops
that endpoint's sessions and re-tries only at the next 5 s re-enumerate. A `PermissionDenied` (mac, future) is terminal for the instance.

## 4. Platform notes

### Windows (`Platforms/Windows/Wasapi_AppMeter.cs`, `Wasapi_AppMeterInterop.cs`)

- Interop is a COPY of the ~40 lines of MMDevice consts/wrappers from `AN.Audio/WasapiInterop.cs` (packaging rule: no `AN.Audio` reference) PLUS the
  session/meter vtables from `_EXTERNAL_APIS/WASAPI_AudioSessions_Metering.md`: `IAudioSessionManager2` (5,6,7), `IAudioSessionEnumerator` (3,4),
  `IAudioSessionControl2` (3,13,14,15), `IAudioMeterInformation` (3). Every vtable index and IID is a named const; a layout test asserts the IIDs against the header text.
- `CoInitializeEx(MTA)` on the poll thread. All session objects are cached (AddRef'd) between polls; re-enumeration releases the stale set.
- `GetProcessId` returning `AUDCLNT_S_NO_SINGLE_PROCESS` (0x0889000D, a SUCCESS HRESULT) → `ProcessId 0` + `Unattributed`.
- `IsSystemSoundsSession == S_OK` → `SystemSounds` flag (Monitor will group it as "System").
- `SessionId` = FNV-1a 64 of `GetSessionInstanceIdentifier` (stable, unique per instance; the string itself is only kept for `LookupDisplayName`).
- Master peak: `IMMDevice::Activate(IID_IAudioMeterInformation)` on the default endpoint, re-activated on `IMMNotificationClient::OnDefaultDeviceChanged` (copy the small callback vtable too) or on the 5 s re-enumerate.

### Linux (`Platforms/Linux/Pulse_AppMeter.cs`, `PulseInterop.cs`) — Phase 2

- `libpulse.so.0` only (`pa_threaded_mainloop_*`, `pa_context_*`, `pa_stream_*`); PipeWire desktops satisfy it through `pipewire-pulse`. No libpulse → `NoAudioServer`.
- One record stream per sink-input: `pa_stream_new(ctx, name, {PA_SAMPLE_FLOAT32LE, 1 ch, rate = PulsePeakRateHz})`, `pa_stream_set_monitor_stream(idx)`,
  `pa_stream_connect_record(monitor_source_name, attr{fragsize = 4, maxlength = -1}, PA_STREAM_PEAK_DETECT | PA_STREAM_DONT_MOVE | PA_STREAM_ADJUST_LATENCY)`.
  Read callback: `pa_stream_peek` → fold `abs(sample)` into the session max → `pa_stream_drop`. Stream torn down on `SINK_INPUT_REMOVE` or when the input moves sinks (recreate against the new monitor).
- pid from proplist `application.process.id` (string → int); absent → `Unattributed`. `corked` → `Inactive`; `mute` → `Muted`. `SessionId` = sink-input index.
- All callbacks run on the PA mainloop thread; they only touch a lock-free per-session float slot. `ReadSessions` reads-and-resets under the meter's own lock.

### macOS (`Platforms/MacOS/CoreAudioTap_AppMeter.cs`) — Phase 3 STUB

- Phase 3 ships `Capability.None, UnavailableReason.NotImplemented`. Design recorded so spec 81 can implement once and serve both interfaces:
  `kAudioHardwarePropertyProcessObjectList` → per process `CATapDescription(initStereoMixdownOfProcesses:[pobj])` via `objc_msgSend` → `AudioHardwareCreateProcessTap`
  → one PRIVATE tap-only aggregate (`kAudioAggregateDeviceTapListKey`, no sub-devices — avoids the HFP/idle-clock stall) → IOProc computes peak (`Internal/PeakFromPcm.cs`).
  Below 14.2 → `OSTooOld`; tap creation failing with `kAudioHardwareIllegalOperationError` → `PermissionDenied`. Chromium's three stall listeners + a 2 s buffer-arrival watchdog are required, not optional.

## 5. Repository additions

```
src/AN.Audio.AppMeter/
├── AN.Audio.AppMeter.csproj          TFMs net8.0;net9.0;net10.0, AllowUnsafeBlocks (interop), IsPackable, PackageId ArtificialNecessity.Audio.AppMeter
├── AudioAppMeter.cs                  factory (OS switch → backend; Capability probe without opening handles)
├── IAudioAppMeter.cs
├── AudioAppMeter_Types.cs            Sample, SessionId, ProcessId, Peak, SessionState, SessionFlags, Capability, UnavailableReason, Options, Scope, UnavailableException
├── Internal/AppMeter_SessionTable.cs platform-neutral: id → (max-peak slot, state, flags); read-and-reset; expiry bookkeeping (D3/D5) — unit-tested without hardware
├── Internal/PeakFromPcm.cs           (Phase 3/spec 81) abs-max over Float32/Int16 spans, zero-alloc
└── Platforms/{Windows,Linux,MacOS}/  one backend each + *Interop.cs, every native constant a named const/enum (rule 1)
tests/AN.Audio.AppMeter.Tests/       SessionTable semantics (max-since-read, reset, expiry), Windows IID/vtable constants vs header text (self-skip off-Windows), factory Capability per OS
tests/SimpleAppMeterTest/            console smoke: prints top-N sessions by peak every 250 ms (manual)
```

`cmd/publish-local.cs` and `nuget-publish-audio.cs` pick the new project up (they pack the solution). `AN.Audio.Build.props` unchanged.

## 6. Phases

### Phase 0 — contract
- [x] Project, csproj, slnx entry, test project
- [x] All public types above; `AudioAppMeter.Create` THROWS `AudioAppMeter_UnavailableException` where the OS has no backend (per §3); `AudioAppMeter.CreateOrNull` returns the `NullAppMeter` instead. A backend that fails bring-up returns a `NullAppMeter` from `Create` (D7).
- [x] `AppMeter_SessionTable` + tests: max-since-read, reset on read, two-strike expiry, `SessionsChanged` coalescing (10 tests)
- [x] `00_AN_Audio_Overview.md` feature matrix row + `_PROJECT_STRUCTURE.md` entry

### Phase 1 — Windows
- [x] `Wasapi_AppMeterInterop.cs` (consts, IIDs, typed vtable wrappers, our `IAudioSessionNotification` vtable; `IMMNotificationClient` dropped — D9)
- [x] Poll thread: MTA init, per-endpoint managers (D4), 100 ms peak fold (D8), 5 s re-enumerate, notification-triggered re-enumerate (D5)
- [x] `ReadSessions` / `ReadMasterPeak` / `LookupDisplayName`
- [x] Constant test vs `audiopolicy.h`/`endpointvolume.h` text; `SimpleAppMeterTest` live: 5 endpoints, 8 idle sessions, a WAV player process seen at 0.98 peak within one poll, Rainmeter visualizer at half gain, `SessionsChanged` fired on arrival (§9)
- [ ] `publish-local` → Monitor consumes (Monitor spec `05_AN_Monitor_Audio_Stat.md`)

### Phase 2 — Linux
- [ ] `PulseInterop.cs` (threaded mainloop, context, subscribe, sink-input list, monitor record streams)
- [ ] Backend per §4; validated on the Linux box against PipeWire (`pipewire-pulse`) and, if available, real PulseAudio

### Phase 3 — macOS stub
- [x] `CoreAudioTap_AppMeter.cs` returns `NotImplemented`; spec 81 still to be opened with the §4 design

## 7. Open questions

- ~~Windows: should `Inactive` sessions be included in `ReadSessions` at all~~ → **D10: include**, consumer filters by `State`.
- Pulse: `PA_STREAM_PEAK_DETECT` at 20 Hz gives the max over each 50 ms window server-side — confirm the sample is a true window peak (docs imply) and not a decimated sample.
- Whether `AudioAppMeter_Peak` should also carry an RMS/loudness estimate later (mac tap could compute it; Windows/Pulse cannot without PCM) — not now; would break the "same shape everywhere" invariant unless optional.

## 8. Decisions taken at build time (2026-09-19)

The purpose of this package is one question: **which process on this machine is making sound right now** (the 2017 `SoundLevelMonitor`
`AudioLevelMonitor.cs` brought into AN.Audio so AN.Monitor can answer "what is dinging"). Everything below serves that.

- **D8 — poll interval is an option, default 100 ms.** `AudioAppMeter_Options.PollIntervalMs = 100` (the old monitor used 50). Windows only; Pulse uses `PulsePeakRateHz`.
- **D9 — no default-device tracking.** The `IMMNotificationClient` vtable in §4 is NOT built. `ReadMasterPeak` activates `IAudioMeterInformation` on the
  default render endpoint and re-resolves it only at the 5 s re-enumerate. Output-device changes are not this package's concern; sessions on every
  active endpoint are already covered by D4.
- **D10 — `Inactive` sessions are included** (§7 resolved).
- **D11 — `AudioAppMeter_Sample` carries `AudioAppMeter_EndpointId`** (FNV-1a 64 of the MMDevice id string) so the two rows a process gets under D4 are
  distinguishable; still blittable.
- **D12 — `LookupDisplayName` returns the raw WASAPI `GetDisplayName` string (often empty), nothing else.** pid → exe / window title is the consumer's job.
  It is part of `IAudioAppMeter` (the §3 listing omitted it).
- **D13 — `AudioAppMeter_Scope { AllRenderEndpoints, DefaultRenderEndpointOnly }`** declared (referenced but missing from §3).
- **Build order this pass:** Phase 0 + Phase 1 (Windows) + Phase 3 (mac stub). Phase 2 (Linux libpulse) deferred — cannot be validated on this box.

## 9. As built (2026-09-19) — deviations from §3/§4 and things learned live

| # | Spec said | Built | Why |
|---|---|---|---|
| A1 | `Create` returns a `NullAppMeter` on every OS (Phase 0 checklist) | `Create` throws `AudioAppMeter_UnavailableException(Reason)` where `Capability == None` (as §3 says); `CreateOrNull` gives the null meter; a Windows bring-up failure (no `CoCreateInstance`) returns a null meter carrying `NoAudioServer` | §3 and the checklist disagreed; §3 wins, and Monitor gets a non-throwing path |
| A2 | `LookupDisplayName` calls the OS on demand | Display names are read ONCE per session at enumerate time on the poll thread and cached; lookup is a dictionary read | Keeps every COM call on the one MTA thread; `GetDisplayName` never changes for a session |
| A3 | `ReadMasterPeak` = instantaneous | D3 semantics like sessions: poll thread folds `GetPeakValue` of the default endpoint into a max; `ReadMasterPeak` reads-and-resets | Same reason as A2 (no cross-thread COM) and consistent with the per-app rows |
| A4 | `IMMNotificationClient` for default-device change | not built (D9) | out of scope for "which app is dinging" |
| A5 | 50 ms poll | `PollIntervalMs` option, default 100 ms (D8) | |
| A6 | peak read for every session | `GetPeakValue` only when `GetState == Active`; Inactive/Expired rows are observed with peak 0 | saves the call on the dozens of idle sessions a busy box has |
| A7 | — | Cached session objects are released as soon as the OS reports `Expired` (or a `GetState` call fails); the table then strikes the row out as absent | Expired never comes back |
| A8 | `Muted` flag | via `ISimpleAudioVolume::GetMute`, QI'd from the `IAudioSessionControl` (as CSCore does); `ISimpleAudioVolume` added to the API notes | the session IID table in the notes lacked it |
| A9 | `SessionsChanged` "any thread" | fires on the poll thread, inside the poll, exceptions from the consumer swallowed | |
| A10 | `IAudioSessionNotification` "UnmanagedCallersOnly + GCHandle" | native object = `{ vtbl, refcount, ownerGCHandle }` in `NativeMemory`; static vtable built once; owner slot is a WEAK handle zeroed on Dispose so a late `OnSessionCreated` from the audio service is a no-op; memory freed when the COM refcount reaches 0 (service may still hold it briefly after `Unregister`) | rule 4; do not free memory the service may still call |
| A11 | — | Re-enumerate is a full teardown + rebuild of the cached graph (release every session, unregister + release every manager, re-`Activate`). Table rows keyed by id survive; nothing flickers | simpler than diffing, a few dozen COM calls every 5 s |

**Live observations (this dev box, Windows 11 26200, 5 active render endpoints):** every endpoint carries one `SystemSounds` session (pid 0 → `Unattributed`, display name
`@%SystemRoot%\System32\AudioSrv.Dll,-202` — a resource string, so `LookupDisplayName` IS non-empty for system sounds); Spotify/3RVX idle as `Inactive`; Rainmeter's
visualizer session is permanently `Active` and mirrors whatever plays at ≈½ gain; a `Media.SoundPlayer` process appeared as a new session within one 100 ms poll at
peak 0.98 and `SessionsChanged` fired once. `GetDisplayName` for ordinary apps was empty in every case (as predicted) — consumers must map pid → exe themselves.

**Open for Monitor:** the same process on N endpoints = N rows (D4); aggregate by `ProcessId`, and treat `SystemSounds` rows as one "System" line.