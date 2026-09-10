# CoreMIDI (`CoreMIDI.framework`) — ground truth for AN.Audio.Midi on macOS

Source of truth: the macOS SDK headers on this machine, read 2026-09-10:
`/Library/Developer/CommandLineTools/SDKs/MacOSX.sdk/System/Library/Frameworks/CoreMIDI.framework/Headers/MIDIServices.h`
(2720 lines; types, structs, prototypes, errors, property keys), `CoreFoundation/CFString.h`, `CFRunLoop.h`, `mach/mach_time.h`.
Struct sizes/offsets below were **measured** with a compiled C probe (`clang`, arm64, macOS 15.7.7), not inferred. The probe also enumerated a live
device (Hercules DJControl Inpulse 500: 1 source, 1 destination, `kMIDIPropertyProtocolID = 1`).

**It is a pure C API.** No Objective-C runtime, no blocks are required (every block-taking function has a C-function-pointer twin or an older
C-function-pointer form that is *soft*-deprecated, `API_TO_BE_DEPRECATED`, still present and functional). `[DllImport]` straight into the framework
binary; no `AN.OSXBindings` dependency.

```csharp
const string CoreMidiLib       = "/System/Library/Frameworks/CoreMIDI.framework/CoreMIDI";              // dlopen verified (dyld shared cache)
const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
const string LibSystem         = "/usr/lib/libSystem.dylib";                                            // mach_timebase_info, mach_absolute_time
```

## Object model

```
MIDIDeviceRef  (physical or virtual device; e.g. "MPK mini IV")
  └── MIDIEntityRef  (one per logical sub-unit / USB cable; groups a source+destination PAIR)
        ├── MIDIEndpointRef  source       (we RECEIVE from sources)      MIDIEntityGetSource(entity, i)
        └── MIDIEndpointRef  destination  (we SEND to destinations)      MIDIEntityGetDestination(entity, i)
MIDIClientRef  (our per-process handle; owns MIDIPortRefs)
  └── MIDIPortRef  input port  ── MIDIPortConnectSource(port, source, connRefCon) ── N sources → ONE callback
```

- **Ports are multi-client and shareable.** There is no "port in use" error; any number of processes may connect to the same source.
- **In/out pairing is structural**, not by name: `MIDIEndpointGetEntity(source) → MIDIEntityGetDestination(entity, 0)`. Virtual endpoints
  (`MIDISourceCreate` by other apps, IAC buses) have **no** entity (`MIDIEndpointGetEntity` fails / returns 0) — treat as `HasPairedOutput = false`.
- One input port can be connected to every source; `srcConnRefCon` in the callback identifies which. We pass the port-table slot index.

## Types (MIDIServices.h)

```c
typedef UInt32 MIDIObjectRef;                        // 4 bytes, NOT pointer-sized. 0 = null. C#: uint
typedef MIDIObjectRef MIDIClientRef, MIDIPortRef, MIDIDeviceRef, MIDIEntityRef, MIDIEndpointRef;
typedef UInt64 MIDITimeStamp;                        // mach_absolute_time() host ticks
typedef SInt32 MIDIUniqueID;                         // kMIDIInvalidUniqueID = 0; persistent per endpoint across reboots/replug (same USB port+device)
typedef SInt32 OSStatus;                             // 0 = noErr
typedef unsigned long ItemCount;                     // 8 bytes on 64-bit. C#: nuint
typedef CF_ENUM(SInt32, MIDIProtocolID) { kMIDIProtocol_1_0 = 1, kMIDIProtocol_2_0 = 2 };
typedef CF_ENUM(SInt32, MIDIObjectType) { Other = -1, Device = 0, Entity = 1, Source = 2, Destination = 3,
                                          ExternalDevice = 0x10, ExternalEntity = 0x11, ExternalSource = 0x12, ExternalDestination = 0x13 };

typedef void (*MIDINotifyProc)(const MIDINotification *message, void *refCon);
typedef void (*MIDIReadProc)(const MIDIPacketList *pktlist, void *readProcRefCon, void *srcConnRefCon);   // legacy MIDI 1.0 bytes, API_TO_BE_DEPRECATED
typedef void (^MIDIReceiveBlock)(const MIDIEventList *evtlist, void *srcConnRefCon);                       // UMP; BLOCK — no C-function-pointer twin
typedef void (*MIDICompletionProc)(MIDISysexSendRequest *request);
```

## Structures — `#pragma pack(push, 4)` — measured on arm64

```c
struct MIDIPacket      { MIDITimeStamp timeStamp; UInt16 length; Byte data[256]; };   // sizeof 268; off: timeStamp 0, length 8, data 10
struct MIDIPacketList  { UInt32 numPackets; MIDIPacket packet[1]; };                 // sizeof 272; off: numPackets 0, packet 4  ← packet[0] starts at +4, so timeStamp is only 4-aligned
struct MIDIEventPacket { MIDITimeStamp timeStamp; UInt32 wordCount; UInt32 words[64]; };   // sizeof 268; off: timeStamp 0, wordCount 8, words 12
struct MIDIEventList   { MIDIProtocolID protocol; UInt32 numPackets; MIDIEventPacket packet[1]; };   // sizeof 276; off: protocol 0, numPackets 4, packet 8

struct MIDINotification                     { MIDINotificationMessageID messageID; UInt32 messageSize; };                                        // 8
struct MIDIObjectAddRemoveNotification      { messageID; messageSize; MIDIObjectRef parent; MIDIObjectType parentType; MIDIObjectRef child; MIDIObjectType childType; };  // 24; child @16, childType @20
struct MIDIObjectPropertyChangeNotification { messageID; messageSize; MIDIObjectRef object; MIDIObjectType objectType; CFStringRef propertyName; };   // 24; propertyName @16
struct MIDIIOErrorNotification              { messageID; messageSize; MIDIDeviceRef driverDevice; OSStatus errorCode; };                            // 16

struct MIDISysexSendRequest { MIDIEndpointRef destination; const Byte *data; UInt32 bytesToSend; Boolean complete; Byte reserved[3];
                              MIDICompletionProc completionProc; void *completionRefCon; };   // sizeof 40; off: data 8, bytesToSend 16, complete 20, completionProc 24, completionRefCon 32
```

**Variable-length packets — never index `packet[i]`.** Reimplement the header macros in C#:

```c
// MIDIPacketNext: arm64 rounds UP to 4-byte alignment; x86_64 does not.
#if TARGET_CPU_ARM64   next = (MIDIPacket*)(((uintptr_t)&pkt->data[pkt->length] + 3) & ~3);
#else                  next = (MIDIPacket*)&pkt->data[pkt->length];
// MIDIEventPacketNext: no alignment fix-up on any arch (words are already 4-aligned)
next = (MIDIEventPacket*)&pkt->words[pkt->wordCount];
```
C#: `RuntimeInformation.ProcessArchitecture == Architecture.Arm64` chooses the branch. Read `timeStamp` with `Unsafe.ReadUnaligned<ulong>` —
it is only 4-byte aligned inside a list (packet[0] at +4).

### Packet content rules (header text)
- "Running status is not allowed" — every message carries its status byte. Data bytes in one `MIDIPacket` may hold **several complete** messages back-to-back
  (e.g. `90 3C 40 80 3C 00`); parse sequentially by status → expected length.
- SysEx: "a packet may only contain a single message, or portion of one, with no other MIDI events" — a SysEx may be **split across packets and across
  callbacks**; `F0` starts, `F7` ends. System Real-Time bytes (`F8`..`FF`) are complete 1-byte messages and MAY appear inside a SysEx packet stream in
  theory, but CoreMIDI drivers deliver them in separate packets in practice; the reassembler must tolerate either.
- `timeStamp` applies to the **first** byte of the packet; 0 on receive is not expected but means "unknown".

## Prototypes (all `extern "C"`, AAPCS64 / SysV — one calling convention per arch)

```c
// client / ports
OSStatus MIDIClientCreate(CFStringRef name, MIDINotifyProc notifyProc, void *notifyRefCon, MIDIClientRef *outClient);          // 10.0
OSStatus MIDIClientDispose(MIDIClientRef client);                                                                            // disposes all its ports
OSStatus MIDIInputPortCreate(MIDIClientRef client, CFStringRef portName, MIDIReadProc readProc, void *refCon, MIDIPortRef *outPort);   // 10.0, API_TO_BE_DEPRECATED (still exported)
OSStatus MIDIInputPortCreateWithProtocol(MIDIClientRef client, CFStringRef portName, MIDIProtocolID protocol, MIDIPortRef *outPort, MIDIReceiveBlock receiveBlock);  // 11.0, UMP; needs a block
OSStatus MIDIOutputPortCreate(MIDIClientRef client, CFStringRef portName, MIDIPortRef *outPort);
OSStatus MIDIPortDispose(MIDIPortRef port);
OSStatus MIDIPortConnectSource(MIDIPortRef port, MIDIEndpointRef source, void *connRefCon);
OSStatus MIDIPortDisconnectSource(MIDIPortRef port, MIDIEndpointRef source);

// enumeration (indices are transient — resolve to MIDIUniqueID immediately)
ItemCount       MIDIGetNumberOfSources(void);          MIDIEndpointRef MIDIGetSource(ItemCount i);
ItemCount       MIDIGetNumberOfDestinations(void);     MIDIEndpointRef MIDIGetDestination(ItemCount i);
ItemCount       MIDIGetNumberOfDevices(void);          MIDIDeviceRef   MIDIGetDevice(ItemCount i);
ItemCount       MIDIDeviceGetNumberOfEntities(MIDIDeviceRef);           MIDIEntityRef   MIDIDeviceGetEntity(MIDIDeviceRef, ItemCount i);
ItemCount       MIDIEntityGetNumberOfSources(MIDIEntityRef);            MIDIEndpointRef MIDIEntityGetSource(MIDIEntityRef, ItemCount i);
ItemCount       MIDIEntityGetNumberOfDestinations(MIDIEntityRef);       MIDIEndpointRef MIDIEntityGetDestination(MIDIEntityRef, ItemCount i);
OSStatus        MIDIEndpointGetEntity(MIDIEndpointRef, MIDIEntityRef *outEntity);      // 10.2; fails for virtual endpoints
OSStatus        MIDIEntityGetDevice(MIDIEntityRef, MIDIDeviceRef *outDevice);           // 10.2
OSStatus        MIDIObjectFindByUniqueID(MIDIUniqueID id, MIDIObjectRef *outObject, MIDIObjectType *outType);   // 10.2; kMIDIObjectNotFound when gone

// properties
OSStatus MIDIObjectGetIntegerProperty(MIDIObjectRef obj, CFStringRef propertyID, SInt32 *outValue);
OSStatus MIDIObjectGetStringProperty (MIDIObjectRef obj, CFStringRef propertyID, CFStringRef *str);   // caller CFRelease()s *str

// output (for Identity Request; Sprint 3 IMidiOutput)
OSStatus MIDISend(MIDIPortRef port, MIDIEndpointRef dest, const MIDIPacketList *pktlist);   // API_TO_BE_DEPRECATED; short messages
OSStatus MIDISendSysex(MIDISysexSendRequest *request);   // async; request + data must stay pinned until completionProc; set request->complete = 1 to abort
OSStatus MIDISendEventList(MIDIPortRef port, MIDIEndpointRef dest, const MIDIEventList *evtlist);   // 11.0, UMP
OSStatus MIDIFlushOutput(MIDIEndpointRef dest);
OSStatus MIDIRestart(void);   // 10.1 — restarts the MIDIServer: drastic, never call from a library

// mach / CF
kern_return_t mach_timebase_info(mach_timebase_info_data_t *info);   // struct { uint32 numer; uint32 denom; }; this machine: 125/3 → 41.67 ns/tick
uint64_t      mach_absolute_time(void);                                // same clock as MIDITimeStamp AND as .NET Stopwatch.GetTimestamp() on macOS (see below)
CFStringRef   CFStringCreateWithCString(CFAllocatorRef alloc /*NULL*/, const char *cStr, CFStringEncoding enc);   // for client/port names
Boolean       CFStringGetCString(CFStringRef s, char *buffer, CFIndex bufferSize, CFStringEncoding enc);       // false if buffer too small
CFIndex       CFStringGetLength(CFStringRef s);
void          CFRelease(CFTypeRef cf);
CFRunLoopRef  CFRunLoopGetCurrent(void);  void CFRunLoopRun(void);  void CFRunLoopStop(CFRunLoopRef rl);
// kCFStringEncodingUTF8 = 0x08000100
```

**Property keys are exported `CFStringRef` globals**, not literals: `kMIDIPropertyName`, `kMIDIPropertyDisplayName` (10.4), `kMIDIPropertyManufacturer`,
`kMIDIPropertyModel`, `kMIDIPropertyUniqueID`, `kMIDIPropertyOffline` (10.1), `kMIDIPropertyDriverOwner`, `kMIDIPropertyProtocolID` (11.0),
`kMIDIPropertyMaxSysExSpeed`. Read each once via `NativeLibrary.Load(CoreMidiLib)` + `NativeLibrary.GetExport(h, "kMIDIPropertyName")` → `*(nint*)ptr`
(the export is the address **of the variable**, dereference to get the `CFStringRef`). Verified `dlsym("kMIDIPropertyName")` resolves.

| Property | Type | Notes |
|---|---|---|
| `kMIDIPropertyDisplayName` | string | endpoint: device name + endpoint name when the endpoint name is not unique ("MPK mini IV MIDI Port"). **Use this for `MidiInput_DeviceInfo.Name`.** |
| `kMIDIPropertyName` | string | bare endpoint name, often just "Port 1" |
| `kMIDIPropertyUniqueID` | int32 | **stable per endpoint**, survives replug (same device+USB port) and reboot; the natural `MidiInput_DeviceKey` base |
| `kMIDIPropertyOffline` | int32 | 1 = device unplugged but the OS remembered it (CoreMIDI keeps configured devices in the setup). **Filter `Offline == 1` out of enumeration** — they are not usable. |
| `kMIDIPropertyManufacturer` / `kMIDIPropertyModel` | string | driver-supplied, on the device (inherited by endpoints); may be absent (`kMIDIUnknownProperty`) |
| `kMIDIPropertyProtocolID` | int32 | 1 or 2 — what the endpoint natively speaks; 1 on the probe device |
| `kMIDIPropertyDriverOwner` | string | e.g. `com.apple.AppleMIDIUSBDriver`, `com.apple.AppleMIDIBluetoothDriver`, `com.apple.AppleMIDIRTPDriver` (network), `com.apple.AppleMIDIIACDriver` |

## Errors (`OSStatus`, `CF_ENUM` in MIDIServices.h)

| Name | Value | Meaning |
|---|---|---|
| `noErr` | 0 | |
| `kMIDIInvalidClient` | -10830 | |
| `kMIDIInvalidPort` | -10831 | |
| `kMIDIWrongEndpointType` | -10832 | connected a destination to an input port |
| `kMIDINoConnection` | -10833 | `MIDIPortDisconnectSource` on a source not connected |
| `kMIDIUnknownEndpoint` | -10834 | endpoint ref is stale (device unplugged) |
| `kMIDIUnknownProperty` | -10835 | property not set on this object — normal for Manufacturer/Model; not an error for us |
| `kMIDIWrongPropertyType` | -10836 | |
| `kMIDINoCurrentSetup` | -10837 | |
| `kMIDIMessageSendErr` | -10838 | |
| `kMIDIServerStartErr` | -10839 | MIDIServer could not start (sandbox / launchd) — no MIDI at all |
| `kMIDISetupFormatErr` | -10840 | |
| `kMIDIWrongThread` | -10841 | called from a thread the API forbids |
| `kMIDIObjectNotFound` | -10842 | `MIDIObjectFindByUniqueID` for a vanished device |
| `kMIDIIDNotUnique` | -10843 | |
| `kMIDINotPermitted` | -10844 | **missing `NSBluetoothAlwaysUsageDescription` / entitlement** (Bluetooth MIDI) or sandbox |
| `kMIDIUnknownError` | -10845 | |

There is **no** "allocated / in use" error: CoreMIDI is multi-client by design.

## Threading contract (header text, MIDIServices.h §Callback Functions)

- **`MIDIReadProc` / `MIDIReceiveBlock`**: "The CoreMIDI framework will create a **high-priority receive thread** on your client's behalf, and from that
  thread, your MIDIReadProc will be called." One thread per client. Same discipline as WinMM `MidiInProc`: no allocation, no locks, no blocking,
  never `MIDIPortDispose`/`MIDIClientDispose` from inside. `[UnmanagedCallersOnly]` static; identify the port via `srcConnRefCon` (slot index cast to
  `void*`), never a closure.
- **`MIDINotifyProc`**: "called on the **runloop (thread) on which `MIDIClientCreate` was first called**." Therefore that thread MUST run a
  `CFRunLoop` or notifications are silently never delivered. A .NET console/xunit process has **no** run loop → the library owns a dedicated thread:
  `MIDIClientCreate` on it, then `CFRunLoopRun()`; `CFRunLoopStop(loopRef)` to end. Data callbacks do NOT depend on this run loop.
- `MIDIClientCreateWithBlock`'s notify block is "called on an arbitrary thread" — but it is a block, so not usable without ObjC block ABI.
- Everything else (`MIDIObjectGet*Property`, enumeration, `MIDIPortConnectSource`) is thread-safe and may be called from any thread.

## Hot-plug on macOS (proper notifications — no polling needed)

```
kMIDIMsgSetupChanged            1   catch-all; ignore if you handle 2/3
kMIDIMsgObjectAdded             2   MIDIObjectAddRemoveNotification: child + childType (Device, Entity AND Source/Destination each arrive as separate notifications)
kMIDIMsgObjectRemoved           3   same struct; the child ref is already invalid — resolve identity from OUR table by ref, not by asking CoreMIDI
kMIDIMsgPropertyChanged         4   MIDIObjectPropertyChangeNotification — kMIDIPropertyOffline flips 1↔0 when a KNOWN device is unplugged/replugged
                                    (CoreMIDI may keep the device in the setup and toggle Offline INSTEAD of Removed/Added — handle BOTH paths)
kMIDIMsgThruConnectionsChanged  5
kMIDIMsgSerialPortOwnerChanged  6
kMIDIMsgIOError                 7   MIDIIOErrorNotification: driverDevice + errorCode → DeviceLost(DriverError)
```
Recommended strategy (what CoreMIDI-based DAWs do): on ANY of 1/2/3/4(Offline) → debounce (~50 ms, several arrive per plug event) → **full re-enumerate
and diff by `MIDIUniqueID`**. The notification is the trigger, not the truth. `MidiInput_HotPlugSource.Poll` remains a valid fallback and
`NotifyDeviceChange()` maps to the same re-enumerate.

## Timestamps

- `MIDITimeStamp` = `mach_absolute_time()` ticks (41.67 ns/tick on Apple silicon: numer/denom = 125/3; 1 ns/tick on x86_64).
- **Measured 2026-09-10 (.NET 10, arm64):** `.NET Stopwatch.GetTimestamp()` on macOS is the **same clock in nanoseconds** — `Stopwatch.Frequency == 1_000_000_000`
  and `Stopwatch == mach_absolute_time × numer / denom` to the nanosecond (CoreCLR PAL uses `clock_gettime_nsec_np(CLOCK_UPTIME_RAW)`).
  So `DriverTimestamp = MIDITimeStamp × numer / denom` is an **exact** conversion into the `ArrivalTicks` base with no anchor (SPEC-30 D36 amended).
  ⚠ An earlier draft of this doc claimed "same unit, identity" — wrong by the 125/3 ratio; caught by the first layout-test run.
- Prove it at start-up (`Stopwatch.Frequency == 1e9` and scaled values agree < 1 ms); if the proof fails fall back to the D6 min-anchor on the scaled value.
- Resolution is ns — `DeliveryLagMs` is meaningful, not 1 ms-quantised like WinMM.

## SysEx via CoreMIDI (Identity Request, D8/D9)

Send: fill a pinned `MIDISysexSendRequest` (`destination`, `data` → pinned 6 bytes, `bytesToSend = 6`, `complete = 0`, `completionProc` =
`[UnmanagedCallersOnly]` static), `MIDISendSysex(&req)`. The completion proc runs on a CoreMIDI thread; free after it fires. Timeout → set
`req.complete = 1` (aborts) and wait for the completion callback before freeing — never free under the server. `MIDISendSysex` does NOT need an
output port (it takes the destination directly). Alternatively `MIDIOutputPortCreate` + `MIDISend` with a 6-byte packet works for a message this
small (a `MIDIPacket` carries up to 256 bytes) and completes synchronously from our side.

Receive: SysEx arrives as `MIDIPacket`s containing only SysEx bytes (possibly `F0 ..` fragment, `..` middle fragments, `.. F7`). No fixed buffers, no
re-adding — CoreMIDI owns the memory. Feed the existing `Midi_SysExReassembler` byte-by-byte / span-by-span.

## Platform facts that shape the backend

- **USB class-compliant** devices need no vendor driver (`AppleMIDIUSBDriver`); multi-cable devices = one **entity** per cable (each with its own
  source/destination pair) under one device — the pairing problem WinMM solves by name is solved structurally here.
- **Bluetooth MIDI** appears as ordinary sources via `AppleMIDIBluetoothDriver` once paired (pairing UI = Audio MIDI Setup or the ObjC
  `CABTLEMIDIWindowController`, out of scope). Sandboxed/hardened apps need `com.apple.security.device.bluetooth` or get `kMIDINotPermitted`.
- **Network (RTP) MIDI** sessions and **IAC** buses are virtual endpoints (no entity) — enumerate fine, `HasPairedOutput = false` by our rule
  unless we later pair IAC by matching the destination with the same `kMIDIPropertyName`.
- **Sandbox**: no entitlement is needed for USB MIDI. `MIDIServer` is a launchd daemon started on first `MIDIClientCreate`; first call can take ~100 ms.
- **UMP / MIDI 2.0** (macOS 11+): `MIDIInputPortCreateWithProtocol` delivers `MIDIEventList` (native UMP words → `MidiInput_Message.FromUmp` directly,
  D25) — but the callback is a **block**. Doable without ObjC via a hand-built block literal (`isa = _NSConcreteGlobalBlock`, `flags`, `invoke` =
  `UnmanagedCallersOnly` fn ptr) — a ~30-line struct, no ObjC messaging. Sprint 4a uses the C `MIDIReadProc`/`MIDIPacketList` path (MIDI 1.0 bytes →
  `FromMidi1`, identical to WinMM); Sprint 4b adds the UMP port when a MIDI 2.0 device is on hand to validate against.
- Deprecation status: `MIDIInputPortCreate`, `MIDIReadProc`, `MIDISend`, `MIDIPacketList` are `API_TO_BE_DEPRECATED` — Apple's marker for "will be
  deprecated in a future release, no date". They are exported in the macOS 15 binary (dlsym verified) and are what every non-UMP DAW still uses.

## C# interop shape (for `Platforms/MacOS/CoreMidi_Interop.cs`)

```csharp
[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
static void ReadProc(MidiPacketList* list, void* readProcRefCon, void* srcConnRefCon) { /* slot = (int)(nint)srcConnRefCon */ }
// pass as: (delegate* unmanaged[Cdecl]<CoreMidi_PacketList*, void*, void*, void>)&ReadProc

[StructLayout(LayoutKind.Sequential, Pack = 4)] struct CoreMidi_Packet     { public ulong TimeStamp; public ushort Length; /* data follows at +10 */ }
[StructLayout(LayoutKind.Sequential, Pack = 4)] struct CoreMidi_PacketList { public uint NumPackets; /* first packet at +4 */ }
// iterate: byte* p = (byte*)list + 4; ts = Unsafe.ReadUnaligned<ulong>(p); len = Unsafe.ReadUnaligned<ushort>(p + 8); data = p + 10;
//          next = data + len; if (Arm64) next = (byte*)(((nuint)next + 3) & ~(nuint)3);
```
Assert in tests (mirrors `WinMm_InteropLayoutTests`): `sizeof(CoreMidi_Packet) == 12`-with-data-offset-10 semantics via `Marshal.OffsetOf`, error enum values
equal the header constants, `MIDIGetNumberOfSources()` callable, `mach_timebase_info` returns non-zero, `Stopwatch` ≡ `mach_absolute_time`.