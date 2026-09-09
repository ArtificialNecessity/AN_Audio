# WASAPI `IAudioClient3` low-latency shared streams + MMCSS (`avrt.dll`)

Source read: `audioclient.h` (mingw-w64 mirror, WIDL-generated from `audioclient.idl`, fetched 2026-09-09) — the C vtable structs are the ground truth
for our manual dispatch. MMCSS from `avrt.h` (Windows SDK). Microsoft's "Low Latency Audio" driver doc (learn.microsoft.com/windows-hardware/drivers/audio/low-latency-audio) for behaviour.

## Interface chain and vtable indices (after IUnknown 0,1,2)

`IAudioClient3 : IAudioClient2 : IAudioClient : IUnknown` — a single object; QI/Activate with the IID you need.

| idx | interface | method | signature |
|---|---|---|---|
| 3 | IAudioClient | Initialize | `(AUDCLNT_SHAREMODE, DWORD flags, REFERENCE_TIME buf, REFERENCE_TIME period, const WAVEFORMATEX*, LPCGUID)` |
| 4 | | GetBufferSize | `(UINT32*)` |
| 5 | | GetStreamLatency | `(REFERENCE_TIME*)` |
| 6 | | GetCurrentPadding | `(UINT32*)` |
| 7 | | IsFormatSupported | `(AUDCLNT_SHAREMODE, const WAVEFORMATEX*, WAVEFORMATEX**)` |
| 8 | | GetMixFormat | `(WAVEFORMATEX**)` |
| 9 | | GetDevicePeriod | `(REFERENCE_TIME* default, REFERENCE_TIME* minimum)` |
| 10 | | Start | |
| 11 | | Stop | |
| 12 | | Reset | |
| 13 | | SetEventHandle | `(HANDLE)` |
| 14 | | GetService | `(REFIID, void**)` |
| 15 | IAudioClient2 | IsOffloadCapable | `(AUDIO_STREAM_CATEGORY, BOOL*)` |
| 16 | | SetClientProperties | `(const AudioClientProperties*)` |
| 17 | | GetBufferSizeLimits | `(const WAVEFORMATEX*, BOOL eventDriven, REFERENCE_TIME* min, REFERENCE_TIME* max)` |
| 18 | IAudioClient3 | **GetSharedModeEnginePeriod** | `(const WAVEFORMATEX*, UINT32* default, UINT32* fundamental, UINT32* min, UINT32* max)` — all in FRAMES |
| 19 | | **GetCurrentSharedModeEnginePeriod** | `(WAVEFORMATEX** ppFormat /*CoTaskMemFree*/, UINT32* currentPeriodFrames)` |
| 20 | | **InitializeSharedAudioStream** | `(DWORD flags, UINT32 periodFrames, const WAVEFORMATEX*, LPCGUID session)` |

IIDs:
- `IID_IAudioClient  = 1cb9ad4c-dbfa-4c32-b178-c2f568a703b2`
- `IID_IAudioClient2 = 726778cd-f60a-4eda-82de-e47610cd78aa`
- `IID_IAudioClient3 = 7ed4ee07-8e67-4cd4-8c1a-2b7a5987ad42` (Windows 10 1607+; `IMMDevice::Activate` with this IID fails with `E_NOINTERFACE` on older systems → fall back to `IAudioClient`)

## Rules for `InitializeSharedAudioStream` (from the doc + header)

- `PeriodInFrames` must be an integral multiple of `fundamental` and within `[min, max]` from `GetSharedModeEnginePeriod(sameFormat)`; otherwise
  `AUDCLNT_E_INVALID_DEVICE_PERIOD` (`0x88890020`). No buffer-size parameter: the buffer is derived from the period.
- `AUDCLNT_E_ENGINE_PERIODICITY_LOCKED` (`0x88890028`): another stream already runs the engine at a different small period → call
  `GetCurrentSharedModeEnginePeriod` and initialise with THAT value. `AUDCLNT_E_ENGINE_FORMAT_LOCKED` (`0x88890029`) likewise for the format.
- Behavioural: by default every app uses 10 ms; when one app asks for a small period the engine switches ALL shared streams on that endpoint to
  it and switches back when that app exits. Typical modern driver (WaveRT): min 48–128 frames @ 48 kHz (1–2.7 ms), fundamental 4–48, default 448–480.
- Use `AUDCLNT_STREAMFLAGS_EVENTCALLBACK` (0x00040000) + `SetEventHandle` exactly as with `Initialize`; `GetBufferSize`/`GetCurrentPadding` still apply.
- `AUDCLNT_ERR(n) = MAKE_HRESULT(1, FACILITY_AUDCLNT=0x889, n)` → `0x8889_00nn`.

## MMCSS (`avrt.dll`)

```c
HANDLE AvSetMmThreadCharacteristicsW(LPCWSTR TaskName, LPDWORD TaskIndex);   // TaskName "Pro Audio" | "Audio" | "Games" … (HKLM\...\Multimedia\SystemProfile\Tasks); *TaskIndex = 0 on first call
BOOL   AvRevertMmThreadCharacteristics(HANDLE AvrtHandle);
BOOL   AvSetMmThreadPriority(HANDLE AvrtHandle, AVRT_PRIORITY Priority);      // AVRT_PRIORITY_LOW=-1, NORMAL=0, HIGH=1, CRITICAL=2
```
Returns NULL on failure (`GetLastError`). Call on the thread that pumps the WASAPI event; revert before the thread exits. "Pro Audio" gives the
thread a real-time-class priority (~26) managed by the scheduler service, which is what the Microsoft low-latency doc recommends for WASAPI clients
(alongside, or instead of, the RT Work Queue API).