using System.Runtime.InteropServices;

namespace AN.Audio.Platforms.MacOS;

/// <summary>
/// PInvoke declarations for macOS CoreAudio HAL (Hardware Abstraction Layer).
/// Used for device enumeration, device selection, and change notification.
/// All calls target the CoreAudio framework (AudioHardware.h).
/// </summary>
#pragma warning disable AN0100 // nint is intentional for CoreAudio interop
internal static unsafe class CoreAudioInterop
{
    private const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    // ─── AudioObjectPropertyAddress ──────────────────────────────────────

    /// <summary>
    /// Identifies a specific property of an audio object.
    /// Maps to AudioObjectPropertyAddress in AudioHardware.h.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AudioObjectPropertyAddress
    {
        public uint mSelector;
        public uint mScope;
        public uint mElement;
    }

    // ─── Constants ───────────────────────────────────────────────────────

    /// <summary>The AudioObjectID for the system-wide audio object.</summary>
    public const uint kAudioObjectSystemObject = 1;

    // Property selectors (four-character codes)
    /// <summary>'dev#' — Array of AudioDeviceIDs for all devices.</summary>
    public const uint kAudioHardwarePropertyDevices = 0x64657623;

    /// <summary>'dOut' — The default output device AudioDeviceID.</summary>
    public const uint kAudioHardwarePropertyDefaultOutputDevice = 0x644F7574;

    /// <summary>'lnam' — CFStringRef name of an audio object.</summary>
    public const uint kAudioObjectPropertyName = 0x6C6E616D;

    /// <summary>'uid ' — CFStringRef unique identifier for a device.</summary>
    public const uint kAudioDevicePropertyDeviceUID = 0x75696420;

    /// <summary>'stm#' — Array of AudioStreamIDs for a device.</summary>
    public const uint kAudioDevicePropertyStreams = 0x73746D23;

    // Property scopes
    /// <summary>'glob' — Global scope (applies to the whole object).</summary>
    public const uint kAudioObjectPropertyScopeGlobal = 0x676C6F62;

    /// <summary>'outp' — Output scope (playback direction).</summary>
    public const uint kAudioObjectPropertyScopeOutput = 0x6F757470;

    // Property elements
    /// <summary>Main element (was kAudioObjectPropertyElementMaster, renamed).</summary>
    public const uint kAudioObjectPropertyElementMain = 0;

    // AudioQueue property for setting current device
    /// <summary>'aqcd' — Set the current device on an AudioQueue.</summary>
    public const uint kAudioQueueProperty_CurrentDevice = 0x61716364;

    // ─── AudioObject Functions ───────────────────────────────────────────

    /// <summary>
    /// Get the size of a property's data.
    /// </summary>
    [DllImport(CoreAudio, CallingConvention = CallingConvention.Cdecl)]
    public static extern int AudioObjectGetPropertyDataSize(
        uint inObjectID,
        AudioObjectPropertyAddress* inAddress,
        uint inQualifierDataSize,
        nint inQualifierData,
        out uint outDataSize);

    /// <summary>
    /// Get the value of a property.
    /// </summary>
    [DllImport(CoreAudio, CallingConvention = CallingConvention.Cdecl)]
    public static extern int AudioObjectGetPropertyData(
        uint inObjectID,
        AudioObjectPropertyAddress* inAddress,
        uint inQualifierDataSize,
        nint inQualifierData,
        ref uint ioDataSize,
        void* outData);

    /// <summary>
    /// Set the value of a property.
    /// </summary>
    [DllImport(CoreAudio, CallingConvention = CallingConvention.Cdecl)]
    public static extern int AudioObjectSetPropertyData(
        uint inObjectID,
        AudioObjectPropertyAddress* inAddress,
        uint inQualifierDataSize,
        nint inQualifierData,
        uint inDataSize,
        void* inData);

    /// <summary>
    /// Check if a property exists on an audio object.
    /// </summary>
    [DllImport(CoreAudio, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool AudioObjectHasProperty(
        uint inObjectID,
        AudioObjectPropertyAddress* inAddress);

    /// <summary>
    /// Register a property listener callback.
    /// Callback signature: OSStatus (*)(AudioObjectID, UInt32 numberAddresses,
    ///     const AudioObjectPropertyAddress* addresses, void* clientData)
    /// </summary>
    [DllImport(CoreAudio, CallingConvention = CallingConvention.Cdecl)]
    public static extern int AudioObjectAddPropertyListener(
        uint inObjectID,
        AudioObjectPropertyAddress* inAddress,
        nint inListener,  // function pointer
        nint inClientData);

    /// <summary>
    /// Unregister a property listener callback.
    /// Must pass the same address, listener, and clientData as when registering.
    /// </summary>
    [DllImport(CoreAudio, CallingConvention = CallingConvention.Cdecl)]
    public static extern int AudioObjectRemovePropertyListener(
        uint inObjectID,
        AudioObjectPropertyAddress* inAddress,
        nint inListener,
        nint inClientData);

    // ─── AudioQueue Property Setting ─────────────────────────────────────

    /// <summary>
    /// Set a property on an AudioQueue (used for device selection).
    /// </summary>
    [DllImport("/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox", CallingConvention = CallingConvention.Cdecl)]
    public static extern int AudioQueueSetProperty(
        nint inAQ,
        uint inID,
        void* inData,
        uint inDataSize);

    // ─── CoreFoundation Helpers ──────────────────────────────────────────

    /// <summary>Get the length of a CFString in UTF-16 code units.</summary>
    [DllImport(CoreFoundation, CallingConvention = CallingConvention.Cdecl)]
    public static extern nint CFStringGetLength(nint theString);

    /// <summary>
    /// Copy the contents of a CFString into a buffer as UTF-8.
    /// Returns true if successful.
    /// </summary>
    [DllImport(CoreFoundation, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool CFStringGetCString(
        nint theString,
        byte* buffer,
        nint bufferSize,
        uint encoding);

    /// <summary>Create a CFString from a C string (UTF-8).</summary>
    [DllImport(CoreFoundation, CallingConvention = CallingConvention.Cdecl)]
    public static extern nint CFStringCreateWithCString(
        nint alloc,  // pass 0 for default allocator
        byte* cStr,
        uint encoding);

    /// <summary>Release a CoreFoundation object.</summary>
    [DllImport(CoreFoundation, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CFRelease(nint cf);

    /// <summary>kCFStringEncodingUTF8</summary>
    public const uint kCFStringEncodingUTF8 = 0x08000100;

    // ─── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Convert a CFStringRef to a managed string. Returns null if the pointer is zero.
    /// Does NOT release the CFString.
    /// </summary>
    public static string? CFStringToString(nint cfString)
    {
        if (cfString == 0) return null;

        // Try to get as UTF-8 C string
        byte* buffer = stackalloc byte[512];
        if (CFStringGetCString(cfString, buffer, 512, kCFStringEncodingUTF8))
        {
            return Marshal.PtrToStringUTF8((nint)buffer);
        }

        return null;
    }

}
