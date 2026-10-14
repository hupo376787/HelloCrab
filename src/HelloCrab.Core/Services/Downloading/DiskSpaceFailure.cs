using System.ComponentModel;

namespace HelloCrab.Core.Services.Downloading;

internal static class DiskSpaceFailure
{
    public static bool IsDiskFull(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is AggregateException aggregate
                && aggregate.InnerExceptions.Any(IsDiskFull))
            {
                return true;
            }

            // Windows: ERROR_DISK_FULL (112), ERROR_HANDLE_DISK_FULL (39).
            // Unix: ENOSPC (28). The low word is preserved in .NET's IO HRESULT.
            if (current is Win32Exception win32
                && win32.NativeErrorCode is 112 or 39 or 28)
            {
                return true;
            }

            if (current is IOException
                && (current.HResult & 0xffff) is 112 or 39 or 28)
            {
                return true;
            }

            if (current.Message.Contains("磁盘空间不足", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("磁盘已满", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("设备上没有空间", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("not enough space on the disk", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("there is not enough space", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("no space left on device", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("disk full", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
