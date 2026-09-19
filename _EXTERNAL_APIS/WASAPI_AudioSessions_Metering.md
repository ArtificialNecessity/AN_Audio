# WASAPI audio sessions + per-session metering (`audiopolicy.h`, `endpointvolume.h`)

Source read: Windows SDK `10.0.26100.0` `um\audiopolicy.h` and `um\endpointvolume.h` on disk (2026-09-19), vtable structs extracted
verbatim (`IAudioSessionManager2Vtbl` etc.). Used by spec 80 (`AN.Audio.AppMeter`). MMDevice pieces (`IMMDeviceEnumerator`, `IMMDevice::Activate`)
are already in `WasapiInterop.cs`.

## Object graph

```
CoCreateInstance(CLSID_MMDeviceEnumerator) → IMMDeviceEnumerator
  ::GetDefaultAudioEndpoint(eRender=0, eMultimedia=1) → IMMDevice            (eConsole=0 also fine; Windows treats them alike)
    ::Activate(IID_IAudioSessionManager2, CLSCTX_ALL, NULL) → IAudioSessionManager2
      ::GetSessionEnumerator → IAudioSessionEnumerator (a SNAPSHOT: re-fetch to see new sessions, or RegisterSessionNotification)
        ::GetSession(i) → IAudioSessionControl
          QI IID_IAudioSessionControl2 → GetProcessId / GetSessionInstanceIdentifier / GetState / IsSystemSoundsSession
          QI IID_IAudioMeterInformation → GetPeakValue            (per-SESSION meter; post session volume/mute)
    ::Activate(IID_IAudioMeterInformation, CLSCTX_ALL, NULL) → IAudioMeterInformation   (ENDPOINT master meter)
```

All of it is MTA-safe; call from any thread after `CoInitializeEx(NULL, COINIT_MULTITHREADED)`. Sessions belong to ONE endpoint: an app
playing to a non-default device is not in the default endpoint's enumerator (spec 80 D4 — enumerate every active render endpoint).

## IIDs

| interface | IID | header |
|---|---|---|
| `IAudioSessionManager2` | `77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F` | audiopolicy.h |
| `IAudioSessionEnumerator` | `E2F5BB11-0570-40CA-ACDD-3AA01277DEE8` | audiopolicy.h |
| `IAudioSessionControl` | `F4B1A599-7266-4319-A8CA-E70ACB11E8CD` | audiopolicy.h |
| `IAudioSessionControl2` | `bfb7ff88-7239-4fc9-8fa2-07c950be9c6d` | audiopolicy.h |
| `IAudioSessionNotification` | `641DD20B-4D41-49CC-ABA3-174B9477BB08` | audiopolicy.h |
| `IAudioSessionEvents` | `24918ACC-64B3-37C1-8CA9-74A66E9957A8` | audiopolicy.h |
| `IAudioMeterInformation` | `C02216F6-8C67-4B5B-9D00-D008E73E0064` | endpointvolume.h |

## Vtables (index after IUnknown 0,1,2; all return HRESULT unless noted)

**IAudioSessionManager2 : IAudioSessionManager : IUnknown**

| idx | method | signature |
|---|---|---|
| 3 | GetAudioSessionControl | `(LPCGUID sessionGuid, DWORD streamFlags, IAudioSessionControl**)` |
| 4 | GetSimpleAudioVolume | `(LPCGUID, DWORD, ISimpleAudioVolume**)` |
| 5 | **GetSessionEnumerator** | `(IAudioSessionEnumerator**)` |
| 6 | **RegisterSessionNotification** | `(IAudioSessionNotification*)` — NOTE: notifications only fire once `GetSessionEnumerator` has been called at least once after registering (documented quirk) |
| 7 | UnregisterSessionNotification | `(IAudioSessionNotification*)` |
| 8 | RegisterDuckNotification | `(LPCWSTR sessionID, IAudioVolumeDuckNotification*)` |
| 9 | UnregisterDuckNotification | `(IAudioVolumeDuckNotification*)` |

**IAudioSessionEnumerator**

| idx | method | signature |
|---|---|---|
| 3 | GetCount | `(int*)` |
| 4 | GetSession | `(int index, IAudioSessionControl**)` |

**IAudioSessionControl2 : IAudioSessionControl : IUnknown**

| idx | method | signature |
|---|---|---|
| 3 | GetState | `(AudioSessionState*)` — `AudioSessionStateInactive=0, Active=1, Expired=2` |
| 4 | GetDisplayName | `(LPWSTR*)` CoTaskMemFree; often empty |
| 5 | SetDisplayName | `(LPCWSTR, LPCGUID)` |
| 6 | GetIconPath | `(LPWSTR*)` |
| 7 | SetIconPath | `(LPCWSTR, LPCGUID)` |
| 8 | GetGroupingParam | `(GUID*)` |
| 9 | SetGroupingParam | `(LPCGUID, LPCGUID)` |
| 10 | RegisterAudioSessionNotification | `(IAudioSessionEvents*)` |
| 11 | UnregisterAudioSessionNotification | `(IAudioSessionEvents*)` |
| 12 | GetSessionIdentifier | `(LPWSTR*)` — same for all instances of one app+endpoint |
| 13 | **GetSessionInstanceIdentifier** | `(LPWSTR*)` — unique per session instance → our `AudioAppMeter_SessionId` |
| 14 | **GetProcessId** | `(DWORD*)` — 0 for cross-process sessions; `AUDCLNT_S_NO_SINGLE_PROCESS (0x0889000D)` is a SUCCESS code meaning "shared by several processes" |
| 15 | IsSystemSoundsSession | `()` returns `S_OK` (0) if system sounds, `S_FALSE` (1) otherwise |
| 16 | SetDuckingPreference | `(BOOL)` |

**IAudioSessionNotification** (WE implement this: one vtable with QI/AddRef/Release + slot 3)

| idx | method | signature |
|---|---|---|
| 3 | OnSessionCreated | `(IAudioSessionControl* newSession)` — fires on an MTA thread of the audio service |

**IAudioMeterInformation**

| idx | method | signature |
|---|---|---|
| 3 | **GetPeakValue** | `(float*)` — peak sample amplitude 0.0..1.0 since the previous call / over the last engine period (≈10 ms) |
| 4 | GetMeteringChannelCount | `(UINT*)` |
| 5 | GetChannelsPeakValues | `(UINT32 count, float*)` |
| 6 | QueryHardwareSupport | `(DWORD*)` |

**ISimpleAudioVolume** (`audioclient.h`, IID `87CE5498-68D6-44E5-9215-6DA47EF883D8`) — obtainable by QI on an `IAudioSessionControl`
(same as CSCore's `session.QueryInterface<SimpleAudioVolume>()`); used only for the `Muted` flag.

| idx | method | signature |
|---|---|---|
| 3 | SetMasterVolume | `(float, LPCGUID)` |
| 4 | GetMasterVolume | `(float*)` |
| 5 | SetMute | `(BOOL, LPCGUID)` |
| 6 | **GetMute** | `(BOOL*)` |

`IID_IUnknown` = `00000000-0000-0000-C000-000000000046` (our `IAudioSessionNotification` implementation must answer QI for it).

## Behaviour notes (measured / documented)

- The meter value is NOT accumulated: it reports the peak of the most recent engine period. Polling at 50–100 ms therefore SAMPLES the
  envelope; short transients between polls can be missed. Spec 80 accepts this (Monitor wants "did it ding in the last few seconds").
- `GetPeakValue` on a session is AFTER the session's own volume and mute (a muted app reads 0) but BEFORE the endpoint master volume.
- Sessions linger in `Inactive` for a while after a stream stops and become `Expired` when the last stream is released; `Expired`
  sessions still enumerate briefly. Filter `State == Active` for "producing audio now"; keep Inactive ones metered (they read 0).
- `GetProcessId` for the system-sounds session is the audiodg/explorer host; use `IsSystemSoundsSession` to tag it.
- The enumerator is a snapshot: either re-enumerate each poll (cheap, a few COM calls) or register `IAudioSessionNotification`.
  A re-enumeration every N polls PLUS the notification is what pavucontrol-class tools do; spec 80 D5 picks re-enumerate-on-notify + 5 s safety re-enumerate.

## Observed live (2026-09-19, Windows 11 26200, spec 80 Phase 1 smoke)

- **Every active render endpoint has its own system-sounds session**, and on this box `GetProcessId` returned pid 0 for it (not a host pid) while `IsSystemSoundsSession`
  returned `S_OK`. Its `GetDisplayName` is the resource reference `@%SystemRoot%\System32\AudioSrv.Dll,-202`, NOT resolved text.
- `GetDisplayName` was **empty for every ordinary application** (Spotify, Rainmeter, 3RVX, a PowerShell `Media.SoundPlayer`). Do not rely on it for names.
- `GetSessionInstanceIdentifier` strings are of the form `{0.0.0.00000000}.{endpoint-guid}|\Device\HarddiskVolumeN\...\app.exe%b{session-guid}|1%b<pid>` — the
  endpoint GUID is embedded, so the same app on two endpoints yields two distinct instance ids (confirming spec 80 D4's two-rows model).
- A new render stream showed up in the enumerator (after the `OnSessionCreated`-triggered re-enumerate) within one 100 ms poll of the app starting playback;
  its first `GetPeakValue` was 0.98.
- QI'ing `ISimpleAudioVolume` and `IAudioMeterInformation` directly from the `IAudioSessionControl` returned by the enumerator works (same object supports all of
  `IAudioSessionControl2`, `ISimpleAudioVolume`, `IAudioMeterInformation`) — no need for `IAudioSessionManager::GetSimpleAudioVolume`.