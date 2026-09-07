# SPEC-30 AN.Audio.Midi — cross-platform MIDI input (and the minimal output it needs)

- **Status:** Approved 2026-09-07; Sprint 1 implemented and hardware-validated (see §8 log)
- **Consumers:** MusicStudio `_SPECS/Bringup/17_NoteCapturePath.md` Milestone 1 (sound on keypress, zero config)
- **Ground truth:** `_EXTERNAL_APIS/WinMM_MidiIn.md` (read from the Windows SDK headers)
- **Sibling specs:** `10_Audio_Bringup.md`, `20_Audio_Device_Management.md`

- [x] Sprint 1 — Windows/WinMM input: enumerate, open-all, short messages, SysEx Identity Request/Reply, polling hot-plug, ring + raw callback, tests (55 xunit + `SimpleMidiTest` on Akai MPK mini IV)
- [ ] Sprint 2 — Hot-plug options: host `WM_DEVICECHANGE` hook; library-owned hidden window on a side thread
- [ ] Sprint 3 — `IMidiOutput` (full send path: short + SysEx), Windows
- [ ] Sprint 4 — macOS CoreMIDI, Linux ALSA-seq (input, then output)
- [ ] Sprint 5 — Android (`android.media.midi`), iOS (CoreMIDI) once AN.Audio itself has those platforms
- [ ] Later — Windows MIDI Services / MIDI 2.0 UMP backend behind the same interface

## 1. Overview

AN.Audio does output PCM. AN.Audio.Midi does MIDI events, with the same philosophy: PInvoke straight into the OS API, one AnyCPU
managed DLL, no native blobs, one hot callback that must be allocation-free, control-plane events on background threads.

The founding consumer requirement (spec 17): **plug in a controller, press a key, hear a note — no dialogs.** So the default
policy opens every input port, merges them into one stream, and hot-plug arrivals join automatically.

## 2. Decisions

| # | Decision | Choice | Rationale |
|---|---|---|---|
| D1 | Packaging | New project **`src/AN.Audio.Midi/`**, assembly `AN.Audio.Midi.dll`, root namespace `AN.Audio.Midi`, **packed into the existing `ArtificialNecessity.Audio` nupkg** (both DLLs in `lib/<tfm>/`). Same `AN.Audio.Build.props` version stream. | User decision. One package to reference; playback-only consumers pay nothing at runtime beyond an unused DLL. |
| D2 | Project layout | Mirrors `AN.Audio`: `Internal/`, `Platforms/Windows|MacOS|Linux|Android|iOS/`, tests in **`tests/AN.Audio.Midi.Tests/`** (xunit, like `AN.Audio.Tests`) plus a console smoke `tests/SimpleMidiTest/`. | Consistency with the sibling project. |
| D3 | Windows backend | **WinMM `midiIn*`/`midiOut*`** via `[UnmanagedCallersOnly]` function-pointer callback. | Zero deps, every Windows version, short messages pre-packed (no buffer parsing). WinRT needs CsWinRT/TFM pinning; MIDI Services needs an installed runtime. |
| D4 | Delivery to consumer | **Both**: `IMidiInput.Ring` (library-owned SPSC `MidiInput_MessageRing`, default) and an optional raw `MidiInput_Callback`. | The consumer's audio thread drains the ring with `TryDequeue` — this is the driver-thread → audio-thread hand-off; apps needing zero-copy fan-out use the raw callback. |
| D5 | Drop policy | Ring grows from `RingInitialCapacity` to `RingMaxCapacity` (D23); only when **at max and full** is **the newest message dropped and `DroppedCount` incremented**; `Overflow` event fires (rate-limited) on a background thread. Never blocks the driver thread. In raw-callback mode the ring is unused: `DroppedCount` stays 0 and `Overflow` never fires. | Spec 17 wants nothing dropped; 16384 × 16 B = 256 KB is enormous burst headroom, and a counter makes any drop visible instead of silent. |
| D6 | Timestamps | Every message carries `ArrivalTicks` (`Stopwatch.GetTimestamp()` at callback entry) AND `DriverTimestamp` (backend-native; WinMM = ms since Start). | WinMM's 1 ms clock is too coarse for audio-frame correlation; CoreMIDI's host-time isn't. The library never knows the audio clock — correlation is the app's job. |
| D7 | Message model | 16-byte blittable `MidiInput_Message` for all **short** messages (channel voice/mode, system common, real-time). SysEx is a **separate** path (`MidiInput_SysExMessage` with pooled `byte[]`, delivered on a background thread, not through the ring). | Keeps the hot path fixed-size and allocation-free; SysEx is rare and never latency-critical. |
| D8 | SysEx scope (Sprint 1) | Receive any SysEx (reassembled), but the only **sent** SysEx is Universal Identity Request; `IMidiInput.RequestIdentity(port)` and `MidiInput_DeviceInfo.Identity` (manufacturer id, family, member, revision) populated from the reply. | User: identify gear. Requires a minimal out-port open on the same device (D9). |
| D9 | Output in Sprint 1 | `Internal` only, per request: `midiOutOpen` the paired out port (`CALLBACK_FUNCTION`), `midiOutLongMsg` the 6-byte request from a pinned header, `midiOutUnprepareHeader` + `midiOutClose` on `MOM_DONE`. The reply arrives asynchronously on the IN port; 500 ms timeout. **Any failure (`MMSYSERR_ALLOCATED` on the out port, no `MOM_DONE`, no reply) is silent**: `Identity` stays null, `TypeId.IsUnknownType = true`, `IdentityResolved` does not fire, and it is never reported as `DeviceLost`. No public `IMidiOutput` until Sprint 3. | Smallest surface that satisfies D8; holding an extra exclusive out handle under legacy WinMM would block other apps. Lifecycle to be validated with real hardware (§8). |
| D10 | Hot-plug v1 | Background poll thread, `PollIntervalMs` default 1000 (option). Diff by `MidiInput_DeviceKey`. | WinMM has no notification. Sprint 2 adds `MidiInput_HotPlugSource { Poll, HostSupplied, LibraryWindow }`: host calls `MidiInput.NotifyDeviceChange()` from its own `WM_DEVICECHANGE`; or `MidiInput.StartDeviceChangeWindow()` spins a message-only HWND on a side thread. |
| D11 | Device identity | Two identities per port. **Instance key** `MidiInput_DeviceKey` = base key + ordinal-among-same-base-key. Base key = `nameguid:{NameGuid}|{szPname}` when `NameGuid` non-zero, else `productguid:{ProductGuid}|{szPname}`, else `caps:{szPname}|{wMid}|{wPid}`; a `#n` suffix is appended for the 2nd+ port sharing a base key. WinMM index is never exposed. **Why the name and ordinal are always included:** the Akai MPK mini IV driver reports the SAME `NameGuid` for all four of its cable ports (observed 2026-09-07) — a GUID alone is NOT unique. **Type id** `MidiInput_DeviceTypeId` (GUID) = SHA-256-derived hash of the Identity Reply `(manufacturer, family, member)`; when no reply arrives, a hash of `(szPname, wMid, wPid)` flagged `IsUnknownType`. Consumers address gear by `TypeId` (per-device-type settings, mappings) and use `Key` only to tell two units apart. **Known limitation:** two identical units with GUID-less drivers → unplugging the first renumbers the second's ordinal → `DeviceLost` + `DeviceOpened` under a new `Key`; its `TypeId` is unchanged, so type-keyed consumers are unaffected. Per-unit serials (vendor SysEx / MIDI-CI) are future work. | Indices renumber on unplug. User intent 2026-09-07: stable per-device-TYPE ids are the goal of the SysEx identity work; two-identical-units is the accepted edge case until vendors expose serials. |
| D12 | Constants | **Every native constant lives in an `enum` (or `static class` of typed consts) in `Platforms/*/…Interop.cs` and is used symbolically** — `WinMm_MidiInMessage.Data`, not `0x3C3`; `WinMm_Result.Allocated`, not `4`. MIDI wire constants likewise (`Midi_Status`, `Midi_SysExId`, `Midi_UniversalSubId`). | User rule; also matches AN.Audio interop style. |
| D13 | Naming | Scope-prefixed public types: `MidiInput_*` for input-side, `Midi_*` for wire-protocol vocabulary shared by input/output, `WinMm_*` for interop. No bare `int`/`string` in public data structures. | Linguistic keying. |
| D14 | Threading contract | Raw callback: driver thread, same rules as `AudioCallback`. Ring: SPSC — exactly one consumer thread. Events (`DeviceOpened/Lost`, `Overflow`, `SysExReceived`, `IdentityResolved`): background thread; marshal to UI yourself. `Stop()` blocks until callbacks are quiescent (per-port in-flight counter + `ManualResetEventSlim`; the closing flag stops SysEx buffer re-adds; then `midiInStop` → `midiInReset` → unprepare → `midiInClose`). **Restart:** `Start()`/`Start(raw)` may be called again after `Stop()`, in either delivery mode. **In-callback guard:** `IsInsideCallback` is set on the driver thread for the duration of the callback; `Stop()`, `Dispose()` and `RequestIdentity()` throw `InvalidOperationException` when it is true (WinMM forbids `midiInClose` from the callback and would deadlock). | Identical to `IAudioOutput`. Throw rather than ignore/defer: a silent path hides a consumer bug. User 2026-09-07 (throw chosen as the assumption; ignore/defer were the alternatives). |
| D15 | Note-on velocity 0 | Delivered **as received** (`Kind == NoteOn`, `Data2 == 0`); helper `IsNoteOff` folds it. The library never rewrites wire data. | Take-streams (spec 17) must be raw. |
| D16 | Port exclusivity | `MMSYSERR_ALLOCATED` → `DeviceLost(reason: InUseByAnotherApplication)` once; retried only on the next hot-plug diff that shows the device changed. | Avoids hammering a port owned by another DAW. |
| D17 | USB devices | **Nothing USB-specific in our code.** Class-compliant USB MIDI is enumerated by the inbox class driver (`usbaudio.sys` / `usbmidi2.sys`) as WinMM ports; multi-cable devices appear as one port per cable. No WinUSB/libusb, no vendor SDKs. BLE MIDI is NOT visible through WinMM without vendor drivers — out of scope until the MIDI Services backend. | Verified against MS Windows MIDI Services docs (2026). |
| D18 | Bimodal exclusivity | Never assume a port is exclusive or shared. Legacy WinMM: one app per port (`MMSYSERR_ALLOCATED`). With **Windows MIDI Services** (Windows Update KB5074105, Feb 2026, staged rollout; WinMM routed via `wdmaud2.drv` → `midisrv.exe`) every port is multi-client. Same code path handles both; D16 covers the failure. | Ground truth 2026. |
| D19 | Persisted identity | Consumers persist `MidiInput_DeviceKey`, never the display name: MIDI Services **renamed ports** system-wide and MS explicitly warns name-keyed reconnection breaks. `MidiInput_DeviceInfo.Name` is display-only. | Ground truth 2026; reinforces D11. |
| D20 | SysEx size cap | `Midi_SysExReassembler` is bounded by `MidiInput_Options.SysExMaxBytes` (default 64 KB). A message exceeding it is discarded, `SysExReceived` is not fired, and a `SysExDiscardedCount` increments. Pooled `byte[]` never grows past the cap. | Bulk dumps can be arbitrarily long; the cold path must still be memory-bounded. User approved 2026-09-07. |
| D21 | Packaging mechanics | New thin **`src/AN.Audio.Package/AN.Audio.Package.csproj`** is the ONLY `IsPackable=true` project: `PackageId=ArtificialNecessity.Audio`, `ProjectReference` to `AN.Audio` and `AN.Audio.Midi` with `PrivateAssets="all"`, `TargetsForTfmSpecificBuildOutput` copies both DLLs into `lib/<tfm>/`. `AN.Audio.csproj` becomes `IsPackable=false`; README/LICENSE pack items and `<Description>` move to the package project; both `cmd/*publish*.ps1` pack the package project. Verify by unzipping the nupkg in Sprint 1. | `AN.Audio.Midi` → `AN.Audio` is the only reference direction; a pack recipe inside `AN.Audio.csproj` would need the reverse reference (circular). User approved 2026-09-07. |
| D22 | `MIDI_IO_STATUS` / `MIM_MOREDATA` | Open with `MIDI_IO_STATUS` by default (`MidiInput_Options.EnableIoStatus`). `MIM_MOREDATA` **is delivered exactly like `MIM_DATA`** (it is real data) and increments `MidiInput_MessageRing.LagCount`. It never touches `DroppedCount`. | `DroppedCount` means "we discarded a message" and nothing else; driver-reported lag is a separate, visible signal. User rule 2026-09-07. |
| D23 | Growable ring | `MidiInput_MessageRing` is an **encapsulated, growable** SPSC queue: `RingInitialCapacity` (default 1024) up to `RingMaxCapacity` (default 16384 = 256 KB of 16-byte messages; `Max == Initial` means fixed). **No power-of-two requirement** (modulo instead of mask; irrelevant cost). Internally a circular linked list of fixed-size segments (segment size = initial capacity): when the tail segment is full and the next segment is still the consumer's, the producer (driver thread) inserts one more segment with a single `Volatile.Write` — nothing is copied, the consumer simply follows `Next` after draining. Segments the consumer has LEFT are reused, never freed, so once the queue has grown to its working size the steady state is allocation-free. At `MaxCapacity` the D5 drop-newest rule applies. **Guarantee:** the producer may only reuse a segment the consumer has left (writing into the consumer's current segment would reorder), and the consumer leaves a drained segment on its next dequeue — so the guaranteed undropped backlog is `Max − Initial` (default 15360), not `Max`. Consumers never see storage; only `TryDequeue`/`DequeueAll` and the counters (`CurrentCapacity`, `GrowCount`, `DroppedCount`, `LagCount`). | User preference 2026-09-07: a bounded number of allocations while converging to stability is acceptable; a per-loop allocation is not. Segments (not realloc+copy) because the consumer may be mid-dequeue when the producer grows. Optional future refinement: a background thread pre-allocates one spare segment so the driver thread never allocates. |

`DroppedCount` invariant: it increments **only** when this library discards a message it received (ring full, D5; SysEx over cap, D20 — via its own counter). Never for driver-side conditions.

### USB notes for implementers

- Identity Request pairing (D8/D9): match the **port** name (`szPname`) of the in-port to an out-port with the same `szPname`; for multi-cable devices each cable pair has its own suffixed name, so this stays per-port. If no same-named out-port exists, `HasPairedOutput = false` and no request is sent.
- Vendor `.drv` drivers (old Korg/Roland) can make enumeration slow or make ports vanish; MS's guidance is to uninstall them. We surface whatever WinMM reports and never retry-loop an open that fails.
- WinMM through the MIDI Services shim exposes MIDI 1.0 semantics only (a MIDI 2.0 device appears as a 1.0 port). Sufficient for spec 17; UMP/MIDI 2.0 is the "Later" backend behind the same interface.
- When two identical devices are attached, older shim builds could open the wrong one (fixed by MS April 2026). Our GUID-first key + ordinal fallback (D11) is the right defence.

## 3. Public API (`src/AN.Audio.Midi/`)

```csharp
namespace AN.Audio.Midi;

// ---- wire vocabulary (Midi_*) --------------------------------------------------------------
public enum Midi_MessageKind : byte { NoteOff, NoteOn, PolyPressure, ControlChange, ProgramChange, ChannelPressure, PitchBend, SystemCommon, SystemRealTime, SysEx /* never in the ring */ }
public enum Midi_Status : byte { NoteOff = 0x80, NoteOn = 0x90, PolyPressure = 0xA0, ControlChange = 0xB0, ProgramChange = 0xC0, ChannelPressure = 0xD0, PitchBend = 0xE0,
                                 SysExStart = 0xF0, MtcQuarterFrame = 0xF1, SongPosition = 0xF2, SongSelect = 0xF3, TuneRequest = 0xF6, SysExEnd = 0xF7,
                                 TimingClock = 0xF8, Start = 0xFA, Continue = 0xFB, Stop = 0xFC, ActiveSensing = 0xFE, Reset = 0xFF }
public enum Midi_Controller : byte { BankSelectMsb = 0, ModWheel = 1, Expression = 11, Sustain = 64, /* … */ AllSoundOff = 120, ResetAllControllers = 121, AllNotesOff = 123 }
public enum Midi_SysExId : byte { NonCommercial = 0x7D, UniversalNonRealTime = 0x7E, UniversalRealTime = 0x7F, ExtendedManufacturer = 0x00 }
public enum Midi_UniversalNonRealTimeSubId1 : byte { GeneralInformation = 0x06 /* … */ }
public enum Midi_GeneralInformationSubId2 : byte { IdentityRequest = 0x01, IdentityReply = 0x02 }
public readonly record struct Midi_Channel(byte Index0To15);
public readonly record struct Midi_Note(byte Number);            // 60 = C4
public readonly record struct Midi_Velocity(byte Value);
public readonly record struct Midi_ManufacturerId(int Value, bool IsExtended);   // 1- or 3-byte SysEx manufacturer id

// ---- the one struct on the hot path (16 bytes, blittable) -------------------------------
[StructLayout(LayoutKind.Sequential)]                          // size asserted == 16 by test; never add fields
public readonly record struct MidiInput_Message
{
    public long   ArrivalTicks   { get; }   // Stopwatch.GetTimestamp() at callback entry (D6)
    public uint   DriverTimestamp{ get; }   // backend-native (WinMM: ms since Start)
    public byte   Status         { get; }   // raw, channel in low nibble for channel messages
    public byte   Data1          { get; }
    public byte   Data2          { get; }
    public MidiInput_PortIndex Port { get; } // byte-sized index into IMidiInput.OpenPorts
    public Midi_MessageKind Kind => …;  public Midi_Channel Channel => …;
    public bool IsNoteOn  => Kind == Midi_MessageKind.NoteOn && Data2 != 0;
    public bool IsNoteOff => Kind == Midi_MessageKind.NoteOff || (Kind == Midi_MessageKind.NoteOn && Data2 == 0);
    public Midi_Note Note => new(Data1); public Midi_Velocity Velocity => new(Data2);
    public int PitchBend14 => (Data2 << 7) | Data1;   // 0..16383, 8192 = centre
}
public readonly record struct MidiInput_PortIndex(byte Value);

// ---- SysEx (cold path) --------------------------------------------------------------------
public sealed class MidiInput_SysExMessage { public long ArrivalTicks; public MidiInput_PortIndex Port; public ReadOnlyMemory<byte> Bytes /* F0..F7 inclusive */; }
public sealed record MidiInput_DeviceIdentity(Midi_ManufacturerId Manufacturer, ushort Family, ushort Member, uint SoftwareRevision);
/// Stable per device TYPE (D11): derived from the Identity Reply (manufacturer, family, member) when available, else a hash of (szPname, wMid, wPid) flagged IsUnknownType.
public readonly record struct MidiInput_DeviceTypeId(Guid Value, bool IsUnknownType);

// ---- devices ------------------------------------------------------------------------------
public readonly record struct MidiInput_DeviceKey(string Value);       // per port INSTANCE, stable across renumbering (D11)
public sealed record MidiInput_DeviceInfo(MidiInput_DeviceKey Key, MidiInput_DeviceTypeId TypeId, string Name, MidiInput_DeviceIdentity? Identity, bool HasPairedOutput);
public enum MidiInput_LostReason { Unplugged, InUseByAnotherApplication, DriverError, Stopped }
public enum MidiInput_OpenPolicy { AllDevices /* default */, PreferenceList, None /* enumerate only */ }
public enum MidiInput_HotPlugSource { Poll /* v1 */, HostSupplied, LibraryWindow }

public sealed class MidiInput_Options
{
    public MidiInput_OpenPolicy OpenPolicy { get; init; } = MidiInput_OpenPolicy.AllDevices;
    public IReadOnlyList<MidiInput_DeviceKey>? PreferredDevices { get; init; }
    public int RingInitialCapacity { get; init; } = 1024;      // D23 — any positive value; no power-of-two requirement
    public int RingMaxCapacity     { get; init; } = 16384;     // D23 — >= RingInitialCapacity (equal = fixed size, never grows); else ArgumentException
    public int PollIntervalMs { get; init; } = 1000;
    public MidiInput_HotPlugSource HotPlugSource { get; init; } = MidiInput_HotPlugSource.Poll;
    public bool RequestIdentityOnOpen { get; init; } = true;   // D8
    public bool EnableIoStatus { get; init; } = true;          // D22: MIDI_IO_STATUS on midiInOpen (MIM_MOREDATA → LagCount)
    public int SysExBuffersPerPort { get; init; } = 2; public int SysExBufferBytes { get; init; } = 256;
    public int SysExMaxBytes { get; init; } = 64 * 1024;       // D20: reassembly cap; longer messages are discarded and counted
}

// ---- delivery -----------------------------------------------------------------------------
/// Driver thread. No alloc, no locks, no I/O. Same rules as AN.Audio.AudioCallback.
public delegate void MidiInput_Callback(in MidiInput_Message message);

/// Lock-free single-producer/single-consumer queue of MidiInput_Message. Exactly ONE consumer thread.
/// Fully encapsulated: consumers only call the methods below; the backing storage (segments, growth) is invisible (D23).
public sealed class MidiInput_MessageRing
{
    public int  CurrentCapacity { get; }                       // D23 — starts at RingInitialCapacity, grows up to RingMaxCapacity, never shrinks. Guaranteed undropped backlog = Max − Initial.
    public int  MaxCapacity     { get; }                       // D23 — RingMaxCapacity
    public long GrowCount       { get; }                       // D23 — how many times the queue grew; stable == 0 growth after warm-up
    public bool TryDequeue(out MidiInput_Message message);    // consumer side (your audio callback or UI tick)
    public int  DequeueAll(Span<MidiInput_Message> into);     // batch drain, returns count
    public long DroppedCount { get; }                          // D5 — messages WE discarded because the ring was full
    public long LagCount     { get; }                          // D22 — MIM_MOREDATA arrivals: delivered, not dropped; the driver says we were slow
}

public interface IMidiInput : IDisposable
{
    MidiInput_MessageRing Ring { get; }                        // always present; filled unless a raw callback is set
    void Start();                                              // ring delivery; callable again after Stop() (D14)
    void Start(MidiInput_Callback rawCallback);                // raw delivery (ring NOT filled; Overflow never fires); mode may differ from the previous Start (D14)
    void Stop();                                               // blocks until quiescent (D14); throws InvalidOperationException if called from inside the callback
    bool IsInsideCallback { get; }                             // true only on the driver thread while the callback is running (D14)
    IReadOnlyList<MidiInput_DeviceInfo> OpenPorts { get; }     // snapshot of open ports (compacted; NOT indexed by PortIndex)
    bool TryGetPort(MidiInput_PortIndex port, out MidiInput_DeviceInfo info);   // resolve a message's Port; slots are stable while open, reused after close
    MidiInput_OpenPolicy OpenPolicy { get; set; }
    IReadOnlyList<MidiInput_DeviceKey>? PreferredDevices { get; set; }
    void RequestIdentity(MidiInput_PortIndex port);            // async; result via IdentityResolved + DeviceInfo.Identity; silent no-op on failure/timeout (D9)

    event Action<MidiInput_DeviceInfo>? DeviceOpened;
    event Action<MidiInput_DeviceInfo, MidiInput_LostReason>? DeviceLost;
    event Action<MidiInput_DeviceInfo, MidiInput_DeviceIdentity>? IdentityResolved;
    event Action<MidiInput_SysExMessage>? SysExReceived;       // background thread, buffer valid for the call only
    event Action<MidiInput_PortIndex, long /*droppedTotal*/>? Overflow;
}

public interface IMidiInput_DeviceManager : IDisposable       // singleton per process
{
    IReadOnlyList<MidiInput_DeviceInfo> GetInputDevices();
    event Action<DeviceChangeType, MidiInput_DeviceInfo?>? DeviceListChanged;   // DeviceChangeType reused from AN.Audio
    void NotifyDeviceChange();                                 // Sprint 2: host-supplied WM_DEVICECHANGE hook
}

public static class MidiInput
{
    public static bool IsAvailable { get; }
    public static IMidiInput Create(MidiInput_Options? options = null);
    public static IMidiInput_DeviceManager? GetDeviceManager();
}
```

`AN.Audio.Midi` references `AN.Audio` (for `DeviceChangeType` and the `Internal` allocation-free helpers). No reverse reference — which is why the nupkg is assembled by `AN.Audio.Package` (D21), not by `AN.Audio.csproj`.

## 4. Consumer sketch (MusicStudio `MusicAudioHost`)

```csharp
midi = MidiInput.Create();                    // AllDevices, poll hot-plug, identity on open
midi.DeviceOpened += d => ui(() => status = $"MIDI: {d.Name}");
midi.Start();
// in Render(): drain BEFORE rendering the block
while (midi.Ring.TryDequeue(out var m))
    if (m.IsNoteOn)  instruments.LiveNoteOn(auditionInstrument, m.Note.Number, m.Velocity.Value / 127f);
    else if (m.IsNoteOff) instruments.LiveNoteOff(…);
```
(The `LiveNoteOn` signature gains velocity in MusicStudio; that is spec 17 M1 work, not this spec.)

## 5. Windows implementation plan (`Platforms/Windows/`)

| File | Contents |
|---|---|
| `WinMm_MidiInterop.cs` | `[LibraryImport("winmm.dll")]` prototypes exactly as in `_EXTERNAL_APIS/WinMM_MidiIn.md`; `WinMm_MidiHdr`, `WinMm_MidiInCaps2W`, `WinMm_MidiOutCapsW` (`LayoutKind.Sequential`, `CharSet.Unicode`); enums `WinMm_Result`, `WinMm_MidiInMessage` (`Open=0x3C1 … MoreData=0x3CC`), `WinMm_MidiOutMessage`, `WinMm_OpenFlags` (`CallbackFunction=0x30000, IoStatus=0x20`), `WinMm_HdrFlags`. **No literal constants outside this file (D12).** |
| `WinMm_MidiInPort.cs` | One open port: handle, `GCHandle` for `dwInstance`, N pinned SysEx headers/buffers (`NativeMemory`), reassembly state, `[UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] static void Proc(…)` that stamps, unpacks `dwParam1`, writes to ring or raw callback, re-adds SysEx buffers unless closing. |
| `WinMm_MidiInput.cs` | `IMidiInput`: policy, enumerate→diff→open/close, poll thread, identity request orchestration (pair out-port by name, `midiOutLongMsg`, 500 ms timeout), events. |
| `WinMm_MidiInDeviceManager.cs` | `IMidiInput_DeviceManager`, poll-based; Sprint 2 adds `NotifyDeviceChange` and the message-only window. |
| `WinMm_MidiOutLongSender.cs` (Internal) | Sprint-1 minimal sender for Identity Request; grows into `WinMm_MidiOutput` in Sprint 3. |

`Internal/`: `Midi_SysExReassembler`, `Midi_IdentityReplyParser`. `MidiInput_MessageRing` lives at the project root (public type; D23: segmented SPSC — `Volatile` head/tail per segment, `Volatile` `Next` link; producer allocates a new segment when the tail is full and below `RingMaxCapacity`; drained segments are recycled, never freed). Also `WinMm_MidiInEnumerator.cs` (shared by input and device manager: caps → `WinMm_MidiInDeviceEntry` with key/type-id, paired-output lookup by `szPname`).

## 6. Tests (`tests/AN.Audio.Midi.Tests/`, xunit)

- `MidiInput_MessageTests`: kind/channel decode for every status; velocity-0 folding; pitch-bend 14-bit; struct size == 16 (`Unsafe.SizeOf`).
- `MidiInput_MessageRingTests`: SPSC ordering; wrap within a segment; **growth**: burst past `RingInitialCapacity` → no drops, `CurrentCapacity` and `GrowCount` increase, every message dequeued in order across the segment boundary; **cap**: burst past `RingMaxCapacity` → drop-newest + `DroppedCount`; `Max == Initial` never grows; steady-state zero allocations across 1e6 enqueue/dequeue after growth has settled (`GC.GetAllocatedBytesForCurrentThread` delta == 0).
- `Midi_SysExReassemblerTests`: single-buffer, split across 3 buffers, aborted (no F7 then new F0), Identity Reply parse for 1-byte and 3-byte manufacturer ids (fixtures: Arturia `00 20 6B`, Roland `41`).
- `WinMm_InteropLayoutTests` (Windows only): `sizeof(WinMm_MidiHdr)` == 120 on x64 / 64 on x86, `WinMm_MidiInCaps2W` == 124, `midiInGetNumDevs()` callable (no device required), every enum value equals the SDK constant it names (guards D12 typos).
- `tests/SimpleMidiTest/`: console app: list devices, open all, print messages with both timestamps, send Identity Request, exit on Enter. Manual verification with real hardware; not part of CI.

## 7. Documentation changes (this sprint)

- `README.md`: status table gains a MIDI section; API section gains the `MidiInput` snippet; **“Playback only — capture/recording is a separate concern for the future” becomes “Audio input (capture) and MIDI I/O are in scope; capture is not yet implemented”.**
- `README.md` “Project Structure” tree: add `src/AN.Audio.Midi/`, `src/AN.Audio.Package/`, `tests/AN.Audio.Midi.Tests/`, `tests/SimpleMidiTest/`.
- `AN.Audio.Package.csproj` `<Description>`: “Cross-platform audio and MIDI via direct PInvoke …” (D21 moves packaging metadata here; `AN.Audio.csproj` becomes `IsPackable=false`).
- `cmd/publish-local.ps1`, `cmd/nuget-publish-audio.ps1`: pack `src/AN.Audio.Package/AN.Audio.Package.csproj` instead of `AN.Audio.csproj`.
- `AN.Audio.slnx`: add `AN.Audio.Midi`, `AN.Audio.Package`, `AN.Audio.Midi.Tests`, `SimpleMidiTest`.

## 8. Open questions

- Should `RequestIdentityOnOpen` be off by default for controllers that react badly to unsolicited SysEx? Proposed: on; add a per-device deny list only if a real device misbehaves.
- Android/iOS: AN.Audio has no backend yet; MIDI on those platforms waits for AN.Audio parity (Sprint 5 placeholder).
- Identity out-port lifecycle (D9: open → send → close on `MOM_DONE`) validated on Akai MPK mini IV 2026-09-07 (reply within the 500 ms window). Revisit only when Sprint 3 needs a persistent `IMidiOutput` handle anyway.
- Per-unit identity (serial number) is not in the MIDI 1.0 Identity Reply. Track vendor-specific serial SysEx / MIDI-CI (MIDI 2.0) as the path to disambiguating two units of the same `MidiInput_DeviceTypeId`.
- Multi-cable controllers: the MPK mini IV exposes `MPK mini IV` + `MIDIIN2..4 (MPK mini IV)`; only cable 1 has a paired output, so only it gets a real `TypeId`. Should the library propagate the identity to same-`NameGuid` sibling ports (treat the device as one unit with N cables)? Proposed for Sprint 2: `MidiInput_DeviceInfo.SiblingGroup` keyed by `NameGuid`, identity shared across the group.

### Sprint 1 hardware validation log (2026-09-07, Akai MPK mini IV, Windows 11 26200)

- Enumerated 4 ports; all 4 opened; note-on/off, pitch-bend delivered; `[lost:Stopped]` ×4 on `Stop()`.
- Identity Reply received: `mfr=47 (Akai) family=93 member=25 rev=0x4201`; reply was 35 bytes (Akai appends vendor data after the standard 15) — parser correctly reads only the standard prefix.
- Timestamps: `DriverTimestamp − ArrivalTicks(ms)` is a constant offset (~65 ms = `midiInStart` vs Stopwatch origin); pitch-bend sweep showed ~1 ms spacing on both clocks (USB poll rate).
- Device sends real `0x80` NoteOff (not velocity-0 NoteOn), so D15 folding was not exercised by this hardware.
- **Finding → D11 amended:** the driver reports the SAME `NameGuid` for all four ports. A GUID alone is not a unique key.

Resolved 2026-09-07 (moved into Decisions): packaging → D21; `MIM_MOREDATA` → D22; SysEx max size → D20; restart + in-callback guard → D14; ordinal-key limitation and device-type identity → D11.

## 9. Alternatives considered

- Separate `ArtificialNecessity.Audio.Midi` nupkg — rejected by user in favour of one package.
- Pack recipe (`TargetsForTfmSpecificBuildOutput`) inside `AN.Audio.csproj` — rejected: it would need `AN.Audio` → `AN.Audio.Midi` `ProjectReference`, which is circular (D21).
- WinRT `Windows.Devices.Midi` / Windows MIDI Services first — rejected for v1 (deps/TFM); kept as a future backend behind the same interface.
- Library-owned ring only (no raw callback) — rejected: fan-out consumers (record + audition) would need to copy.
- Folding velocity-0 note-on into `NoteOff` at the wire level — rejected (D15).
- `WM_DEVICECHANGE` as the only hot-plug source — rejected for v1: requires a window; kept as Sprint 2 options.
- Silently ignoring or deferring `Stop()`/`Dispose()` called from inside the callback — rejected in favour of throwing (D14): silent handling hides a consumer bug that would otherwise deadlock or crash inside `midiInClose`.