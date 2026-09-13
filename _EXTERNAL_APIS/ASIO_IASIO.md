# ASIO — `IASIO` (Steinberg ASIO SDK 2.3.x) — ground truth + first live probe

Source read 2026-09-13: `C:\PROJECTS\3P_ASIOSDK\common\{asiosys.h, asio.h, iasiodrv.h}` (SDK header says "Steinberg Audio Stream I/O API v2.3").
Declarations for code are **generated** from these headers by spec 71 (`src/AN.Audio/CodeGen/Extraction.report.json` carries the SHA-256s);
this page records what the headers say in prose, the licence position, and what the MOTU M4 driver actually did.

## Licence position

The SDK licence forbids copying/redistributing SDK files. We ship none: spec 71 reads the locally-installed SDK at `extract` time and commits
only our own declaration record + generated C#. README carries "ASIO is a trademark and software of Steinberg Media Technologies GmbH"
(Steinberg ASIO Usage Guidelines). No SDK `.cpp` (asio.cpp/asiodrivers.cpp) is used — the C-API layer is theirs; we talk to `IASIO` directly.

## The object model in one paragraph

An ASIO driver is an in-proc COM server registered under `HKLM\SOFTWARE\ASIO\<name>` (`CLSID`, `Description`) in the registry view of the
driver's bitness (64-bit drivers in the 64-bit view; `WOW6432Node\ASIO` holds 32-bit ones a 64-bit process cannot load). The COM quirk:
`CoCreateInstance(clsid, NULL, CLSCTX_INPROC_SERVER, riid = clsid, ...)` — the IID **is** the CLSID. The returned object is a C++ class
`interface IASIO : public IUnknown` (`iasiodrv.h`), NOT a COM interface: slots 0–2 are stdcall IUnknown, slots 3–23 are **thiscall** virtuals.
Host→driver is the vtable; driver→host is an `ASIOCallbacks` struct of four **cdecl** function pointers passed to `createBuffers`, with no
user-data slot.

## `IASIO` vtable (iasiodrv.h, declaration order = slot)

| slot | method | notes |
|---|---|---|
| 0–2 | QueryInterface / AddRef / Release | stdcall |
| 3 | `ASIOBool init(void* sysHandle)` | Windows: HWND. Returns ASIOTrue(1)/ASIOFalse(0); on 0 read `getErrorMessage` |
| 4 | `void getDriverName(char* name)` | ≥ 32 bytes |
| 5 | `long getDriverVersion()` | |
| 6 | `void getErrorMessage(char* string)` | ≥ 124 bytes |
| 7 | `ASIOError start()` | resets sample position to 0; host must have filled output buffer B (index 1) before |
| 8 | `ASIOError stop()` | on return the driver must not call bufferSwitch again |
| 9 | `getChannels(long* in, long* out)` | |
| 10 | `getLatencies(long* in, long* out)` | frames; `out` = time from buffer switch to that buffer sounding |
| 11 | `getBufferSize(long* min, long* max, long* preferred, long* granularity)` | granularity −1 = powers of two |
| 12 | `canSampleRate(ASIOSampleRate)` | `ASE_NoClock` if unsupported |
| 13 | `getSampleRate(ASIOSampleRate*)` | |
| 14 | `setSampleRate(ASIOSampleRate)` | 0 = external sync; `ASE_InvalidMode` if clock is external; call BEFORE createBuffers |
| 15 | `getClockSources(ASIOClockSource*, long* num)` | |
| 16 | `setClockSource(long)` | |
| 17 | `getSamplePosition(ASIOSamples*, ASIOTimeStamp*)` | block-aligned |
| 18 | `getChannelInfo(ASIOChannelInfo*)` | in: `channel`, `isInput`; out: `isActive`, `channelGroup`, `type`, `name[32]` — type is PER CHANNEL |
| 19 | `createBuffers(ASIOBufferInfo*, long numChannels, long bufferSize, ASIOCallbacks*)` | one call for inputs AND outputs; driver owns the buffers; asks `asioMessage(kAsioSupportsTimeInfo)` from inside |
| 20 | `disposeBuffers()` | implies stop() |
| 21 | `controlPanel()` | opens the vendor panel; return code ignored |
| 22 | `future(long selector, void* opt)` | success is `ASE_SUCCESS` (0x3f4847a0), NOT ASE_OK |
| 23 | `outputReady()` | optional latency shave; `ASE_NotPresent` = unsupported, stop calling it |

## Types and layouts (Windows: `_WIN32` → `NATIVE_INT64 0`, `IEEE754_64FLOAT 1`; `#pragma pack(push,4)` under `_MSC_VER`)

- `ASIOBool`, `ASIOError`, `ASIOSampleType` = `long` = **int32** (Windows LLP64). `ASIOSampleRate` = `double`.
- `ASIOSamples`, `ASIOTimeStamp` = `struct { unsigned long hi; unsigned long lo; }` (8 B) — NOT a 64-bit integer field. Value = `hi<<32 | lo`.
- `ASIOChannelInfo` 52 B · `ASIOBufferInfo` { isInput, channelNum, `void* buffers[2]` } 24 B x64 / 16 B x86 · `ASIOClockSource` 48 B ·
  `ASIOTimeCode` 84 B (pack 4: `unsigned long flags` at 16, `char future[64]` at 20) · `AsioTimeInfo` 48 B · `ASIOTime` { `long reserved[4]`, timeInfo, timeCode } **148 B** ·
  `ASIOCallbacks` 4 pointers. `ASIODriverInfo` (C-API only, not used by us): 172 B on x64 because `void* sysRef` sits at offset 164 under pack 4.
- Enums: `ASE_OK 0`, `ASE_SUCCESS 0x3f4847a0`, `ASE_NotPresent −1000`, then `HWMalfunction −999`, `InvalidParameter −998`, `InvalidMode −997`,
  `SPNotAdvancing −996`, `NoClock −995`, `NoMemory −994`. Sample types: `Int16MSB 0, Int24MSB 1, Int32MSB 2, Float32MSB 3, Float64MSB 4,
  Int32MSB16/18/20/24 = 8–11, Int16LSB 16, Int24LSB 17, Int32LSB 18, Float32LSB 19, Float64LSB 20, Int32LSB16/18/20/24 = 24–27, DSD 32/33/40`.
  asioMessage selectors: `SelectorSupported 1, EngineVersion 2, ResetRequest 3, BufferSizeChange 4, ResyncRequest 5, LatenciesChanged 6,
  SupportsTimeInfo 7, SupportsTimeCode 8, MMCCommand 9, SupportsInputMonitor 10, …InputGain 11, InputMeter 12, OutputGain 13, OutputMeter 14, Overload 15`.
  future selectors: `kAsioCanReportOverload 0x24042012`, `kAsioGetInternalBufferSamples 0x25042012`, DSD `0x23111961/83/2004`.

## Callbacks (`ASIOCallbacks`, all cdecl)

```
void      bufferSwitch(long doubleBufferIndex, ASIOBool directProcess)
void      sampleRateDidChange(ASIOSampleRate sRate)
long      asioMessage(long selector, long value, void* message, double* opt)
ASIOTime* bufferSwitchTimeInfo(ASIOTime* params, long doubleBufferIndex, ASIOBool directProcess)   // used iff host answered kAsioSupportsTimeInfo with 1
```
`index` is the half the host must now FILL (the other half is going to the hardware). `directProcess == ASIOFalse` asks the host to defer —
every Windows DAW processes inline anyway.

## First live probe — MOTU M4, `MOTU M Series` driver v2, `MOTUCoreUACASIO.dll` (x64, `ThreadingModel=Apartment`), 2026-09-13

Throwaway `artifacts/scratch/asio_probe.cs` (hand-typed slots; NOT the generated bindings), x64 process, panel left at defaults:

| call | result |
|---|---|
| `CoInitializeEx(APARTMENTTHREADED)` from .NET `Main` | **`RPC_E_CHANGED_MODE`** — .NET Main is MTA; then `CoCreateInstance` → **`E_NOINTERFACE`**. Driver refuses MTA callers. |
| same on a `Thread` with `SetApartmentState(STA)` | `S_FALSE`/`S_OK`, `CoCreateInstance(iid = clsid)` → `S_OK` |
| `init(hwnd)` with a hidden `"STATIC"` window on that thread | `ASIOTrue` |
| `getChannels` | in = **8** (In 1–4, Loopback 1–2, Loopback Mix 1–2), out = **4** (Out 1–4) |
| `getBufferSize` | min **16**, max **4096**, preferred **128**, granularity −1 (powers of two) |
| `getSampleRate` / `canSampleRate` | 48000; 44.1/48/88.2/96/176.4/192 k all `ASE_OK` |
| `getChannelInfo` every in/out | type **18 = `ASIOSTInt32LSB`**, group 0 |
| `getLatencies` (before and after createBuffers, 128 @ 48 k) | in 177, out **166 frames = 3.46 ms** (≈ 128 + 38 device) |
| `createBuffers(2 outs, 128)` | `ASE_OK`; driver first called `asioMessage(kAsioSupportsTimeInfo)`; buffers 0x200 B apart per channel = 128 × 4 B, planar, halves 64 KiB apart |
| `outputReady()` | **`ASE_NotPresent` — unsupported on this driver** (D9's probe-once rule is right) |
| `start()` → 1 s → `stop()` | 375 `bufferSwitch` calls = exactly 48000/128; `stop/disposeBuffers/Release` all 0 |

Consequences for spec 70: host thread MUST be a true STA (`Thread.SetApartmentState(STA)` + `CoInitializeEx(APARTMENTTHREADED)`), not merely
"our own thread"; a hidden window created on it satisfied `init`; Int32LSB on every channel; preferred 128 → `PeriodFrames = 128`,
`LatencyMs ≈ 3.46` at the default panel setting (panel goes down to 16).