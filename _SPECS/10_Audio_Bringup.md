# SPEC-ANAudio: Cross-Platform Audio via Direct PInvoke

**Status:** Draft  
**Last Updated:** 2026-05-16

- [ ] Milestone 1 — Core abstraction + Windows WASAPI backend
- [ ] Milestone 2 — Linux ALSA backend
- [ ] Milestone 3 — macOS CoreAudio backend
- [ ] Milestone 4 — Android AAudio backend (future)
- [ ] Milestone 5 — Simple mixer for layered playback

## Overview

AN.Audio is a thin cross-platform audio playback layer for .NET that talks directly to native OS audio APIs via PInvoke — no native libraries to bundle, no NuGet packages with precompiled C blobs, no dependency management headaches. Each platform backend is ~200-400 lines of C# interop code wrapping 4-8 native calls.

The .NET cross-platform audio ecosystem is fragmented: NAudio is Windows-only in practice, OpenAL NuGet packages require manual native installs, SDL2 audio requires bundling SDL2 (which FluidUI's version may not include), and miniaudio wrappers are thin unsafe PInvoke over a C library you still have to compile and ship. The actual platform audio APIs are simple — the libraries are just hiding that from you.

This module targets Mirica (UI sounds, terminal bells, notification audio, potential browser audio) and Arcane Siege (SFX, music, ambient). Both need low-latency event-driven playback of PCM data. Neither needs capture (recording) in the near term.

### Design Constraints

- **Zero native dependencies** beyond the OS itself. No `.so`, `.dylib`, or `.dll` to bundle (the audio APIs are part of the OS).
- **Event-driven only.** The OS calls us when it needs samples (or we use an event/semaphore to wake a feeder thread). No polling loops. No spin-waits.
- **NativeAOT compatible.** No reflection-based COM interop. Manual vtable calls for Windows COM interfaces.
- **Playback only** for V1. Capture is a separate concern.
- **PCM focus.** The abstraction deals in PCM buffers. Decoding (MP3, OGG, WAV) is a separate layer above this.

## Architecture

```
┌─────────────────────────────────────────────┐
│  Consumer Code (Mirica, Arcane Siege)       │
├─────────────────────────────────────────────┤
│  IAudioOutput                               │
│  - Start / Stop / Dispose                   │
│  - OnSamplesNeeded callback                 │
├──────────┬──────────┬───────────┬───────────┤
│ WASAPI   │ ALSA     │ CoreAudio │ AAudio    │
│ Backend  │ Backend  │ Backend   │ Backend   │
├──────────┴──────────┴───────────┴───────────┤
│  OS Audio API (already installed)           │
└─────────────────────────────────────────────┘
```

### Core Abstraction

```csharp
// pseudocode — illustrative only

public record struct AudioFormat(
    int SampleRate,      // 44100, 48000
    int Channels,        // 1 = mono, 2 = stereo
    SampleFormat Format  // Int16, Float32
);

public enum SampleFormat { Int16, Float32 }

/// <summary>
/// Callback invoked on the audio thread when the backend needs more samples.
/// Write interleaved PCM data into the buffer. Return the number of frames written.
/// If fewer frames are written than requested, remaining samples are filled with silence.
/// </summary>
public delegate int AudioCallback(Span<byte> buffer, int frameCount, AudioFormat format);

public interface IAudioOutput : IDisposable
{
    AudioFormat Format { get; }
    
    /// <summary>
    /// Start playback. The AudioCallback will be invoked on a dedicated audio thread
    /// whenever the OS needs more samples. This is NOT the calling thread.
    /// </summary>
    void Start(AudioCallback callback);
    
    /// <summary>
    /// Stop playback. Blocks until the audio thread has drained or stopped.
    /// After Stop(), Start() can be called again.
    /// </summary>
    void Stop();
    
    /// <summary>
    /// Estimated latency from buffer submission to DAC output, in milliseconds.
    /// </summary>
    double LatencyMs { get; }
}

public static class AudioOutput
{
    /// <summary>
    /// Create the platform-appropriate audio output.
    /// Throws PlatformNotSupportedException if no backend exists.
    /// </summary>
    public static IAudioOutput Create(AudioFormat format, int bufferSizeMs = 20)
    {
        // pseudocode
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new WasapiAudioOutput(format, bufferSizeMs);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return new AlsaAudioOutput(format, bufferSizeMs);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return new CoreAudioOutput(format, bufferSizeMs);
        throw new PlatformNotSupportedException();
    }
}
```

The `AudioCallback` is the only extension point. Consumers write PCM into the provided buffer. The mixer (Milestone 5) is just a callback that sums multiple source callbacks — it's above this layer, not inside it.

### Buffer Size and Latency

`bufferSizeMs` controls the trade-off:
- **10ms** — ~441 frames at 44100Hz. Low latency, but the callback must be fast or you underrun.
- **20ms** (default) — ~882 frames. Good for UI sounds and game SFX.
- **50ms+** — Music playback where latency doesn't matter.

The backend rounds to the nearest hardware-supported period size. The actual latency is reported via `LatencyMs` after initialization.

---

## Backend: Windows (WASAPI)

WASAPI (Windows Audio Session API) is the modern Windows audio API, available since Vista. It uses COM interfaces but the vtable layout is stable and simple to call via raw function pointers — no need for `ComImport` attributes or the RCW machinery.

### Mechanism

WASAPI supports an **event-driven shared mode**: you create an `AutoResetEvent`, hand it to `IAudioClient.SetEventHandle()`, and the audio engine signals it every period when it needs more data. A dedicated thread waits on this event and fills the buffer. This is the correct path — no polling.

### PInvoke Surface

The interop needs:
1. **MMDeviceEnumerator** — COM CoClass to get the default audio endpoint
2. **IMMDevice** — represents the audio device
3. **IAudioClient** — initialize the stream, start/stop, get buffer info
4. **IAudioRenderClient** — get the buffer pointer, write data, release

All four are COM interfaces with known IIDs and stable vtable layouts.

```csharp
// pseudocode — illustrative vtable calls

// Activation sequence:
CoCreateInstance(CLSID_MMDeviceEnumerator, IID_IMMDeviceEnumerator, out enumerator);
enumerator.GetDefaultAudioEndpoint(eRender, eConsole, out device);
device.Activate(IID_IAudioClient, CLSCTX_ALL, null, out audioClient);

// Query the mix format (what the shared-mode endpoint is currently using):
audioClient.GetMixFormat(out mixFormat);  // Usually 32-bit float, 48kHz, stereo

// Initialize in shared mode with event-driven buffering:
audioClient.Initialize(
    AUDCLNT_SHAREMODE_SHARED,
    AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
    hnsRequestedDuration,  // buffer size in 100ns units
    0,                     // periodicity (0 = default for shared mode)
    mixFormat,
    sessionGuid: null
);

audioClient.SetEventHandle(bufferReadyEvent);  // AutoResetEvent
audioClient.GetBufferSize(out bufferFrameCount);
audioClient.GetService(IID_IAudioRenderClient, out renderClient);

// Audio thread loop (event-driven, NOT polling):
while (running)
{
    WaitForSingleObject(bufferReadyEvent, INFINITE);  // blocks until signaled
    audioClient.GetCurrentPadding(out padding);
    int framesAvailable = bufferFrameCount - padding;
    renderClient.GetBuffer(framesAvailable, out dataPtr);
    // invoke AudioCallback to fill dataPtr
    renderClient.ReleaseBuffer(framesAvailable, flags);
}

audioClient.Stop();
```

### Format Negotiation

WASAPI shared mode requires you to use the endpoint's mix format (usually float32/48kHz/stereo). If the consumer requests a different format, the backend must resample. Options:
- **Preferred**: request float32/48kHz and let the consumer deal with it (Arcane Siege and Mirica can both work in float32).
- **Fallback**: use `IAudioClient.IsFormatSupported()` to negotiate, or do simple conversion in the callback wrapper (int16→float32 is trivial).

### COM Interop Without RCW

For NativeAOT compatibility, use manual vtable dispatch:

```csharp
// pseudocode — manual COM vtable call pattern

[StructLayout(LayoutKind.Sequential)]
struct IAudioClientVtbl
{
    // IUnknown
    public nint QueryInterface;  // index 0
    public nint AddRef;          // index 1
    public nint Release;         // index 2
    // IAudioClient
    public nint Initialize;      // index 3
    public nint GetBufferSize;   // index 4
    public nint GetStreamLatency;// index 5
    public nint GetCurrentPadding;// index 6
    public nint IsFormatSupported;// index 7
    public nint GetMixFormat;    // index 8
    public nint GetDevicePeriod; // index 9
    public nint Start;           // index 10
    public nint Stop;            // index 11
    public nint Reset;           // index 12
    public nint SetEventHandle;  // index 13
    public nint GetService;      // index 14
}

// To call: read vtable pointer from the interface pointer, index into it,
// and use Marshal.GetDelegateForFunctionPointer or calli.
```

### Key Constants and IIDs

```
CLSID_MMDeviceEnumerator: BCDE0395-E52F-467C-8E3D-C4579291692E
IID_IMMDeviceEnumerator:  A95664D2-9614-4F35-A746-DE8DB63617E6
IID_IAudioClient:         1CB9AD4C-DBFA-4C32-B178-C2F568A703B2
IID_IAudioRenderClient:   F294ACFC-3146-4483-A7BF-ADDCA7C260E2
AUDCLNT_SHAREMODE_SHARED: 0
AUDCLNT_STREAMFLAGS_EVENTCALLBACK: 0x00040000
```

### References

- [IAudioClient (MSDN)](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nn-audioclient-iaudioclient)
- [IAudioRenderClient (MSDN)](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nn-audioclient-iaudiorenderclient)
- [IMMDeviceEnumerator (MSDN)](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-immdeviceenumerator)
- [WASAPI Rendering a Stream (MSDN)](https://learn.microsoft.com/en-us/windows/win32/coreaudio/rendering-a-stream)
- [WAVEFORMATEX (MSDN)](https://learn.microsoft.com/en-us/windows/win32/api/mmeapi/ns-mmeapi-waveformatex)

---

## Backend: Linux (ALSA)

ALSA (Advanced Linux Sound Architecture) is the kernel-level audio API on Linux. All higher-level servers (PulseAudio, PipeWire) provide ALSA compatibility layers, so targeting ALSA directly means we work everywhere without caring which sound server is running. The library is `libasound.so.2`, which is present on essentially every Linux desktop/server installation.

### Mechanism

ALSA supports two async models:
1. **poll()-based**: get file descriptors via `snd_pcm_poll_descriptors()`, use `epoll`/`poll` to wait for writability, then `snd_pcm_writei()`. This is the recommended production approach.
2. **Async callback**: `snd_async_add_pcm_handler()` — installs a signal-based callback. Works but has known quirks (signal safety, interactions with .NET's signal handling). Avoid.

**Recommended approach**: Use a dedicated thread with `poll()` on the ALSA file descriptors. When `poll` returns (buffer space available), invoke the `AudioCallback` and `snd_pcm_writei`. The thread blocks on `poll`, not a spin-wait — this is O(1) event-driven.

### PInvoke Surface

All calls go to `libasound.so.2`:

```csharp
// pseudocode — the ~8 PInvoke declarations needed

// Open device
snd_pcm_open(out pcm_handle, "default", SND_PCM_STREAM_PLAYBACK, 0);

// Configure hardware params
snd_pcm_set_params(pcm_handle,
    format: SND_PCM_FORMAT_S16_LE,  // or SND_PCM_FORMAT_FLOAT_LE
    access: SND_PCM_ACCESS_RW_INTERLEAVED,
    channels: 2,
    rate: 48000,
    soft_resample: 1,     // allow ALSA to resample if needed
    latency_us: 20000     // 20ms target latency
);

// Get period size (frames per callback)
snd_pcm_get_params(pcm_handle, out buffer_size, out period_size);

// Get poll descriptors for event-driven waiting
int nfds = snd_pcm_poll_descriptors_count(pcm_handle);
snd_pcm_poll_descriptors(pcm_handle, fds, nfds);

// Audio thread loop (event-driven via poll):
while (running)
{
    poll(fds, nfds, timeout_ms);  // blocks until buffer space available
    snd_pcm_poll_descriptors_revents(pcm_handle, fds, nfds, out revents);
    if (revents & POLLOUT)
    {
        // invoke AudioCallback to fill buffer
        snd_pcm_writei(pcm_handle, buffer, period_size);
    }
}

snd_pcm_close(pcm_handle);
```

### Error Recovery

ALSA requires explicit recovery from underruns:

```csharp
// pseudocode
int err = snd_pcm_writei(pcm_handle, buffer, frames);
if (err == -EPIPE)  // underrun
{
    snd_pcm_prepare(pcm_handle);  // reset the stream
    snd_pcm_writei(pcm_handle, buffer, frames);  // retry
}
```

### PipeWire / PulseAudio Compatibility

Both PipeWire and PulseAudio install ALSA plugins (`pipewire-alsa`, `pulseaudio-alsa`) that intercept `snd_pcm_open("default", ...)` and route through the sound server transparently. Applications that target ALSA "just work" on PipeWire desktops. This is the standard pattern — PipeWire's own documentation recommends it for applications that don't need PipeWire-specific features.

Targeting ALSA directly (rather than PulseAudio's `libpulse` or PipeWire's native API) is the right call: one backend covers all three configurations, and `libasound.so.2` is always present.

### Key Constants

```
SND_PCM_STREAM_PLAYBACK: 0
SND_PCM_ACCESS_RW_INTERLEAVED: 3
SND_PCM_FORMAT_S16_LE: 2
SND_PCM_FORMAT_FLOAT_LE: 14
EPIPE: 32 (underrun)
```

### References

- [ALSA PCM Interface (alsa-project.org)](https://www.alsa-project.org/alsa-doc/alsa-lib/group___p_c_m.html)
- [ALSA Asynchronous Playback Howto](https://alsa.opensrc.org/Asynchronous_Playback_(Howto))
- [ALSA Programming HOWTO](https://users.suse.com/~mana/alsa090_howto.html)

---

## Backend: macOS (CoreAudio / AudioQueue)

macOS provides two levels of audio API:
- **AudioQueue Services** — high-level, callback-driven, perfect for simple playback. This is what we want.
- **AudioUnit / Core Audio HAL** — low-level, graph-based. Overkill for our needs.

AudioQueue is the simplest path: you create an output queue, allocate buffers, and the system calls your callback when it needs more data. It's available in the `AudioToolbox` framework, which is always present.

### Mechanism

AudioQueue uses an explicit callback model: `AudioQueueNewOutput` takes a function pointer. When a buffer has been consumed, the callback fires and you refill it. You typically rotate 2-3 buffers to keep the pipeline fed.

### PInvoke Surface

All calls go to `/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox`:

```csharp
// pseudocode

// Define the stream format
AudioStreamBasicDescription format = new()
{
    mSampleRate = 48000,
    mFormatID = kAudioFormatLinearPCM,
    mFormatFlags = kLinearPCMFormatFlagIsFloat | kLinearPCMFormatFlagIsPacked,
    mBytesPerPacket = 8,    // 2 channels * 4 bytes (float32)
    mFramesPerPacket = 1,
    mBytesPerFrame = 8,
    mChannelsPerFrame = 2,
    mBitsPerChannel = 32,
};

// Create output queue with callback
AudioQueueNewOutput(ref format, callbackPtr, userData, IntPtr.Zero, IntPtr.Zero, 0, out queue);

// Allocate and prime buffers (typically 3)
for (int i = 0; i < 3; i++)
{
    AudioQueueAllocateBuffer(queue, bufferByteSize, out buffers[i]);
    // Fill initial buffer via callback logic
    AudioQueueEnqueueBuffer(queue, buffers[i], 0, null);
}

// Start playback
AudioQueueStart(queue, null);

// Callback (invoked by CoreAudio when buffer consumed):
void AudioQueueCallback(IntPtr userData, IntPtr queue, IntPtr buffer)
{
    // invoke AudioCallback to fill buffer->mAudioData
    // set buffer->mAudioDataByteSize
    AudioQueueEnqueueBuffer(queue, buffer, 0, null);
}

// Stop
AudioQueueStop(queue, immediate: true);
AudioQueueDispose(queue, immediate: true);
```

### Key Constants

```
kAudioFormatLinearPCM: 0x6C70636D ("lpcm")
kLinearPCMFormatFlagIsFloat: 0x1
kLinearPCMFormatFlagIsPacked: 0x8
```

### References

- [Audio Queue Services (Apple)](https://developer.apple.com/documentation/audiotoolbox/audio_queue_services)
- [AudioStreamBasicDescription (Apple)](https://developer.apple.com/documentation/coreaudiotypes/audiostreambasicdescription)

---

## Backend: Android (AAudio) — Future

AAudio is the modern Android native audio API (API level 26+, Android 8.0+). It provides a callback-driven model very similar to the others. The native library is `libaaudio.so`, always present on supported Android versions.

Key calls: `AAudioStreamBuilder_setDataCallback`, `AAudioStream_requestStart`. The callback model is identical in spirit to the others — Android calls you when it needs samples.

This backend is deferred until Mirica or Arcane Siege targets Android. The abstraction is designed to accommodate it without changes.

### References

- [AAudio (Android NDK)](https://developer.android.com/ndk/guides/audio/aaudio/aaudio)

---

## Mixer (Milestone 5)

The mixer is NOT part of the platform backends. It sits above `IAudioOutput` as a callback that sums multiple sources:

```csharp
// pseudocode — illustrative only

public class AudioMixer
{
    private readonly List<IAudioSource> _sources = new();
    
    public int MixCallback(Span<byte> buffer, int frameCount, AudioFormat format)
    {
        // zero the buffer
        buffer.Clear();
        
        // accumulate each source (in float32, then clamp)
        foreach (var source in _sources)
        {
            source.ReadFrames(tempBuffer, frameCount);
            // add tempBuffer into buffer with volume scaling
        }
        return frameCount;
    }
}

public interface IAudioSource
{
    void ReadFrames(Span<float> buffer, int frameCount);
    float Volume { get; set; }
    bool IsPlaying { get; }
}
```

Sources can be: decoded audio clips (WAV/OGG loaded into memory), streaming decoders, procedural generators (sine waves, noise for UI). The mixer does its work in float32 and converts to the output format at the end if needed.

---

## Threading Model

Each backend creates exactly one dedicated audio thread. The thread's only job is to wait for the OS event (WASAPI event, ALSA poll, AudioQueue callback) and invoke the `AudioCallback`. The callback must be fast — no allocations, no locks that contend with the main thread, no I/O.

Rules for callback implementors:
1. Do not allocate managed memory in the callback. Pre-allocate buffers.
2. Do not take locks that the main thread holds. Use lock-free ring buffers if you need to communicate.
3. Do not call back into the audio API from the callback (except buffer enqueue on macOS which is required).
4. If you can't fill the buffer in time, write silence — an underrun click is worse than a gap.

---

## Project Structure

```
AN.Audio/
├── IAudioOutput.cs          // interface + AudioFormat + AudioCallback
├── AudioOutput.cs           // factory (platform detection)
├── Platforms/
│   ├── Windows/
│   │   ├── WasapiAudioOutput.cs
│   │   └── WasapiInterop.cs       // COM vtable structs + PInvoke
│   ├── Linux/
│   │   ├── AlsaAudioOutput.cs
│   │   └── AlsaInterop.cs         // libasound PInvoke
│   └── MacOS/
│       ├── CoreAudioOutput.cs
│       └── AudioToolboxInterop.cs  // AudioQueue PInvoke
└── Mixer/
    ├── AudioMixer.cs
    └── IAudioSource.cs
```

Platform-specific files are conditionally compiled (or runtime-selected; conditional compilation is cleaner for NativeAOT trimming).

---

## Open Questions

- **Format conversion**: If WASAPI shared mode returns float32/48kHz but the consumer wants int16/44100, should the backend do the conversion or should there be a resampler layer between the backend and the consumer? Leaning toward: the backend always exposes the native format, and a `ResamplingAudioOutput` wrapper handles conversion if needed.
- **Device selection**: V1 uses the default device only. Device enumeration and hot-plug detection is a future concern. WASAPI, ALSA, and CoreAudio all support enumeration but the APIs differ significantly.
- **Error reporting**: Underruns are common during development. Should the backend surface underrun counts or just log them?

## Alternatives Considered

- **miniaudio via PInvoke**: Well-designed C library, but still requires compiling and bundling a native binary per platform. The whole point of this spec is to avoid that — the OS audio APIs are already on the machine.
- **SDL2 audio subsystem**: FluidUI uses SDL2, but the SDL2 build in use doesn't include audio. Even if it did, SDL2 audio is just doing what we're doing here — calling WASAPI/ALSA/CoreAudio — with an extra layer of indirection and a native dependency.
- **NAudio**: Windows-only in practice. Cross-platform story is marketing, not reality.
- **OpenAL**: Requires end-user installation of the OpenAL runtime. Unacceptable.
- **NetCoreAudio**: Shells out to `ffplay`/`aplay` command-line tools. Not a serious option for low-latency interactive audio.