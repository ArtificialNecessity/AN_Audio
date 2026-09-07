# WinMM MIDI (`winmm.dll`) — ground truth for AN.Audio.Midi

Source of truth: the Windows SDK headers on this machine, read 2026-09-07:
`C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\um\mmeapi.h` (structs/prototypes), `um\mmsyscom.h` (constants),
`um\Dbt.h` + `um\WinUser.h` (device-change constants). Callback semantics from teragonaudio low-level MIDI notes and
production callbacks (mt32emu Win32MidiIn, LinuxSampler MME). MS Learn pages render only navigation chrome via our fetcher.

## Types (mmsyscom.h / mmeapi.h)

```c
typedef UINT MMRESULT;            // 0 == MMSYSERR_NOERROR
typedef UINT MMVERSION;           // major = high byte, minor = low byte
#define MAXPNAMELEN 32            // includes NUL
DECLARE_HANDLE(HMIDIIN); DECLARE_HANDLE(HMIDIOUT);   // opaque pointer-sized handles

typedef void (CALLBACK DRVCALLBACK)(HDRVR hdrvr, UINT uMsg, DWORD_PTR dwUser, DWORD_PTR dw1, DWORD_PTR dw2);
// MidiInProc has this exact shape: (HMIDIIN, UINT wMsg, DWORD_PTR dwInstance, DWORD_PTR dwParam1, DWORD_PTR dwParam2)

typedef struct tagMIDIINCAPSW {      // midiInGetDevCapsW, cbmic = sizeof
    WORD      wMid;                  // Windows manufacturer id (NOT the MIDI SysEx manufacturer id)
    WORD      wPid;                  // Windows product id
    MMVERSION vDriverVersion;
    WCHAR     szPname[MAXPNAMELEN];
    DWORD     dwSupport;             // WINVER >= 0x0400
} MIDIINCAPSW;                        // 2+2+4+64+4 = 76 bytes

typedef struct tagMIDIINCAPS2W {     // same call, pass the larger cbmic; driver fills what it knows (GUIDs may be zero)
    WORD wMid; WORD wPid; MMVERSION vDriverVersion; WCHAR szPname[MAXPNAMELEN]; DWORD dwSupport;
    GUID ManufacturerGuid; GUID ProductGuid; GUID NameGuid;
} MIDIINCAPS2W;                       // 76 + 48 = 124 bytes

typedef struct tagMIDIOUTCAPSW {
    WORD wMid; WORD wPid; MMVERSION vDriverVersion; WCHAR szPname[MAXPNAMELEN];
    WORD wTechnology; WORD wVoices; WORD wNotes; WORD wChannelMask; DWORD dwSupport;
} MIDIOUTCAPSW;                       // 84 bytes

typedef struct midihdr_tag {
    LPSTR     lpData;                // pointer to locked (pinned) data block
    DWORD     dwBufferLength;
    DWORD     dwBytesRecorded;       // input only
    DWORD_PTR dwUser;
    DWORD     dwFlags;               // MHDR_DONE 1, MHDR_PREPARED 2, MHDR_INQUEUE 4, MHDR_ISSTRM 8
    struct midihdr_tag *lpNext;      // reserved
    DWORD_PTR reserved;
    DWORD     dwOffset;              // WINVER >= 0x0400 (stream output only)
    DWORD_PTR dwReserved[8];
} MIDIHDR;                            // x64: 8+4+4+8+4(+4 pad)+8+8+4(+4 pad)+64 = 120 bytes; use sizeof of the C# struct with LayoutKind.Sequential
```

## Prototypes (mmeapi.h, all `WINAPI` = stdcall; on x64 there is one calling convention)

```c
UINT     midiInGetNumDevs(void);
MMRESULT midiInGetDevCapsW(UINT_PTR uDeviceID, LPMIDIINCAPSW pmic, UINT cbmic);   // NOTE: UINT_PTR
MMRESULT midiInGetErrorTextW(MMRESULT mmrError, LPWSTR pszText, UINT cchText);
MMRESULT midiInOpen(LPHMIDIIN phmi, UINT uDeviceID, DWORD_PTR dwCallback, DWORD_PTR dwInstance, DWORD fdwOpen);
MMRESULT midiInClose(HMIDIIN hmi);
MMRESULT midiInPrepareHeader(HMIDIIN hmi, LPMIDIHDR pmh, UINT cbmh);
MMRESULT midiInUnprepareHeader(HMIDIIN hmi, LPMIDIHDR pmh, UINT cbmh);
MMRESULT midiInAddBuffer(HMIDIIN hmi, LPMIDIHDR pmh, UINT cbmh);
MMRESULT midiInStart(HMIDIIN hmi);
MMRESULT midiInStop(HMIDIIN hmi);
MMRESULT midiInReset(HMIDIIN hmi);
MMRESULT midiInGetID(HMIDIIN hmi, LPUINT puDeviceID);

// Output subset needed for Identity Request (v1) and full IMidiOutput (later)
UINT     midiOutGetNumDevs(void);
MMRESULT midiOutGetDevCapsW(UINT_PTR uDeviceID, LPMIDIOUTCAPSW pmoc, UINT cbmoc);
MMRESULT midiOutOpen(LPHMIDIOUT phmo, UINT uDeviceID, DWORD_PTR dwCallback, DWORD_PTR dwInstance, DWORD fdwOpen);
MMRESULT midiOutClose(HMIDIOUT hmo);
MMRESULT midiOutShortMsg(HMIDIOUT hmo, DWORD dwMsg);
MMRESULT midiOutPrepareHeader(HMIDIOUT hmo, LPMIDIHDR pmh, UINT cbmh);
MMRESULT midiOutUnprepareHeader(HMIDIOUT hmo, LPMIDIHDR pmh, UINT cbmh);
MMRESULT midiOutLongMsg(HMIDIOUT hmo, LPMIDIHDR pmh, UINT cbmh);      // completes via MOM_DONE (0x3C9); header must stay pinned until then
```

## Constants (mmsyscom.h / mmeapi.h)

| Name | Value | Meaning |
|---|---|---|
| `CALLBACK_NULL` | 0x00000000 | |
| `CALLBACK_FUNCTION` | 0x00030000 | `dwCallback` is a function pointer |
| `MIDI_IO_STATUS` | 0x00000020 | `midiInOpen` flag: deliver `MIM_MOREDATA` when the app lags |
| `MIM_OPEN` / `MIM_CLOSE` | 0x3C1 / 0x3C2 | |
| `MIM_DATA` | 0x3C3 | `dwParam1` = packed short msg (`status = byte0`, `data1 = byte1`, `data2 = byte2`, little-endian), `dwParam2` = ms since `midiInStart` |
| `MIM_LONGDATA` | 0x3C4 | `dwParam1` = `MIDIHDR*`, `dwBytesRecorded` bytes of SysEx (fragment if buffer filled before `F7`; `== 0` means buffer returned on Reset/Close — do NOT re-add) |
| `MIM_ERROR` | 0x3C5 | invalid short message in `dwParam1` |
| `MIM_LONGERROR` | 0x3C6 | invalid/aborted SysEx in the header — re-add the buffer |
| `MIM_MOREDATA` | 0x3CC | a short message the app did not take fast enough (only with `MIDI_IO_STATUS`): it IS data, and it is an overflow signal |
| `MOM_OPEN` / `MOM_CLOSE` / `MOM_DONE` | 0x3C7 / 0x3C8 / 0x3C9 | output; `MOM_DONE` returns a `midiOutLongMsg` header |
| `MMSYSERR_NOERROR` | 0 | |
| `MMSYSERR_BADDEVICEID` | 2 | index out of range (device vanished) |
| `MMSYSERR_ALLOCATED` | 4 | another app holds the port exclusively (WinMM ports are NOT shareable on many drivers) |
| `MMSYSERR_INVALHANDLE` | 5 | |
| `MMSYSERR_NODRIVER` | 6 | device gone |
| `MMSYSERR_NOMEM` | 7 | |
| `MIDIERR_BASE` | 64 | |
| `MIDIERR_UNPREPARED` | 64 | header not prepared |
| `MIDIERR_STILLPLAYING` | 65 | Close while buffers queued → `midiInReset` first |
| `MIDIERR_NODEVICE` | 68 | “port no longer connected” |
| `MHDR_DONE / PREPARED / INQUEUE` | 1 / 2 / 4 | `MIDIHDR.dwFlags` |

**64-bit gotcha:** `dwInstance`, `dwParam1`, `dwParam2` are `DWORD_PTR`. Declaring them 32-bit truncates the `MIDIHDR*` in
`MIM_LONGDATA`. C#: `nuint`; callback `[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]` static, passed as
`(nuint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nuint, nuint, void>)&MidiInProc`. Identify the port via `dwInstance` (a
`GCHandle` or an index into a static table), never by capturing a closure.

## Callback rules

- Runs on a **driver thread**. Documented safe calls: `PostMessage`, `timeGetTime`, `timeGetSystemTime`, `OutputDebugString`,
  `midiIn*/midiOut*` Prepare/Unprepare/AddBuffer/ShortMsg/LongMsg. No blocking, no allocation, never `midiInClose` from inside.
- Short messages always carry an explicit status byte (the driver resolves running status). System Real-Time (`F8`..`FF`) arrives
  as `MIM_DATA` with `data1 = data2 = 0`.
- `dwParam2` resolution is 1 ms. Take `Stopwatch.GetTimestamp()` at callback entry for the high-resolution stamp.

## SysEx buffer discipline (Identity Reply is 15–17 bytes; 2×256 B buffers per port is ample)

1. Allocate N fixed buffers (`NativeMemory.Alloc`) + N `MIDIHDR` in fixed memory; `midiInPrepareHeader`; `midiInAddBuffer` each **before** `midiInStart`.
2. `MIM_LONGDATA` with `dwBytesRecorded > 0`: copy out, then `midiInAddBuffer` the same header again (Unprepare is not required between uses).
3. Reassembly: complete when the last recorded byte is `F7`; otherwise the next `MIM_LONGDATA` continues the message.
4. Shutdown: set a closing flag (so the callback stops re-adding), `midiInStop`, `midiInReset` (returns every buffer with 0 bytes), `midiInUnprepareHeader` all, free, `midiInClose`.

## Hot-plug on Windows

- WinMM has **no** device arrival/removal callback. Poll `midiInGetNumDevs` + caps (cheap), or receive `WM_DEVICECHANGE` (0x0219) on a window:
  `wParam` = `DBT_DEVNODES_CHANGED` (0x0007) always fires; `DBT_DEVICEARRIVAL` (0x8000) / `DBT_DEVICEREMOVECOMPLETE` (0x8004) with
  `DBT_DEVTYP_DEVICEINTERFACE` (5) require `RegisterDeviceNotificationW(hwnd, &DEV_BROADCAST_DEVICEINTERFACE_W{GUID_DEVINTERFACE_USB_DEVICE}, DEVICE_NOTIFY_WINDOW_HANDLE)`.
  Either way, re-enumerate on receipt; the message is a hint, not a device list.
- Device **indices shift** when a lower-index device disappears. Identity: `MIDIINCAPS2W.NameGuid`/`ProductGuid` when non-zero, else `(szPname, wMid, wPid, ordinal among same-name devices)`.
- An unplugged open port: later calls return `MMSYSERR_NODRIVER`/`MIDIERR_NODEVICE`/`MMSYSERR_INVALHANDLE`; some drivers deliver `MIM_CLOSE`. All mean device lost.
- `MMSYSERR_ALLOCATED` on open = another process owns the port (classic WinMM limitation). Report, do not retry in a loop.

## MIDI 1.0 Identity Request / Reply (Universal Non-Real-Time SysEx)

```
Request: F0 7E <dev> 06 01 F7                    dev = 7F -> all devices
Reply:   F0 7E <dev> 06 02 mm ff ff dd dd ss ss ss ss F7
         mm          = manufacturer SysEx id; if mm == 00 the id is 3 bytes (00 mm mm) and the reply is 2 bytes longer
         ff ff       = device family (14-bit, LSB first)
         dd dd       = family member (14-bit, LSB first)
         ss ss ss ss = software revision, vendor-specific layout
```
The request must go OUT on the same device's output port (`midiOutOpen` + `midiOutLongMsg`); WinMM does not pair in/out ports,
so pairing is by `szPname` match (in-port name == out-port name is the near-universal driver convention). Not every controller answers:
time out (~500 ms) and fall back to the WinMM name.

## USB devices and the 2026 Windows MIDI Services stack (devblogs.microsoft.com/windows-music-dev, microsoft.github.io/MIDI, Feb–May 2026)

- Class-compliant USB MIDI needs **no vendor driver**: inbox `usbaudio.sys` (legacy) / `usbmidi2.sys` (MIDI Services) expose WinMM ports. Multi-cable devices = one port per cable, name-suffixed.
- **Windows MIDI Services** ships via Windows Update (KB5074105, Feb 2026, staged 30-day rollout). It replaces the WinMM plumbing: `Drivers32` registry
  `midi = wdmaud.drv` (inbox GS synth), `midi1 = wdmaud2.drv` (routes WinMM to `midisrv.exe`). Effects on a WinMM client:
  - **multi-client for free** (legacy stack: one app per port, `MMSYSERR_ALLOCATED`);
  - **port names changed system-wide** — MS explicitly warns apps that reconnect by name will break; key devices by GUID/ids instead;
  - MIDI 2.0 devices appear as MIDI 1.0 ports (UMP needs the App SDK, a separate future backend);
  - old vendor `.drv` drivers (Korg, Roland UM-ONE vendor mode) conflict with the service: slow enumeration or missing ports. Not our problem to fix; do not retry-loop.
  - identical same-name devices could be mis-opened by early shim builds (fix rolling out from 2026-04-30).
- **BLE MIDI is not exposed through WinMM** by Windows itself (only via Korg's legacy `korgbm64.drv` or the WinRT API). MS is writing a user-mode BLE transport for MIDI Services post-release.
- Nothing USB-specific is required in client code: no WinUSB, no libusb, no descriptors.