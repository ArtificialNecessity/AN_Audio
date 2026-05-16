using System.Runtime.InteropServices;

namespace AN.Audio.Platforms.MacOS;

/// <summary>
/// PInvoke declarations for macOS AudioToolbox (AudioQueue Services).
/// All calls target the AudioToolbox framework, always present on macOS.
/// </summary>
internal static unsafe class AudioToolboxInterop
{
    private const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

    // ─── AudioStreamBasicDescription ─────────────────────────────────────

    /// <summary>
    /// Describes the audio data format for an audio stream.
    /// Maps to AudioStreamBasicDescription in CoreAudioTypes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AudioStreamBasicDescription
    {
        public double mSampleRate;
        public uint mFormatID;
        public uint mFormatFlags;
        public uint mBytesPerPacket;
        public uint mFramesPerPacket;
        public uint mBytesPerFrame;
        public uint mChannelsPerFrame;
        public uint mBitsPerChannel;
        public uint mReserved;
    }

    // ─── Format Constants ────────────────────────────────────────────────

    /// <summary>'lpcm' — Linear PCM format ID.</summary>
    public const uint kAudioFormatLinearPCM = 0x6C70636D;

    /// <summary>Samples are floating point.</summary>
    public const uint kLinearPCMFormatFlagIsFloat = 0x1;

    /// <summary>Samples are signed integers.</summary>
    public const uint kLinearPCMFormatFlagIsSignedInteger = 0x4;

    /// <summary>Samples are packed (no padding between samples).</summary>
    public const uint kLinearPCMFormatFlagIsPacked = 0x8;

    // ─── AudioQueueBuffer ────────────────────────────────────────────────

    /// <summary>
    /// Represents an audio queue buffer. The system allocates these;
    /// we fill mAudioData and set mAudioDataByteSize.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AudioQueueBuffer
    {
        public uint mAudioDataBytesCapacity;
        public nint mAudioData;           // void*
        public uint mAudioDataByteSize;
        public nint mUserData;            // void*
        public uint mPacketDescriptionCapacity;
        public nint mPacketDescriptions;  // AudioStreamPacketDescription*
        public uint mPacketDescriptionCount;
    }

    // ─── Callback Delegate ───────────────────────────────────────────────

#pragma warning disable AN0100 // nint is intentional for AudioToolbox interop

    /// <summary>
    /// AudioQueueOutputCallback — invoked when a buffer has been consumed
    /// and needs to be refilled.
    /// </summary>
    /// <param name="inUserData">User data pointer passed to AudioQueueNewOutput.</param>
    /// <param name="inAQ">The audio queue that owns the buffer.</param>
    /// <param name="inBuffer">The buffer to refill.</param>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void AudioQueueOutputCallback(
        nint inUserData,
        nint inAQ,
        AudioQueueBuffer* inBuffer);

    // ─── AudioQueue Functions ────────────────────────────────────────────

    /// <summary>
    /// Create a new output (playback) audio queue.
    /// </summary>
    [DllImport(AudioToolbox, CallingConvention = CallingConvention.Cdecl)]
    public static extern int AudioQueueNewOutput(
        AudioStreamBasicDescription* inFormat,
        nint inCallbackProc,       // AudioQueueOutputCallback function pointer
        nint inUserData,
        nint inCallbackRunLoop,    // NULL = internal thread
        nint inCallbackRunLoopMode,// NULL = default
        uint inFlags,              // reserved, pass 0
        out nint outAQ);

    /// <summary>
    /// Allocate a buffer for the audio queue.
    /// </summary>
    [DllImport(AudioToolbox, CallingConvention = CallingConvention.Cdecl)]
    public static extern int AudioQueueAllocateBuffer(
        nint inAQ,
        uint inBufferByteSize,
        out AudioQueueBuffer* outBuffer);

    /// <summary>
    /// Enqueue a filled buffer for playback.
    /// </summary>
    [DllImport(AudioToolbox, CallingConvention = CallingConvention.Cdecl)]
    public static extern int AudioQueueEnqueueBuffer(
        nint inAQ,
        AudioQueueBuffer* inBuffer,
        uint inNumPacketDescs,
        nint inPacketDescs);  // NULL for PCM

    /// <summary>
    /// Start the audio queue (begin playback).
    /// </summary>
    [DllImport(AudioToolbox, CallingConvention = CallingConvention.Cdecl)]
    public static extern int AudioQueueStart(
        nint inAQ,
        nint inStartTime);  // NULL = start immediately

    /// <summary>
    /// Stop the audio queue.
    /// </summary>
    /// <param name="inAQ">The audio queue.</param>
    /// <param name="inImmediate">true = stop immediately; false = stop after queued buffers drain.</param>
    [DllImport(AudioToolbox, CallingConvention = CallingConvention.Cdecl)]
    public static extern int AudioQueueStop(
        nint inAQ,
        [MarshalAs(UnmanagedType.I1)] bool inImmediate);

    /// <summary>
    /// Pause the audio queue (can be resumed with AudioQueueStart).
    /// </summary>
    [DllImport(AudioToolbox, CallingConvention = CallingConvention.Cdecl)]
    public static extern int AudioQueuePause(nint inAQ);

    /// <summary>
    /// Flush all enqueued buffers (blocks until all buffers have been consumed).
    /// </summary>
    [DllImport(AudioToolbox, CallingConvention = CallingConvention.Cdecl)]
    public static extern int AudioQueueFlush(nint inAQ);

    /// <summary>
    /// Reset the audio queue — stops, removes all buffers, resets timeline.
    /// </summary>
    [DllImport(AudioToolbox, CallingConvention = CallingConvention.Cdecl)]
    public static extern int AudioQueueReset(nint inAQ);

    /// <summary>
    /// Dispose of the audio queue and free its resources.
    /// </summary>
    /// <param name="inAQ">The audio queue.</param>
    /// <param name="inImmediate">true = dispose immediately; false = dispose after buffers drain.</param>
    [DllImport(AudioToolbox, CallingConvention = CallingConvention.Cdecl)]
    public static extern int AudioQueueDispose(
        nint inAQ,
        [MarshalAs(UnmanagedType.I1)] bool inImmediate);

    /// <summary>
    /// Free a buffer previously allocated with AudioQueueAllocateBuffer.
    /// </summary>
    [DllImport(AudioToolbox, CallingConvention = CallingConvention.Cdecl)]
    public static extern int AudioQueueFreeBuffer(
        nint inAQ,
        AudioQueueBuffer* inBuffer);

#pragma warning restore AN0100

    // ─── OSStatus Helpers ────────────────────────────────────────────────

    /// <summary>noErr — success.</summary>
    public const int noErr = 0;

    /// <summary>
    /// Throw if the OSStatus indicates an error.
    /// </summary>
    public static void ThrowIfError(int status, string operation)
    {
        if (status != noErr)
            throw new InvalidOperationException(
                $"AudioToolbox {operation} failed with OSStatus {status} (0x{status:X8})");
    }
}
