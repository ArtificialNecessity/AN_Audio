using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AN.Audio.Midi.Platforms.Windows;

#pragma warning disable AN0100 // Win32 handles / DWORD_PTR

/// <summary>
/// Sprint-1 minimal output (SPEC-30 D9): open an out port, send one SysEx buffer, wait for MOM_DONE, close.
/// Runs on a background thread; blocking is fine here. Grows into WinMm_MidiOutput in Sprint 3.
/// </summary>
internal static unsafe class WinMm_MidiOutLongSender
{
    /// <summary>Returns true if the driver accepted and completed the buffer within the timeout. Never throws.</summary>
    public static bool TrySendLongMessage(uint outDeviceIndex, ReadOnlySpan<byte> sysex, int timeoutMs)
    {
        using var done = new ManualResetEventSlim(false);
        GCHandle doneHandle = GCHandle.Alloc(done);
        nint hmo = 0;
        WinMm_MidiHdr* hdr = null;
        bool prepared = false;
        try
        {
            var callback = (delegate* unmanaged[Stdcall]<nint, uint, nuint, nuint, nuint, void>)&MidiOutProc;
            var r = WinMm_MidiInterop.midiOutOpen(&hmo, outDeviceIndex, (nuint)callback, (nuint)GCHandle.ToIntPtr(doneHandle), WinMm_OpenFlags.CallbackFunction);
            if (r != WinMm_Result.NoError) return false;

            hdr = (WinMm_MidiHdr*)NativeMemory.AllocZeroed(WinMm_MidiInterop.MidiHdrSize);
            hdr->lpData = (byte*)NativeMemory.Alloc((nuint)sysex.Length);
            sysex.CopyTo(new Span<byte>(hdr->lpData, sysex.Length));
            hdr->dwBufferLength = (uint)sysex.Length;
            hdr->dwBytesRecorded = (uint)sysex.Length;

            r = WinMm_MidiInterop.midiOutPrepareHeader(hmo, hdr, WinMm_MidiInterop.MidiHdrSize);
            if (r != WinMm_Result.NoError) return false;
            prepared = true;

            r = WinMm_MidiInterop.midiOutLongMsg(hmo, hdr, WinMm_MidiInterop.MidiHdrSize);
            if (r != WinMm_Result.NoError) return false;

            // The header must stay pinned until MOM_DONE. If the driver never completes, we still must not free
            // it under the driver: leak the header rather than corrupt memory (documented D9 fallback).
            if (!done.Wait(timeoutMs))
            {
                hdr = null;   // intentionally leaked
                prepared = false;
                return false;
            }
            return true;
        }
        finally
        {
            if (hmo != 0)
            {
                if (prepared && hdr != null) WinMm_MidiInterop.midiOutUnprepareHeader(hmo, hdr, WinMm_MidiInterop.MidiHdrSize);
                WinMm_MidiInterop.midiOutClose(hmo);
            }
            if (hdr != null)
            {
                if (hdr->lpData != null) NativeMemory.Free(hdr->lpData);
                NativeMemory.Free(hdr);
            }
            doneHandle.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void MidiOutProc(nint hmo, uint uMsg, nuint dwInstance, nuint dwParam1, nuint dwParam2)
    {
        if ((WinMm_MidiOutMessage)uMsg != WinMm_MidiOutMessage.Done) return;
        if (GCHandle.FromIntPtr((nint)dwInstance).Target is ManualResetEventSlim done) done.Set();
    }
}

#pragma warning restore AN0100