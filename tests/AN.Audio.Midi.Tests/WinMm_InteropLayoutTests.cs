using System.Runtime.InteropServices;
using AN.Audio.Midi.Platforms.Windows;
using Xunit;

namespace AN.Audio.Midi.Tests;

/// <summary>Guards the SDK-derived layouts and constants (SPEC-30 D12, _EXTERNAL_APIS/WinMM_MidiIn.md).</summary>
public unsafe class WinMm_InteropLayoutTests
{
    [Fact]
    public void MidiHdr_size_matches_sdk()
    {
        // x64: 8+4+4+8+4(+4 pad)+8+8+4(+4 pad)+64 = 120. Our struct sizes dwReserved for x64 on both bitnesses.
        if (Environment.Is64BitProcess) Assert.Equal(120, sizeof(WinMm_MidiHdr));
        else Assert.True(sizeof(WinMm_MidiHdr) >= 64);
    }

    [Fact]
    public void MidiInCaps2W_is_124_bytes() => Assert.Equal(124, sizeof(WinMm_MidiInCaps2W));

    [Fact]
    public void MidiOutCapsW_is_84_bytes() => Assert.Equal(84, sizeof(WinMm_MidiOutCapsW));

    [Fact]
    public void Constants_match_sdk_values()
    {
        Assert.Equal(0x3C1u, (uint)WinMm_MidiInMessage.Open);
        Assert.Equal(0x3C2u, (uint)WinMm_MidiInMessage.Close);
        Assert.Equal(0x3C3u, (uint)WinMm_MidiInMessage.Data);
        Assert.Equal(0x3C4u, (uint)WinMm_MidiInMessage.LongData);
        Assert.Equal(0x3C5u, (uint)WinMm_MidiInMessage.Error);
        Assert.Equal(0x3C6u, (uint)WinMm_MidiInMessage.LongError);
        Assert.Equal(0x3CCu, (uint)WinMm_MidiInMessage.MoreData);
        Assert.Equal(0x3C7u, (uint)WinMm_MidiOutMessage.Open);
        Assert.Equal(0x3C8u, (uint)WinMm_MidiOutMessage.Close);
        Assert.Equal(0x3C9u, (uint)WinMm_MidiOutMessage.Done);
        Assert.Equal(0x00030000u, (uint)WinMm_OpenFlags.CallbackFunction);
        Assert.Equal(0x00000020u, (uint)WinMm_OpenFlags.MidiIoStatus);
        Assert.Equal(1u, (uint)WinMm_HdrFlags.Done);
        Assert.Equal(2u, (uint)WinMm_HdrFlags.Prepared);
        Assert.Equal(4u, (uint)WinMm_HdrFlags.InQueue);
        Assert.Equal(0u, (uint)WinMm_Result.NoError);
        Assert.Equal(2u, (uint)WinMm_Result.BadDeviceId);
        Assert.Equal(4u, (uint)WinMm_Result.Allocated);
        Assert.Equal(5u, (uint)WinMm_Result.InvalidHandle);
        Assert.Equal(6u, (uint)WinMm_Result.NoDriver);
        Assert.Equal(7u, (uint)WinMm_Result.NoMem);
        Assert.Equal(64u, (uint)WinMm_Result.MidiUnprepared);
        Assert.Equal(65u, (uint)WinMm_Result.MidiStillPlaying);
        Assert.Equal(68u, (uint)WinMm_Result.MidiNoDevice);
        Assert.Equal(32, (int)WinMm_Limits.MaxPNameLen);
    }

    [Fact]
    public void UnpackShortMessage_is_little_endian_status_first()
    {
        WinMm_MidiInterop.UnpackShortMessage(0x00403C90, out var status, out var d1, out var d2);
        Assert.Equal(0x90, status);
        Assert.Equal(0x3C, d1);
        Assert.Equal(0x40, d2);
    }

    [Fact]
    public void midiInGetNumDevs_is_callable()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        uint n = WinMm_MidiInterop.midiInGetNumDevs();
        Assert.True(n < 1000);
    }

    [Fact]
    public void Enumeration_does_not_throw_and_keys_are_unique()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var entries = WinMm_MidiInEnumerator.EnumerateInputs();
        Assert.Equal(entries.Count, entries.Select(e => e.Key).Distinct().Count());
    }
}