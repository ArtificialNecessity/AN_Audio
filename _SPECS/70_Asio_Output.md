# 70 — ASIO output backend (Windows)

- **Status:** Phases 1–2 **BUILT 2026-09-13**, hardware-validated on the MOTU M4 (§7.2). Phases 3–4 pending. Depends on `71_CHeader_Bindings_Autogen.md` (BUILT) for every struct/enum/vtable declaration.
- **Parent:** `00_AN_Audio_Overview.md`; extends `60_Low_Latency_Output.md` (whose §1 listed ASIO as a non-goal — amended by D1 below);
  device semantics from `20_Audio_Device_Management.md`.
- **Ground truth:** Steinberg ASIO SDK 2.3.x at `C:\PROJECTS\3P_ASIOSDK` (`common/asiosys.h`, `common/asio.h`, `common/iasiodrv.h`), read
  2026-09-13; declarations are extracted from it by spec 71, evidence in `src/AN.Audio/CodeGen/Extraction.report.json`. Vendor guidance
  (2026-09-13, from the user's ASIO notes) is folded into D5–D9.
- **Hardware on the dev box:** MOTU M4 — `HKLM\SOFTWARE\ASIO\MOTU M Series`, CLSID `{3E08EBEA-46E3-45B9-8216-312892B30F94}` (64-bit view).
  `Realtek ASIO` exists only in `WOW6432Node` (32-bit) and is invisible to a 64-bit process.
- **Why:** spec 60 §4.1 showed that on a typical Windows box neither shared low-latency nor exclusive WASAPI is available without user action.
  A class-compliant pro interface ships an ASIO driver that *is* its low-latency path (M4: 32–4096 frames, set in the M-Series panel).

## 1. Goal and non-goals

**Goal:** `IAudioOutput` over an installed ASIO driver, selectable by the consumer, same callback contract, same D4/D7 reporting as spec 60,
reaching the driver's panel-configured buffer size (expected M4 @ 48 kHz: 64–256 frames, `LatencyMs` = `outputLatency`/rate ≈ 2–6 ms).

**Non-goals (this spec):** input/capture (spec 40 — but D10 shapes the runtime so inputs plug in), DSD (`kAsioSetIoFormat`), time-code, clock-source
selection UI, multiple simultaneous ASIO drivers in one process (drivers are single-instance; we enforce one `Asio_DriverHost` per CLSID),
hosting the driver's control panel inside our UI (we can *open* it — `controlPanel()` — nothing more).

## 2. Decisions

- **D1 — I1 amendment (user-approved 2026-09-13).** An ASIO driver the user installed with their hardware is treated like `libasound` or
  `AudioToolbox`: already on the machine, nothing bundled by us. I1 still forbids shipping any native binary; it does not forbid loading the
  user's own driver. Spec 60 §1 and overview I1 get a one-line amendment pointing here.
- **D2 — Optional backend, default Auto.** `AudioOutputOptions.Backend : AudioOutput_Backend { Auto = 0, Wasapi, Asio }` (Windows-only
  meaning; macOS/Linux ignore it). `Auto` = WASAPI **unless** the resolved device id is an ASIO id (`asio:{CLSID}`, D3). `Asio` with no ASIO
  device resolvable → the first registered driver; none registered → fall back to WASAPI with `LatencyFallbackReason.BackendUnavailable`
  (new enum member) — never throw for a preference (60 D3).
- **D3 — ASIO drivers are devices.** `AsioDeviceManager : IAudioDeviceManager` enumerates `HKLM\SOFTWARE\ASIO` in the registry view matching
  `Environment.Is64BitProcess` (`RegistryView.Registry64`/`Registry32`); `AudioDeviceInfo.Id = "asio:{CLSID}"` (branded `Asio_DriverKey`
  internally), `Name` = the key's `Description` (falls back to key name). Drivers whose COM server DLL (`HKCR\CLSID\{..}\InprocServer32`) is
  missing are skipped. No hot-plug notifications (the registry is static); `DeviceListChanged` never fires. `AudioOutput.GetDeviceManager()`
  stays WASAPI; new `AudioOutput.GetDeviceManager(AudioOutput_Backend)` returns the ASIO one on request.
- **D4 — Three threads, strict roles.**
  | thread | owner | does |
  |---|---|---|
  | consumer control thread | app | `Create/Start/Stop/Dispose` — posts work to the host thread and waits (I5) |
  | `"AN.Audio ASIO host"` | ours, one per driver | `CoInitializeEx(APARTMENTTHREADED)`; `RegisterClassExW` + `CreateWindowExW` **hidden top-level** window (NOT `HWND_MESSAGE`: message-only windows cannot own dialogs and `controlPanel()` needs an owner); `CoCreateInstance(clsid, CLSCTX_INPROC_SERVER, iid = clsid)` (the ASIO quirk: IID == CLSID); `init(hwnd)`; **every** driver method except `outputReady`; message pump via `MsgWaitForMultipleObjectsEx` + a work queue; services `kAsioResetRequest` |
  | driver RT thread | driver | `bufferSwitchTimeInfo`/`bufferSwitch` → pull → deinterleave → `outputReady()`; nothing else |
  Rationale: several drivers are apartment-sensitive (init and start must come from one thread — PortAudio does the same); the HWND must be
  on a thread that pumps; and the HWND must never be the consumer's UI thread (a blocked UI would stall driver messages) nor the RT thread.
  Escape hatch: `AudioOutputOptions.Asio_OwnerWindow : AudioOutput_NativeWindowHandle?` — if set, `controlPanel()` dialogs are parented to it
  (we still `init()` with OUR hwnd; the owner is used only via `SetWindowLongPtr(GWLP_HWNDPARENT)` on our hidden window).
- **D5 — Buffer size = the driver's `preferredSize`.** ASIO buffer size is driver-global (M-Series panel), not per-client. `getBufferSize` → use
  `preferred`; never request another value. `PeriodFrames = preferred`. `BufferSizeMs` is ignored; `Latency` mode is informational:
  `LatencyModeActual = LowLatency` always (ASIO has no "default" path), `StreamProcessingActual = Raw` (no APO chain by construction).
- **D6 — Sample rate: adopt the driver's by default; switching the device is opt-in** (amended 2026-09-13 after listening: the first
  version tried `setSampleRate(consumer rate)` first, and the M4 **mutes its outputs for 1–2 s while its clock relocks** — the head of a
  44.1 k voice sample was lost). `AudioOutputOptions.Asio_SampleRate : Asio_SampleRatePolicy { AdoptDriverRate (default), SetDeviceRate }`.
  Default: `getSampleRate()` and resample through `AudioFormatConverter` (sinc), reported as `LatencyFallbackReason.DriverRateAdopted` (informational).
  `SetDeviceRate`: `canSampleRate` → `setSampleRate` BEFORE `createBuffers` (SDK order); refusal → adopt. `sampleRateDidChange` at runtime → treated as `kAsioResetRequest`.
- **D7 — Channel types are per channel; read them.** `getChannelInfo` for each wanted output; supported `Asio_SampleType`: `Int32LSB` (M4),
  `Int16LSB`, `Int24LSB`, `Float32LSB`, `Float64LSB`. Anything else (MSB variants, `Int32LSBnn`, DSD) → `NotSupportedException` at open naming
  the channel and type. Mixed types across the used channels are allowed (per-channel converter selection).
  `DeviceFormat` reports `(driverRate, usedChannels, Int32|Int16|Float32)` of channel 0 — it exists for zero-copy consumers; with planar ASIO
  buffers passthrough is impossible anyway, so the converter path is always taken (§4).
- **D8 — Channel mapping.** Use outputs `[Asio_OutputChannelOffset, Asio_OutputChannelOffset + consumer.Channels)`, default offset 0 (M4 outs 1-2),
  `AudioOutputOptions.Asio_OutputChannelOffset : int` (branded `Asio_ChannelIndex`) lets a consumer target outs 3-4. Consumer channels beyond
  the driver's outputs are dropped; fewer are upmixed by the converter's existing `MapChannel` rule.
- **D9 — `outputReady()` after every fill.** Call once after `createBuffers`; if it returns `ASE_OK` the driver supports it (saves one block of
  latency on drivers that latch) and we call it at the end of every `bufferSwitch`; `ASE_NotPresent` → never call it again.
- **D10 — Runtime split for spec 40.** `Asio_DriverHost` (thread, HWND, COM object, `Asio_Callbacks` table, buffers) knows inputs AND outputs;
  `AsioAudioOutput` is one client of it that only asks for outputs. Capture later adds `AsioAudioInput` on the same host — one `createBuffers`
  call carries both (ASIO is I/O-synchronous), so the host owns buffer creation and both clients register before `start`.
- **D11 — Callbacks have no user-data slot.** Overview rule 4's GCHandle-in-user-data cannot apply. `Asio_DriverHost` keeps a static
  `Asio_DriverHost? s_active` (one live host per process, D-enforced with an exception on a second); the `[UnmanagedCallersOnly(Cdecl)]`
  trampolines dispatch to it. Exceptions are fenced (counter + `CallbackExceptionReported` event on the host thread), never unwound into the driver.
- **D12 — Underruns (60 D7).** `asioMessage(kAsioOverload)` → `UnderrunCount++` when the driver supports `future(kAsioCanReportOverload) ==
  ASE_SUCCESS`; additionally `kAsioResyncRequest` → `UnderrunCount++` and re-`start`. Our own watchdog: if a `bufferSwitch` arrives while the
  previous one is still running (re-entrancy flag) count it too — that is exactly the "host too slow" case.
- **D13 — Reset protocol.** `asioMessage(kAsioResetRequest)` returns 1 and posts to the host thread: `stop` → `disposeBuffers` → re-query
  rate/buffer size/latencies/channel types → `createBuffers` → prefill → `start`; then `DeviceFormatChanged` if the rate/type changed and
  `DeviceSwitched(CurrentDevice)` because `PeriodFrames` may have changed (60 D4). `kAsioLatenciesChanged` → re-read `getLatencies` only.
  `kAsioBufferSizeChange` → answer 0 (SDK: "use kAsioResetRequest instead"). Hardware unplugged (driver returns `ASE_NotPresent`/`ASE_HWMalfunction`
  from `start` after a reset) → `DeviceLost(DeviceRemoved)`, `_running = false` (overview rule 8: once, no retry loop).
- **D14 — `SwitchPolicy` semantics.** `FollowDefault`/`PreferenceList` have no ASIO meaning (there is no "default ASIO driver"); the backend
  behaves as `None` and documents it. A `PreferredDevices` list mixing `asio:` and MMDevice ids is resolved by `AudioOutput.Create` once, at
  creation (Auto rule in D2); we do not switch backends at runtime.

## 3. Public surface additions

```csharp
public enum AudioOutput_Backend { Auto = 0, Wasapi = 1, Asio = 2 }                       // NEW (Windows meaning; ignored elsewhere)
public readonly record struct AudioOutput_NativeWindowHandle(nint Value);                 // NEW, branded HWND
public readonly record struct Asio_ChannelIndex(int Value);                                // NEW

public sealed class AudioOutputOptions {
    public AudioOutput_Backend Backend { get; set; } = AudioOutput_Backend.Auto;           // NEW (D2)
    public AudioOutput_NativeWindowHandle? Asio_OwnerWindow { get; set; }                  // NEW (D4)
    public Asio_ChannelIndex Asio_OutputChannelOffset { get; set; } = new(0);              // NEW (D8)
    // Latency, Processing, BufferSizeMs, SwitchPolicy, PreferredDevices unchanged
}
public enum AudioOutput_LatencyFallbackReason { …, BackendUnavailable = 7, DriverRateAdopted = 8 }   // NEW members (D2, D6)
public static class AudioOutput { public static IAudioDeviceManager? GetDeviceManager(AudioOutput_Backend backend); }  // NEW overload (D3)
```

`IAudioOutput` is unchanged — D4/D7 members from spec 60 already carry everything ASIO reports. Optional later: `IAsioAudioOutput :
IAudioOutput { void OpenControlPanel(); }` — listed in §8, not built until a consumer asks.

## 4. Hot path (driver RT thread)

```
bufferSwitchTimeInfo(ASIOTime* t, long index, ASIOBool direct):        // we answer kAsioSupportsTimeInfo = 1, so this is the one that fires;
    if (re-entered) { UnderrunCount++; return t; }                       //   bufferSwitch(index, direct) forwards here with t = null
    frames = PeriodFrames
    written = converter.FillDeviceBuffer(scratchInterleaved, frames, callback)   // consumer format → Float32 @ driverRate × usedChannels (interleaved)
    for ch in usedChannels:                                                      // planar deinterleave + per-channel type convert
        dst = bufferInfos[ch].Buffer[index]
        switch channelType[ch]: Int32LSB → AudioSampleConvert.FloatToInt32 strided copy; Int16LSB → FloatToInt16; Int24LSB → WriteInt24; Float32LSB → strided copy; Float64LSB → widen
        if written < frames: zero the tail
    if (outputReadySupported) driver.outputReady()
    return t
```

`scratchInterleaved` is allocated once per `createBuffers` (size = `PeriodFrames × usedChannels × 4`) — no allocation in the callback (I3).
The converter's device format is `(driverRate, usedChannels, Float32)`; the deinterleave stage is the only ASIO-specific code and lives in
`Asio_PlanarWriter` (static, span-based, unit-tested against `AudioSampleConvert` for every supported type). Prefill before `start`: both
halves zeroed (SDK: host has filled buffer B before `ASIOStart`).

## 5. Files

```
src/AN.Audio/
├── AudioOutput_Backend.cs                       enums/records of §3
├── AudioOutput.cs                               backend resolution (D2), GetDeviceManager(backend)
├── Generated/Asio.*.generated.cs                spec 71 output
└── Platforms/Windows/Asio/
    ├── AsioInterop.cs                           hand-written ONLY: ole32 CoCreateInstance(iid=clsid), user32 window/pump P/Invokes, registry read helpers
    ├── Asio_DriverRegistry.cs                   D3 enumeration → Asio_DriverKey, Asio_DriverInfo(Name, Clsid, Dll)
    ├── AsioDeviceManager.cs                     IAudioDeviceManager over the registry
    ├── Asio_DriverHost.cs                       D4 thread + HWND + COM object + callbacks table + buffers; RunOnHost(action), RunOnHostAndWait
    ├── Asio_PlanarWriter.cs                     §4 deinterleave/convert
    └── AsioAudioOutput.cs                       IAudioOutput
tests/AN.Audio.Tests/Asio/
    ├── Asio_LayoutTests.cs                      AssertLayouts constants vs §5 of spec 71 (both ABIs), enum values vs header (ASE_NotPresent = -1000, ASIOSTInt32LSB = 18, kAsioResetRequest = 3, kAsioOverload = 15 …)
    ├── Asio_PlanarWriterTests.cs                 every type, tail zeroing, zero-alloc loop
    └── Asio_DriverRegistryTests.cs               skips when no driver registered; on this box expects "MOTU M Series"
tests/SimpleAudioTest/Program.cs                 --asio [--asio-driver "MOTU M Series"] [--asio-offset 2] [--asio-panel]; trace prints channel types, buffer min/max/preferred/granularity, latencies, rate
```

## 6. Phases

- [x] **Phase 0 — spec 71 P0–P4** (generated declarations + probe green on this box).
- [x] **Phase 1 — Enumeration + host**: `Asio_DriverRegistry`, `AsioDeviceManager`, `Asio_DriverHost` up to `init(hwnd)` + `getDriverName/Version`,
      `getChannels`, `getBufferSize`, `getLatencies`, `getChannelInfo`; `SimpleAudioTest --asio --probe` prints them for the M4. No audio yet.
- [x] **Phase 2 — Output**: `createBuffers` (outputs), callbacks table, `Asio_PlanarWriter`, `AsioAudioOutput` Start/Stop/Dispose, D5–D9, D11, D12.
      440 Hz tone for 10 s on the M4: record `PeriodFrames`, `LatencyMs`, `UnderrunCount` in §7.
- [ ] **Phase 3 — Reset/loss**: D13 is coded (`RequestReset`), NOT yet exercised: change buffer size in the M-Series panel while playing → seamless re-create; unplug → `DeviceLost`. D14.
- [ ] **Phase 4 — Docs/consumers**: D2/D3 wiring is done (`AudioOutput.Create`, `GetDeviceManager(backend)`); pending: README (backend table row, trademark line §7), spec 60 §1/§9
      amendment, overview I1 note + matrix row, `_PROJECT_STRUCTURE.md`. MusicStudio opts in via `MUSICSTUDIO_AUDIO_BACKEND=asio`.

## 7. Verification / measurements

### 7.1 First probe — MOTU M4, 2026-09-13 (throwaway `artifacts/scratch/asio_probe.cs`, details in `_EXTERNAL_APIS/ASIO_IASIO.md`)

| | measured |
|---|---|
| threading | `ThreadingModel=Apartment`; from .NET's MTA `Main`: `CoInitializeEx` → `RPC_E_CHANGED_MODE`, `CoCreateInstance` → `E_NOINTERFACE`. On a `SetApartmentState(STA)` thread: OK. **D4 host thread must be a true STA.** |
| channels | 8 in (4 analog + 2 loopback + 2 loopback-mix), 4 out; every channel `ASIOSTInt32LSB` (18), group 0 |
| buffer | min 16 / max 4096 / **preferred 128** / granularity −1; `createBuffers(2 outs, 128)` OK; planar, 512 B per channel half |
| rate | 48000; `canSampleRate` OK for 44.1 / 48 / 88.2 / 96 / 176.4 / 192 k |
| latency | `getLatencies` in 177 / **out 166 frames = 3.46 ms** at 128 (same before/after createBuffers) |
| `outputReady` | `ASE_NotPresent` — unsupported (D9 probe-once confirmed) |
| run | 1 s → 375 `bufferSwitch` = 48000/128 exactly; driver asked `kAsioSupportsTimeInfo` during `createBuffers`; stop/dispose/Release clean |

### 7.2 Phase 2 through the real backend — MOTU M4, panel at default (128), 2026-09-13

`SimpleAudioTest --asio [--tone] [--asio-offset 2]`, consumer Int16, `Backend = Asio`, listened to on Out 1/2 and Out 3/4:

| case | device format | `PeriodFrames` | `LatencyMs` (= `outputLatency`) | underruns | heard |
|---|---|---|---|---|---|
| 440 Hz tone, 48 k stereo, 10 s | 48000 / 2 / Int32 | 128 = 2.67 ms | **3.46** | 0 | clean, immediate start |
| voice WAV 44.1 k mono, **first version of D6 (setSampleRate 44100)** | 44100 / 1 / Int32 | 128 = 2.90 ms | 3.8 | 0 | **head clipped ~1–2 s** — M4 relock mute → D6 amended |
| 440 Hz tone 48 k while the device sat at 44.1 k, **AdoptDriverRate** | 44100 / 2 / Int32, `DriverRateAdopted` | 128 | 3.8 | 0 | clean, immediate start (user-confirmed) |
| tone, `--asio-offset 2` | 48000 / 2 / Int32 | 128 | 3.46 | 0 | Out 3/4 |

Also fixed on the way: `SimpleAudioTest --volume` was parsed and never applied (played at unity); default is now 0.25.

### 7.3 Phase 3 — panel buffer-size changes while playing (D13), MOTU M4, 2026-09-13

First attempt (dispose/create on the same instance): sound stopped, driver still reported the old preferred size, no `bufferSwitch` ever again.
With `Asio_DriverHost.Reinitialize()` (Release + CoCreateInstance + init): **seven consecutive panel changes in one 30 s run** — 64 → 128 → 256 → 64 →
128 → 64 → **32** — each producing one `DeviceSwitched` with the new period, 0 underruns throughout, clean exit; user-confirmed "a small gap every time,
then it resumed". `kAsioResetRequest` is delivered on a driver thread (t5), never the host thread.

| panel | `PeriodFrames` | `LatencyMs` (`outputLatency`) |
|---|---|---|
| 32 | 32 = 0.73 ms | **1.59 ms** |
| 64 | 64 = 1.45 ms | 2.31 ms |
| 128 | 128 = 2.90 ms | 3.76 ms |
| 256 | 256 = 5.80 ms | 6.67 ms |
| 512 | 512 = 11.61 ms | 12.47 ms |

- Unit as in §5; hardware: `SimpleAudioTest --asio` on the M4 at panel sizes 32/64/128/256 — table of `PeriodFrames`, `LatencyMs`
  (`outputLatency`), `UnderrunCount` after 10 s, to be recorded here (mirrors 60 §4.1).
- README line (Steinberg usage guidelines): "ASIO is a trademark and software of Steinberg Media Technologies GmbH." No SDK file is shipped;
  declarations are generated from a locally-installed SDK (spec 71 D1).

## 8. Open questions

- ~~`Auto` escalating to ASIO when WASAPI falls back to ≥ 10 ms~~ — **decided 2026-09-13: no.** `Auto` never picks ASIO by itself (it would
  silently change the device's global sample rate, D6). ASIO is chosen only by `Backend = Asio` or an `asio:` device id (D2). Revisit after the first measurements.
- `IAsioAudioOutput.OpenControlPanel()` — add when a consumer wants a "driver settings…" button.
- x86 hardware validation: none available; the D7 probe on `msvc-x86` is the only x86 check.
- `directProcess == ASIOFalse` (driver called us from a very low level and asks us to defer): we process inline anyway, like every DAW does on
  Windows; noted in case a driver ever misbehaves.