# ALSA rawmidi (`/dev/snd/midiC*D*`) — ground truth for AN.Audio.Midi on Linux

Source of truth: the Linux kernel ALSA rawmidi interface as observed on this machine (Ubuntu, kernel 6.x, 2026-09-10) with an Akai MPK mini IV
attached, plus `/usr/include/asm-generic/{errno-base,fcntl,poll}.h` for constants. **No libasound is used** — rawmidi character devices speak plain
MIDI 1.0 byte streams and are driven with `open`/`poll`/`read`/`write`/`close` from libc. (`AN.Audio` proper uses `libasound.so.2` for PCM; MIDI does not need it.)

```csharp
const string LibC = "libc";   // [LibraryImport("libc")] resolves to libc.so.6 through the dlopen search path
```

## Object model

```
card N            /sys/class/sound/cardN, /proc/asound/cardN/       one per sound card (HDA chip, USB device, ...)
  └── rawmidi device D    /dev/snd/midiC{N}D{D}   /proc/asound/cardN/midiD     one char device per (card, device)
        ├── Input substream 0..k     ("cables" of a multi-port USB device)
        └── Output substream 0..m
```

- **One `/dev` node per (card, device) — NOT per substream.** `open()` on the node always gets **substream 0** in each direction. Other substreams
  are selected by issuing `SNDRV_CTL_IOCTL_RAWMIDI_PREFER_SUBDEVICE` on `/dev/snd/controlC{N}` immediately before the `open()` (this is what
  libasound does for `hw:N,D,S`). The preference is **per process** and consumed by the next rawmidi open on that card, so the prefer→open pair
  must be serialised within the process; another process racing in between is inherent to the API. Precisely: the kernel default is "first *free*
  substream", so once cable 1 is open by us, an un-preferred second open would get cable 2 — set the preference explicitly for **every** open,
  including 0. Measured: the MPK mini IV reports `Input 0..3`, `Output 0..4` on `midiC1D0`; with the ioctl all four input cables open and each
  answers the Identity Request on its own output substream.
  ```c
  #define SNDRV_CTL_IOCTL_RAWMIDI_PREFER_SUBDEVICE _IOW('U', 0x42, int)   // sound/asound.h → 0x40045542 (dir=W<<30 | size 4<<16 | 'U'<<8 | 0x42)
  ```
- The node is **bidirectional**: `O_RDWR` gives input substream 0 + output substream 0 in one fd. `write()` goes to the device — this is how the
  Identity Request is sent, no separate output object needed. With the preference set, `O_RDWR` gets input **and** output substream `S`.
- Card numbers are assigned at plug time and **renumber** across replug/boot (the MPK was card 2, then card 1 after a replug, while the HDA chips
  moved 1→2). Never persist `C{N}D{D}`; key on the USB serial (below).

## Enumeration (no API — read the filesystem)

| Path | Content | Use |
|---|---|---|
| `/dev/snd/midiC{N}D{D}` | char device `116,minor`, `root:audio 0660` + ACL | the port; glob it to discover ports |
| `/proc/asound/cardN/midiD` | line 1 = rawmidi name (`MPK mini IV`); then `Type: Legacy`; then `Output i` / `Input i` sections each with `Tx/Rx bytes` | display name; **which directions exist** (an output-only device has no `Input` section → skip) |
| `/proc/asound/cards` | ` N [id  ]: driver - long name` | human summary only |
| `/sys/class/sound/cardN/id` | short card id (`IV`, `Generic_1`) — first 15 chars of the USB product name for USB-Audio, so **not unique** | fallback key material when there is no serial |
| `/sys/class/sound/cardN/device` | symlink to the driver device; for USB cards → the USB **interface** (`.../usb5/5-1/5-1:1.1`) | walk to the parent |
| `/sys/class/sound/cardN/device/../serial` | USB iSerial (`E82605267968110` — same string the Akai Identity Reply carries) | **stable per-unit key** |
| `/sys/class/sound/cardN/device/../{idVendor,idProduct,product,manufacturer}` | `09e8` / `005d` / `MPK mini IV` / `Akai Professional` | available, not yet used |

PCI/HDA cards have none of the USB attributes (`ENOENT`) — every read must tolerate absence. Devices without an iSerial (common on cheap gear) fall
back to `alsa:{cardId}|{name}|D{D}` plus an ordinal suffix for duplicates (same rule as WinMM D11).

## Permissions

Nodes are `crw-rw----+ root audio`. The `+` is a **logind `uaccess` ACL**: the user on the active seat gets `user:jeske:rw-` automatically (verified with
`getfacl`), so **membership in `audio` is NOT required** for a desktop login. Headless / SSH-only sessions do need `audio` group membership (or a udev
rule). `open()` failing with `EACCES` (13) is a permissions problem, not a MIDI one.

## Constants (asm-generic headers)

```c
O_RDONLY 0x0000   O_RDWR 0x0002   O_NONBLOCK 0x0800 (00004000 octal)
POLLIN 0x0001     POLLERR 0x0008  POLLHUP 0x0010
EINTR 4   EAGAIN 11   EBUSY 16   EACCES 13   ENODEV 19
struct pollfd { int fd; short events; short revents; }   // 8 bytes
```

## I/O semantics

- **Exclusive per substream direction.** A second `open()` of a substream already open for input fails with `EBUSY` → `InUseByAnotherApplication`
  (rawmidi is single-client, unlike the ALSA sequencer, which multiplexes). PipeWire/JACK do not hold rawmidi nodes by default; `aseqdump`/`amidi` do.
- `read()` returns whatever bytes have arrived (1..N); message boundaries are **not** preserved — a NoteOn can arrive as `90` then `3C 40` across two
  reads, and running status is passed through verbatim from the device. Hence the stateful `Midi_ByteStreamParser`.
- `O_NONBLOCK` + `poll()` with a timeout is the shutdown mechanism: a blocking `read()` cannot be interrupted portably from managed code.
  `poll()` timeout 100 ms bounds `Close()` latency; `EINTR` from `poll` is retried.
- **Unplug:** `poll()` returns `POLLERR|POLLHUP` (and `read()` then fails with `ENODEV`). The fd stays valid until closed; the `/dev` node vanishes at
  the same time, so the 1 s enumeration diff also notices. Both paths map to `DeviceLost(Unplugged)`.
- `write()` of a 6-byte SysEx completes synchronously (kernel buffer); larger writes may return short counts under `O_NONBLOCK` — fine for v1's single
  Identity Request, not for a general `IMidiOutput`.
- **No timestamps.** Rawmidi is a byte pipe; the kernel does not stamp bytes. `DriverTimestamp = ArrivalTicks` (the `Stopwatch.GetTimestamp()` taken
  right after `read()` returns), so `DeliveryLagMs` is 0 by construction. Real driver stamps require the **ALSA sequencer** (`/dev/snd/seq`,
  `snd_seq_event.time` with `SNDRV_SEQ_TIME_STAMP_REAL`) — a different, event-structured API; see "Alternatives".
- USB full-speed MIDI arrives in 1 ms USB frames; several messages may be in one `read()` with one arrival stamp (same as CoreMIDI packets).

## Hot-plug

No notification without libudev (`udev_monitor` on subsystem `sound`) or a netlink socket. v1 polls the `/dev/snd` glob once per second on the worker
thread (I4 exception, same as WinMM). `NotifyDeviceChange()` forces an immediate rescan. A future opt-in `MidiInput_HotPlugSource.OsNotification`
on Linux would be a `libudev` P/Invoke (`udev_new`, `udev_monitor_new_from_netlink`, `udev_monitor_filter_add_match_subsystem_devtype("sound")`,
`udev_monitor_get_fd` → same `poll()` loop).

## C# interop shape (for `Platforms/Linux/Alsa_Interop.cs`)

```csharp
[LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)] static partial int Open(string path, int flags);
[LibraryImport("libc", EntryPoint = "read",  SetLastError = true)] static unsafe partial nint Read(int fd, byte* buf, nuint count);
[LibraryImport("libc", EntryPoint = "write", SetLastError = true)] static unsafe partial nint Write(int fd, byte* buf, nuint count);
[LibraryImport("libc", EntryPoint = "poll",  SetLastError = true)] static unsafe partial int Poll(PollFd* fds, nuint nfds, int timeoutMs);
[LibraryImport("libc", EntryPoint = "close", SetLastError = true)] static partial int Close(int fd);
// errno via Marshal.GetLastPInvokeError() immediately after the call.
```

## Alternatives (not chosen for v1)

| Option | Why not (yet) |
|---|---|
| **ALSA sequencer** (`/dev/snd/seq` ioctls, or `snd_seq_*` via libasound) | Multi-client, timestamped, hot-plug announce events (`SNDRV_SEQ_EVENT_PORT_START/EXIT`), all substreams as ports — strictly better semantics. Cost: `snd_seq_event` is a 28-byte union with many ioctls (`SNDRV_SEQ_IOCTL_*`), i.e. a real interop surface to measure and test. The natural Sprint-4c upgrade once output (`IMidiOutput`) needs it too. |
| **libasound rawmidi** (`snd_rawmidi_open("hw:N,D,S")`) | Solves the substream selection via one call, but adds the `libasound.so.2` dependency to a package whose selling point is none; the device-node path is 5 libc calls. |
| **ALSA UMP rawmidi** (kernel 6.5+, `/dev/snd/umpC*D*`) | MIDI 2.0 endpoints as 32-bit word streams → `MidiInput_Message.FromUmp` directly (D25). Needs a MIDI 2.0 device to validate. |