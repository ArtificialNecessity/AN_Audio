using System.Runtime.InteropServices;

namespace AN.Audio.Platforms.Linux;

/// <summary>
/// PInvoke declarations for ALSA (libasound.so.2) and libc.
/// Used for PCM playback, device enumeration, and poll-based event-driven I/O.
/// libasound.so.2 is present on essentially every Linux desktop/server installation.
/// </summary>
#pragma warning disable AN0100 // nint is intentional for ALSA/libc interop
internal static unsafe class AlsaInterop
{
    private const string Libasound = "libasound.so.2";
    private const string Libc = "libc";

    // ─── PCM Stream Direction ────────────────────────────────────────────

    public const int SND_PCM_STREAM_PLAYBACK = 0;
    public const int SND_PCM_STREAM_CAPTURE = 1;

    // ─── PCM Access Modes ────────────────────────────────────────────────

    public const int SND_PCM_ACCESS_RW_INTERLEAVED = 3;

    // ─── PCM Formats ─────────────────────────────────────────────────────

    public const int SND_PCM_FORMAT_S16_LE = 2;
    public const int SND_PCM_FORMAT_FLOAT_LE = 14;

    // ─── PCM State ───────────────────────────────────────────────────────

    public const int SND_PCM_STATE_OPEN = 0;
    public const int SND_PCM_STATE_SETUP = 1;
    public const int SND_PCM_STATE_PREPARED = 2;
    public const int SND_PCM_STATE_RUNNING = 3;
    public const int SND_PCM_STATE_XRUN = 4;
    public const int SND_PCM_STATE_DRAINING = 5;
    public const int SND_PCM_STATE_PAUSED = 6;
    public const int SND_PCM_STATE_SUSPENDED = 7;
    public const int SND_PCM_STATE_DISCONNECTED = 8;

    // ─── Error Codes ─────────────────────────────────────────────────────

    /// <summary>EPIPE — underrun occurred.</summary>
    public const int EPIPE = 32;

    /// <summary>ENODEV — device removed.</summary>
    public const int ENODEV = 19;

    /// <summary>EIO — I/O error.</summary>
    public const int EIO = 5;

    /// <summary>ESTRPIPE — device suspended.</summary>
    public const int ESTRPIPE = 86;

    // ─── Poll Event Flags ────────────────────────────────────────────────

    public const short POLLIN = 0x0001;
    public const short POLLOUT = 0x0004;
    public const short POLLERR = 0x0008;
    public const short POLLHUP = 0x0010;
    public const short POLLNVAL = 0x0020;

    // ─── PollFd Structure ────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int fd;
        public short events;
        public short revents;
    }

    // ─── PCM Open/Close ──────────────────────────────────────────────────

    /// <summary>
    /// Open a PCM device.
    /// </summary>
    /// <param name="pcm">Output: PCM handle pointer.</param>
    /// <param name="name">Device name (e.g., "default", "hw:0,0", "plughw:1,0").</param>
    /// <param name="stream">SND_PCM_STREAM_PLAYBACK or SND_PCM_STREAM_CAPTURE.</param>
    /// <param name="mode">Open mode (0 = blocking, SND_PCM_NONBLOCK = non-blocking).</param>
    /// <returns>0 on success, negative error code on failure.</returns>
    [DllImport(Libasound, CallingConvention = CallingConvention.Cdecl)]
    public static extern int snd_pcm_open(out nint pcm, [MarshalAs(UnmanagedType.LPStr)] string name, int stream, int mode);

    /// <summary>
    /// Close a PCM device.
    /// </summary>
    [DllImport(Libasound, CallingConvention = CallingConvention.Cdecl)]
    public static extern int snd_pcm_close(nint pcm);

    // ─── PCM Configuration (Simple) ─────────────────────────────────────

    /// <summary>
    /// Set hardware and software parameters in a simple way.
    /// This is a convenience function that sets the most common params.
    /// </summary>
    /// <param name="pcm">PCM handle.</param>
    /// <param name="format">Sample format (SND_PCM_FORMAT_*).</param>
    /// <param name="access">Access mode (SND_PCM_ACCESS_*).</param>
    /// <param name="channels">Number of channels.</param>
    /// <param name="rate">Sample rate in Hz.</param>
    /// <param name="soft_resample">1 = allow ALSA to resample if hardware doesn't support the rate.</param>
    /// <param name="latency">Desired latency in microseconds.</param>
    /// <returns>0 on success, negative error code on failure.</returns>
    [DllImport(Libasound, CallingConvention = CallingConvention.Cdecl)]
    public static extern int snd_pcm_set_params(
        nint pcm,
        int format,
        int access,
        uint channels,
        uint rate,
        int soft_resample,
        uint latency);

    /// <summary>
    /// Get the buffer size and period size after configuration.
    /// </summary>
    [DllImport(Libasound, CallingConvention = CallingConvention.Cdecl)]
    public static extern int snd_pcm_get_params(nint pcm, out ulong buffer_size, out ulong period_size);

    // ─── PCM State and Control ───────────────────────────────────────────

    /// <summary>
    /// Prepare PCM for use (after setup or recovery).
    /// </summary>
    [DllImport(Libasound, CallingConvention = CallingConvention.Cdecl)]
    public static extern int snd_pcm_prepare(nint pcm);

    /// <summary>
    /// Start PCM playback.
    /// </summary>
    [DllImport(Libasound, CallingConvention = CallingConvention.Cdecl)]
    public static extern int snd_pcm_start(nint pcm);

    /// <summary>
    /// Drop (stop) PCM immediately without draining.
    /// </summary>
    [DllImport(Libasound, CallingConvention = CallingConvention.Cdecl)]
    public static extern int snd_pcm_drop(nint pcm);

    /// <summary>
    /// Drain PCM — stop after all pending samples have been played.
    /// </summary>
    [DllImport(Libasound, CallingConvention = CallingConvention.Cdecl)]
    public static extern int snd_pcm_drain(nint pcm);

    /// <summary>
    /// Resume a PCM from suspended state.
    /// </summary>
    [DllImport(Libasound, CallingConvention = CallingConvention.Cdecl)]
    public static extern int snd_pcm_resume(nint pcm);

    /// <summary>
    /// Recover the PCM state from an error.
    /// Handles -EPIPE (underrun), -ESTRPIPE (suspend), etc.
    /// </summary>
    /// <param name="pcm">PCM handle.</param>
    /// <param name="err">The error code to recover from.</param>
    /// <param name="silent">0 = print error on stderr, 1 = silent.</param>
    /// <returns>0 on success, negative error code if recovery failed.</returns>
    [DllImport(Libasound, CallingConvention = CallingConvention.Cdecl)]
    public static extern int snd_pcm_recover(nint pcm, int err, int silent);

    /// <summary>
    /// Get the current PCM state.
    /// </summary>
    [DllImport(Libasound, CallingConvention = CallingConvention.Cdecl)]
    public static extern int snd_pcm_state(nint pcm);

    /// <summary>
    /// Get the count of frames available in the ring buffer (ready to be written to).
    /// </summary>
    [DllImport(Libasound, CallingConvention = CallingConvention.Cdecl)]
    public static extern long snd_pcm_avail_update(nint pcm);

    // ─── PCM I/O ─────────────────────────────────────────────────────────

    /// <summary>
    /// Write interleaved frames to the PCM device.
    /// </summary>
    /// <param name="pcm">PCM handle.</param>
    /// <param name="buffer">Pointer to the interleaved sample buffer.</param>
    /// <param name="size">Number of frames to write.</param>
    /// <returns>Number of frames written on success, negative error code on failure.</returns>
    [DllImport(Libasound, CallingConvention = CallingConvention.Cdecl)]
    public static extern long snd_pcm_writei(nint pcm, void* buffer, ulong size);

    // ─── Poll Descriptors ────────────────────────────────────────────────

    /// <summary>
    /// Get the count of poll descriptors for this PCM.
    /// </summary>
    [DllImport(Libasound, CallingConvention = CallingConvention.Cdecl)]
    public static extern int snd_pcm_poll_descriptors_count(nint pcm);

    /// <summary>
    /// Fill pollfd structures with the PCM's poll descriptors.
    /// </summary>
    [DllImport(Libasound, CallingConvention = CallingConvention.Cdecl)]
    public static extern int snd_pcm_poll_descriptors(nint pcm, PollFd* pfds, uint space);

    /// <summary>
    /// Get returned events from revents fields of pollfd structures.
    /// </summary>
    [DllImport(Libasound, CallingConvention = CallingConvention.Cdecl)]
    public static extern int snd_pcm_poll_descriptors_revents(nint pcm, PollFd* pfds, uint nfds, out ushort revents);

    // ─── Device Enumeration ──────────────────────────────────────────────

    /// <summary>
    /// Enumerate devices using device name hints.
    /// </summary>
    /// <param name="card">Card number, -1 = all cards.</param>
    /// <param name="iface">Interface identifier (e.g., "pcm", "ctl", "rawmidi").</param>
    /// <param name="hints">Output: pointer to null-terminated array of hint pointers.</param>
    /// <returns>0 on success, negative error code on failure.</returns>
    [DllImport(Libasound, CallingConvention = CallingConvention.Cdecl)]
    public static extern int snd_device_name_hint(int card, [MarshalAs(UnmanagedType.LPStr)] string iface, out nint hints);

    /// <summary>
    /// Get a hint value (NAME, DESC, or IOID) from a device hint.
    /// The returned string is allocated with malloc and must be freed with free().
    /// </summary>
    /// <param name="hint">Hint pointer from snd_device_name_hint array.</param>
    /// <param name="id">"NAME", "DESC", or "IOID".</param>
    /// <returns>Pointer to allocated string, or null if not available.</returns>
    [DllImport(Libasound, CallingConvention = CallingConvention.Cdecl)]
    public static extern nint snd_device_name_get_hint(nint hint, [MarshalAs(UnmanagedType.LPStr)] string id);

    /// <summary>
    /// Free the hints array returned by snd_device_name_hint.
    /// </summary>
    [DllImport(Libasound, CallingConvention = CallingConvention.Cdecl)]
    public static extern int snd_device_name_free_hint(nint hints);

    // ─── Error String ────────────────────────────────────────────────────

    /// <summary>
    /// Get a human-readable error message for an ALSA error code.
    /// </summary>
    [DllImport(Libasound, CallingConvention = CallingConvention.Cdecl)]
    public static extern nint snd_strerror(int errnum);

    // ─── libc: poll ──────────────────────────────────────────────────────

    /// <summary>
    /// Wait for events on file descriptors.
    /// </summary>
    /// <param name="fds">Array of pollfd structures.</param>
    /// <param name="nfds">Number of file descriptors.</param>
    /// <param name="timeout">Timeout in milliseconds (-1 = infinite).</param>
    /// <returns>Number of fds with events, 0 on timeout, -1 on error.</returns>
    [DllImport(Libc, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern int poll(PollFd* fds, uint nfds, int timeout);

    // ─── libc: pipe/read/write for wakeup mechanism ──────────────────────

    /// <summary>
    /// Create a pipe. pipefd[0] is read end, pipefd[1] is write end.
    /// </summary>
    [DllImport(Libc, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern int pipe(int* pipefd);

    /// <summary>
    /// Read from a file descriptor.
    /// </summary>
    [DllImport(Libc, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern long read(int fd, void* buf, ulong count);

    /// <summary>
    /// Write to a file descriptor.
    /// </summary>
    [DllImport(Libc, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern long write(int fd, void* buf, ulong count);

    /// <summary>
    /// Close a file descriptor.
    /// </summary>
    [DllImport(Libc, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern int close(int fd);

    // ─── libc: free ──────────────────────────────────────────────────────

    /// <summary>
    /// Free memory allocated by malloc (used for snd_device_name_get_hint strings).
    /// </summary>
    [DllImport(Libc, CallingConvention = CallingConvention.Cdecl)]
    public static extern void free(nint ptr);

    // ─── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Convert an ALSA error code to an ALSA format constant.
    /// </summary>
    public static int GetAlsaFormat(SampleFormat format) => format switch
    {
        SampleFormat.Int16 => SND_PCM_FORMAT_S16_LE,
        SampleFormat.Float32 => SND_PCM_FORMAT_FLOAT_LE,
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    /// <summary>
    /// Get the human-readable error string for an ALSA error code.
    /// </summary>
    public static string GetErrorString(int errnum)
    {
        nint ptr = snd_strerror(errnum);
        return ptr != 0 ? Marshal.PtrToStringUTF8(ptr) ?? $"Unknown error {errnum}" : $"Unknown error {errnum}";
    }

    /// <summary>
    /// Check if the error indicates the device is gone (unrecoverable).
    /// </summary>
    public static bool IsDeviceLostError(int err)
    {
        int positiveErr = err < 0 ? -err : err;
        return positiveErr == ENODEV || positiveErr == EIO;
    }
}
