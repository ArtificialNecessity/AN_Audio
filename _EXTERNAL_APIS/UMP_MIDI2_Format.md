# Universal MIDI Packet (UMP) / MIDI 2.0 — ground truth for AN.Audio.Midi

Sources (read 2026-09-07): M2-104-UM v1.1.1 *UMP Format and MIDI 2.0 Protocol* (amei.or.jp mirror), M2-115-U v1.0.1
*MIDI 2.0 Bit Scaling and Resolution*, M2-101-UM v1.2 *MIDI-CI*, M2-103-UM *Common Rules for Property Exchange*,
microsoft.github.io/MIDI (Windows MIDI Services overview, SDK reference), USB MIDI 2.0 class spec (usb.org).
Only what AN.Audio.Midi needs is recorded here.

## Packet shape

- Every UMP is 1–4 little-endian-agnostic **32-bit words**; size is fixed by the Message Type nibble (bits 31..28 of word 0).
- Word 0 universal fields: `mt` bits 31..28, `group` bits 27..24 (0..15 = Group 1..16), `status` bits 23..16.
  For channel messages the status byte is `opcode<<4 | channel`, exactly the MIDI 1.0 status byte.
- MT 0x0 (Utility) and MT 0xF (UMP Stream) have **no** group field.

| MT | Words | Meaning |
|---|---|---|
| 0x0 | 1 | Utility (NOOP, JR Clock, JR Timestamp, Delta Clockstamp) |
| 0x1 | 1 | System Common + System Real Time (status byte in bits 23..16, data bytes in 15..8, 7..0) |
| 0x2 | 1 | **MIDI 1.0 Channel Voice**: bytes 2,3,4 of the packet are the MIDI 1.0 status/data1/data2; 2-byte messages zero-fill byte 4 |
| 0x3 | 2 | Data 64: SysEx7 in 6-byte chunks (status nibble = Complete/Start/Continue/End, count nibble) |
| 0x4 | 2 | **MIDI 2.0 Channel Voice**: word0 `mt|group|status|index(16)`, word1 = 32-bit data |
| 0x5 | 4 | Data 128: SysEx8 / Mixed Data Set |
| 0xD | 4 | Flex Data (text, tempo, key sig …) |
| 0xF | 4 | UMP Stream (Endpoint Discovery/Info, Device Identity, Function Block Info, names) |
| others | see spec | reserved (0x6,0x7 = 1 word; 0x8–0xA = 2; 0xB,0xC = 3; 0xE = 4) |

## MT 0x4 MIDI 2.0 Channel Voice — field layout

```
word0: [mt=4:4][group:4][opcode:4][channel:4][byteA:8][byteB:8]
word1: 32-bit value
```

| opcode | Message | byteA | byteB | word1 |
|---|---|---|---|---|
| 0x8 / 0x9 | Note Off / Note On | note (7-bit) | attribute type | `velocity(16) << 16 \| attributeData(16)` |
| 0xA | Poly Pressure | note | — | pressure 32-bit |
| 0xB | Control Change | controller # (7-bit) | — | value 32-bit |
| 0xC | Program Change | — | flags (bit0 = bank valid) | `program<<24 \| bankMsb<<8 \| bankLsb` |
| 0xD | Channel Pressure | — | — | pressure 32-bit |
| 0xE | Pitch Bend | — | — | 32-bit, 0x80000000 = centre |
| 0x0 / 0x1 | Registered / Assignable Per-Note Controller | note | index | value 32-bit |
| 0x2 / 0x3 | Registered (RPN) / Assignable (NRPN) Controller | bank (7) | index (7) | value 32-bit |
| 0x4 / 0x5 | Relative RPN / NRPN | bank | index | signed 32-bit delta |
| 0x6 | Per-Note Pitch Bend | note | — | 32-bit |
| 0xF | Per-Note Management | note | flags (bit0 reset, bit1 detach) | reserved |

- MIDI 2.0 Note On **velocity 0 is NOT a note-off** (unlike 1.0). Translators 2.0→1.0 replace a scaled-to-zero velocity with 1.
- Attribute types: 0 none, 1 manufacturer specific, 2 profile specific, 3 Pitch 7.9 (note + fractional pitch).

## Bit scaling MIDI 1.0 → 2.0 (M2-115-U, Min-Center-Max, the normative method)

Upscale `src` with `srcBits` significant bits to `dstBits`:
```
scaleBits = dstBits - srcBits
srcCenter = 1 << (srcBits - 1)
if src <= srcCenter: return src << scaleBits                       // lower half: plain shift, preserves 0 and centre
repeat = srcBits - 1; bitShifted = (src - srcCenter) << scaleBits  // upper half: replicate low bits so max → all ones
result = bitShifted | (dstCenter = 1 << (dstBits-1))
fill = scaleBits; while fill > 0: bitShifted >>= repeat; result |= bitShifted; fill -= repeat
```
Reference values (7→16): 0→0x0000, 32→0x4000, 64→0x8000, 96→0xC104, 127→0xFFFF. (7→32): 64→0x80000000, 127→0xFFFFFFFF.
(14→32 pitch bend): 8192→0x80000000, 16383→0xFFFFFFFF. Downscale is plain truncation (`>>`), and downscaling an
upscaled value returns the original. **Min-Center-Max upscaling is lossless in that sense** — the only reason it is safe to
expose 1.0 data through 2.0-sized accessors.

## Timestamps

- UMP itself: optional JR Timestamp (MT 0x0, 16-bit, 1/31250 s units) prefixed to a message. Rarely used on USB.
- Windows MIDI Services SDK: every received message carries a 64-bit `QueryPerformanceCounter` timestamp (`MidiClock.Now`).
- CoreMIDI: `MIDITimeStamp` = 64-bit `mach_absolute_time`. ALSA UMP: `snd_seq` real-time or tick timestamps.
- WinMM: 32-bit ms since `midiInStart`. → the library's driver-timestamp field must be **64-bit** to be backend-neutral.

## Windows MIDI Services facts relevant to backend design

- Inside `midisrv.exe` everything is UMP. MIDI 1.0 devices (inbox class driver or vendor driver) are wrapped as a UMP
  endpoint; each MIDI 1.0 cable/port becomes one **group** ("aggregated MIDI 1.0 endpoints").
- MIDI 1.0 bytes ↔ UMP MT1/2/3 translation is done in the service; MT4 sent to a 1.0 device is downscaled.
- WinMM and WinRT MIDI 1.0 are re-plumbed to the service, so a WinMM client sees every device (incl. MIDI 2.0 ones)
  as MIDI 1.0 ports, one per group, multi-client. UMP-native access requires the **App SDK** (WinRT,
  `Microsoft.Windows.Devices.Midi2.dll`, separately installed runtime; an app prompts the user to install if missing).
- SDK enumeration (`MidiEndpointDeviceInformation`) pre-caches Endpoint Discovery: endpoint name, **product instance id**
  (per-unit serial), function blocks, protocol. Legacy mapping helpers: `MidiLegacyPortDeviceInformation.FindAllForAssociatedEndpoint`,
  `CreateFromAssociatedMidi1PortNumber(portNumber, flow)` (WinMM port number → parent UMP endpoint + group).
- SDK delivers received messages via a `MessageReceived` event on the connection with `(timestamp, words[])`.

## MIDI-CI (M2-101-UM v1.2) — summary for a future sprint

- Plain Universal SysEx: `F0 7E <devId> 0D <subId2> <ver> <srcMUID:4> <dstMUID:4> … F7`; works over a MIDI 1.0 byte stream
  (no UMP needed; Device ID 0x7F = whole Function Block, Function Block field 0x7F = none).
- Sub-ID#2 ranges: 0x20–2F Profile Configuration, 0x30–3F Property Exchange (0x30/31 Capabilities, 0x34/35 Get, 0x36/37 Set,
  0x38/39 Subscribe, 0x3F Notify), 0x40–4F Process Inquiry, 0x70 Discovery, 0x71 Reply to Discovery, 0x72 Endpoint Inquiry,
  0x7E Invalidate MUID, 0x7F NAK. 0x10–1F Protocol Negotiation is deprecated.
- MUID: 28-bit, LSB-first 7-bit bytes; broadcast = 0x0FFFFFFF.
- Reply to Discovery carries manufacturer (3 bytes), family (2), model (2), revision (4), category bitmap, max SysEx size.
- Property Exchange payload = JSON header + (JSON | Mcoded7 | zlib) property data, chunked by the responder's max SysEx size.
  Foundational resources: `ResourceList`, `DeviceInfo` (includes `serialNumber`), `ChannelList`, `JSONSchema`.
- Requires a bidirectional connection → depends on `IMidiOutput` (SPEC-30 Sprint 3).