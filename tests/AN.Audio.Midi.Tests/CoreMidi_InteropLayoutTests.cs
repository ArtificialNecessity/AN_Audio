using System.Diagnostics;
using System.Runtime.InteropServices;
using AN.Audio.Midi.Platforms.MacOS;
using Xunit;

namespace AN.Audio.Midi.Tests;

/// <summary>Guards the SDK-derived layouts and constants (SPEC-30 D12/D38, _EXTERNAL_APIS/CoreMidi.md — offsets measured with a C probe).</summary>
public unsafe class CoreMidi_InteropLayoutTests
{
    private static bool OnMac => OperatingSystem.IsMacOS();

    [Fact]
    public void Notification_structs_match_measured_sizes_and_offsets()
    {
        Assert.Equal(8, sizeof(CoreMidi_Notification));
        Assert.Equal(24, sizeof(CoreMidi_ObjectAddRemoveNotification));
        Assert.Equal(16, (int)Marshal.OffsetOf<CoreMidi_ObjectAddRemoveNotification>(nameof(CoreMidi_ObjectAddRemoveNotification.Child)));
        Assert.Equal(20, (int)Marshal.OffsetOf<CoreMidi_ObjectAddRemoveNotification>(nameof(CoreMidi_ObjectAddRemoveNotification.ChildType)));
        Assert.Equal(16, (int)Marshal.OffsetOf<CoreMidi_PropertyChangeNotification>(nameof(CoreMidi_PropertyChangeNotification.PropertyName)));
        Assert.Equal(16, sizeof(CoreMidi_IOErrorNotification));
        if (Environment.Is64BitProcess) Assert.Equal(24, sizeof(CoreMidi_PropertyChangeNotification));
    }

    [Fact]
    public void SysexSendRequest_matches_measured_layout()
    {
        if (!Environment.Is64BitProcess) return;
        Assert.Equal(40, sizeof(CoreMidi_SysexSendRequest));
        Assert.Equal(8, (int)Marshal.OffsetOf<CoreMidi_SysexSendRequest>(nameof(CoreMidi_SysexSendRequest.Data)));
        Assert.Equal(16, (int)Marshal.OffsetOf<CoreMidi_SysexSendRequest>(nameof(CoreMidi_SysexSendRequest.BytesToSend)));
        Assert.Equal(20, (int)Marshal.OffsetOf<CoreMidi_SysexSendRequest>(nameof(CoreMidi_SysexSendRequest.Complete)));
        Assert.Equal(24, (int)Marshal.OffsetOf<CoreMidi_SysexSendRequest>(nameof(CoreMidi_SysexSendRequest.CompletionProc)));
        Assert.Equal(32, (int)Marshal.OffsetOf<CoreMidi_SysexSendRequest>(nameof(CoreMidi_SysexSendRequest.CompletionRefCon)));
    }

    [Fact]
    public void Packet_layout_constants_match_header()
    {
        Assert.Equal(4, (int)CoreMidi_Layout.PacketListFirstPacketOffset);
        Assert.Equal(0, (int)CoreMidi_Layout.PacketTimeStampOffset);
        Assert.Equal(8, (int)CoreMidi_Layout.PacketLengthOffset);
        Assert.Equal(10, (int)CoreMidi_Layout.PacketDataOffset);
        Assert.Equal(4, (int)CoreMidi_Layout.PacketAlignmentArm);
        Assert.Equal(8, (int)CoreMidi_Layout.EventListFirstPacketOffset);
        Assert.Equal(12, (int)CoreMidi_Layout.EventPacketWordsOffset);
    }

    [Fact]
    public void Constants_match_sdk_values()
    {
        Assert.Equal(0, (int)CoreMidi_Status.NoError);
        Assert.Equal(-10830, (int)CoreMidi_Status.InvalidClient);
        Assert.Equal(-10831, (int)CoreMidi_Status.InvalidPort);
        Assert.Equal(-10832, (int)CoreMidi_Status.WrongEndpointType);
        Assert.Equal(-10833, (int)CoreMidi_Status.NoConnection);
        Assert.Equal(-10834, (int)CoreMidi_Status.UnknownEndpoint);
        Assert.Equal(-10835, (int)CoreMidi_Status.UnknownProperty);
        Assert.Equal(-10839, (int)CoreMidi_Status.ServerStartErr);
        Assert.Equal(-10841, (int)CoreMidi_Status.WrongThread);
        Assert.Equal(-10842, (int)CoreMidi_Status.ObjectNotFound);
        Assert.Equal(-10844, (int)CoreMidi_Status.NotPermitted);
        Assert.Equal(-10845, (int)CoreMidi_Status.UnknownError);
        Assert.Equal(1, (int)CoreMidi_NotificationId.SetupChanged);
        Assert.Equal(2, (int)CoreMidi_NotificationId.ObjectAdded);
        Assert.Equal(3, (int)CoreMidi_NotificationId.ObjectRemoved);
        Assert.Equal(4, (int)CoreMidi_NotificationId.PropertyChanged);
        Assert.Equal(7, (int)CoreMidi_NotificationId.IOError);
        Assert.Equal(2, (int)CoreMidi_ObjectType.Source);
        Assert.Equal(3, (int)CoreMidi_ObjectType.Destination);
        Assert.Equal(0x12, (int)CoreMidi_ObjectType.ExternalSource);
        Assert.Equal(1, (int)CoreMidi_ProtocolId.Midi1);
        Assert.Equal(2, (int)CoreMidi_ProtocolId.Midi2);
        Assert.Equal(0x08000100u, (uint)CoreFoundation_Interop.CFStringEncoding.Utf8);
    }

    [Fact]
    public void PacketNext_rounds_to_4_on_arm_only()
    {
        byte* basePtr = (byte*)0x1000;
        byte* next = CoreMidi_Interop.PacketNext(basePtr, 3);   // data ends at 0x1000 + 10 + 3 = 0x100D
        bool arm = RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm;
        Assert.Equal(arm ? (nint)0x1010 : (nint)0x100D, (nint)next);
        Assert.Equal((nint)0x1010, (nint)CoreMidi_Interop.PacketNext(basePtr, 6));   // already aligned: no change either way
    }

    [Fact]
    public void MIDIGetNumberOfSources_is_callable()
    {
        if (!OnMac) return;
        nuint n = CoreMidi_Interop.MIDIGetNumberOfSources();
        Assert.True(n < 1000);
    }

    [Fact]
    public void Property_keys_resolve()
    {
        if (!OnMac) return;
        Assert.NotEqual(0, CoreMidi_PropertyKeys.Name);
        Assert.NotEqual(0, CoreMidi_PropertyKeys.DisplayName);
        Assert.NotEqual(0, CoreMidi_PropertyKeys.UniqueId);
        Assert.NotEqual(0, CoreMidi_PropertyKeys.Offline);
        Assert.Equal("name", CoreFoundation_Interop.ToManagedString(CoreMidi_PropertyKeys.Name));   // the CFString VALUE of kMIDIPropertyName is "name" (measured)
    }

    [Fact]
    public void CFString_round_trips_utf8()
    {
        if (!OnMac) return;
        nint cf = CoreFoundation_Interop.CreateCFString("MPK mini IV — café ♪");
        try { Assert.Equal("MPK mini IV — café ♪", CoreFoundation_Interop.ToManagedString(cf)); }
        finally { CoreFoundation_Interop.CFRelease(cf); }
    }

    [Fact]
    public void Stopwatch_is_mach_absolute_time_in_nanoseconds()
    {
        if (!OnMac) return;
        Mach_Interop.TimebaseInfo tb = default;
        Assert.Equal(0, Mach_Interop.mach_timebase_info(&tb));
        Assert.NotEqual(0u, tb.Numer); Assert.NotEqual(0u, tb.Denom);
        // Measured 2026-09-10: Stopwatch.Frequency == 1e9; Stopwatch == mach_absolute_time × numer/denom to the nanosecond (same clock, ns unit).
        Assert.Equal(1_000_000_000, Stopwatch.Frequency);
        long sw = Stopwatch.GetTimestamp();
        ulong mach = Mach_Interop.mach_absolute_time();
        double machNs = (double)mach * tb.Numer / tb.Denom;
        double diffMs = Math.Abs(sw - machNs) / 1e6;
        Assert.True(diffMs < 1.0, $"Stopwatch and scaled mach_absolute_time differ by {diffMs} ms (D36 assumption broken)");
        Assert.True(CoreMidi_Client.Instance.MachClockIsStopwatchClock);
        Assert.Equal((long)(1000UL * tb.Numer / tb.Denom), CoreMidi_Client.Instance.MachToStopwatchTicks(1000));
    }

    [Fact]
    public void Client_creates_and_enumeration_keys_are_unique()
    {
        if (!OnMac) return;
        Assert.Equal(CoreMidi_Status.NoError, CoreMidi_Client.Instance.CreateStatus);
        Assert.False(CoreMidi_Client.Instance.Client.IsNull);
        var entries = CoreMidi_MidiInEnumerator.EnumerateInputs();
        Assert.Equal(entries.Count, entries.Select(e => e.Key).Distinct().Count());
        Assert.All(entries, e => Assert.StartsWith("coremidi-uid:", e.Key.Value));
    }
}