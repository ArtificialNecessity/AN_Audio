# SPEC-30 AN.Audio.Midi — cross-platform MIDI input (and the minimal output it needs)

- **Status:** Approved 2026-09-07. **Input implemented and hardware-validated on Windows (WinMM, §5/§8), macOS (CoreMIDI, §10) and Linux (ALSA rawmidi, §11).** Output (`IMidiOutput`) not started.
- **Consumers:** MusicStudio `_SPECS/Bringup/17_NoteCapturePath.md` Milestone 1 (sound on keypress, zero config)
- **Ground truth:** `_EXTERNAL_APIS/WinMM_MidiIn.md` (Windows SDK headers); `_EXTERNAL_APIS/CoreMidi.md` (macOS 15 SDK headers, offsets measured); `_EXTERNAL_APIS/ALSA_RawMidi.md` (kernel rawmidi devices, `/proc`/sysfs layout, `asm-generic` + `sound/asound.h` constants, measured)
- **Sibling specs:** `10_Audio_Bringup.md`, `20_Audio_Device_Management.md`

- [x] Sprint 1 — Windows/WinMM input: enumerate, open-all, short messages, SysEx Identity Request/Reply, polling hot-plug, ring + raw callback, tests (55 xunit + `SimpleMidiTest` on Akai MPK mini IV)
- [x] Sprint 1b (2026-09-07) — D24 drop `AN.Audio` dependency; D25 MIDI 2.0-ready `MidiInput_Message` (UMP-word storage, `Protocol`, 64-bit `DriverTimestamp`, two accessor tiers, raw fields internal); D26 `Midi_RelativeDecode` / `Midi_BitScaling`; 109 xunit
- [ ] Sprint 2 — Hot-plug options: host `WM_DEVICECHANGE` hook; library-owned hidden window on a side thread
- [ ] Sprint 3 — `IMidiOutput` (full send path: short + SysEx), Windows
- [x] Sprint 4a (2026-09-10) — macOS CoreMIDI input via the C API (`MIDIReadProc`/`MIDIPacketList`), OS hot-plug notifications, Identity Request via `MIDISendSysex` — §10, hardware-validated (DJControl Inpulse 500)
- [x] Sprint 4c (2026-09-10) — Linux ALSA rawmidi input: all substreams as ports, Identity Request via the same node, polling hot-plug — §11, hardware-validated (MPK mini IV, 4 cables); `Midi_ByteStreamParser` + 11 tests; 137 xunit total
- [ ] Sprint 4b — macOS UMP receive (`MIDIInputPortCreateWithProtocol`, hand-built block); Linux ALSA **sequencer** backend (timestamps, multi-client, udev-free hot-plug; §11 Q1)
- [ ] Sprint 5 — Android (`android.media.midi`), iOS (CoreMIDI) once AN.Audio itself has those platforms
- [ ] Later — Windows MIDI Services App SDK / MIDI 2.0 UMP backend behind the same interface (`MidiInput_Message.FromUmp` is the entry point; no consumer change, D25)
- [ ] Later — MIDI-CI Discovery + Property Exchange `DeviceInfo` (per-unit serial), needs Sprint 3 output; see `_EXTERNAL_APIS/UMP_MIDI2_Format.md` §MIDI-CI

## 1. Overview

AN.Audio does output PCM. AN.Audio.Midi does MIDI events, with the same philosophy: PInvoke straight into the OS API, one AnyCPU
managed DLL, no native blobs, one hot callback that must be allocation-free, control-plane events on background threads.

The founding consumer requirement (spec 17): **plug in a controller, press a key, hear a note — no dialogs.** So the default
policy opens every input port, merges them into one stream, and hot-plug arrivals join automatically.

## 2. Decisions

| # | Decision | Choice | Rationale |
|---|---|---|---|
| D1 | Packaging | New project **`src/AN.Audio.Midi/`**, assembly `AN.Audio.Midi.dll`, root namespace `AN.Audio.Midi`. ~~Packed into the existing `ArtificialNecessity.Audio` nupkg~~ → **its own package `ArtificialNecessity.Audio.Midi` (D27, 2026-09-08)**. Same `AN.Audio.Build.props` version stream. | Originally "one package to reference"; revised once D24 made the libraries independent. |
| D2 | Project layout | Mirrors `AN.Audio`: `Internal/`, `Platforms/Windows|MacOS|Linux|Android|iOS/`, tests in **`tests/AN.Audio.Midi.Tests/`** (xunit, like `AN.Audio.Tests`) plus a console smoke `tests/SimpleMidiTest/`. | Consistency with the sibling project. |
| D3 | Windows backend | **WinMM `midiIn*`/`midiOut*`** via `[UnmanagedCallersOnly]` function-pointer callback. | Zero deps, every Windows version, short messages pre-packed (no buffer parsing). WinRT needs CsWinRT/TFM pinning; MIDI Services needs an installed runtime. |
| D4 | Delivery to consumer | **Both**: `IMidiInput.Ring` (library-owned SPSC `MidiInput_MessageRing`, default) and an optional raw `MidiInput_Callback`. | The consumer's audio thread drains the ring with `TryDequeue` — this is the driver-thread → audio-thread hand-off; apps needing zero-copy fan-out use the raw callback. |
| D5 | Drop policy | Ring grows from `RingInitialCapacity` to `RingMaxCapacity` (D23); only when **at max and full** is **the newest message dropped and `DroppedCount` incremented**; `Overflow` event fires (rate-limited) on a background thread. Never blocks the driver thread. In raw-callback mode the ring is unused: `DroppedCount` stays 0 and `Overflow` never fires. | Spec 17 wants nothing dropped; 16384 × 16 B = 256 KB is enormous burst headroom, and a counter makes any drop visible instead of silent. |
| D6 | Timestamps | Every message carries `ArrivalTicks` (`Stopwatch.GetTimestamp()` at callback entry) AND `DriverTimestamp`. **Amended 2026-09-09:** `DriverTimestamp` is the driver's receipt time **converted into the `ArrivalTicks` tick base**, not a raw backend clock. WinMM: `dwParam2` (integer ms since `midiInStart`) is anchored per port by `WinMm_MidiInPort.ConvertDriverTimestamp` — the anchor is the **minimum observed `Arrival − driverMs`** (delivery latency is never negative, so the minimum converges to the true origin from above within a few messages; reset on every `midiInStart`). `MidiInput_Message.DeliveryLagTicks / DeliveryLagMs = ArrivalTicks − DriverTimestamp` is the **OS delivery latency** (driver receipt → our callback on WinMM's thread), quantised to 1 ms on WinMM. Struct stays 32 bytes (accessors only). `SimpleMidiTest` prints it per message and min/avg/max on `s`. | Investigation 2026-09-09: MIDI-in felt slow and nothing in our path could show where. Everything between `MidiInProc` and the ring is sub-µs and lock-free; the only unmeasured stage was the OS hop before our callback, and a raw per-port ms counter with an unknown origin could not expose it. Self-calibrating anchor (user's proposal) rather than a one-shot stamp at `midiInStart`, whose fixed offset never disappears. The library still never knows the audio clock. |
| D7 | Message model | Blittable `MidiInput_Message` (**superseded by D25** for layout and accessors: UMP-word storage, 32 bytes, raw fields internal) for all **short** messages (channel voice/mode, system common, real-time). SysEx is a **separate** path (`MidiInput_SysExMessage` with pooled `byte[]`, delivered on a background thread, not through the ring). | Keeps the hot path fixed-size and allocation-free; SysEx is rare and never latency-critical. |
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
| ~~D21~~ | ~~Packaging mechanics~~ **SUPERSEDED by D27** | ~~Thin `src/AN.Audio.Package` umbrella project bundling both DLLs into one nupkg.~~ Built 2026-09-07, purged 2026-09-08. Kept here only so the D-numbering and the §9 rationale stay readable. | See D27. |
| D22 | `MIDI_IO_STATUS` / `MIM_MOREDATA` | Open with `MIDI_IO_STATUS` by default (`MidiInput_Options.EnableIoStatus`). `MIM_MOREDATA` **is delivered exactly like `MIM_DATA`** (it is real data) and increments `MidiInput_MessageRing.LagCount`. It never touches `DroppedCount`. | `DroppedCount` means "we discarded a message" and nothing else; driver-reported lag is a separate, visible signal. User rule 2026-09-07. |
| D23 | Growable ring | `MidiInput_MessageRing` is an **encapsulated, growable** SPSC queue: `RingInitialCapacity` (default 1024) up to `RingMaxCapacity` (default 16384 = 512 KB of 32-byte messages, D25; `Max == Initial` means fixed). **No power-of-two requirement** (modulo instead of mask; irrelevant cost). Internally a circular linked list of fixed-size segments (segment size = initial capacity): when the tail segment is full and the next segment is still the consumer's, the producer (driver thread) inserts one more segment with a single `Volatile.Write` — nothing is copied, the consumer simply follows `Next` after draining. Segments the consumer has LEFT are reused, never freed, so once the queue has grown to its working size the steady state is allocation-free. At `MaxCapacity` the D5 drop-newest rule applies. **Guarantee:** the producer may only reuse a segment the consumer has left (writing into the consumer's current segment would reorder), and the consumer leaves a drained segment on its next dequeue — so the guaranteed undropped backlog is `Max − Initial` (default 15360), not `Max`. Consumers never see storage; only `TryDequeue`/`DequeueAll` and the counters (`CurrentCapacity`, `GrowCount`, `DroppedCount`, `LagCount`). | User preference 2026-09-07: a bounded number of allocations while converging to stability is acceptable; a per-loop allocation is not. Segments (not realloc+copy) because the consumer may be mid-dequeue when the producer grows. Optional future refinement: a background thread pre-allocates one spare segment so the driver thread never allocates. |
| D24 | No `AN.Audio` dependency | `AN.Audio.Midi` has **no `ProjectReference` to `AN.Audio`**. The only thing it used was the `DeviceChangeType` enum; it now has its own `MidiInput_DeviceChangeType { Added, Removed }` (D13 naming). (The umbrella package was briefly kept after this decision; D27 removed it.) | User 2026-09-07: the dependency was "not necessary". A 60 KB DLL should not drag in another 60 KB DLL for one enum. |
| D27 | **Packaging — supersedes D1/D21** | **Two independent packages, one per project, FluidUI-style**: `AN.Audio.csproj` → `ArtificialNecessity.Audio`, `AN.Audio.Midi.csproj` → `ArtificialNecessity.Audio.Midi` (and, spec 50, `AN.Audio.Formats.csproj` → `ArtificialNecessity.Audio.Formats`). Each csproj is `IsPackable=true` with its own metadata; README/LICENSE are packed into each. **No `AN.Audio.Package` project, no cross-project pack hooks; no inter-project references EXCEPT to `AN.Audio.Common`** (`ArtificialNecessity.Audio.Common` — the minimum shared PCM vocabulary, spec 50 D15; amended 2026-09-08. `AN.Audio.Midi` has no PCM type and does NOT reference it). Scripts `dotnet pack` the solution; `AN.Audio.Build.props`'s `DeployToLocalNuGet` copies each nupkg to the feed. Verified 2026-09-08: `cmd\publish-local.cmd` deploys both, each with `lib/net8.0|net9.0|net10.0/<own>.dll`. | User 2026-09-08: the umbrella project was cruft ("we do not want any AN.Audio.Package"); after D24 the libraries are independent, so separate packages is the honest shape. It also fixed a real bug: the umbrella's pack hook produced a 10 KB nupkg with **no DLLs** under `dotnet pack --no-build`. |
| D25 | **MIDI 2.0-ready message contract** | `MidiInput_Message` is redefined so that a future UMP backend (Windows MIDI Services SDK, CoreMIDI UMP, ALSA UMP) can fulfil the **same public contract** with no consumer change. Rules: (1) **Raw wire fields (`Status`, `Data1`, `Data2`) are no longer public** — consumers use typed accessors only. (2) The struct stores the message as **UMP words** (`Word0`, `Word1`, internal) plus `Protocol : Midi_Protocol { Midi1, Midi2 }` — what the device actually sent (UMP MT 0x1/0x2 → Midi1, MT 0x4 → Midi2). (3) `DriverTimestamp` becomes **`long`** (WinMM ms fits; SDK/CoreMIDI 64-bit clocks need it). (4) `Group : Midi_Group` (0..15) is present; WinMM always reports group 0 — the WinMM *port* is the cable; a UMP backend maps `(endpoint, group)` → one `MidiInput_PortIndex` slot so `Port` semantics are unchanged. (5) **Two accessor tiers**: *native-resolution* — `Velocity7`, `ControllerValue7`, `PitchBend14`, `Pressure7` (exact wire values when `Protocol == Midi1`; truncating downscale when `Midi2`); *protocol-neutral* — `Velocity16`, `ControllerValue32`, `PitchBend32`, `Pressure32` (exact when `Midi2`; **Min-Center-Max upscale** per M2-115-U when `Midi1` — reversible, so nothing is lost). Plus `IsRelativeController` / `RelativeDelta32` (signed) for MIDI 2.0 Relative Registered/Assignable Controller messages, which the wire itself declares relative. (6) The WinMM backend converts each 3-byte short message to a MT 0x1/0x2 word at decode time (a shift+or, allocation-free). (7) **Struct size is an implementation detail** asserted by test (`Unsafe.SizeOf`), no longer a public promise; D7's "never add fields" is retired. `MidiInput_MessageRing` capacity semantics (D5/D23) are unchanged; memory per message roughly doubles (32 B; default ring max 16384 × 32 B = 512 KB). | User 2026-09-07: "every line of MusicStudio code I write against the 1.0 shape has to be rewritten later" — the accessor *types* are the API and must be 2.0-sized now; the wire format is not. Ground truth `_EXTERNAL_APIS/UMP_MIDI2_Format.md`: Windows MIDI Services does **not** upscale MT2→MT4, so 1.0 devices' 7-bit values survive intact through every backend; `Protocol` tells the consumer which tier is native. |
| D26 | Controller semantics are **above** the library | Everything that needs knowledge the wire does not carry — relative-encoder encodings (two's-complement 7, binary-offset-64, sign-magnitude, vendor "delegate" modes), 14-bit CC MSB/LSB pairing, RPN/NRPN CC-sequence assembly, knob→parameter mappings — is **out of scope** for `AN.Audio.Midi`. The library reports values faithfully (D15/D25) and ships only **stateless, pure helpers** as vocabulary: `Midi_RelativeDecode.TwosComplement7(byte)`, `.BinaryOffset64(byte)`, `.SignMagnitude(byte)` and `Midi_BitScaling.Upscale(value, srcBits, dstBits)` / `.Downscale(...)`. The consumer (MusicStudio) owns per-`TypeId` device profiles. When MIDI-CI Property Exchange arrives, the library will *report* `ControllerResources` metadata (`type: relative`, `numSigBits`) as device info for the consumer's profile layer to consume — still above us. | User 2026-09-07: "CC mapping happens above us, we just report the values." MIDI 2.0 solved signed deltas only for Relative RC/AC (32-bit two's complement, M2-104 §7.4.8), *not* for plain CC and *not* translatable from 1.0 — so a receiver-side profile remains necessary and belongs in the app. |

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
public enum Midi_Controller : byte { BankSelectMsb = 0, ModWheel = 1, Volume = 7, Pan = 10, Expression = 11, Sustain = 64, /* … RPN/NRPN … */ AllSoundOff = 120, ResetAllControllers = 121, AllNotesOff = 123, /* … */ PolyOn = 127 }
public enum Midi_SysExId : byte { NonCommercial = 0x7D, UniversalNonRealTime = 0x7E, UniversalRealTime = 0x7F, ExtendedManufacturer = 0x00 }
public enum Midi_UniversalNonRealTimeSubId1 : byte { SampleDumpHeader = 0x01, /* … */ GeneralInformation = 0x06, /* … */ Ack = 0x7F }
public enum Midi_GeneralInformationSubId2 : byte { IdentityRequest = 0x01, IdentityReply = 0x02 }
public enum Midi_SysExDeviceId : byte { AllCall = 0x7F }
public readonly record struct Midi_Channel(byte Index0To15) { int DisplayNumber1To16 }
public readonly record struct Midi_Note(byte Number);            // 60 = C4
public readonly record struct Midi_Velocity(byte Value) { float Normalized0To1 }
public readonly record struct Midi_ManufacturerId(int Value, bool IsExtended);   // 1- or 3-byte SysEx manufacturer id

// ---- UMP / MIDI 2.0 vocabulary (D25) -------------------------------------------------------
public enum Midi_Protocol : byte { Midi1 = 1, Midi2 = 2 }                 // what the DEVICE sent; decides which accessor tier is native
public readonly record struct Midi_Group(byte Index0To15) { int DisplayNumber1To16 }   // WinMM always 0
public enum Midi_UmpMessageType : byte { Utility = 0x0, SystemCommonRealTime = 0x1, Midi1ChannelVoice = 0x2, Data64 = 0x3, Midi2ChannelVoice = 0x4, Data128 = 0x5, FlexData = 0xD, UmpStream = 0xF }
public enum Midi_Midi2Opcode : byte { RegisteredPerNoteController = 0x0, AssignablePerNoteController = 0x1, RegisteredController = 0x2, AssignableController = 0x3,
                                      RelativeRegisteredController = 0x4, RelativeAssignableController = 0x5, PerNotePitchBend = 0x6, NoteOff = 0x8, NoteOn = 0x9, PolyPressure = 0xA,
                                      ControlChange = 0xB, ProgramChange = 0xC, ChannelPressure = 0xD, PitchBend = 0xE, PerNoteManagement = 0xF }
public enum Midi_NoteAttributeType : byte { None = 0, ManufacturerSpecific = 1, ProfileSpecific = 2, Pitch7_9 = 3 }
public readonly record struct Midi_Velocity16(ushort Value) { float Normalized0To1 }
public readonly record struct Midi_Value32(uint Value)      { float Normalized0To1 }
public readonly record struct Midi_PitchBend32(uint Value)  { static readonly uint Centre = 0x80000000; float NormalizedMinus1To1 }
public static class Midi_RelativeDecode { static int TwosComplement7(byte); static int BinaryOffset64(byte); static int SignMagnitude(byte); static int SignMagnitudeInverted(byte); }   // D26: vocabulary only

// ---- the one struct on the hot path (blittable; 32 bytes asserted by test — NOT a public promise, D25) ----
[StructLayout(LayoutKind.Sequential)]
public readonly struct MidiInput_Message
{
    public long ArrivalTicks { get; }         // Stopwatch.GetTimestamp() at callback entry (D6)
    public long DriverTimestamp { get; }      // driver receipt time in the ArrivalTicks base (D6). WinMM: anchored ms; CoreMIDI: exact mach scale (D36); ALSA rawmidi: == ArrivalTicks, no stamp exists (D39)
    internal uint Word0 { get; } internal uint Word1 { get; }   // UMP words — raw wire is NOT public
    public MidiInput_PortIndex Port { get; }  // byte-sized slot; a UMP backend maps (endpoint, group) → slot
    public Midi_Protocol Protocol { get; }

    public static MidiInput_Message FromMidi1(long arrival, long driverTs, byte status, byte d1, byte d2, MidiInput_PortIndex port, Midi_Group group = default);   // → MT 0x1/0x2
    public static MidiInput_Message FromUmp(long arrival, long driverTs, uint word0, uint word1, MidiInput_PortIndex port);                                      // MT 0x1/0x2/0x4

    // addressing
    public Midi_UmpMessageType MessageType; public Midi_Group Group; public Midi_Channel Channel; public Midi_MessageKind Kind; public Midi_Midi2Opcode Midi2Opcode; public Midi_Status SystemStatus;
    public bool IsChannelMessage; public bool IsSystemMessage;
    // notes  — IsNoteOff folds MIDI 1.0 velocity-0 note-on (D15); for MIDI 2.0 velocity 0 is a real note-on
    public bool IsNoteOn; public bool IsNoteOff; public Midi_Note Note;
    public Midi_Velocity Velocity7;  public Midi_Velocity16 Velocity16;  public float VelocityNormalized;     // native 7-bit | protocol-neutral 16-bit
    public Midi_NoteAttributeType NoteAttributeType; public ushort NoteAttributeData;
    // controllers — the library reports; interpretation (encoders, 14-bit pairs) is the consumer's (D26)
    public Midi_Controller Controller; public byte ControllerValue7; public Midi_Value32 ControllerValue32;
    public bool IsRelativeController; public int RelativeDelta32;                                            // MIDI 2.0 Relative RC/AC only (wire-declared)
    public byte RegisteredControllerBank; public byte RegisteredControllerIndex;
    // program / bend
    public byte Program; public int PitchBend14; public Midi_PitchBend32 PitchBend32;
    // diagnostics only (monitors/loggers)
    public (uint Word0, uint Word1) RawUmpWords; public (byte Status, byte Data1, byte Data2) RawMidi1Bytes;
}
public readonly record struct MidiInput_PortIndex(byte Value);

// ---- SysEx (cold path) --------------------------------------------------------------------
public sealed class MidiInput_SysExMessage { public long ArrivalTicks { get; init; } public MidiInput_PortIndex Port { get; init; } public ReadOnlyMemory<byte> Bytes { get; init; } /* F0..F7 inclusive */ }
public sealed record MidiInput_DeviceIdentity(Midi_ManufacturerId Manufacturer, ushort Family, ushort Member, uint SoftwareRevision);
/// Stable per device TYPE (D11): derived from the Identity Reply (manufacturer, family, member) when available, else a hash of driver-reported caps/strings flagged IsUnknownType.
public readonly record struct MidiInput_DeviceTypeId(Guid Value, bool IsUnknownType) { static FromIdentity(MidiInput_DeviceIdentity); static UnknownFromDriverCaps(string portName, ushort driverMid, ushort driverPid) /* WinMM */; static UnknownFromDriverStrings(string displayName, string? manufacturer, string? model) /* D31: CoreMIDI, ALSA */; }

// ---- devices ------------------------------------------------------------------------------
public readonly record struct MidiInput_DeviceKey(string Value);       // per port INSTANCE, stable across renumbering (D11)
public sealed record MidiInput_DeviceInfo(MidiInput_DeviceKey Key, MidiInput_DeviceTypeId TypeId, string Name, MidiInput_DeviceIdentity? Identity, bool HasPairedOutput);
public enum MidiInput_LostReason { Unplugged, InUseByAnotherApplication, DriverError, Stopped }
public enum MidiInput_OpenPolicy { AllDevices /* default */, PreferenceList, None /* enumerate only */ }
public enum MidiInput_HotPlugSource { Poll /* default on Windows + Linux */, HostSupplied, LibraryWindow /* Windows-only, Sprint 2 */, OsNotification /* D34: macOS default; throws elsewhere */ }

public sealed class MidiInput_Options
{
    public MidiInput_OpenPolicy OpenPolicy { get; init; } = MidiInput_OpenPolicy.AllDevices;
    public IReadOnlyList<MidiInput_DeviceKey>? PreferredDevices { get; init; }
    public int RingInitialCapacity { get; init; } = 1024;      // D23 — any positive value; no power-of-two requirement
    public int RingMaxCapacity     { get; init; } = 16384;     // D23 — >= RingInitialCapacity (equal = fixed size, never grows); else ArgumentException
    public int PollIntervalMs { get; init; } = 1000;
    public MidiInput_HotPlugSource HotPlugSource { get; init; } = MidiInput_HotPlugSource.Poll;
    public bool RequestIdentityOnOpen { get; init; } = true;   // D8
    public int IdentityReplyTimeoutMs { get; init; } = 500;    // D9 — how long the out-port sender waits for MOM_DONE
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
    bool IsRunning { get; }
    bool IsInsideCallback { get; }                             // true only on the driver thread while the callback is running (D14)
    IReadOnlyList<MidiInput_DeviceInfo> OpenPorts { get; }     // snapshot of open ports (compacted; NOT indexed by PortIndex)
    bool TryGetPort(MidiInput_PortIndex port, out MidiInput_DeviceInfo info);   // resolve a message's Port; slots are stable while open, reused after close
    MidiInput_OpenPolicy OpenPolicy { get; set; }
    IReadOnlyList<MidiInput_DeviceKey>? PreferredDevices { get; set; }
    void RequestIdentity(MidiInput_PortIndex port);            // async; result via IdentityResolved + DeviceInfo.Identity; silent no-op on failure/timeout (D9)
    long SysExDiscardedCount { get; }                          // D20 — SysEx messages discarded (over cap / aborted), summed over open ports

    event Action<MidiInput_DeviceInfo>? DeviceOpened;
    event Action<MidiInput_DeviceInfo, MidiInput_LostReason>? DeviceLost;
    event Action<MidiInput_DeviceInfo, MidiInput_DeviceIdentity>? IdentityResolved;
    event Action<MidiInput_SysExMessage>? SysExReceived;       // background thread, buffer valid for the call only
    event Action<MidiInput_PortIndex, long /*droppedTotal*/>? Overflow;
}

public interface IMidiInput_DeviceManager : IDisposable       // singleton per process
{
    IReadOnlyList<MidiInput_DeviceInfo> GetInputDevices();
    event Action<MidiInput_DeviceChangeType, MidiInput_DeviceInfo?>? DeviceListChanged;   // MIDI-scoped enum (D24: no AN.Audio dependency)
    void NotifyDeviceChange();                                 // Sprint 2: host-supplied WM_DEVICECHANGE hook
}

public static class MidiInput
{
    public static bool IsAvailable { get; }
    public static IMidiInput Create(MidiInput_Options? options = null);
    public static IMidiInput_DeviceManager? GetDeviceManager();
}
```

`AN.Audio.Midi` has **no reference to `AN.Audio`** (D24) and ships as its **own package** `ArtificialNecessity.Audio.Midi` (D27). Consumers that want both reference both packages.

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
| `WinMm_MidiInEnumerator.cs` | Shared by input and device manager: `midiInGetDevCapsW` → `WinMm_MidiInDeviceEntry` (index, name, wMid/wPid, `Key`, fallback `TypeId`, `HasPairedOutput`); `FindPairedOutputIndex(name)`. Key builder per D11. |
| `WinMm_MidiInPort.cs` | One open port: handle, `GCHandle` for `dwInstance`, N pinned SysEx headers/buffers (`NativeMemory`), reassembler, in-flight counter, `[UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] static void MidiInProc(…)` that stamps, unpacks `dwParam1`, hands off to the owner (ring or raw callback), re-adds SysEx buffers unless closing, swallows exceptions at the native boundary. `Close()` = closing flag → `midiInStop` → `midiInReset` → spin until in-flight == 0 → unprepare → free → `midiInClose`. |
| `WinMm_MidiInput.cs` | `IMidiInput`: 256-slot port table (slot == `MidiInput_PortIndex`), one worker thread (poll rescan + SysEx dispatch + identity requests + overflow reporting), `[ThreadStatic]` callback depth for `IsInsideCallback`, `_failedKeys` for D16. `Start()` blocks until the first rescan completes so `OpenPorts` is populated on return. Events fire on the worker thread, wrapped so a throwing handler cannot kill it. |
| `WinMm_MidiInDeviceManager.cs` | `IMidiInput_DeviceManager` singleton; poller thread starts on first `DeviceListChanged` subscription; `NotifyDeviceChange()` wakes it immediately (usable today as the host hook; Sprint 2 adds the library-owned window). |
| `WinMm_MidiOutLongSender.cs` | Sprint-1 minimal sender: open → prepare → `midiOutLongMsg` → wait `MOM_DONE` (timeout) → unprepare → close. On timeout the header is intentionally leaked rather than freed under the driver. Grows into `WinMm_MidiOutput` in Sprint 3. |

`Internal/`: `Midi_SysExReassembler`, `Midi_IdentityReplyParser`. `MidiInput_MessageRing` lives at the project root (public type; D23: segmented SPSC — `Volatile` head/tail per segment, `Volatile` `Next` link; producer allocates a new segment when the tail is full and below `RingMaxCapacity`; drained segments are recycled, never freed). Also `WinMm_MidiInEnumerator.cs` (shared by input and device manager: caps → `WinMm_MidiInDeviceEntry` with key/type-id, paired-output lookup by `szPname`).

## 6. Tests (`tests/AN.Audio.Midi.Tests/`, xunit)

As built (137 tests, all passing; platform-specific interop tests self-skip off their OS):

- `MidiInput_MessageTests`: kind decode for every status byte (theory), channel nibble, velocity-0 folding (D15), pitch-bend 14-bit LSB-first, struct size == 16 (`Unsafe.SizeOf`).
- `MidiInput_MessageRingTests`: empty; order + wrap within one segment; fixed-size drop-newest + `DroppedCount` and recovery after drain; **growth** to max with `GrowCount`/`CurrentCapacity`; order across segment boundaries under random interleaving (bounded by the `Max − Initial` guarantee, D23); grown ring reuses segments with no further growth; steady-state zero allocations (1e6 ops); real two-thread producer/consumer 500k messages in order; `LagCount` independent of `DroppedCount`; invalid capacities throw.
- `Midi_SysExTests`: request bytes; Identity Reply parse for 3-byte (Arturia `00 20 6B`) and 1-byte (Roland `41`) ids; rejects non-identity/truncated; `MidiInput_DeviceTypeId` deterministic and distinct per identity, unknown-type fallback deterministic; reassembler single-buffer, 3-fragment join, aborted message discarded, `SysExMaxBytes` cap enforced with recovery, continuation-without-start ignored.
- `WinMm_InteropLayoutTests`: `sizeof(WinMm_MidiHdr)` == 120 on x64, `WinMm_MidiInCaps2W` == 124, `WinMm_MidiOutCapsW` == 84, every enum value equals its SDK constant (D12), `UnpackShortMessage` byte order, `midiInGetNumDevs()` callable, enumeration keys unique (this test found the same-`NameGuid` issue).
- `tests/SimpleMidiTest/` (`cmd/test-midi.cmd`): lists ports, opens all, prints each message with both timestamps + decoded form, prints opened/lost/identity/SysEx/overflow events; keys `i` (identity request), `s` (stats), `q` (quit). Manual, not CI.

## 7. Documentation changes (this sprint)

All done 2026-09-07:

- [x] `README.md`: MIDI section (snippet, key-types table, platform table); intro says “audio playback and MIDI input”; the “Playback only” principle became “Scope — output, capture and MIDI I/O behind one API shape; capture not yet implemented”; project-structure tree lists the new projects and `cmd/test-midi.cmd`.
- [x] Each library csproj carries its own package metadata (D27): `AN.Audio.csproj` → `ArtificialNecessity.Audio`, `AN.Audio.Midi.csproj` → `ArtificialNecessity.Audio.Midi`.
- [x] Publish scripts rewritten as cross-platform C# (`cmd/publish-local.cs`, `cmd/nuget-publish-audio.cs`, with `.cmd` runners); the `.ps1` versions were deleted. Both `dotnet pack` the **solution** and pass one timestamp to MSBuild so every DLL and nupkg shares a version. `publish-local` fails unless both packages land in the feed.
- [x] `cmd/test-midi.cmd` runs `tests/SimpleMidiTest`.
- [x] `AN.Audio.slnx` lists `AN.Audio.Midi`, `AN.Audio.Midi.Tests`, `SimpleMidiTest` (no package project).

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

### Hardware validation log 2 (2026-09-08, M-Audio Oxygen 49 MKV + MPK mini IV, Windows MIDI Services now active)

- **Windows MIDI Services activated between runs** (`midisrv` endpoints present). The MPK's ports were renamed by the OS (`MPK mini IV MIDI Port` / `DAW Port` / `Plugin Port` / `Software Contro`, plus a new `Din Port`) and it now reports a `ProductGuid` instead of a shared `NameGuid`, so its `MidiInput_DeviceKey` changed from `nameguid:…` to `productguid:…`. No code change was needed, but **any key persisted before the switch is stale** — exactly the D19 scenario. Consumers should fall back to `TypeId` (+ name) when a persisted `Key` is not found.
- Oxygen 49 initially did not enumerate at all (absent from Windows PnP, not just from WinMM). Root cause: a passive USB-A extension cable — bus-powered controller could not complete USB enumeration. Not a library issue; recorded because "controller is dead" will recur and the first check is *does Windows PnP list it*.
- Hot-plug: `DeviceListChanged Added ×3` and `DeviceOpened ×3` within one poll interval while running. ✔
- `DriverTimestamp` is **per port** (ms since that port's `midiInStart`); `ArrivalTicks` is global. The Oxygen showed `drv=118 ms` at `arrival=101184 ms` because it was opened 100 s after the MPK. Correlate across ports with `ArrivalTicks` only.
- **Finding → parser fixed:** the Oxygen answers the Identity Request with `F0 7E 7F 06 02 00 01 05 00 02 30 30 35 30 F7` — a 3-byte manufacturer id (`00 01 05`) but only **two** software-revision bytes (the spec says four). The original parser required 17 bytes and rejected it, leaving `TypeId.IsUnknownType = true`. `Midi_IdentityReplyParser` now requires manufacturer + family + member and accepts **1–4 revision bytes** (whatever precedes `F7`); the captured reply is a test fixture. Lesson: treat the Identity Reply's revision field as variable length in every backend.

Resolved 2026-09-07 (moved into Decisions): packaging → D21; `MIM_MOREDATA` → D22; SysEx max size → D20; restart + in-callback guard → D14; ordinal-key limitation and device-type identity → D11.

## 9. Alternatives considered

- Separate `ArtificialNecessity.Audio.Midi` nupkg — rejected by user in favour of one package.
- Umbrella `AN.Audio.Package` project bundling both DLLs into one nupkg (original D21) — **built, then purged 2026-09-08** (D27): it was a third project with no code whose only job was to work around packaging, its pack hook silently produced an empty package under `--no-build`, and once D24 removed the inter-project reference there was no reason for the two libraries to share a package at all.
- Pack recipe (`TargetsForTfmSpecificBuildOutput`) inside `AN.Audio.csproj` pulling `AN.Audio.Midi.dll` by file path — rejected 2026-09-08 in favour of D27: cross-project file-path hacks are exactly the kind of cruft the FluidUI one-package-per-project model avoids.
- WinRT `Windows.Devices.Midi` / Windows MIDI Services first — rejected for v1 (deps/TFM); kept as a future backend behind the same interface.
- Library-owned ring only (no raw callback) — rejected: fan-out consumers (record + audition) would need to copy.
- Folding velocity-0 note-on into `NoteOff` at the wire level — rejected (D15).
- `WM_DEVICECHANGE` as the only hot-plug source — rejected for v1: requires a window; kept as Sprint 2 options.
- Silently ignoring or deferring `Stop()`/`Dispose()` called from inside the callback — rejected in favour of throwing (D14): silent handling hides a consumer bug that would otherwise deadlock or crash inside `midiInClose`.

## 10. macOS implementation plan (`Platforms/MacOS/`, Sprint 4a/4b) — added 2026-09-10

- **Ground truth:** `_EXTERNAL_APIS/CoreMidi.md` (macOS 15 SDK headers + compiled probe; struct offsets measured on arm64)
- **Status:** Spec'd, not started. Hardware on hand: Hercules DJControl Inpulse 500 (1 source / 1 destination, MIDI 1.0)

### 10.1 Decisions

| # | Decision | Choice | Rationale |
|---|---|---|---|
| D28 | macOS backend | **CoreMIDI C API via `[DllImport("/System/Library/Frameworks/CoreMIDI.framework/CoreMIDI")]`** plus a 6-function CoreFoundation shim (`CFStringCreateWithCString`, `CFStringGetCString`, `CFStringGetLength`, `CFRelease`, `CFRunLoopRun`, `CFRunLoopStop`, `CFRunLoopGetCurrent`) and `mach_timebase_info` from libSystem. **No dependency on `AN.OSXBindings`**, no Objective-C runtime. | CoreMIDI is `extern "C"` end to end; every needed entry point has a C-function-pointer form. Same "PInvoke straight into the OS" philosophy as D3, one AnyCPU DLL, no native blobs. |
| D29 | Receive path, two steps | **Sprint 4a:** `MIDIInputPortCreate` + `MIDIReadProc` (`MIDIPacketList`, MIDI 1.0 bytes) → `MidiInput_Message.FromMidi1`, identical decode to WinMM. **Sprint 4b:** `MIDIInputPortCreateWithProtocol(kMIDIProtocol_2_0)` + `MIDIReceiveBlock` (`MIDIEventList`, native UMP) → `FromUmp` (D25), which requires a hand-built ObjC block literal (`isa=_NSConcreteGlobalBlock`, `invoke` = `UnmanagedCallersOnly` fn ptr; ~30 lines, no ObjC messaging). Both ports on the same client; 4b replaces 4a per-port when the endpoint reports `kMIDIPropertyProtocolID == 2`, else keeps the byte path. | The C path is `API_TO_BE_DEPRECATED` (no removal date, exported in macOS 15, what every non-UMP DAW uses) and reuses 100 % of the tested WinMM decode. UMP needs a MIDI 2.0 device to validate against; none on hand. **See open question Q1.** |
| D30 | Device key on macOS | `MidiInput_DeviceKey = "coremidi-uid:{MIDIUniqueID}"`. **No ordinal suffix** — `MIDIUniqueID` is unique by construction and persists across replug/reboot for the same device on the same USB port. Enumeration index is never exposed (as WinMM index, D11). | The D11 "two identical GUID-less units" limitation does not exist here; the key is honest without the ordinal. A device moved to a different USB port gets a new uid → new key, old one appears `Offline` — the D19 rule (fall back to `TypeId` + name) already covers this. |
| D31 | Type-id fallback on macOS | `MidiInput_DeviceTypeId.UnknownFromDriverStrings(displayName, manufacturer, model)` — **new overload** (hash of `midi-driverstrings|{displayName}|{manufacturer}|{model}`), because CoreMIDI reports manufacturer/model as strings, not `ushort` ids. The Identity-Reply path (`FromIdentity`) is unchanged and preferred. | D11 fallback must not force strings through a `ushort` signature; a second canonical form is the honest shape. |
| D32 | Paired output | **Structural**, not by name: `MIDIEndpointGetEntity(source)` → `MIDIEntityGetDestination(entity, 0)`. `HasPairedOutput = false` when the source has no entity (virtual endpoints: IAC, network sessions, other apps' `MIDISourceCreate`). | CoreMIDI groups each cable's in/out pair into one entity; the WinMM name-matching heuristic becomes unnecessary and would be wrong for IAC buses. |
| D33 | Identity Request send | `MIDISendSysex` with a pinned `MIDISysexSendRequest` (40 bytes, offsets in the API doc) and an `UnmanagedCallersOnly` completion proc; **no output port needed**. Timeout (`IdentityReplyTimeoutMs`) → set `complete = 1` (abort), wait for the completion callback, then free. Same D9 silence-on-failure semantics. Grows into `CoreMidi_MidiOutput` in Sprint 3-mac. | Mirrors `WinMm_MidiOutLongSender` one-to-one; the header explicitly documents `complete = true` as the abort mechanism. |
| D34 | Hot-plug on macOS | **`MidiInput_HotPlugSource.OsNotification` (new enum value, default on macOS)**: `MIDINotifyProc` on a library-owned run-loop thread (D35) receives `ObjectAdded / ObjectRemoved / PropertyChanged(kMIDIPropertyOffline) / SetupChanged / IOError`; each is a **trigger** for a debounced (50 ms) full re-enumerate + diff by `MIDIUniqueID` — never trusted as the device list. `Poll` still works (same re-enumerate); `NotifyDeviceChange()` maps to the same path. `HostSupplied`/`LibraryWindow` are Windows-only and throw `PlatformNotSupportedException` on macOS. **Sources with `kMIDIPropertyOffline == 1` are excluded from enumeration.** | CoreMIDI keeps a remembered device in the setup and may toggle `Offline` instead of removing it; several notifications arrive per plug event. Debounce + diff is what CoreMIDI DAWs do and matches the existing WinMM rescan/diff code. |
| D35 | Client + run loop ownership | One process-wide **`CoreMidi_Client`** singleton (created lazily, shared by `CoreMidi_MidiInput` instances and `CoreMidi_MidiInDeviceManager`) owning **one dedicated thread** that calls `MIDIClientCreate` and then `CFRunLoopRun()`. Disposed via `CFRunLoopStop` → `MIDIClientDispose` at process exit only (ports are disposed per instance). Every `IMidiInput` gets its own `MIDIPortRef`. | Header: notifications are delivered "on the runloop (thread) on which `MIDIClientCreate` was first called" — a .NET process has no run loop, so the library must own one. Apple recommends one client per process. |
| D36 | Timestamps on macOS | **Amended 2026-09-10 after measurement:** `MIDITimeStamp` and `Stopwatch.GetTimestamp()` are the **same clock but different units** — Stopwatch on macOS is `clock_gettime_nsec_np(CLOCK_UPTIME_RAW)` = `mach_absolute_time × numer/denom` **nanoseconds** (`Stopwatch.Frequency == 1e9`; agreement to 1 ns measured). So `DriverTimestamp = MIDITimeStamp × numer / denom` (`CoreMidi_Client.MachToStopwatchTicks`, exact integer math) — **no anchor needed**. Proven at client start (`Stopwatch.Frequency == 1e9` and scaled values agree < 1 ms → `MachClockIsStopwatchClock`); if the proof fails the port falls back to the D6 min-anchor on the scaled value. | The original assumption ("same unit, identity") was wrong by the timebase ratio (125/3 on Apple silicon) and was caught by the layout test on the first run; the mechanism is now measured, not assumed. `DeliveryLagMs` has ns resolution. |
| D37 | Exclusivity on macOS | `MidiInput_LostReason.InUseByAnotherApplication` is **never** raised on macOS (multi-client by design, no such error code). `kMIDINotPermitted` (-10844, Bluetooth entitlement / sandbox) → `DriverError`; `kMIDIUnknownEndpoint` / `kMIDIObjectNotFound` → `Unplugged`. | D16/D18 are Windows realities; the enum stays cross-platform, the mapping is per backend. |
| D38 | Packet iteration | Hand-rolled `MIDIPacketNext` in C#: `next = data + length`, then on `Architecture.Arm64` round up to 4 bytes (header macro); `timeStamp` read with `Unsafe.ReadUnaligned` (packet[0] sits at list offset +4). `MIDIEventPacketNext` = `words + wordCount`, no rounding. A `MIDIPacket` may hold several complete non-SysEx messages back to back → sequential status-driven parse; SysEx packets contain only SysEx (possibly fragments) → `Midi_SysExReassembler`. | The macros are `CF_INLINE` — not exported, must be reimplemented; arm64 vs x86_64 differ. Measured offsets in the API doc are asserted by test. |

### 10.2 Files

| File | Contents |
|---|---|
| `CoreMidi_Interop.cs` | `[DllImport]` prototypes exactly as in `_EXTERNAL_APIS/CoreMidi.md`; `CoreMidi_ObjectRef` (`uint`-backed record struct; `Client/Port/Device/Entity/Endpoint` as distinct record structs), `CoreMidi_UniqueId` (`int`), `CoreMidi_Status : int` enum (`NoErr = 0, InvalidClient = -10830 … UnknownError = -10845`), `CoreMidi_NotificationId : int` (`SetupChanged = 1 … IOError = 7`), `CoreMidi_ObjectType : int`, `CoreMidi_ProtocolId : int` (`Midi1 = 1, Midi2 = 2`); `Pack = 4` structs `CoreMidi_Packet`, `CoreMidi_PacketList`, `CoreMidi_EventPacket`, `CoreMidi_EventList`, `CoreMidi_Notification`, `CoreMidi_ObjectAddRemoveNotification`, `CoreMidi_PropertyChangeNotification`, `CoreMidi_IOErrorNotification`, `CoreMidi_SysexSendRequest`; `CoreMidi_PropertyKeys` (resolves `kMIDIProperty*` via `NativeLibrary.GetExport` + dereference, once). `CoreFoundation_Interop` and `Mach_Interop` in the same file. **No literal constants outside this file (D12).** |
| `CoreMidi_Client.cs` | D35 singleton: run-loop thread, `MIDIClientCreate`, `[UnmanagedCallersOnly] NotifyProc` → debounced `DeviceSetupChanged` event (internal), timebase check (D36), `CFRunLoopStop` on dispose. |
| `CoreMidi_MidiInEnumerator.cs` | `MIDIGetNumberOfSources` → for each non-Offline source: `CoreMidi_MidiInDeviceEntry` (endpoint ref, uid, `DisplayName`, manufacturer/model, `Key` per D30, fallback `TypeId` per D31, paired destination ref per D32). Shared by input and device manager. |
| `CoreMidi_MidiInPort.cs` | One `MIDIPortRef` per `IMidiInput` instance (not per source): `MIDIInputPortCreate`, `MIDIPortConnectSource(port, source, (void*)slot)` per opened source, `[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] static void ReadProc(...)` that stamps `ArrivalTicks`, walks packets (D38), hands short messages to the owner (ring / raw callback) and SysEx bytes to the reassembler, swallows exceptions at the boundary. In-flight counter + `IsInsideCallback` exactly as WinMM (D14). `DisconnectSource` on close; `MIDIPortDispose` on Stop. |
| `CoreMidi_MidiInput.cs` | `IMidiInput`: 256-slot port table, worker thread (rescan on `DeviceSetupChanged` or poll, SysEx dispatch, identity requests, overflow reporting). Same shape as `WinMm_MidiInput`; the two should share an `Internal/MidiInput_PortTable` if the duplication is >100 lines (decide during implementation). |
| `CoreMidi_MidiInDeviceManager.cs` | `IMidiInput_DeviceManager` singleton: subscribes to `CoreMidi_Client.DeviceSetupChanged`, diffs by uid, fires `DeviceListChanged`. |
| `CoreMidi_SysexSender.cs` | D33 Identity Request sender. |
| `MidiInput.cs` (root) | `IsAvailable` adds `OSPlatform.OSX`; `Create` / `GetDeviceManager` branch to `CoreMidi_*`. |

### 10.3 Public API deltas (all additive)

```csharp
public enum MidiInput_HotPlugSource { Poll, HostSupplied, LibraryWindow, OsNotification /* D34: macOS default; CoreMIDI notifications */ }
public readonly record struct MidiInput_DeviceTypeId { static UnknownFromDriverStrings(string displayName, string? manufacturer, string? model); }   // D31
// MidiInput_Options: no new members. PollIntervalMs is honoured on macOS only when HotPlugSource == Poll.
// MidiInput_Options.EnableIoStatus / SysExBuffersPerPort / SysExBufferBytes are ignored on macOS (documented on the properties).
```

### 10.4 Tests

- `CoreMidi_InteropLayoutTests` (`[Fact]`s skip unless `OperatingSystem.IsMacOS()`): `Marshal.OffsetOf` for every struct field equals the measured values in the API doc (`Packet.Length @8`, data @10, `PacketList.packet @4`, `EventPacket.words @12`, `EventList.packet @8`, `SysexSendRequest` 40 bytes with `completionProc @24`); every enum value equals its header constant (D12); `MIDIGetNumberOfSources()` callable; `kMIDIPropertyName` resolves non-null; `mach_timebase_info` non-zero; `Stopwatch.GetTimestamp()` ≡ `mach_absolute_time()` within 1 ms (D36).
- `CoreMidi_PacketWalkTests`: synthetic `MIDIPacketList` byte images (arm64 padding and x86_64 no-padding variants, built by hand from the offsets) → expected message sequence; multi-message packet; SysEx split across three packets → one `SysExReceived`.
- Existing `Midi_*` / `MidiInput_*` tests are platform-neutral and already run on macOS (verify in CI matrix).
- `tests/SimpleMidiTest` gains nothing platform-specific; add `cmd/test-midi.sh`. Hardware log to be appended to §8 (DJControl Inpulse 500).

### 10.5 Sprint 4a checklist

- [x] `_EXTERNAL_APIS/CoreMidi.md` (2026-09-10)
- [x] `CoreMidi_Interop.cs` + `CoreMidi_InteropLayoutTests` green on this Mac (126 xunit total, all passing)
- [x] `CoreMidi_Client` (run-loop thread, notify proc, timebase proof — D36 amended)
- [x] Enumerator + device manager; `SimpleMidiTest` lists and opens the DJControl Inpulse 500
- [x] Port + input: packet walker + byte parser covered by `CoreMidi_PacketWalkTests` (multi-message packet, 3-fragment SysEx with interleaved clock, truncated tail)
- [x] Identity Request via `MIDISendSysex` — DJControl answered (see log)
- [x] **Hands-on** (user, 2026-09-10): CCs and NoteOn/velocity-0 in the ring, `lag` 0.02–0.03 ms
- [ ] Hot-plug: unplug/replug → `DeviceLost`/`DeviceOpened`, key stable (D30) — hands-on
- [x] README platform table: macOS ✔ (input); `cmd/test-midi.sh` added
- [ ] Sprint 4b: UMP port via hand-built block (needs a MIDI 2.0 device or a virtual UMP source for validation)

### 10.7 Hardware log (2026-09-10, Hercules DJControl Inpulse 500, macOS 15.7.7 arm64, .NET 10)

- Enumerated 1 source: `DJControl Inpulse 500`, key `coremidi-uid:-1187549607`, `HasPairedOutput = true` (entity has 1 destination — D32 worked without name matching), `kMIDIPropertyProtocolID = 1`.
- `Start()` opened it (`[opened]`), `Stop()` closed it (`[lost:Stopped]`); no exceptions across client create → port create → connect → disconnect → port dispose.
- **Identity Reply received** via `MIDISendSysex` → reassembler → parser: `F0 7E 7F 06 02 00 01 4E 02 00 1C 00 01 00 00 00 F7` (17 bytes) — `mfr=00 01 4E` (Guillemot/Hercules, 3-byte id), family=2, member=28, rev=1. `TypeId` upgraded from the D31 string-hash to the identity-derived GUID. (The earlier piped-stdin run quit before the reply arrived — it is not silent after all.)
- Interactive (user): CCs on ch3 (#8 always 0 + #40 = jog/encoder pairs), pads as NoteOn ch8 with velocity-0 releases (D15 folding exercised: `IsNoteOff` true, `Kind == NoteOn`). Multiple messages per packet delivered in order with identical timestamps.
- **Delivery lag 0.02–0.03 ms** (CoreMIDI receive thread → our callback), ns resolution — vs. ~1 ms quantised on WinMM.
- **Finding → D36 amended:** first layout-test run showed `Stopwatch.GetTimestamp()` ≠ `mach_absolute_time()`; probe confirmed Stopwatch is the same clock in **nanoseconds** (× 125/3 here). Conversion is now an exact scale; the identity assumption is gone.
- **Finding → interop:** `MIDISysexSendRequest` is declared AFTER the header's `#pragma pack(pop)`, so it is naturally aligned (40 bytes, `data@8`); the first draft's `Pack = 4` gave 36. Layout tests caught it before any send.
- Implementation note: `CoreMidi_MidiInput` duplicates ~250 lines of `WinMm_MidiInput` (slot table, worker loop, SysEx/identity drains). `Alsa_MidiInput` (§11) is a third copy. A shared `Internal/MidiInput_PortTable` base is the obvious refactor; it is deferred until all three backends can be exercised in one CI run (each currently needs its own machine).

### 10.6 Open questions (macOS)

- **Q1 (D29):** Go straight to `MIDIInputPortCreateWithProtocol` (UMP, block-based) in 4a and skip the legacy byte path entirely? Pro: one path, no `API_TO_BE_DEPRECATED` surface, `FromUmp` exercised for real. Con: block ABI hand-rolled without a MIDI 2.0 device to prove the MT 0x4 branch; the 1.0 devices we have arrive as MT 0x2 either way. **Proposed: 4a byte path first (reuses tested decode), 4b UMP — needs user approval as the deviation from "UMP everywhere" is deliberate.**
- **Q2:** Virtual endpoints (IAC / network) — enumerate and open them under `AllDevices` (they are legitimate sources, e.g. from a DAW), or exclude by `kMIDIPropertyDriverOwner`? Proposed: include; consumers filter by `Name`/`Key`.
- **Q3:** Should `CoreMidi_Client` also be exposed as an opt-in `MidiInput_Options.RunLoopThread = Host` for apps that already own the main `CFRunLoop` (FluidUI on macOS)? Proposed: not in 4a; the dedicated thread is always correct, merely one extra thread.
- **Q4:** Minimum macOS. .NET 8 requires macOS 12+, so every API here (incl. UMP, 11.0) is always present; no runtime version gating needed. Confirm we do not target `net8.0-macos`-style TFMs (we do not — plain `net8.0`/`net9.0`/`net10.0`, AnyCPU).

## 11. Linux implementation (`Platforms/Linux/`, Sprint 4c) — implemented and hardware-validated 2026-09-10

- **Ground truth:** `_EXTERNAL_APIS/ALSA_RawMidi.md` (kernel rawmidi char devices, `/proc/asound`, sysfs; constants from `asm-generic` headers; measured on this machine)
- **Status:** Working. Hardware: Akai MPK mini IV (USB, 4 input / 5 output substreams on one rawmidi device — all 4 input cables exposed as ports)

### 11.1 Decisions

| # | Decision | Choice | Rationale |
|---|---|---|---|
| D39 | Linux backend | **ALSA rawmidi character devices** (`/dev/snd/midiC{card}D{dev}`) via five libc calls (`open`/`poll`/`read`/`write`/`close`, `[LibraryImport("libc")]`). **No `libasound`**, no sequencer. Bytes are decoded by `Internal/Midi_ByteStreamParser` (running status, interleaved real-time, system common, SysEx staged into `Midi_SysExReassembler`) → `MidiInput_Message.FromMidi1`. **Rawmidi has no timestamps: `DriverTimestamp == ArrivalTicks`** (the stamp taken after `read()` returns), so `DeliveryLagMs` is 0 by construction on this backend — documented on the property; consumers wanting real stamps need the sequencer backend (Q1). | The package's premise is zero native dependencies; rawmidi is a byte pipe the kernel exposes directly, so the only interop is libc. The ALSA sequencer has better semantics (multi-client, stamps, hot-plug events, all substreams) but is a 28-byte-union + many-ioctl surface to measure and test — the right upgrade when `IMidiOutput` lands, not the right first step. |
| D40 | Device key on Linux | `alsa-serial:{usbSerial}|{name}|D{dev}` when the card's parent USB device has an iSerial (`/sys/class/sound/cardN/device/../serial`), else `alsa:{cardId}|{name}|D{dev}`; **ordinal suffix `#n` for duplicates** of the same base key (D11 rule). Card and device numbers are never exposed — they renumber on every replug. | Verified: card 2 → card 1 across one replug. The iSerial is the per-unit identity WinMM/CoreMIDI cannot give us (the MPK's iSerial equals the serial in its Identity Reply extension). |
| D41 | Paired output / Identity Request | **Same node, opened `O_RDWR`, same substream index in both directions** (`HasPairedOutput = sub < outputCount`) when `/proc/asound/cardN/midiD` lists an `Output` section (`HasPairedOutput = true`); the 6-byte request is `write()`n to the fd. No name matching, no second handle. Falls back to `O_RDONLY` if the R/W open fails (`CanWrite = false`, no request). D9 silence-on-failure semantics unchanged. | Rawmidi pairs input and output substream 0 structurally in one device node — simpler than both other backends. |
| D42 | Exclusivity on Linux | `open()` → `EBUSY` (16) → `DeviceLost(InUseByAnotherApplication)` once, retried only after the device changes (D16). Any other `errno` → `DriverError`. `EACCES` is a permissions problem: nodes are `root:audio 0660` **plus a logind `uaccess` ACL for the seated user**, so `audio` group membership is only needed for headless/SSH sessions (verified with `getfacl`). | Rawmidi substreams are single-client (unlike the sequencer); `amidi`/`aseqdump` will collide with us, PipeWire/JACK will not. |
| D43 | Hot-plug on Linux | `MidiInput_HotPlugSource.Poll` (default): the worker re-globs `/dev/snd/midiC*D*` every `PollIntervalMs` and diffs by key. Additionally the per-port reader thread sees `POLLERR|POLLHUP` on unplug and reports `Unplugged` immediately. `OsNotification` is **not** implemented on Linux (would be `libudev` netlink monitor; throws `PlatformNotSupportedException`); `HostSupplied`/`LibraryWindow` are Windows-only. | I4 exception, same as WinMM: the kernel offers no rawmidi arrival event without udev. |
| D44 | Substreams | **Every input substream is a port.** `/proc/asound/cardN/midiD` lists `Input i` / `Output i` sections; the enumerator emits one entry per input substream (name `{name}` for cable 1, `{name} [i+1]` after — WinMM's `MIDIIN2 (…)` convention), key suffix `D{dev}S{sub}`. Before each `open()` the port issues `SNDRV_CTL_IOCTL_RAWMIDI_PREFER_SUBDEVICE` (`_IOW('U', 0x42, int)` = `0x40045542`, verified against `sound/asound.h`) on `/dev/snd/controlC{N}` — **always, including for substream 0**, because the kernel default is "first free" and would hand cable 1 to whichever port opened first. The prefer→open pair runs under a process-wide lock. If `controlC{N}` cannot be opened, substream 0 still opens by default and the others fail with `DriverError`. | The MPK mini IV has `Input 0..3` / `Output 0..4` on one node; without this the Linux backend showed one port where WinMM shows four. Verified: all four enumerate, open, and each answers the Identity Request. | |
| D45 | Thread model | One **reader thread per open port** (`poll(100 ms)` → non-blocking `read` → parse) plus the shared worker thread (rescan, SysEx dispatch, identity, overflow) exactly as WinMM/CoreMIDI. `IsInsideCallback` is set on the reader thread while parsing (D14). `Close()` sets a flag and joins the reader (≤ 100 ms). | A blocking `read()` cannot be interrupted portably from managed code; `poll` with a timeout is the shutdown primitive. One thread per port is the honest rawmidi shape (one fd each); the sequencer would collapse this to one. |

### 11.2 Files

| File | Contents |
|---|---|
| `Alsa_Interop.cs` | `[LibraryImport("libc")]` `Open`/`Close`/`Read`/`Write`/`Poll`; `Ioctl`; `PollFd` (8 bytes); `O_*`, `POLL*`, `EAGAIN`, `SNDRV_CTL_IOCTL_RAWMIDI_PREFER_SUBDEVICE` constants. **No literal constants outside this file (D12).** |
| `Alsa_MidiInEnumerator.cs` | Globs `/dev/snd/midiC*D*`; reads name + Input/Output substream counts from `/proc/asound/cardN/midiD`, card id and USB serial from sysfs → `Alsa_MidiInDeviceEntry` (path, card, device, name, `Key` per D40, fallback `TypeId` via `UnknownFromDriverStrings(name, cardId, null)` (D31), `HasPairedOutput`). Output-only devices are skipped. Shared by input and device manager. |
| `Alsa_MidiInPort.cs` | One open fd: `Open(out reason)` (R/W then R/O, D41/D42), reader thread (D45), implements `IMidi_ByteStreamSink` → `FromMidi1(arrival, arrival, …)` / `QueueSysExFromDriver`; `TryWrite` under a lock for the identity request; `Close()`. |
| `Alsa_MidiInput.cs` | `IMidiInput`: same shape as `WinMm_MidiInput` (256-slot table, worker loop, `_failedKeys`, SysEx/identity drains, overflow reporting). Identity requests go to `port.TryWrite` instead of a separate sender. |
| `Alsa_MidiInDeviceManager.cs` | `IMidiInput_DeviceManager` singleton, 1 s poller started on first subscription, `NotifyDeviceChange()` wakes it. Same shape as WinMM. |
| `Internal/Midi_ByteStreamParser.cs` | Platform-neutral stateful MIDI 1.0 byte-stream parser + `IMidi_ByteStreamSink`. Allocation-free after construction. Used by the ALSA port; **candidate to replace `CoreMidi_MidiInPort.ParsePacketBytes`** (which is the same state machine minus running status) once the macOS side can be re-tested. |
| `MidiInput.cs` (root) | `IsAvailable` includes `OSPlatform.Linux`; `Create` / `GetDeviceManager` branch to `Alsa_*`. |

### 11.3 Public API deltas

None. `MidiInput_Options.EnableIoStatus`, `SysExBuffersPerPort` are ignored on Linux; `SysExBufferBytes` sizes the parser's SysEx stage (fragments handed to the reassembler). `IdentityReplyTimeoutMs` is unused (the write is synchronous; the reply arrives whenever it arrives).

### 11.4 Tests

- `Midi_ByteStreamParserTests` (11, platform-neutral): explicit status; running status incl. velocity-0 NoteOn as received (D15); real-time bytes inside a message and inside SysEx; system common 0/1/2-byte lengths and running-status cancel; stray data dropped; SysEx over the stage size flushed as fragments and reassembled; over-cap SysEx discarded + counted + recovery (D20); status byte inside SysEx aborts it **and is counted** (found by the test: bytes still in the stage were being dropped uncounted); `Reset()`; **chunk-boundary independence** (1 byte per `Feed` ≡ one shot).
- No Linux-only layout tests: the only native struct is `pollfd` (8 bytes, `int`+`short`+`short`), constants are asserted by inspection of `asm-generic`.
- `tests/SimpleMidiTest` unchanged; `cmd/test-midi.sh` runs it.

### 11.5 Hardware log (2026-09-10, Akai MPK mini IV, Ubuntu / kernel 6.x, x86-64, .NET 10)

- Enumerated 1 port: `MPK mini IV`, `HasPairedOutput = true`, key `alsa:IV|MPK mini IV|D0` on the first run (serial lookup added after; now `alsa-serial:E82605267968110|MPK mini IV|D0`).
- `Start()` opened it R/W; Identity Request written; **Identity Reply received** through reader → parser → reassembler → `Midi_IdentityReplyParser`: `mfr=47 family=93 member=25 rev=00004201 serial=E82605267968110` (35 bytes, the Akai extension layout) — `TypeId` upgraded from the D31 string hash to the identity GUID.
- Interactive: NoteOn/NoteOff on ch1 with correct 7-bit and Min-Center-Max 16-bit velocities (`105/54093`); the device sends real `0x80` NoteOff and full status bytes, so running status was not exercised by this hardware (covered by unit tests instead).
- `lag = 0.00 ms` on every message — by construction (D39), not a measurement.
- `[lost:Stopped]` on `Stop()`; no exceptions across open → read → write → close. Replug moved the device from card 2 to card 1 (D40 rationale).
- **Finding → D44:** `/proc/asound/card1/midi0` lists `Input 0..3` / `Output 0..4` — four cables behind one node; the first build showed one port. After the prefer-subdevice ioctl: 4 ports enumerate (`MPK mini IV`, `… [2]`, `… [3]`, `… [4]`), all four open, and **each cable answers its own Identity Request** (four `[identity]` events, same serial).
- **Finding → D42:** `getfacl /dev/snd/midiC1D0` shows `user:jeske:rw-` from logind; the user is not in `audio` and did not need to be.

### 11.6 Open questions (Linux)

- **Q1 — sequencer backend.** `/dev/snd/seq` gives kernel timestamps (real `DeliveryLagMs`), multi-client access (no `EBUSY` collisions with `amidi`), port announce events (no polling), and every substream as a port. Proposed: implement as `Platforms/Linux/AlsaSeq_*` alongside rawmidi when Sprint 3 (`IMidiOutput`) reaches Linux, then make it the default and keep rawmidi as the zero-ioctl fallback.
- **Q2 — substreams `> 0` on rawmidi (D44).** **Resolved — implemented in D44** (the prefer-subdevice ioctl). The remaining race — another process opening the node between our `prefer` and `open` — is inherent to the rawmidi API and would surface as the wrong cable; the sequencer (Q1) has no such race.
- **Q3 — udev hot-plug.** `libudev` P/Invoke would make `OsNotification` real on Linux. The 1 s poll is adequate for spec 17's "plug in and play"; revisit if battery/CPU on a laptop matters.
- **Q4 — UMP rawmidi (kernel 6.5+, `/dev/snd/umpC*D*`).** Word stream → `FromUmp` directly (D25). Needs a MIDI 2.0 device.