using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AN.Audio.AppMeter.Platforms.Windows;

/// <summary>
/// Raw COM interop for WASAPI audio sessions + metering (spec 80 §4 Windows). Manual vtable dispatch — no RCW, no ComImport.
/// The MMDevice pieces are a deliberate COPY of the ~40 lines in <c>AN.Audio/WasapiInterop.cs</c> (packaging rule: no reference to AN.Audio);
/// the session/meter pieces come from <c>_EXTERNAL_APIS/WASAPI_AudioSessions_Metering.md</c> (SDK 10.0.26100.0 audiopolicy.h / endpointvolume.h).
/// </summary>
internal static unsafe class Wasapi_AppMeterInterop
{
    // ─── GUIDs ───────────────────────────────────────────────────────────

    public static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    public static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");

    // audiopolicy.h
    public static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    public static readonly Guid IID_IAudioSessionEnumerator = new("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8");
    public static readonly Guid IID_IAudioSessionControl = new("F4B1A599-7266-4319-A8CA-E70ACB11E8CD");
    public static readonly Guid IID_IAudioSessionControl2 = new("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d");
    public static readonly Guid IID_IAudioSessionNotification = new("641DD20B-4D41-49CC-ABA3-174B9477BB08");
    // endpointvolume.h
    public static readonly Guid IID_IAudioMeterInformation = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");
    // audioclient.h
    public static readonly Guid IID_ISimpleAudioVolume = new("87CE5498-68D6-44E5-9215-6DA47EF883D8");

    // ─── Constants ───────────────────────────────────────────────────────

    public const uint CLSCTX_ALL = 0x17;
    public const uint COINIT_MULTITHREADED = 0;
    public const int eRender = 0;
    public const int eMultimedia = 1;
    public const uint DEVICE_STATE_ACTIVE = 0x00000001;

    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_NOINTERFACE = unchecked((int)0x80004002);
    public const int E_POINTER = unchecked((int)0x80004003);
    public const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
    /// <summary>SUCCESS code from <c>IAudioSessionControl2::GetProcessId</c>: the session is shared by several processes → pid 0 + Unattributed.</summary>
    public const int AUDCLNT_S_NO_SINGLE_PROCESS = 0x0889000D;

    public enum AudioSessionState { Inactive = 0, Active = 1, Expired = 2 }

    // ─── Vtable indices (after IUnknown 0,1,2) ───────────────────────────────────────

    public const int IUnknown_QueryInterface = 0;
    public const int IUnknown_AddRef = 1;
    public const int IUnknown_Release = 2;

    public const int IMMDeviceEnumerator_EnumAudioEndpoints = 3;
    public const int IMMDeviceEnumerator_GetDefaultAudioEndpoint = 4;

    public const int IMMDeviceCollection_GetCount = 3;
    public const int IMMDeviceCollection_Item = 4;

    public const int IMMDevice_Activate = 3;
    public const int IMMDevice_GetId = 5;

    public const int IAudioSessionManager2_GetSessionEnumerator = 5;
    public const int IAudioSessionManager2_RegisterSessionNotification = 6;
    public const int IAudioSessionManager2_UnregisterSessionNotification = 7;

    public const int IAudioSessionEnumerator_GetCount = 3;
    public const int IAudioSessionEnumerator_GetSession = 4;

    public const int IAudioSessionControl_GetState = 3;
    public const int IAudioSessionControl_GetDisplayName = 4;
    public const int IAudioSessionControl2_GetSessionInstanceIdentifier = 13;
    public const int IAudioSessionControl2_GetProcessId = 14;
    public const int IAudioSessionControl2_IsSystemSoundsSession = 15;

    public const int IAudioSessionNotification_OnSessionCreated = 3;

    public const int IAudioMeterInformation_GetPeakValue = 3;

    public const int ISimpleAudioVolume_GetMute = 6;

    // ─── COM helpers ───────────────────────────────────────────────────────

    public static bool Succeeded(int hr) => hr >= 0;

    public static nint GetVtableMethod(nint comPtr, int index)
    {
        nint* vtable = *(nint**)comPtr;
        return vtable[index];
    }

    public static int QueryInterface(nint comPtr, ref Guid iid, out nint iface)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)GetVtableMethod(comPtr, IUnknown_QueryInterface);
        fixed (Guid* pIid = &iid)
        fixed (nint* pIface = &iface)
            return fn(comPtr, pIid, pIface);
    }

    public static uint Release(nint comPtr)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, uint>)GetVtableMethod(comPtr, IUnknown_Release);
        return fn(comPtr);
    }

    /// <summary>Release if non-zero and zero the slot.</summary>
    public static void SafeRelease(ref nint comPtr)
    {
        if (comPtr == 0) return;
        Release(comPtr);
        comPtr = 0;
    }

    /// <summary>Copies a CoTaskMem LPWSTR to a managed string and frees it. Null pointer → empty string.</summary>
    public static string TakeCoTaskString(nint pwsz)
    {
        if (pwsz == 0) return string.Empty;
        try { return Marshal.PtrToStringUni(pwsz) ?? string.Empty; }
        finally { CoTaskMemFree(pwsz); }
    }

    // ─── MMDevice (copy of AN.Audio WasapiInterop) ─────────────────────────────────────

    public static int EnumAudioEndpoints(nint enumerator, int dataFlow, uint stateMask, out nint collection)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, int, uint, nint*, int>)GetVtableMethod(enumerator, IMMDeviceEnumerator_EnumAudioEndpoints);
        fixed (nint* p = &collection)
            return fn(enumerator, dataFlow, stateMask, p);
    }

    public static int GetDefaultAudioEndpoint(nint enumerator, int dataFlow, int role, out nint device)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)GetVtableMethod(enumerator, IMMDeviceEnumerator_GetDefaultAudioEndpoint);
        fixed (nint* p = &device)
            return fn(enumerator, dataFlow, role, p);
    }

    public static int DeviceCollectionGetCount(nint collection, out uint count)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, uint*, int>)GetVtableMethod(collection, IMMDeviceCollection_GetCount);
        fixed (uint* p = &count)
            return fn(collection, p);
    }

    public static int DeviceCollectionItem(nint collection, uint index, out nint device)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)GetVtableMethod(collection, IMMDeviceCollection_Item);
        fixed (nint* p = &device)
            return fn(collection, index, p);
    }

    public static int DeviceActivate(nint device, ref Guid iid, uint clsCtx, nint activationParams, out nint iface)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)GetVtableMethod(device, IMMDevice_Activate);
        fixed (Guid* pIid = &iid)
        fixed (nint* pIface = &iface)
            return fn(device, pIid, clsCtx, activationParams, pIface);
    }

    /// <summary><c>IMMDevice::GetId</c> — caller owns the CoTaskMem string (use <see cref="TakeCoTaskString"/>).</summary>
    public static int DeviceGetId(nint device, out nint idPtr)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, nint*, int>)GetVtableMethod(device, IMMDevice_GetId);
        fixed (nint* p = &idPtr)
            return fn(device, p);
    }

    // ─── IAudioSessionManager2 ──────────────────────────────────────────────────

    /// <summary>Returns a SNAPSHOT enumerator. Also what arms <c>RegisterSessionNotification</c> (documented quirk).</summary>
    public static int SessionManagerGetSessionEnumerator(nint manager, out nint sessionEnumerator)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, nint*, int>)GetVtableMethod(manager, IAudioSessionManager2_GetSessionEnumerator);
        fixed (nint* p = &sessionEnumerator)
            return fn(manager, p);
    }

    public static int SessionManagerRegisterSessionNotification(nint manager, nint notification)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, nint, int>)GetVtableMethod(manager, IAudioSessionManager2_RegisterSessionNotification);
        return fn(manager, notification);
    }

    public static int SessionManagerUnregisterSessionNotification(nint manager, nint notification)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, nint, int>)GetVtableMethod(manager, IAudioSessionManager2_UnregisterSessionNotification);
        return fn(manager, notification);
    }

    // ─── IAudioSessionEnumerator ────────────────────────────────────────────────

    public static int SessionEnumeratorGetCount(nint sessionEnumerator, out int count)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, int*, int>)GetVtableMethod(sessionEnumerator, IAudioSessionEnumerator_GetCount);
        fixed (int* p = &count)
            return fn(sessionEnumerator, p);
    }

    public static int SessionEnumeratorGetSession(nint sessionEnumerator, int index, out nint sessionControl)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, int, nint*, int>)GetVtableMethod(sessionEnumerator, IAudioSessionEnumerator_GetSession);
        fixed (nint* p = &sessionControl)
            return fn(sessionEnumerator, index, p);
    }

    // ─── IAudioSessionControl / IAudioSessionControl2 ───────────────────────────────────────

    public static int SessionControlGetState(nint control, out AudioSessionState state)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, AudioSessionState*, int>)GetVtableMethod(control, IAudioSessionControl_GetState);
        fixed (AudioSessionState* p = &state)
            return fn(control, p);
    }

    /// <summary>CoTaskMem LPWSTR out; often empty.</summary>
    public static int SessionControlGetDisplayName(nint control, out nint namePtr)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, nint*, int>)GetVtableMethod(control, IAudioSessionControl_GetDisplayName);
        fixed (nint* p = &namePtr)
            return fn(control, p);
    }

    /// <summary>CoTaskMem LPWSTR out; unique per session instance → hashed into <c>AudioAppMeter_SessionId</c>.</summary>
    public static int SessionControl2GetSessionInstanceIdentifier(nint control2, out nint idPtr)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, nint*, int>)GetVtableMethod(control2, IAudioSessionControl2_GetSessionInstanceIdentifier);
        fixed (nint* p = &idPtr)
            return fn(control2, p);
    }

    /// <summary>May return <see cref="AUDCLNT_S_NO_SINGLE_PROCESS"/> (a success) with pid 0.</summary>
    public static int SessionControl2GetProcessId(nint control2, out uint pid)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, uint*, int>)GetVtableMethod(control2, IAudioSessionControl2_GetProcessId);
        fixed (uint* p = &pid)
            return fn(control2, p);
    }

    /// <summary>S_OK (0) = system sounds session; S_FALSE (1) = not.</summary>
    public static int SessionControl2IsSystemSoundsSession(nint control2)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, int>)GetVtableMethod(control2, IAudioSessionControl2_IsSystemSoundsSession);
        return fn(control2);
    }

    // ─── IAudioMeterInformation / ISimpleAudioVolume ────────────────────────────────────────

    public static int MeterGetPeakValue(nint meter, out float peak)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, float*, int>)GetVtableMethod(meter, IAudioMeterInformation_GetPeakValue);
        fixed (float* p = &peak)
            return fn(meter, p);
    }

    public static int SimpleAudioVolumeGetMute(nint volume, out int mute)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, int*, int>)GetVtableMethod(volume, ISimpleAudioVolume_GetMute);
        fixed (int* p = &mute)
            return fn(volume, p);
    }

    // ─── Our IAudioSessionNotification implementation (spec 80 D5, rule 4: UnmanagedCallersOnly statics + GCHandle) ──────

    /// <summary>Native object layout handed to the audio service. Slot 0 MUST be the vtable pointer.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SessionNotificationObject
    {
        public nint Vtable;
        public int RefCount;
        public int _pad;
        /// <summary><see cref="GCHandle"/> (as nint) of the owning <see cref="Wasapi_AppMeter"/>; 0 once the owner has detached.</summary>
        public nint OwnerHandle;
    }

    private static readonly nint s_notificationVtable = BuildNotificationVtable();

    private static nint BuildNotificationVtable()
    {
        nint* vtbl = (nint*)NativeMemory.Alloc((nuint)(4 * sizeof(nint)));
        vtbl[IUnknown_QueryInterface] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&Notification_QueryInterface;
        vtbl[IUnknown_AddRef] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Notification_AddRef;
        vtbl[IUnknown_Release] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Notification_Release;
        vtbl[IAudioSessionNotification_OnSessionCreated] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&Notification_OnSessionCreated;
        return (nint)vtbl;
    }

    /// <summary>Allocates one notification object with RefCount 1 (the owner's reference). Free it with <see cref="DetachAndReleaseNotification"/>.</summary>
    public static nint CreateNotificationObject(GCHandle owner)
    {
        var obj = (SessionNotificationObject*)NativeMemory.AllocZeroed((nuint)sizeof(SessionNotificationObject));
        obj->Vtable = s_notificationVtable;
        obj->RefCount = 1;
        obj->OwnerHandle = GCHandle.ToIntPtr(owner);
        return (nint)obj;
    }

    /// <summary>Owner is done: zero the owner slot (late callbacks become no-ops) and drop the owner's reference. Memory is freed when the last reference goes.</summary>
    public static void DetachAndReleaseNotification(nint notificationObject)
    {
        if (notificationObject == 0) return;
        var obj = (SessionNotificationObject*)notificationObject;
        Interlocked.Exchange(ref obj->OwnerHandle, 0);
        Release(notificationObject);   // through our own vtable: refcount 0 frees the memory
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Notification_QueryInterface(nint self, Guid* riid, nint* ppv)
    {
        if (ppv == null) return E_POINTER;
        if (*riid == IID_IUnknown || *riid == IID_IAudioSessionNotification)
        {
            *ppv = self;
            Interlocked.Increment(ref ((SessionNotificationObject*)self)->RefCount);
            return S_OK;
        }
        *ppv = 0;
        return E_NOINTERFACE;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint Notification_AddRef(nint self)
        => (uint)Interlocked.Increment(ref ((SessionNotificationObject*)self)->RefCount);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint Notification_Release(nint self)
    {
        var obj = (SessionNotificationObject*)self;
        int remaining = Interlocked.Decrement(ref obj->RefCount);
        if (remaining == 0) NativeMemory.Free(obj);
        return (uint)remaining;
    }

    /// <summary>Fires on an MTA thread of the audio service. We only flag the owner; the poll thread does the re-enumerate (D5).</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Notification_OnSessionCreated(nint self, nint newSession)
    {
        nint h = Volatile.Read(ref ((SessionNotificationObject*)self)->OwnerHandle);
        if (h != 0 && GCHandle.FromIntPtr(h).Target is Wasapi_AppMeter owner)
            owner.OnSessionCreatedNotification();
        return S_OK;
    }

    // ─── Win32 PInvoke ───────────────────────────────────────────────────────────

#pragma warning disable AN0100 // nint is intentional for COM/Win32 interop

    [DllImport("ole32.dll")]
    public static extern int CoCreateInstance(ref Guid rclsid, nint pUnkOuter, uint dwClsContext, ref Guid riid, out nint ppv);

    [DllImport("ole32.dll")]
    public static extern int CoInitializeEx(nint pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    public static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(nint pv);

#pragma warning restore AN0100
}