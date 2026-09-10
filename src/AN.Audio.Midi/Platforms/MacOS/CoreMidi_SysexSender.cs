using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AN.Audio.Midi.Platforms.MacOS;

#pragma warning disable AN0100

/// <summary>
/// Sprint 4a minimal SysEx sender (SPEC-30 D33): MIDISendSysex with a pinned request, wait for the completion proc (timeout), free.
/// No output port is needed — the request names the destination directly. Grows into CoreMidi_MidiOutput in Sprint 3-mac.
/// </summary>
internal static unsafe class CoreMidi_SysexSender
{
    /// <summary>The state block the completion proc flips. Lives in native memory next to the request so nothing managed is touched from CoreMIDI's thread.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SendBlock
    {
        public CoreMidi_SysexSendRequest Request;
        public int Completed;         // set to 1 by CompletionProc
        public fixed byte Payload[64]; // Identity Request is 6 bytes; room for small messages
    }

    /// <summary>Send <paramref name="bytes"/> (F0..F7) to <paramref name="destination"/>. True when CoreMIDI reported completion within the timeout. Never throws.</summary>
    public static bool TrySend(CoreMidi_EndpointRef destination, ReadOnlySpan<byte> bytes, int timeoutMs)
    {
        if (destination.IsNull || bytes.Length == 0 || bytes.Length > 64) return false;

        var block = (SendBlock*)NativeMemory.AllocZeroed((nuint)sizeof(SendBlock));
        bytes.CopyTo(new Span<byte>(block->Payload, 64));
        block->Request.Destination = destination.Value;
        block->Request.Data = block->Payload;
        block->Request.BytesToSend = (uint)bytes.Length;
        block->Request.Complete = 0;
        block->Request.CompletionProc = (nint)(delegate* unmanaged[Cdecl]<CoreMidi_SysexSendRequest*, void>)&CompletionProc;
        block->Request.CompletionRefCon = block;

        var r = CoreMidi_Interop.MIDISendSysex(&block->Request);
        if (r != CoreMidi_Status.NoError) { NativeMemory.Free(block); return false; }

        if (WaitCompleted(block, timeoutMs)) { NativeMemory.Free(block); return true; }

        // Timed out: ask CoreMIDI to abort (header: client may set complete = true at any time), then give it a moment to call back.
        Volatile.Write(ref block->Request.Complete, 1);
        if (WaitCompleted(block, 100)) { NativeMemory.Free(block); return false; }

        // Still not called back: leak the block rather than free memory the server may still touch.
        return false;
    }

    private static bool WaitCompleted(SendBlock* block, int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        var spin = new SpinWait();
        while (Volatile.Read(ref block->Completed) == 0)
        {
            if (Environment.TickCount64 >= deadline) return false;
            if (spin.NextSpinWillYield) Thread.Sleep(1); else spin.SpinOnce();
        }
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CompletionProc(CoreMidi_SysexSendRequest* request)
    {
        var block = (SendBlock*)request->CompletionRefCon;
        if (block != null) Volatile.Write(ref block->Completed, 1);
    }
}

#pragma warning restore AN0100