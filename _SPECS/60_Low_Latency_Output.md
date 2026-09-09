# 60 — Low-latency PCM output on Windows, macOS and Linux

- **Status:** Phase A (Windows) BUILT 2026-09-09, published `0.260909.80722`, MusicStudio opted in. macOS (§5) and Linux (§6) specified, not started. See §4.1 for what the first machine measured.
- **Parent:** `00_AN_Audio_Overview.md` (invariants I1–I5). Builds on `10_Audio_Bringup.md` (callback contract) and `20_Audio_Device_Management.md` (switching).
- **Ground truth:** `_EXTERNAL_APIS/WASAPI_IAudioClient3_MMCSS.md`; macOS/Linux header notes are Phase deliverables (§5.6, §6.6).
- **Driving consumer:** MusicStudio (`C:\PROJECTS\AN_MusicStudio\_SPECS\Bringup\18_MidiInputLatency_Findings.md`): measured key→sound is dominated by
  the OUTPUT side once the MIDI delivery bug was fixed. Every platform here has the same job: make `AudioCallback` run at the smallest period the OS
  will schedule reliably, on a thread the OS treats as real-time.

## 1. Goal and non-goals

**Goal:** an opt-in `AudioOutputOptions.Latency = LowLatency` that, on each platform, uses the OS's own low-latency shared path — no exclusive
device lock, no third-party drivers (I1) — and reports what it actually got. Target key→speaker budget for the output stage:

| platform | today (default path) | target (this spec) | mechanism |
|---|---|---|---|
| Windows | 20 ms buffer + 10 ms engine period ≈ 30 ms | **≈ 3–6 ms** | `IAudioClient3::InitializeSharedAudioStream` at engine min period + MMCSS "Pro Audio" |
| macOS | AudioQueue, 3 × `BufferSizeMs` = 60 ms | **≈ 3–6 ms** | AUHAL (`kAudioUnitSubType_HALOutput`) render callback, `kAudioDevicePropertyBufferFrameSize` 64–128 |
| Linux | `snd_pcm_set_params(latency = BufferSizeMs)` ≈ 20 ms, no RT | **≈ 3–8 ms** | explicit `hw_params` period 64–128 × 2–3, `SCHED_FIFO` on the poll thread |

**Non-goals:** exclusive mode (Windows) / hog mode (macOS) — they take the device from every other app; ASIO/JACK (third-party or non-default
stacks, I1); sub-block scheduling of events inside a period (consumer's job — MusicStudio's engine places note-ons by `ArrivalTicks`); latency
*measurement* hardware (a loopback tool is §8, capture is spec 40).

## 2. Decisions

- **D1 — One knob, three idioms.** `AudioOutputOptions.Latency : AudioOutput_LatencyMode { Default, LowLatency, Exclusive }`. `Default` = today's
  behaviour bit-for-bit (Mirica, Arcane Siege see no change — **the library default stays `Default`; only MusicStudio opts in**). `LowLatency` = each
  backend's §4–§6 shared path. **`Exclusive`** (added after §4.1: Windows WASAPI exclusive, event-driven, at the driver's minimum device period) is the
  only sub-10 ms path when the driver exposes no small shared period; it falls back `Exclusive → LowLatency → Default`. macOS/Linux treat it as
  `LowLatency`. `BufferSizeMs` is the DEFAULT-mode size only.
- **D1b — RAW is a separate, orthogonal option.** `AudioOutputOptions.Processing : AudioOutput_StreamProcessing { SystemEffects, Raw }`
  (`AUDCLNT_STREAMOPTIONS_RAW` via `IAudioClient2::SetClientProperties`, before `GetMixFormat`). Default `SystemEffects`; MusicStudio requests `Raw`.
  Exclusive streams report `Raw` regardless (APOs are bypassed by construction). Refusal → `RawModeUnsupported`, stream continues with effects.
- **D2 — Ask the OS, never guess.** Query the supported range (`GetSharedModeEnginePeriod`, `kAudioDevicePropertyBufferFrameSizeRange`,
  `snd_pcm_hw_params_get_period_size_min`) and take the minimum the OS reports. Never hard-code 64 or 128.
- **D3 — Fall back, loudly.** If the low-latency path fails (old OS, driver refuses, RT rights missing), initialise the Default path and set
  `IAudioOutput.LatencyModeActual = Default` with `LatencyFallbackReason` (an enum, not a string). Never throw for a latency preference.
- **D4 — Report what you got.** New read-only members on `IAudioOutput`: `PeriodFrames` (callback granularity actually in effect),
  `LatencyModeActual`, `LatencyFallbackReason`. `LatencyMs` stays (submission→DAC estimate). All three backends implement them.
- **D5 — Real-time thread on every platform.** The thread that runs `AudioCallback` is registered with the OS's RT scheduler class:
  MMCSS "Pro Audio" / already-RT on CoreAudio's I/O thread (and joined to the device workgroup on macOS 11+) / `SCHED_FIFO` on Linux.
  `ThreadPriority.Highest` remains as the fallback when registration fails. Applies in BOTH modes — it costs nothing and removes jitter.
- **D6 — Callback contract unchanged.** `AudioCallback(Span<byte>, frames, format)` is called with `frames = PeriodFrames` (or the free space, as
  today). Consumers already handle variable `frames`; MusicStudio renders in 64-frame sub-blocks so any period ≥ 64 costs nothing extra.
- **D7 — Underrun counter** (the spec 10 open question, now needed): `IAudioOutput.UnderrunCount` incremented on the hot path with `Interlocked`
  when the OS reports a glitch (`AUDCLNT_BUFFERFLAGS_*`/padding == 0 after wake on Windows; `kAudioDevicePropertyOverload`/skipped render on macOS;
  `-EPIPE` on ALSA). Small periods without visibility into dropouts are a trap.

## 3. Public surface (all platforms)

```csharp
public enum AudioOutput_LatencyMode { Default = 0, LowLatency = 1 }
public enum AudioOutput_LatencyFallbackReason { None = 0, OsTooOld = 1, DriverRefused = 2, RealtimeRightsMissing = 3, EnginePeriodLocked = 4, Unknown = 99 }

public sealed class AudioOutputOptions
{
    public AudioOutput_LatencyMode Latency { get; set; } = AudioOutput_LatencyMode.Default;   // NEW
    public int BufferSizeMs { get; set; } = 20;                                                // Default mode only
    // SwitchPolicy, PreferredDevices unchanged
}

public interface IAudioOutput
{
    int PeriodFrames { get; }                                       // NEW: frames per callback the OS actually scheduled
    AudioOutput_LatencyMode LatencyModeActual { get; }              // NEW
    AudioOutput_LatencyFallbackReason LatencyFallbackReason { get; } // NEW
    long UnderrunCount { get; }                                     // NEW (D7)
    double LatencyMs { get; }                                       // unchanged
}
```

Device switch (spec 20) re-runs the platform's open path with the same options; a switch may change `PeriodFrames` — `DeviceSwitched` already
tells the consumer to re-read.

## 4. Windows — WASAPI (`Platforms/Windows/WasapiAudioOutput.cs`) — BUILT (Phase A)

- [x] `WasapiInterop`: `IID_IAudioClient2/3`, vtable indices 15–20, typed wrappers (`SetClientProperties`, `GetSharedModeEnginePeriod`,
      `GetCurrentSharedModeEnginePeriod`, `InitializeSharedAudioStream`, `IsFormatSupported`, `GetDevicePeriod`), `AudioClientHResult` enum,
      `AudioClientProperties`, `avrt.dll` `AvSetMmThreadCharacteristicsW`/`AvRevertMmThreadCharacteristics`.
- [x] `OpenDevice`: `ActivateNewestAudioClient` (3 → 2 → 1; `OsTooOld` when LowLatency needs 3). RAW via `SetClientProperties` BEFORE `GetMixFormat`.
      `Exclusive` → `InitializeExclusive` (§4.2); `LowLatency` → `InitializeLowLatency` (min period; `ENGINE_PERIODICITY_LOCKED` adopts the current
      period); any failure → `Initialize` Default + `DriverRefused`/`ExclusiveRefused`. `ReactivateClient` after a failed exclusive `Initialize`.
- [x] `AudioThreadProc`: MMCSS "Pro Audio" registered after `CoInitializeEx`, reverted in `finally`; `ThreadPriority.Highest` stays as fallback.
- [x] `PeriodFrames` = period used (LowLatency/Exclusive) or `GetDevicePeriod` default converted to frames (Default). `UnderrunCount` = wake with
      `padding == 0` after the first fill (shared modes only — exclusive event-driven always writes the whole buffer).
- [x] `tests/SimpleAudioTest`: `--low-latency`, `--exclusive`, `--raw`, `--device <id>`, `--probe-all` (opens every render endpoint in LowLatency and
      prints the period it grants), `--volume` (default 0.3). `AN_AUDIO_TRACE=1` prints the engine range / exclusive negotiation to stderr.

### 4.1 First machine measured (2026-09-09, Windows 11 25H2 desktop) — the reason `Exclusive` exists

`--probe-all`: **every** render endpoint returned `default = fundamental = min = max` — 480 frames @ 48 kHz / 441 @ 44.1 kHz — i.e. a single legal
shared period of 10 ms: Realtek(R) Audio (UAD) front jack, NVIDIA HDMI, HyperX USB headset (virtual surround), two Steam virtual devices. Our path is
correct (`S_OK` everywhere, `LatencyModeActual = LowLatency`, RAW accepted, MMCSS registered, 0 underruns) — the engine simply has nothing smaller to
offer because none of those drivers implement `KSPROPERTY_RTAUDIO_BUFFER_SIZE_RANGE` (in-box `hdaudio.sys`/`usbaudio2.sys` do; vendor packages and
virtual devices generally do not). `--exclusive` on the same endpoints: `AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED` for every format — the per-endpoint
policy `{b3f8fa53-0004-438e-9003-51a46e139bfc},3 = 0` ("Allow applications to take exclusive control" unticked, as vendor installers leave it).
Fallback chain behaved as specified. **Conclusion:** on a typical consumer box neither shared low-latency nor exclusive is available without user
action (tick the exclusive checkbox, or swap Realtek UAD for the in-box HDA driver); a class-compliant pro interface (MOTU M4 on order) is the
expected happy path for both. Everything is in place to measure it the moment such a device is plugged in.

### 4.2 Exclusive mode details

`GetDevicePeriod` → minimum period; candidate formats at the endpoint's rate/channels in order: mix format, EXTENSIBLE PCM16, EXTENSIBLE Float32,
plain PCM16 — only formats `AudioFormatConverter` can render (Int16/Float32); first `IsFormatSupported(EXCLUSIVE) == S_OK` wins.
`Initialize(EXCLUSIVE, EVENTCALLBACK, min, min)`; `AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED` → `GetBufferSize`, re-activate the client, re-initialise at the
aligned duration. Buffer == period; the callback fills the whole buffer every event. `_deviceFormat` becomes the exclusive format (converter updated).

Expected on a WaveRT driver at 48 kHz: min 48–128 frames (1–2.7 ms); `LatencyMs` ≈ 2 × period.

## 5. macOS — CoreAudio (`Platforms/MacOS/`)

### 5.1 Why AudioQueue cannot do it

`AudioQueueNewOutput` is a *buffered* API: we hand it `BufferCount = 3` buffers of `BufferSizeMs` each and it schedules them; the pipeline depth IS
the latency (3 × 20 ms = 60 ms today) and Apple documents it as "not for low-latency use". Shrinking `BufferSizeMs` to 2–3 ms with three buffers
makes the callback thread (an AudioQueue-owned dispatch thread, not the HAL I/O thread) miss deadlines. The low-latency path on macOS is the
**HAL output AudioUnit (AUHAL)**: CoreAudio calls our render callback ON its real-time I/O thread, once per device buffer.

### 5.2 Target design — `CoreAudioUnitOutput`

New backend class beside `CoreAudioOutput` (AudioQueue stays as the `Default`-mode implementation — D1). Selected by `AudioOutput.Create` when
`Latency == LowLatency`.

```
AudioComponentFindNext(desc{kAudioUnitType_Output, kAudioUnitSubType_HALOutput, kAudioUnitManufacturer_Apple})
AudioComponentInstanceNew → AudioUnit
AudioUnitSetProperty(kAudioOutputUnitProperty_CurrentDevice, deviceId)          // spec 20 device selection; default = kAudioHardwarePropertyDefaultOutputDevice
AudioObjectGetPropertyData(device, kAudioDevicePropertyBufferFrameSizeRange)     // D2: query {min,max}
AudioObjectSetPropertyData(device, kAudioDevicePropertyBufferFrameSize, clamp(min, 64?..))  // ask for the minimum; re-read — the HAL may adjust
AudioUnitSetProperty(kAudioUnitProperty_StreamFormat, input scope, bus 0, ASBD)  // our consumer format; AUHAL converts to the device format
AudioUnitSetProperty(kAudioUnitProperty_SetRenderCallback, {RenderProc, GCHandle})
AudioUnitInitialize; AudioOutputUnitStart
```

`RenderProc` (`[UnmanagedCallersOnly]`, `AURenderCallback` signature `(void* refCon, AudioUnitRenderActionFlags*, const AudioTimeStamp*, UInt32 bus,
UInt32 frames, AudioBufferList*)`) fills `AudioBufferList.mBuffers[0]` through `AudioFormatConverter` exactly like the WASAPI thread does; on
callback failure write silence and set `kAudioUnitRenderAction_OutputIsSilence`. Stop = `AudioOutputUnitStop` + `AudioUnitUninitialize` +
`AudioComponentInstanceDispose`, which blocks until the I/O thread has returned (I5).

### 5.3 Real-time thread (D5)

The HAL I/O thread is already `THREAD_TIME_CONSTRAINT_POLICY`; nothing to raise. **macOS 11+:** join the device's `os_workgroup_t`
(`kAudioDevicePropertyIOThreadOSWorkgroup` → `os_workgroup_join`) so any helper threads inherit the deadline — not needed while we render
inline in the callback; record as the rule if a worker is ever added. Never `Sleep`, allocate, or take a lock in `RenderProc` (I3).

### 5.4 Device switching (spec 20)

`kAudioHardwarePropertyDefaultOutputDevice` listener (exists in `CoreAudioDeviceManager`) → stop, set `kAudioOutputUnitProperty_CurrentDevice`,
re-query buffer range (devices differ: built-in 14–4096, USB class 32–…), re-init. `PeriodFrames` may change → `DeviceSwitched`.

### 5.5 Reporting

`PeriodFrames` = the buffer frame size read back; `LatencyMs` = `kAudioDevicePropertyLatency` + `kAudioStreamPropertyLatency` + `PeriodFrames`
(device + stream + one buffer), in ms at the device rate; `UnderrunCount` from `kAudioDeviceProcessorOverload` listener.

### 5.6 Deliverables

- [ ] `_EXTERNAL_APIS/CoreAudio_AUHAL.md`: `AudioComponentDescription`, `AURenderCallbackStruct`, `AudioBufferList`/`AudioBuffer` layouts, property
      selectors (FourCC values) from `AudioUnit/AUComponent.h`, `AudioUnitProperties.h`, `CoreAudio/AudioHardware.h` — quote the headers.
- [ ] `CoreAudioInterop`: `AudioComponent*`, `AudioUnit*`, `AudioOutputUnit*` P/Invoke into `AudioToolbox.framework`; struct layout tests
      (`AudioBufferList` is `UInt32 mNumberBuffers` + inline `AudioBuffer[1]` — variable length, 8-byte aligned).
- [ ] `CoreAudioUnitOutput.cs` per §5.2; `CoreAudioOutput.cs` untouched except implementing the new D4 members (`PeriodFrames = _framesPerBuffer`).
- [ ] `SimpleAudioTest --low-latency` on a Mac: expect 64–128 frames on built-in audio, `LatencyMs` ≈ 4–8.

## 6. Linux — ALSA (`Platforms/Linux/AlsaAudioOutput.cs`)

### 6.1 What is wrong today

`snd_pcm_set_params(..., SND_PCM_ACCESS_RW_INTERLEAVED, channels, rate, soft_resample=1, latency_us = BufferSizeMs*1000)` lets ALSA choose
period/buffer for a 20 ms *total* and enables the `plug` resampler; the poll thread is an ordinary `SCHED_OTHER` thread. Two problems: no control
over the period, and no RT scheduling — at a 2 ms period a `SCHED_OTHER` thread will xrun the moment the desktop breathes.

### 6.2 Target design — explicit hw_params + RT thread

```
snd_pcm_open("hw:CARD,DEV" if LowLatency else "default", PLAYBACK, 0)   // D2: `hw:` bypasses dmix/plug; fall back to "default" (→ DriverRefused) if busy
snd_pcm_hw_params_malloc / _any
snd_pcm_hw_params_set_access(RW_INTERLEAVED)   // keep writei; mmap is a later optimisation
snd_pcm_hw_params_set_format / _set_channels / _set_rate_near(rate, dir=0)   // if the card rejects our rate/format → Default path
snd_pcm_hw_params_get_period_size_min(&min) → period = max(min, 32); snd_pcm_hw_params_set_period_size_near(&period)
snd_pcm_hw_params_set_periods_near(3)        // 3 periods: one filling, one queued, one playing — 2 is the theoretical minimum, 3 survives jitter
snd_pcm_hw_params(pcm); snd_pcm_get_params → PeriodFrames, buffer
snd_pcm_sw_params: avail_min = period, start_threshold = buffer − period
```

Poll loop unchanged in shape (`snd_pcm_poll_descriptors` + wake-up pipe) but the thread calls `pthread_setschedparam(pthread_self(), SCHED_FIFO,
{sched_priority = 70})` at start (D5). On `EPERM` → keep `SCHED_OTHER` + `ThreadPriority.Highest`, set `RealtimeRightsMissing` and continue at the
small period (it usually works on an idle box; the counter in D7 tells the truth). `-EPIPE` from `writei` → `snd_pcm_recover` + `UnderrunCount++`.

### 6.3 RT rights — the one thing the library cannot do for the user

`SCHED_FIFO` needs `RLIMIT_RTPRIO` > 0 for the user: `/etc/security/limits.d/audio.conf` (`@audio - rtprio 95`, `@audio - memlock unlimited`) and
membership of `audio` — or an RT-capable session via `rtkit` (`rtkit-daemon`, D-Bus `org.freedesktop.RealtimeKit1.MakeThreadRealtime`, what
PipeWire/PulseAudio use). Phase 1 = `pthread_setschedparam` + a clear `RealtimeRightsMissing`; Phase 2 = try `rtkit` over D-Bus when EPERM
(pure managed D-Bus message, no libdbus — I1). Document the limits.d recipe in the README.

### 6.4 PipeWire / PulseAudio hosts

On a PipeWire desktop `"default"` is `pipewire-alsa` and the effective period is PipeWire's quantum (default 1024/48000 ≈ 21 ms; the user can set
`PIPEWIRE_QUANTUM=128/48000` or `pw-metadata -n settings 0 clock.force-quantum 128`). `hw:` opens the card directly and PipeWire will refuse or
be refused (device busy) — hence the fallback in §6.2. Long-term option (not this spec): a native `libpipewire-0.3` backend requesting its own
quantum. Record which path was taken in `LatencyFallbackReason`.

### 6.5 Reporting

`PeriodFrames` = `snd_pcm_get_params` period; `LatencyMs` = buffer frames − period (queued ahead) + `snd_pcm_delay` when available;
`UnderrunCount` per §6.2.

### 6.6 Deliverables

- [ ] `_EXTERNAL_APIS/ALSA_HwParams_Sched.md`: `snd_pcm_hw_params_*` signatures (`snd_pcm_uframes_t` = `unsigned long`), `snd_pcm_sw_params_*`,
      `sched_param`/`SCHED_FIFO` values from `<sched.h>`, `pthread_setschedparam` — quote `alsa/pcm.h` and glibc headers.
- [ ] `AlsaInterop`: the hw/sw params functions + `libc` `pthread_self`/`pthread_setschedparam`/`sched_get_priority_max`.
- [ ] `AlsaAudioOutput`: `LowLatency` branch per §6.2; Default branch untouched; D4/D7 members.
- [ ] `SimpleAudioTest --low-latency` on a Linux box with and without rtprio rights; record both results in the spec.

## 7. Consumer guidance (MusicStudio and friends)

- Request `LowLatency` only in apps where a human plays live; media players gain nothing and pay CPU wake-ups.
- Keep the process-level fixes from MusicStudio spec 18 (1 ms timer, AboveNormal) on Windows: they protect the *input* side; this spec is output.
- Read `PeriodFrames` after `Start` and after `DeviceSwitched`; render granularity should be ≤ that (MusicStudio: 64-frame sub-blocks).
- Show `LatencyModeActual`/`LatencyFallbackReason`/`UnderrunCount` in a status line — users on locked-down Linux boxes need to know why.

## 8. Verification

- Unit: struct layout / constant tests per platform (rule 1 of the overview), fallback-path tests with a fake "refusing" open.
- Manual: `SimpleAudioTest --low-latency` prints `PeriodFrames`, `LatencyMs`, `UnderrunCount` after 10 s of a 440 Hz tone on each OS.
- Ground truth (later, needs spec 40 capture): loopback cable or acoustic measurement — play an impulse, capture it, difference = output+input
  latency; halve as an estimate. MusicStudio's `MidiLatencyTester` measures INPUT arrival only and is not a substitute.

## 9. Phases

- [x] **Phase A — Windows** (§4): interop, `IAudioClient3` path, Exclusive path, RAW, MMCSS, D4/D7 members on all three backends (macOS/Linux report
      their existing values), published `0.260909.80722`; MusicStudio requests `LowLatency + Raw` (`MUSICSTUDIO_AUDIO_LATENCY=exclusive|low|default`,
      `MUSICSTUDIO_AUDIO_RAW=0`) and reports `PeriodFrames/mode/fallback/underruns` in its status line. **Pending: measurement on a driver that offers a small period (MOTU M4).**
- [ ] **Phase B — Linux** (§6): explicit hw_params + `SCHED_FIFO`; needs a Linux box with a real card.
- [ ] **Phase C — macOS** (§5): AUHAL backend; needs a Mac.
- [ ] **Phase D** (optional): rtkit on Linux; workgroup join on macOS if a helper thread ever appears; native PipeWire backend if `hw:` fallback
      proves too common.

## 10. Open questions

- ~~RAW~~ — decided: separate `Processing` option (D1b), MusicStudio turns it on.
- ALSA mmap access (`snd_pcm_mmap_begin/commit`) saves one copy per period; worth it only if `writei` shows up in a profile.
- Android AAudio (`AAUDIO_PERFORMANCE_MODE_LOW_LATENCY`, exclusive-share fallback) and iOS (`AVAudioSession` preferred IO buffer duration) are the
  same knob on the mobile targets; they get sections here when those backends exist.