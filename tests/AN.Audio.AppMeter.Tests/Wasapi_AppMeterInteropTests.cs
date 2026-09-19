using System.Runtime.InteropServices;
using AN.Audio.AppMeter.Platforms.Windows;
using Xunit;
using static AN.Audio.AppMeter.Platforms.Windows.Wasapi_AppMeterInterop;

namespace AN.Audio.AppMeter.Tests;

/// <summary>Guards the SDK-derived IIDs, vtable slots and HRESULTs against _EXTERNAL_APIS/WASAPI_AudioSessions_Metering.md (SDK 10.0.26100.0).</summary>
public unsafe class Wasapi_AppMeterInteropTests
{
    [Fact]
    public void IIDs_match_header_text()
    {
        Assert.Equal(new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), IID_IAudioSessionManager2);
        Assert.Equal(new Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), IID_IAudioSessionEnumerator);
        Assert.Equal(new Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), IID_IAudioSessionControl);
        Assert.Equal(new Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), IID_IAudioSessionControl2);
        Assert.Equal(new Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08"), IID_IAudioSessionNotification);
        Assert.Equal(new Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), IID_IAudioMeterInformation);
        Assert.Equal(new Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), IID_ISimpleAudioVolume);
        Assert.Equal(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), CLSID_MMDeviceEnumerator);
        Assert.Equal(new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), IID_IMMDeviceEnumerator);
        Assert.Equal(new Guid("00000000-0000-0000-C000-000000000046"), IID_IUnknown);
    }

    [Fact]
    public void Vtable_slots_match_header()
    {
        Assert.Equal(5, IAudioSessionManager2_GetSessionEnumerator);
        Assert.Equal(6, IAudioSessionManager2_RegisterSessionNotification);
        Assert.Equal(7, IAudioSessionManager2_UnregisterSessionNotification);
        Assert.Equal(3, IAudioSessionEnumerator_GetCount);
        Assert.Equal(4, IAudioSessionEnumerator_GetSession);
        Assert.Equal(3, IAudioSessionControl_GetState);
        Assert.Equal(4, IAudioSessionControl_GetDisplayName);
        Assert.Equal(13, IAudioSessionControl2_GetSessionInstanceIdentifier);
        Assert.Equal(14, IAudioSessionControl2_GetProcessId);
        Assert.Equal(15, IAudioSessionControl2_IsSystemSoundsSession);
        Assert.Equal(3, IAudioSessionNotification_OnSessionCreated);
        Assert.Equal(3, IAudioMeterInformation_GetPeakValue);
        Assert.Equal(6, ISimpleAudioVolume_GetMute);
        Assert.Equal(3, IMMDeviceEnumerator_EnumAudioEndpoints);
        Assert.Equal(4, IMMDeviceEnumerator_GetDefaultAudioEndpoint);
        Assert.Equal(3, IMMDevice_Activate);
        Assert.Equal(5, IMMDevice_GetId);
    }

    [Fact]
    public void HResults_and_constants_match_header()
    {
        Assert.Equal(0x0889000D, AUDCLNT_S_NO_SINGLE_PROCESS);
        Assert.True(Succeeded(AUDCLNT_S_NO_SINGLE_PROCESS));   // it is a SUCCESS code
        Assert.Equal(unchecked((int)0x80004002), E_NOINTERFACE);
        Assert.Equal(0x17u, CLSCTX_ALL);
        Assert.Equal(0, (int)AudioSessionState.Inactive);
        Assert.Equal(1, (int)AudioSessionState.Active);
        Assert.Equal(2, (int)AudioSessionState.Expired);
        Assert.Equal(0, eRender);
        Assert.Equal(1, eMultimedia);
        Assert.Equal(1u, DEVICE_STATE_ACTIVE);
    }

    [Fact]
    public void NotificationObject_layout_starts_with_vtable_pointer()
    {
        Assert.Equal(0, (int)Marshal.OffsetOf<SessionNotificationObject>(nameof(SessionNotificationObject.Vtable)));
        Assert.Equal(sizeof(nint), (int)Marshal.OffsetOf<SessionNotificationObject>(nameof(SessionNotificationObject.RefCount)));
    }

    [Fact]
    public void NotificationObject_answers_QI_and_refcounts_through_its_own_vtable()
    {
        // Pure managed round-trip through the function pointers we hand to the audio service — runs on every OS.
        var handle = GCHandle.Alloc(new object(), GCHandleType.Weak);
        nint obj = CreateNotificationObject(handle);
        try
        {
            var iid = IID_IAudioSessionNotification;
            Assert.Equal(S_OK, QueryInterface(obj, ref iid, out nint same));
            Assert.Equal(obj, same);
            Assert.Equal(1u, Release(same));                 // back to the owner's single reference

            var unk = IID_IUnknown;
            Assert.Equal(S_OK, QueryInterface(obj, ref unk, out same));
            Assert.Equal(1u, Release(same));

            var wrong = IID_IAudioMeterInformation;
            Assert.Equal(E_NOINTERFACE, QueryInterface(obj, ref wrong, out nint none));
            Assert.Equal(0, none);

            // OnSessionCreated with an owner that is not a Wasapi_AppMeter must be a harmless no-op.
            var fn = (delegate* unmanaged[Stdcall]<nint, nint, int>)GetVtableMethod(obj, IAudioSessionNotification_OnSessionCreated);
            Assert.Equal(S_OK, fn(obj, 0));
        }
        finally
        {
            DetachAndReleaseNotification(obj);   // refcount 1 → 0 frees
            handle.Free();
        }
    }

    [Fact]
    public void Factory_capability_matches_os()
    {
        bool win = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        Assert.Equal(win ? AudioAppMeter_Capability.Metering : AudioAppMeter_Capability.None, AudioAppMeter.Capability);
        Assert.Equal(win, AudioAppMeter.IsAvailable);
        if (!win)
        {
            Assert.Throws<AudioAppMeter_UnavailableException>(() => AudioAppMeter.Create());
            using var nul = AudioAppMeter.CreateOrNull();
            Assert.Equal(AudioAppMeter_Capability.None, nul.Capability);
            Assert.NotEqual(AudioAppMeter_UnavailableReason.None, nul.UnavailableReason);
        }
    }

    [Fact]
    public void Live_meter_enumerates_without_throwing_and_ids_are_unique()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        using var meter = AudioAppMeter.Create(new AudioAppMeter_Options { PollIntervalMs = 20 });
        if (meter.Capability == AudioAppMeter_Capability.None) return;   // headless CI box without an audio service
        Thread.Sleep(150);
        var rows = new AudioAppMeter_Sample[256];
        int n = meter.ReadSessions(rows);
        Assert.True(n <= rows.Length);
        var ids = rows.AsSpan(0, n).ToArray().Select(r => r.SessionId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        foreach (var r in rows.AsSpan(0, n))
        {
            Assert.InRange(r.Peak.Value, 0f, 1f);
            if (r.ProcessId.IsUnknown) Assert.True(r.Flags.HasFlag(AudioAppMeter_SessionFlags.Unattributed));
            _ = meter.LookupDisplayName(r.SessionId);   // must not throw
        }
        Assert.InRange(meter.ReadMasterPeak(), 0f, 1f);
    }
}