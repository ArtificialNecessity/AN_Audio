# SPEC-30 AN.Audio.Midi — cross-platform MIDI input (and the minimal output it needs)

- **Status:** Approved 2026-09-07; Sprint 1 implemented and hardware-validated (see §8 log)
- **Consumers:** MusicStudio `_SPECS/Bringup/17_NoteCapturePath.md` Milestone 1 (sound on keypress, zero config)
- **Ground truth:** `_EXTERNAL_APIS/WinMM_MidiIn.md` (read from the Windows SDK headers)
- **Sibling specs:** `10_Audio_Bringup.md`, `20_Audio_Device_Management.md`

- [x] Sprint 1 — Windows/WinMM input: enumerate, open-all, short messages, SysEx Identity Request/Reply, polling hot-plug, ring + raw callback, tests (55 xunit + `SimpleMidiTest` on Akai MPK mini IV)
- [x] Sprint 1b (2026-09-07) — D24 drop `AN.Audio` dependency; D25 MIDI 2.0-ready `MidiInput_Message` (UMP-word storage, `Protocol`, 64-bit `DriverTimestamp`, two accessor tiers, raw fields internal); D26 `Midi_RelativeDecode` / `Midi_BitScaling`; 109 xunit
- [ ] Sprint 2 — Hot-plug options: host `WM_DEVICECHANGE` hook; library-owned hidden window on a side thread
- [ ] Sprint 3 — `IMidiOutput` (full send path: short + SysEx), Windows
- [ ] Sprint 4 — macOS CoreMIDI, Linux ALSA-seq (input, then output)
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
| D1 | Packaging | New project **`src/AN.Audio.Midi/`**, assembly `AN.Audio.Midi.dll`, root namespace `AN.Audio.Midi`, **packed into the existing `ArtificialNecessity.Audio` nupkg** (both DLLs in `lib/<tfm>/`). Same `AN.Audio.Build.props` version stream. | User decision. One package to reference; playback-only consumers pay nothing at runtime beyond an unused DLL. |
| D2 | Project layout | Mirrors `AN.Audio`: `Internal/`, `Platforms/Windows|MacOS|Linux|Android|iOS/`, tests in **`tests/AN.Audio.Midi.Tests/`** (xunit, like `AN.Audio.Tests`) plus a console smoke `tests/SimpleMidiTest/`. | Consistency with the sibling project. |
| D3 | Windows backend | **WinMM `midiIn*`/`midiOut*`** via `[UnmanagedCallersOnly]` function-pointer callback. | Zero deps, every Windows version, short messages pre-packed (no buffer parsing). WinRT needs CsWinRT/TFM pinning; MIDI Services needs an installed runtime. |
| D4 | Delivery to consumer | **Both**: `IMidiInput.Ring` (library-owned SPSC `MidiInput_MessageRing`, default) and an optional raw `MidiInput_Callback`. | The consumer's audio thread drains the ring with `TryDequeue` — this is the driver-thread → audio-thread hand-off; apps needing zero-copy fan-out use the raw callback. |
| D5 | Drop policy | Ring grows from `RingInitialCapacity` to `RingMaxCapacity` (D23); only when **at max and full** is **the newest message dropped and `DroppedCount` incremented**; `Overflow` event fires (rate-limited) on a background thread. Never blocks the driver thread. In raw-callback mode the ring is unused: `DroppedCount` stays 0 and `Overflow` never fires. | Spec 17 wants nothing dropped; 16384 × 16 B = 256 KB is enormous burst headroom, and a counter makes any drop visible instead of silent. |
| D6 | Timestamps | Every message carries `ArrivalTicks` (`Stopwatch.GetTimestamp()` at callback entry) AND `DriverTimestamp` (backend-native; WinMM = ms since Start). | WinMM's 1 ms clock is too coarse for audio-frame correlation; CoreMIDI's host-time isn't. The library never knows the audio clock — correlation is the app's job. |
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
| D21 | Packaging mechanics | New thin **`src/AN.Audio.Package/AN.Audio.Package.csproj`** is the ONLY `IsPackable=true` project: `PackageId=ArtificialNecessity.Audio`, `ProjectReference` to `AN.Audio` and `AN.Audio.Midi` with `PrivateAssets="all"`, `TargetsForTfmSpecificBuildOutput` copies both DLLs into `lib/<tfm>/`. `AN.Audio.csproj` becomes `IsPackable=false`; README/LICENSE pack items and `<Description>` move to the package project; both `cmd/*publish*.ps1` pack the package project. Verify by unzipping the nupkg in Sprint 1. | `AN.Audio.Midi` → `AN.Audio` is the only reference direction; a pack recipe inside `AN.Audio.csproj` would need the reverse reference (circular). User approved 2026-09-07. |
| D22 | `MIDI_IO_STATUS` / `MIM_MOREDATA` | Open with `MIDI_IO_STATUS` by default (`MidiInput_Options.EnableIoStatus`). `MIM_MOREDATA` **is delivered exactly like `MIM_DATA`** (it is real data) and increments `MidiInput_MessageRing.LagCount`. It never touches `DroppedCount`. | `DroppedCount` means "we discarded a message" and nothing else; driver-reported lag is a separate, visible signal. User rule 2026-09-07. |
| D23 | Growable ring | `MidiInput_MessageRing` is an **encapsulated, growable** SPSC queue: `RingInitialCapacity` (default 1024) up to `RingMaxCapacity` (default 16384 = 512 KB of 32-byte messages, D25; `Max == Initial` means fixed). **No power-of-two requirement** (modulo instead of mask; irrelevant cost). Internally a circular linked list of fixed-size segments (segment size = initial capacity): when the tail segment is full and the next segment is still the consumer's, the producer (driver thread) inserts one more segment with a single `Volatile.Write` — nothing is copied, the consumer simply follows `Next` after draining. Segments the consumer has LEFT are reused, never freed, so once the queue has grown to its working size the steady state is allocation-free. At `MaxCapacity` the D5 drop-newest rule applies. **Guarantee:** the producer may only reuse a segment the consumer has left (writing into the consumer's current segment would reorder), and the consumer leaves a drained segment on its next dequeue — so the guaranteed undropped backlog is `Max − Initial` (default 15360), not `Max`. Consumers never see storage; only `TryDequeue`/`DequeueAll` and the counters (`CurrentCapacity`, `GrowCount`, `DroppedCount`, `LagCount`). | User preference 2026-09-07: a bounded number of allocations while converging to stability is acceptable; a per-loop allocation is not. Segments (not realloc+copy) because the consumer may be mid-dequeue when the producer grows. Optional future refinement: a background thread pre-allocates one spare segment so the driver thread never allocates. |
| D24 | No `AN.Audio` dependency | `AN.Audio.Midi` has **no `ProjectReference` to `AN.Audio`**. The only thing it used was the `DeviceChangeType` enum; it now has its own `MidiInput_DeviceChangeType { Added, Removed }` (D13 naming). The umbrella package (D21) is kept as the packaging vehicle because neither library project should carry pack metadata, but it is no longer *forced* by a circular reference. | User 2026-09-07: the dependency was "not necessary". A 60 KB DLL should not drag in another 60 KB DLL for one enum. |
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
    public long DriverTimestamp { get; }      // backend-native 64-bit (WinMM: ms since Start; SDK/CoreMIDI: host clock)
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
/// Stable per device TYPE (D11): derived from the Identity Reply (manufacturer, family, member) when available, else a hash of (szPname, wMid, wPid) flagged IsUnknownType.
public readonly record struct MidiInput_DeviceTypeId(Guid Value, bool IsUnknownType) { static FromIdentity(MidiInput_DeviceIdentity); static UnknownFromDriverCaps(string portName, ushort driverMid, ushort driverPid); }

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

`AN.Audio.Midi` has **no reference to `AN.Audio`** (D24). The nupkg is still assembled by `AN.Audio.Package` (D21) so that neither library project carries pack metadata.

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

As built (55 tests, all passing):

- `MidiInput_MessageTests`: kind decode for every status byte (theory), channel nibble, velocity-0 folding (D15), pitch-bend 14-bit LSB-first, struct size == 16 (`Unsafe.SizeOf`).
- `MidiInput_MessageRingTests`: empty; order + wrap within one segment; fixed-size drop-newest + `DroppedCount` and recovery after drain; **growth** to max with `GrowCount`/`CurrentCapacity`; order across segment boundaries under random interleaving (bounded by the `Max − Initial` guarantee, D23); grown ring reuses segments with no further growth; steady-state zero allocations (1e6 ops); real two-thread producer/consumer 500k messages in order; `LagCount` independent of `DroppedCount`; invalid capacities throw.
- `Midi_SysExTests`: request bytes; Identity Reply parse for 3-byte (Arturia `00 20 6B`) and 1-byte (Roland `41`) ids; rejects non-identity/truncated; `MidiInput_DeviceTypeId` deterministic and distinct per identity, unknown-type fallback deterministic; reassembler single-buffer, 3-fragment join, aborted message discarded, `SysExMaxBytes` cap enforced with recovery, continuation-without-start ignored.
- `WinMm_InteropLayoutTests`: `sizeof(WinMm_MidiHdr)` == 120 on x64, `WinMm_MidiInCaps2W` == 124, `WinMm_MidiOutCapsW` == 84, every enum value equals its SDK constant (D12), `UnpackShortMessage` byte order, `midiInGetNumDevs()` callable, enumeration keys unique (this test found the same-`NameGuid` issue).
- `tests/SimpleMidiTest/` (`cmd/test-midi.cmd`): lists ports, opens all, prints each message with both timestamps + decoded form, prints opened/lost/identity/SysEx/overflow events; keys `i` (identity request), `s` (stats), `q` (quit). Manual, not CI.

## 7. Documentation changes (this sprint)

All done 2026-09-07:

- [x] `README.md`: MIDI section (snippet, key-types table, platform table); intro says “audio playback and MIDI input”; the “Playback only” principle became “Scope — output, capture and MIDI I/O behind one API shape; capture not yet implemented”; project-structure tree lists the new projects and `cmd/test-midi.cmd`.
- [x] `AN.Audio.Package.csproj` carries all package metadata and the `<Description>` “Cross-platform audio and MIDI …”; `AN.Audio.csproj` is `IsPackable=false` (D21).
- [x] Publish scripts rewritten as cross-platform C# (`cmd/publish-local.cs`, `cmd/nuget-publish-audio.cs`, with `.cmd` runners); the `.ps1` versions were deleted. Both pack `src/AN.Audio.Package` only and pass one timestamp to MSBuild so both DLLs and the nupkg share a version.
- [x] `cmd/test-midi.cmd` runs `tests/SimpleMidiTest`.
- [x] `AN.Audio.slnx` lists `AN.Audio.Midi`, `AN.Audio.Package`, `AN.Audio.Midi.Tests`, `SimpleMidiTest`.

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