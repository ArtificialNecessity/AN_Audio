using System.Runtime.InteropServices;

namespace AN.Audio.Platforms.Windows;

/// <summary>
/// Raw COM interop for WASAPI. Manual vtable dispatch — no RCW, no ComImport.
/// </summary>
internal static unsafe class WasapiInterop
{
    // ─── GUIDs ───────────────────────────────────────────────────────────

    public static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    public static readonly Guid IID_IAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");

    // ─── Constants ───────────────────────────────────────────────────────

    public const uint CLSCTX_ALL = 0x17;
    public const int AUDCLNT_SHAREMODE_SHARED = 0;
    public const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    public const int eRender = 0;
    public const int eConsole = 0;

    // REFERENCE_TIME is in 100ns units. 10,000 = 1ms.
    public const long REFTIMES_PER_MS = 10_000;

    // ─── WAVEFORMATEX ────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct WAVEFORMATEXTENSIBLE
    {
        public WAVEFORMATEX Format;
        public ushort wValidBitsPerSample;
        public uint dwChannelMask;
        public Guid SubFormat;
    }

    public const ushort WAVE_FORMAT_PCM = 1;
    public const ushort WAVE_FORMAT_IEEE_FLOAT = 3;
    public const ushort WAVE_FORMAT_EXTENSIBLE = 0xFFFE;

    public static readonly Guid KSDATAFORMAT_SUBTYPE_PCM = new("00000001-0000-0010-8000-00aa00389b71");
    public static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new("00000003-0000-0010-8000-00aa00389b71");

    // ─── COM Vtable Indices ──────────────────────────────────────────────

    // IMMDeviceEnumerator vtable (after IUnknown 0,1,2)
    public const int IMMDeviceEnumerator_GetDefaultAudioEndpoint = 4;

    // IMMDevice vtable (after IUnknown 0,1,2)
    public const int IMMDevice_Activate = 3;

    // IAudioClient vtable (after IUnknown 0,1,2)
    public const int IAudioClient_Initialize = 3;
    public const int IAudioClient_GetBufferSize = 4;
    public const int IAudioClient_GetStreamLatency = 5;
    public const int IAudioClient_GetCurrentPadding = 6;
    public const int IAudioClient_IsFormatSupported = 7;
    public const int IAudioClient_GetMixFormat = 8;
    public const int IAudioClient_GetDevicePeriod = 9;
    public const int IAudioClient_Start = 10;
    public const int IAudioClient_Stop = 11;
    public const int IAudioClient_Reset = 12;
    public const int IAudioClient_SetEventHandle = 13;
    public const int IAudioClient_GetService = 14;

    // IAudioRenderClient vtable (after IUnknown 0,1,2)
    public const int IAudioRenderClient_GetBuffer = 3;
    public const int IAudioRenderClient_ReleaseBuffer = 4;

    // ─── COM Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Call a COM vtable method by index. Returns HRESULT.
    /// </summary>
    public static int ComCall(nint comPtr, int vtableIndex, nint* args, int argCount)
    {
        // COM object layout: pointer to vtable at offset 0
        nint* vtable = *(nint**)comPtr;
        nint methodPtr = vtable[vtableIndex];

        // We use a delegate approach per-method instead of a generic arg packer.
        // This helper is not used directly — see typed wrappers below.
        throw new NotImplementedException("Use typed wrappers");
    }

    // ─── Typed COM Method Wrappers ───────────────────────────────────────

    public static nint GetVtableMethod(nint comPtr, int index)
    {
        nint* vtable = *(nint**)comPtr;
        return vtable[index];
    }

    // IUnknown::Release
    public static uint Release(nint comPtr)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, uint>)
            GetVtableMethod(comPtr, 2);
        return fn(comPtr);
    }

    // IMMDeviceEnumerator::GetDefaultAudioEndpoint
    public static int GetDefaultAudioEndpoint(nint enumerator, int dataFlow, int role, out nint device)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)
            GetVtableMethod(enumerator, IMMDeviceEnumerator_GetDefaultAudioEndpoint);
        fixed (nint* pDevice = &device)
            return fn(enumerator, dataFlow, role, pDevice);
    }

    // IMMDevice::Activate
    public static int DeviceActivate(nint device, ref Guid iid, uint clsCtx, nint activationParams, out nint iface)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)
            GetVtableMethod(device, IMMDevice_Activate);
        fixed (Guid* pIid = &iid)
        fixed (nint* pIface = &iface)
            return fn(device, pIid, clsCtx, activationParams, pIface);
    }

    // IAudioClient::Initialize
    public static int AudioClientInitialize(nint client, int shareMode, uint flags, long bufferDuration, long periodicity, WAVEFORMATEX* format, nint sessionGuid)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, int, uint, long, long, WAVEFORMATEX*, nint, int>)
            GetVtableMethod(client, IAudioClient_Initialize);
        return fn(client, shareMode, flags, bufferDuration, periodicity, format, sessionGuid);
    }

    // IAudioClient::GetBufferSize
    public static int AudioClientGetBufferSize(nint client, out uint bufferFrameCount)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, uint*, int>)
            GetVtableMethod(client, IAudioClient_GetBufferSize);
        fixed (uint* p = &bufferFrameCount)
            return fn(client, p);
    }

    // IAudioClient::GetStreamLatency
    public static int AudioClientGetStreamLatency(nint client, out long latency)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, long*, int>)
            GetVtableMethod(client, IAudioClient_GetStreamLatency);
        fixed (long* p = &latency)
            return fn(client, p);
    }

    // IAudioClient::GetCurrentPadding
    public static int AudioClientGetCurrentPadding(nint client, out uint padding)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, uint*, int>)
            GetVtableMethod(client, IAudioClient_GetCurrentPadding);
        fixed (uint* p = &padding)
            return fn(client, p);
    }

    // IAudioClient::GetMixFormat
    public static int AudioClientGetMixFormat(nint client, out WAVEFORMATEX* format)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, WAVEFORMATEX**, int>)
            GetVtableMethod(client, IAudioClient_GetMixFormat);
        fixed (WAVEFORMATEX** p = &format)
            return fn(client, p);
    }

    // IAudioClient::Start
    public static int AudioClientStart(nint client)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, int>)
            GetVtableMethod(client, IAudioClient_Start);
        return fn(client);
    }

    // IAudioClient::Stop
    public static int AudioClientStop(nint client)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, int>)
            GetVtableMethod(client, IAudioClient_Stop);
        return fn(client);
    }

    // IAudioClient::Reset
    public static int AudioClientReset(nint client)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, int>)
            GetVtableMethod(client, IAudioClient_Reset);
        return fn(client);
    }

    // IAudioClient::SetEventHandle
    public static int AudioClientSetEventHandle(nint client, nint eventHandle)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, nint, int>)
            GetVtableMethod(client, IAudioClient_SetEventHandle);
        return fn(client, eventHandle);
    }

    // IAudioClient::GetService
    public static int AudioClientGetService(nint client, ref Guid iid, out nint service)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)
            GetVtableMethod(client, IAudioClient_GetService);
        fixed (Guid* pIid = &iid)
        fixed (nint* pService = &service)
            return fn(client, pIid, pService);
    }

    // IAudioRenderClient::GetBuffer
    public static int RenderClientGetBuffer(nint renderClient, uint numFramesRequested, out byte* data)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, uint, byte**, int>)
            GetVtableMethod(renderClient, IAudioRenderClient_GetBuffer);
        fixed (byte** p = &data)
            return fn(renderClient, numFramesRequested, p);
    }

    // IAudioRenderClient::ReleaseBuffer
    public static int RenderClientReleaseBuffer(nint renderClient, uint numFramesWritten, uint flags)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, uint, uint, int>)
            GetVtableMethod(renderClient, IAudioRenderClient_ReleaseBuffer);
        return fn(renderClient, numFramesWritten, flags);
    }

    // ─── Win32 PInvoke ───────────────────────────────────────────────────────────

#pragma warning disable AN0100 // nint is intentional for COM/Win32 interop

    [DllImport("ole32.dll")]
    public static extern int CoCreateInstance(
        ref Guid rclsid,
        nint pUnkOuter,
        uint dwClsContext,
        ref Guid riid,
        out nint ppv);

    [DllImport("ole32.dll")]
    public static extern int CoInitializeEx(nint pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(nint pv);

    [DllImport("kernel32.dll")]
    public static extern nint CreateEventW(nint lpEventAttributes, int bManualReset, int bInitialState, nint lpName);

    [DllImport("kernel32.dll")]
    public static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll")]
    public static extern int CloseHandle(nint hObject);

    public const uint INFINITE = 0xFFFFFFFF;
    public const uint WAIT_OBJECT_0 = 0;
    public const uint COINIT_MULTITHREADED = 0;
    public const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

#pragma warning restore AN0100
}