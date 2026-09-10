using System.Runtime.InteropServices;

namespace AN.Audio.Midi.Platforms.Linux;

#pragma warning disable AN0100 // raw fds

/// <summary>
/// Minimal libc interop for the ALSA rawmidi character devices (/dev/snd/midiC*D*).
/// Rawmidi nodes speak plain MIDI 1.0 byte streams; no libasound needed for read/write.
/// Non-blocking open + poll() lets the reader thread wake for shutdown without a pending read.
/// </summary>
internal static partial class Alsa_Interop
{
    public const int O_RDONLY = 0x0000;
    public const int O_RDWR = 0x0002;
    public const int O_NONBLOCK = 0x0800;

    public const short POLLIN = 0x0001;
    public const short POLLERR = 0x0008;
    public const short POLLHUP = 0x0010;

    public const int EAGAIN = 11;

    /// <summary>SNDRV_CTL_IOCTL_RAWMIDI_PREFER_SUBDEVICE = _IOW('U', 0x42, int) (sound/asound.h). Issued on /dev/snd/controlC{N}
    /// immediately before open()ing midiC{N}D{D} to select a substream other than 0. Value verified against the header 2026-09-10.</summary>
    public const nuint SNDRV_CTL_IOCTL_RAWMIDI_PREFER_SUBDEVICE = 0x40045542;
    /// <summary>Pass as the preferred subdevice to restore the kernel default (first free).</summary>
    public const int PreferAnySubdevice = -1;

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    public static partial int Close(int fd);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    public static unsafe partial nint Read(int fd, byte* buffer, nuint count);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    public static unsafe partial nint Write(int fd, byte* buffer, nuint count);

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    public static unsafe partial int Poll(PollFd* fds, nuint nfds, int timeoutMs);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    public static unsafe partial int Ioctl(int fd, nuint request, int* arg);
}

#pragma warning restore AN0100