using System.Runtime.InteropServices;

namespace AN.Audio.Midi.Platforms.Windows;

// Ground truth: _EXTERNAL_APIS/WinMM_MidiIn.md (mmeapi.h / mmsyscom.h, Windows SDK 10.0.26100.0).
// SPEC-30 D12: every native constant lives here as an enum and is used symbolically. No literals elsewhere.

#pragma warning disable AN0100 // nint/nuint are intentional for Win32 interop (DWORD_PTR, handles)

/// <summary>MMRESULT values (mmsyscom.h / mmeapi.h).</summary>
internal enum WinMm_Result : uint
{
    NoError = 0,                  // MMSYSERR_NOERROR
    Error = 1,                    // MMSYSERR_ERROR
    BadDeviceId = 2,              // MMSYSERR_BADDEVICEID — index out of range (device vanished)
    NotEnabled = 3,               // MMSYSERR_NOTENABLED
    Allocated = 4,                // MMSYSERR_ALLOCATED — another app holds the port
    InvalidHandle = 5,            // MMSYSERR_INVALHANDLE
    NoDriver = 6,                 // MMSYSERR_NODRIVER — device gone
    NoMem = 7,                    // MMSYSERR_NOMEM
    NotSupported = 8,             // MMSYSERR_NOTSUPPORTED
    InvalidFlag = 10,             // MMSYSERR_INVALFLAG
    InvalidParam = 11,            // MMSYSERR_INVALPARAM
    MidiUnprepared = 64,          // MIDIERR_UNPREPARED
    MidiStillPlaying = 65,        // MIDIERR_STILLPLAYING — Close while buffers queued
    MidiNoMap = 66,               // MIDIERR_NOMAP
    MidiNotReady = 67,            // MIDIERR_NOTREADY
    MidiNoDevice = 68,            // MIDIERR_NODEVICE — port no longer connected
    MidiInvalidSetup = 69,        // MIDIERR_INVALIDSETUP
    MidiBadOpenMode = 70,         // MIDIERR_BADOPENMODE
    MidiDontContinue = 71,        // MIDIERR_DONT_CONTINUE
}

/// <summary>uMsg values delivered to a MidiInProc.</summary>
internal enum WinMm_MidiInMessage : uint
{
    Open = 0x3C1,                 // MIM_OPEN
    Close = 0x3C2,                // MIM_CLOSE
    Data = 0x3C3,                 // MIM_DATA      dwParam1 = packed short msg, dwParam2 = ms since Start
    LongData = 0x3C4,             // MIM_LONGDATA  dwParam1 = MIDIHDR*
    Error = 0x3C5,                // MIM_ERROR     invalid short message
    LongError = 0x3C6,            // MIM_LONGERROR invalid/aborted SysEx — re-add the buffer
    MoreData = 0x3CC,             // MIM_MOREDATA  data the app took too slowly (MIDI_IO_STATUS only)
}

/// <summary>uMsg values delivered to a MidiOutProc.</summary>
internal enum WinMm_MidiOutMessage : uint
{
    Open = 0x3C7,                 // MOM_OPEN
    Close = 0x3C8,                // MOM_CLOSE
    Done = 0x3C9,                 // MOM_DONE      returns a midiOutLongMsg header
    PositionCb = 0x3CA,           // MOM_POSITIONCB (stream only)
}

/// <summary>fdwOpen flags for midiInOpen / midiOutOpen.</summary>
[Flags]
internal enum WinMm_OpenFlags : uint
{
    CallbackNull = 0x00000000,    // CALLBACK_NULL
    CallbackFunction = 0x00030000,// CALLBACK_FUNCTION — dwCallback is a function pointer
    MidiIoStatus = 0x00000020,    // MIDI_IO_STATUS — deliver MIM_MOREDATA when lagging
}

/// <summary>MIDIHDR.dwFlags.</summary>
[Flags]
internal enum WinMm_HdrFlags : uint
{
    None = 0,
    Done = 0x00000001,            // MHDR_DONE
    Prepared = 0x00000002,        // MHDR_PREPARED
    InQueue = 0x00000004,         // MHDR_INQUEUE
    IsStream = 0x00000008,        // MHDR_ISSTRM
}

/// <summary>Fixed limits from mmsyscom.h.</summary>
internal enum WinMm_Limits
{
    MaxPNameLen = 32,             // MAXPNAMELEN, includes NUL
}

/// <summary>MIDIHDR. x64: 120 bytes; x86: 64 bytes. Must be pinned (NativeMemory) while the driver owns it.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WinMm_MidiHdr
{
    public byte* lpData;
    public uint dwBufferLength;
    public uint dwBytesRecorded;
    public nuint dwUser;
    public WinMm_HdrFlags dwFlags;
    public WinMm_MidiHdr* lpNext;
    public nuint reserved;
    public uint dwOffset;
    public fixed ulong dwReserved[8];   // DWORD_PTR[8]; sized for x64 — on x86 this over-allocates 32 bytes, harmless (driver only reads up to cbmh)
}

/// <summary>MIDIINCAPS2W — 124 bytes. Pass sizeof to midiInGetDevCapsW; GUIDs may be zero.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct WinMm_MidiInCaps2W
{
    public ushort wMid;                 // Windows manufacturer id (NOT the SysEx manufacturer id)
    public ushort wPid;                 // Windows product id
    public uint vDriverVersion;
    public fixed char szPname[32];      // WinMm_Limits.MaxPNameLen
    public uint dwSupport;
    public Guid ManufacturerGuid;
    public Guid ProductGuid;
    public Guid NameGuid;

    public string Name
    {
        get
        {
            fixed (char* p = szPname)
            {
                int len = 0;
                while (len < (int)WinMm_Limits.MaxPNameLen && p[len] != '\0') len++;
                return new string(p, 0, len);
            }
        }
    }
}

/// <summary>MIDIOUTCAPSW — 84 bytes.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct WinMm_MidiOutCapsW
{
    public ushort wMid;
    public ushort wPid;
    public uint vDriverVersion;
    public fixed char szPname[32];
    public ushort wTechnology;
    public ushort wVoices;
    public ushort wNotes;
    public ushort wChannelMask;
    public uint dwSupport;

    public string Name
    {
        get
        {
            fixed (char* p = szPname)
            {
                int len = 0;
                while (len < (int)WinMm_Limits.MaxPNameLen && p[len] != '\0') len++;
                return new string(p, 0, len);
            }
        }
    }
}

/// <summary>Direct PInvoke into winmm.dll. All WINAPI (stdcall). DWORD_PTR params are nuint (64-bit gotcha).</summary>
internal static unsafe partial class WinMm_MidiInterop
{
    private const string WinMm = "winmm.dll";

    // ── input ──────────────────────────────────────────────────────────────────────────────

    [LibraryImport(WinMm)] public static partial uint midiInGetNumDevs();
    [LibraryImport(WinMm)] public static partial WinMm_Result midiInGetDevCapsW(nuint uDeviceID, WinMm_MidiInCaps2W* pmic, uint cbmic);
    [LibraryImport(WinMm)] public static partial WinMm_Result midiInGetErrorTextW(WinMm_Result mmrError, char* pszText, uint cchText);
    [LibraryImport(WinMm)] public static partial WinMm_Result midiInOpen(nint* phmi, uint uDeviceID, nuint dwCallback, nuint dwInstance, WinMm_OpenFlags fdwOpen);
    [LibraryImport(WinMm)] public static partial WinMm_Result midiInClose(nint hmi);
    [LibraryImport(WinMm)] public static partial WinMm_Result midiInPrepareHeader(nint hmi, WinMm_MidiHdr* pmh, uint cbmh);
    [LibraryImport(WinMm)] public static partial WinMm_Result midiInUnprepareHeader(nint hmi, WinMm_MidiHdr* pmh, uint cbmh);
    [LibraryImport(WinMm)] public static partial WinMm_Result midiInAddBuffer(nint hmi, WinMm_MidiHdr* pmh, uint cbmh);
    [LibraryImport(WinMm)] public static partial WinMm_Result midiInStart(nint hmi);
    [LibraryImport(WinMm)] public static partial WinMm_Result midiInStop(nint hmi);
    [LibraryImport(WinMm)] public static partial WinMm_Result midiInReset(nint hmi);
    [LibraryImport(WinMm)] public static partial WinMm_Result midiInGetID(nint hmi, uint* puDeviceID);

    // ── output (Sprint 1: Identity Request only) ───────────────────────────────────────────────────

    [LibraryImport(WinMm)] public static partial uint midiOutGetNumDevs();
    [LibraryImport(WinMm)] public static partial WinMm_Result midiOutGetDevCapsW(nuint uDeviceID, WinMm_MidiOutCapsW* pmoc, uint cbmoc);
    [LibraryImport(WinMm)] public static partial WinMm_Result midiOutOpen(nint* phmo, uint uDeviceID, nuint dwCallback, nuint dwInstance, WinMm_OpenFlags fdwOpen);
    [LibraryImport(WinMm)] public static partial WinMm_Result midiOutClose(nint hmo);
    [LibraryImport(WinMm)] public static partial WinMm_Result midiOutShortMsg(nint hmo, uint dwMsg);
    [LibraryImport(WinMm)] public static partial WinMm_Result midiOutPrepareHeader(nint hmo, WinMm_MidiHdr* pmh, uint cbmh);
    [LibraryImport(WinMm)] public static partial WinMm_Result midiOutUnprepareHeader(nint hmo, WinMm_MidiHdr* pmh, uint cbmh);
    [LibraryImport(WinMm)] public static partial WinMm_Result midiOutLongMsg(nint hmo, WinMm_MidiHdr* pmh, uint cbmh);

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    public static readonly uint MidiHdrSize = (uint)sizeof(WinMm_MidiHdr);
    public static readonly uint MidiInCaps2Size = (uint)sizeof(WinMm_MidiInCaps2W);
    public static readonly uint MidiOutCapsSize = (uint)sizeof(WinMm_MidiOutCapsW);

    /// <summary>Unpack a MIM_DATA dwParam1: byte0 = status, byte1 = data1, byte2 = data2.</summary>
    public static void UnpackShortMessage(nuint dwParam1, out byte status, out byte data1, out byte data2)
    {
        uint packed = (uint)dwParam1;
        status = (byte)packed;
        data1 = (byte)(packed >> 8);
        data2 = (byte)(packed >> 16);
    }

    public static string GetErrorText(WinMm_Result result)
    {
        const int cch = 256;
        char* buf = stackalloc char[cch];
        if (midiInGetErrorTextW(result, buf, cch) != WinMm_Result.NoError) return result.ToString();
        return new string(buf) + $" ({result})";
    }

    /// <summary>All "the device went away" results collapse to one meaning (ground truth: Hot-plug on Windows).</summary>
    public static bool IsDeviceGone(WinMm_Result r) =>
        r is WinMm_Result.NoDriver or WinMm_Result.MidiNoDevice or WinMm_Result.InvalidHandle or WinMm_Result.BadDeviceId;
}

#pragma warning restore AN0100