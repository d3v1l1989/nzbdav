using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NzbWebDAV.Clients.Usenet.Cache;

internal static class LinuxNative
{
    [DllImport("libc", SetLastError = true)]
    private static extern int posix_fadvise(int fd, long offset, long len, int advice);

    private const int POSIX_FADV_DONTNEED = 4;

    public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    /// <summary>
    /// Drop page cache for a region after reading (prevents RAM bloat on large streams).
    /// Only effective on Linux. No-op on other platforms.
    /// </summary>
    public static void DropPageCache(SafeFileHandle handle, long offset, long length)
    {
        if (!IsLinux) return;
        try
        {
            var fd = handle.DangerousGetHandle().ToInt32();
            // No fdatasync needed — FADV_DONTNEED writes back dirty pages before freeing them
            posix_fadvise(fd, offset, length, POSIX_FADV_DONTNEED);
        }
        catch
        {
            // Best-effort — ignore errors
        }
    }
}
