using System.Runtime.InteropServices;

namespace AN.Audio.Platforms.Windows.Asio;

/// <summary>
/// The HAND-WRITTEN part of the ASIO interop (spec 70 §5): COM activation with the ASIO quirk (IID == CLSID), the hidden host window and
/// its message pump. Everything that mirrors the Steinberg headers (structs, enums, the IASIO vtable) is GENERATED — see
/// <c>Generated/Bindings.*.generated.cs</c> and spec 71; nothing here duplicates a header declaration.
/// </summary>
internal static unsafe class AsioInterop
{
#pragma warning disable AN0100 // nint is intentional for Win32/COM handles

    // ─── COM ───────────────────────────────────────────────────────────────────────────────────

    public const uint COINIT_APARTMENTTHREADED = 0x2;
    public const uint CLSCTX_INPROC_SERVER = 0x1;
    /// <summary><c>RPC_E_CHANGED_MODE</c>: the thread already has a different apartment. Fatal for us — ASIO drivers are <c>ThreadingModel=Apartment</c>.</summary>
    public const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
    public const int S_FALSE = 1;

    [DllImport("ole32.dll")] public static extern int CoInitializeEx(nint reserved, uint coInit);
    [DllImport("ole32.dll")] public static extern void CoUninitialize();
    [DllImport("ole32.dll")] public static extern int CoCreateInstance(ref Guid clsid, nint outer, uint clsContext, ref Guid iid, out nint ppv);

    // ─── Hidden host window + pump (user32/kernel32) ─────────────────────────────────────────────────────────

    /// <summary>A hidden TOP-LEVEL window (not <c>HWND_MESSAGE</c>: message-only windows cannot own dialogs and <c>controlPanel()</c> needs an owner).
    /// The in-box <c>STATIC</c> class gives us a real HWND without registering a class of our own.</summary>
    public const string HostWindowClass = "STATIC";
    public const uint WS_OVERLAPPED = 0x00000000;
    public const int GWLP_HWNDPARENT = -8;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] public static extern int DestroyWindow(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern nint SetWindowLongPtrW(nint hwnd, int index, nint newLong);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public nint hwnd; public uint message; public nint wParam; public nint lParam; public uint time; public int ptX; public int ptY; }
    public const uint PM_REMOVE = 0x0001;
    [DllImport("user32.dll")] public static extern int PeekMessageW(out MSG msg, nint hwnd, uint filterMin, uint filterMax, uint remove);
    [DllImport("user32.dll")] public static extern int TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern nint DispatchMessageW(ref MSG msg);

    public const uint QS_ALLINPUT = 0x04FF;
    public const uint MWMO_INPUTAVAILABLE = 0x0004;
    public const uint WAIT_OBJECT_0 = 0;
    public const uint INFINITE = 0xFFFFFFFF;
    /// <summary>Blocks until a handle is signalled OR a window message arrives — the one wait a pumping thread may use.</summary>
    [DllImport("user32.dll")] public static extern uint MsgWaitForMultipleObjectsEx(uint count, nint* handles, uint milliseconds, uint wakeMask, uint flags);

    [DllImport("kernel32.dll")] public static extern nint CreateEventW(nint attributes, int manualReset, int initialState, nint name);
    [DllImport("kernel32.dll")] public static extern int SetEvent(nint handle);
    [DllImport("kernel32.dll")] public static extern int CloseHandle(nint handle);

#pragma warning restore AN0100
}