using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AN.Audio.Midi.Platforms.MacOS;

// Ground truth: _EXTERNAL_APIS/CoreMidi.md (MIDIServices.h, macOS 15 SDK; struct offsets measured with a C probe on arm64).
// SPEC-30 D12: every native constant lives here as an enum and is used symbolically. No literals elsewhere.
// SPEC-30 D28: pure C API via DllImport into the framework binary. No Objective-C runtime, no AN.OSXBindings.

#pragma warning disable AN0100 // uint-backed CoreMIDI object refs, nint CF handles, nuint ItemCount are intentional interop shapes

/// <summary>OSStatus results from CoreMIDI (MIDIServices.h CF_ENUM(OSStatus)). 0 = noErr.</summary>
internal enum CoreMidi_Status : int
{
    NoError = 0,                       // noErr
    InvalidClient = -10830,            // kMIDIInvalidClient
    InvalidPort = -10831,              // kMIDIInvalidPort
    WrongEndpointType = -10832,        // kMIDIWrongEndpointType — connected a destination to an input port
    NoConnection = -10833,             // kMIDINoConnection — disconnect of a source that was not connected
    UnknownEndpoint = -10834,          // kMIDIUnknownEndpoint — endpoint ref is stale (device unplugged)
    UnknownProperty = -10835,          // kMIDIUnknownProperty — property not set on this object (normal for Manufacturer/Model)
    WrongPropertyType = -10836,        // kMIDIWrongPropertyType
    NoCurrentSetup = -10837,           // kMIDINoCurrentSetup
    MessageSendErr = -10838,           // kMIDIMessageSendErr
    ServerStartErr = -10839,           // kMIDIServerStartErr — MIDIServer could not start; no MIDI at all
    SetupFormatErr = -10840,           // kMIDISetupFormatErr
    WrongThread = -10841,              // kMIDIWrongThread
    ObjectNotFound = -10842,           // kMIDIObjectNotFound — MIDIObjectFindByUniqueID for a vanished device
    IdNotUnique = -10843,              // kMIDIIDNotUnique
    NotPermitted = -10844,             // kMIDINotPermitted — Bluetooth entitlement / sandbox
    UnknownError = -10845,             // kMIDIUnknownError
}

/// <summary>MIDINotificationMessageID — first field of every MIDINotification delivered to a MIDINotifyProc.</summary>
internal enum CoreMidi_NotificationId : int
{
    SetupChanged = 1,                  // kMIDIMsgSetupChanged — catch-all, no payload
    ObjectAdded = 2,                   // kMIDIMsgObjectAdded — CoreMidi_ObjectAddRemoveNotification
    ObjectRemoved = 3,                 // kMIDIMsgObjectRemoved — CoreMidi_ObjectAddRemoveNotification (child ref already invalid)
    PropertyChanged = 4,               // kMIDIMsgPropertyChanged — CoreMidi_PropertyChangeNotification (e.g. kMIDIPropertyOffline)
    ThruConnectionsChanged = 5,        // kMIDIMsgThruConnectionsChanged
    SerialPortOwnerChanged = 6,        // kMIDIMsgSerialPortOwnerChanged
    IOError = 7,                       // kMIDIMsgIOError — CoreMidi_IOErrorNotification
}

/// <summary>MIDIObjectType.</summary>
internal enum CoreMidi_ObjectType : int
{
    Other = -1,
    Device = 0,
    Entity = 1,
    Source = 2,
    Destination = 3,
    ExternalDevice = 0x10 | Device,
    ExternalEntity = 0x10 | Entity,
    ExternalSource = 0x10 | Source,
    ExternalDestination = 0x10 | Destination,
}

/// <summary>MIDIProtocolID — what an endpoint natively speaks (kMIDIPropertyProtocolID) or what an event list carries.</summary>
internal enum CoreMidi_ProtocolId : int
{
    Midi1 = 1,                         // kMIDIProtocol_1_0
    Midi2 = 2,                         // kMIDIProtocol_2_0
}

/// <summary>Fixed layout facts from the header (pack 4) that the packet walker relies on. Asserted by CoreMidi_InteropLayoutTests.</summary>
internal enum CoreMidi_Layout
{
    PacketListFirstPacketOffset = 4,   // MIDIPacketList.packet[0] follows the UInt32 numPackets
    PacketTimeStampOffset = 0,         // MIDIPacket.timeStamp (UInt64, only 4-aligned inside a list)
    PacketLengthOffset = 8,            // MIDIPacket.length (UInt16)
    PacketDataOffset = 10,             // MIDIPacket.data[0]
    PacketAlignmentArm = 4,            // MIDIPacketNext rounds up to this on ARM/ARM64; no rounding on x86_64
    EventListFirstPacketOffset = 8,    // MIDIEventList.packet[0] follows protocol + numPackets
    EventPacketWordCountOffset = 8,    // MIDIEventPacket.wordCount
    EventPacketWordsOffset = 12,       // MIDIEventPacket.words[0]
}

/// <summary>MIDIObjectRef and its typed aliases. 32-bit, NOT pointer-sized; 0 is null.</summary>
internal readonly record struct CoreMidi_ObjectRef(uint Value) { public bool IsNull => Value == 0; public static readonly CoreMidi_ObjectRef Null = new(0); }
internal readonly record struct CoreMidi_ClientRef(uint Value) { public bool IsNull => Value == 0; }
internal readonly record struct CoreMidi_PortRef(uint Value) { public bool IsNull => Value == 0; }
internal readonly record struct CoreMidi_DeviceRef(uint Value) { public bool IsNull => Value == 0; public CoreMidi_ObjectRef AsObject => new(Value); }
internal readonly record struct CoreMidi_EntityRef(uint Value) { public bool IsNull => Value == 0; public CoreMidi_ObjectRef AsObject => new(Value); }
internal readonly record struct CoreMidi_EndpointRef(uint Value) { public bool IsNull => Value == 0; public CoreMidi_ObjectRef AsObject => new(Value); }

/// <summary>MIDIUniqueID — persistent per object across replug/reboot (same device on the same port). kMIDIInvalidUniqueID = 0.</summary>
internal readonly record struct CoreMidi_UniqueId(int Value) { public bool IsInvalid => Value == 0; }

/// <summary>MIDINotification header (8 bytes). Larger notifications start with these two fields.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct CoreMidi_Notification
{
    public CoreMidi_NotificationId MessageId;
    public uint MessageSize;
}

/// <summary>MIDIObjectAddRemoveNotification (24 bytes).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct CoreMidi_ObjectAddRemoveNotification
{
    public CoreMidi_NotificationId MessageId;
    public uint MessageSize;
    public uint Parent;                // MIDIObjectRef, possibly 0
    public CoreMidi_ObjectType ParentType;
    public uint Child;                 // MIDIObjectRef — for Removed it is already invalid
    public CoreMidi_ObjectType ChildType;
}

/// <summary>MIDIObjectPropertyChangeNotification (24 bytes).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct CoreMidi_PropertyChangeNotification
{
    public CoreMidi_NotificationId MessageId;
    public uint MessageSize;
    public uint Object;                // MIDIObjectRef
    public CoreMidi_ObjectType ObjectType;
    public nint PropertyName;          // CFStringRef (not owned by us)
}

/// <summary>MIDIIOErrorNotification (16 bytes).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct CoreMidi_IOErrorNotification
{
    public CoreMidi_NotificationId MessageId;
    public uint MessageSize;
    public uint DriverDevice;          // MIDIDeviceRef
    public CoreMidi_Status ErrorCode;
}

/// <summary>MIDISysexSendRequest (40 bytes, NATURAL alignment — declared after the header's #pragma pack(pop)). Must stay pinned until CompletionProc fires; set Complete = 1 to abort.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CoreMidi_SysexSendRequest
{
    public uint Destination;           // MIDIEndpointRef
    public byte* Data;                 // advanced by CoreMIDI as bytes go out
    public uint BytesToSend;           // decremented by CoreMIDI
    public byte Complete;              // Boolean: CoreMIDI sets 1 when done; WE may set 1 to abort
    public byte Reserved0;             // Byte reserved[3] — written out so the layout is exactly the header's (a fixed buffer changed the packing)
    public byte Reserved1;
    public byte Reserved2;
    public nint CompletionProc;        // MIDICompletionProc (C function pointer)
    public void* CompletionRefCon;
}

/// <summary>Direct PInvoke into CoreMIDI.framework (extern "C", one calling convention per arch).</summary>
internal static unsafe partial class CoreMidi_Interop
{
    public const string CoreMidiLib = "/System/Library/Frameworks/CoreMIDI.framework/CoreMIDI";

    // ── client / ports ───────────────────────────────────────────────────────────────────────

    [LibraryImport(CoreMidiLib)] public static partial CoreMidi_Status MIDIClientCreate(nint name, nint notifyProc, void* notifyRefCon, uint* outClient);
    [LibraryImport(CoreMidiLib)] public static partial CoreMidi_Status MIDIClientDispose(uint client);
    [LibraryImport(CoreMidiLib)] public static partial CoreMidi_Status MIDIInputPortCreate(uint client, nint portName, nint readProc, void* refCon, uint* outPort);
    [LibraryImport(CoreMidiLib)] public static partial CoreMidi_Status MIDIOutputPortCreate(uint client, nint portName, uint* outPort);
    [LibraryImport(CoreMidiLib)] public static partial CoreMidi_Status MIDIPortDispose(uint port);
    [LibraryImport(CoreMidiLib)] public static partial CoreMidi_Status MIDIPortConnectSource(uint port, uint source, void* connRefCon);
    [LibraryImport(CoreMidiLib)] public static partial CoreMidi_Status MIDIPortDisconnectSource(uint port, uint source);

    // ── enumeration (indices are transient — resolve to MIDIUniqueID immediately) ───────────

    [LibraryImport(CoreMidiLib)] public static partial nuint MIDIGetNumberOfSources();
    [LibraryImport(CoreMidiLib)] public static partial uint MIDIGetSource(nuint sourceIndex0);
    [LibraryImport(CoreMidiLib)] public static partial nuint MIDIGetNumberOfDestinations();
    [LibraryImport(CoreMidiLib)] public static partial uint MIDIGetDestination(nuint destIndex0);
    [LibraryImport(CoreMidiLib)] public static partial nuint MIDIEntityGetNumberOfDestinations(uint entity);
    [LibraryImport(CoreMidiLib)] public static partial uint MIDIEntityGetDestination(uint entity, uint destIndex0);
    [LibraryImport(CoreMidiLib)] public static partial CoreMidi_Status MIDIEndpointGetEntity(uint endpoint, uint* outEntity);
    [LibraryImport(CoreMidiLib)] public static partial CoreMidi_Status MIDIEntityGetDevice(uint entity, uint* outDevice);
    [LibraryImport(CoreMidiLib)] public static partial CoreMidi_Status MIDIObjectFindByUniqueID(int uniqueId, uint* outObject, CoreMidi_ObjectType* outType);

    // ── properties ──────────────────────────────────────────────────────────────────────────

    [LibraryImport(CoreMidiLib)] public static partial CoreMidi_Status MIDIObjectGetIntegerProperty(uint obj, nint propertyId, int* outValue);
    [LibraryImport(CoreMidiLib)] public static partial CoreMidi_Status MIDIObjectGetStringProperty(uint obj, nint propertyId, nint* outCFString);

    // ── output (Sprint 4a: Identity Request only) ───────────────────────────────────────────

    [LibraryImport(CoreMidiLib)] public static partial CoreMidi_Status MIDISendSysex(CoreMidi_SysexSendRequest* request);

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Read a string property; null when absent (UnknownProperty) or on any failure. Releases the CFString.</summary>
    public static string? GetStringProperty(CoreMidi_ObjectRef obj, nint propertyKey)
    {
        nint cf = 0;
        if (MIDIObjectGetStringProperty(obj.Value, propertyKey, &cf) != CoreMidi_Status.NoError || cf == 0) return null;
        try { return CoreFoundation_Interop.ToManagedString(cf); }
        finally { CoreFoundation_Interop.CFRelease(cf); }
    }

    /// <summary>Read an integer property; null when absent or on failure.</summary>
    public static int? GetIntegerProperty(CoreMidi_ObjectRef obj, nint propertyKey)
    {
        int v = 0;
        return MIDIObjectGetIntegerProperty(obj.Value, propertyKey, &v) == CoreMidi_Status.NoError ? v : null;
    }

    /// <summary>All "the endpoint went away" results collapse to one meaning (ground truth §Errors).</summary>
    public static bool IsDeviceGone(CoreMidi_Status r) => r is CoreMidi_Status.UnknownEndpoint or CoreMidi_Status.ObjectNotFound;

    /// <summary>MIDIPacketNext: end of this packet's data, rounded up to 4 bytes on ARM (header macro; CF_INLINE, not exported).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte* PacketNext(byte* packet, ushort length)
    {
        byte* next = packet + (int)CoreMidi_Layout.PacketDataOffset + length;
        if (s_isArm) next = (byte*)(((nuint)next + (nuint)((int)CoreMidi_Layout.PacketAlignmentArm - 1)) & ~(nuint)((int)CoreMidi_Layout.PacketAlignmentArm - 1));
        return next;
    }

    private static readonly bool s_isArm = RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm;
}

/// <summary>The kMIDIProperty* CFStringRef globals, resolved once via dlsym (the export is the ADDRESS of the variable; dereference).</summary>
internal static unsafe class CoreMidi_PropertyKeys
{
    public static readonly nint Name = Resolve("kMIDIPropertyName");
    public static readonly nint DisplayName = Resolve("kMIDIPropertyDisplayName");
    public static readonly nint Manufacturer = Resolve("kMIDIPropertyManufacturer");
    public static readonly nint Model = Resolve("kMIDIPropertyModel");
    public static readonly nint UniqueId = Resolve("kMIDIPropertyUniqueID");
    public static readonly nint Offline = Resolve("kMIDIPropertyOffline");
    public static readonly nint DriverOwner = Resolve("kMIDIPropertyDriverOwner");
    public static readonly nint ProtocolId = Resolve("kMIDIPropertyProtocolID");

    private static nint Resolve(string symbol)
    {
        nint lib = NativeLibrary.Load(CoreMidi_Interop.CoreMidiLib);
        nint address = NativeLibrary.GetExport(lib, symbol);
        return *(nint*)address;
    }
}

/// <summary>The handful of CoreFoundation calls the backend needs (CFString for names/keys, CFRunLoop for the notify thread).</summary>
internal static unsafe partial class CoreFoundation_Interop
{
    public const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    /// <summary>CFStringEncoding kCFStringEncodingUTF8.</summary>
    internal enum CFStringEncoding : uint { Utf8 = 0x08000100 }

    [LibraryImport(CoreFoundationLib)] public static partial nint CFStringCreateWithCString(nint allocator, byte* cStr, CFStringEncoding encoding);
    [LibraryImport(CoreFoundationLib)] public static partial byte CFStringGetCString(nint theString, byte* buffer, nint bufferSize, CFStringEncoding encoding);
    [LibraryImport(CoreFoundationLib)] public static partial nint CFStringGetLength(nint theString);
    [LibraryImport(CoreFoundationLib)] public static partial void CFRelease(nint cf);
    [LibraryImport(CoreFoundationLib)] public static partial nint CFRunLoopGetCurrent();
    [LibraryImport(CoreFoundationLib)] public static partial void CFRunLoopRun();
    [LibraryImport(CoreFoundationLib)] public static partial void CFRunLoopStop(nint runLoop);

    /// <summary>Create a CFString from a managed string (caller CFRelease()s).</summary>
    public static nint CreateCFString(string text)
    {
        int max = System.Text.Encoding.UTF8.GetMaxByteCount(text.Length) + 1;
        byte* buf = stackalloc byte[max];
        int n = System.Text.Encoding.UTF8.GetBytes(text, new Span<byte>(buf, max - 1));
        buf[n] = 0;
        return CFStringCreateWithCString(0, buf, CFStringEncoding.Utf8);
    }

    /// <summary>Copy a CFString out as a managed string (does not release it).</summary>
    public static string ToManagedString(nint cfString)
    {
        nint chars = CFStringGetLength(cfString);
        int bufferSize = (int)chars * 4 + 1;   // UTF-8 worst case + NUL
        byte* buf = stackalloc byte[Math.Min(bufferSize, 4096)];
        if (bufferSize <= 4096 && CFStringGetCString(cfString, buf, bufferSize, CFStringEncoding.Utf8) != 0)
            return System.Text.Encoding.UTF8.GetString(buf, StrLen(buf, bufferSize));

        byte* heap = (byte*)NativeMemory.Alloc((nuint)bufferSize);
        try
        {
            if (CFStringGetCString(cfString, heap, bufferSize, CFStringEncoding.Utf8) == 0) return string.Empty;
            return System.Text.Encoding.UTF8.GetString(heap, StrLen(heap, bufferSize));
        }
        finally { NativeMemory.Free(heap); }
    }

    private static int StrLen(byte* p, int max) { int n = 0; while (n < max && p[n] != 0) n++; return n; }
}

/// <summary>mach_absolute_time / mach_timebase_info from libSystem — the clock behind MIDITimeStamp (and behind Stopwatch on macOS).</summary>
internal static unsafe partial class Mach_Interop
{
    public const string LibSystem = "/usr/lib/libSystem.dylib";

    [StructLayout(LayoutKind.Sequential)]
    public struct TimebaseInfo { public uint Numer; public uint Denom; }

    [LibraryImport(LibSystem)] public static partial int mach_timebase_info(TimebaseInfo* info);
    [LibraryImport(LibSystem)] public static partial ulong mach_absolute_time();
}

#pragma warning restore AN0100