using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Mp3PlayerSync.Core.Scanning;

/// <summary>
/// Opens files on the player for reading without causing any writes to it.
/// </summary>
/// <remarks>
/// Reading a file on FAT and exFAT (and on NTFS when enabled) makes Windows write a new last-access date to its
/// directory entry when the handle closes, once a day per file. Setting the last-access time to -1 on the handle
/// turns that off for the handle and writes nothing itself; it needs FILE_WRITE_ATTRIBUTES access, which is only
/// used for this. If that access is denied (for example on write-protected media, where nothing can be written
/// anyway), the file is opened with read access only.
/// </remarks>
internal static class ReadOnlyFile
{
    private const uint FileReadData = 0x1, FileReadAttributes = 0x80, FileWriteAttributes = 0x100, Synchronize = 0x100000;

    // Turns off cache read-ahead, so reading a tag doesn't pull in audio data that isn't needed.
    private const uint FileFlagRandomAccess = 0x10000000;

    private const FileShare ShareWithWriters = FileShare.ReadWrite | FileShare.Delete;

    /// <summary>
    /// Opens a file with shared read access, without buffering and without updating its last-access time.
    /// </summary>
    public static FileStream Open(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var extendedPath = ExtendedPath(Path.GetFullPath(path));

            var handle = CreateFile(extendedPath, FileReadData | FileReadAttributes | FileWriteAttributes | Synchronize, ShareWithWriters, 0, FileMode.Open, FileFlagRandomAccess, 0);
            if (!handle.IsInvalid)
            {
                long preserve = -1;
                if (!SetFileTime(handle, 0, ref preserve, 0))
                {
                    var error = Marshal.GetLastPInvokeError();
                    handle.Dispose();
                    throw new IOException($"Couldn't keep \"{path}\" from being marked as accessed.", new Win32Exception(error));
                }

                return new FileStream(handle, FileAccess.Read, bufferSize: 0);
            }

            handle.Dispose();
            handle = CreateFile(extendedPath, FileReadData | FileReadAttributes | Synchronize, ShareWithWriters, 0, FileMode.Open, FileFlagRandomAccess, 0);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw new IOException($"Couldn't open \"{path}\".", new Win32Exception(error));
            }

            return new FileStream(handle, FileAccess.Read, bufferSize: 0);
        }

        return new FileStream(path, FileMode.Open, FileAccess.Read, ShareWithWriters, bufferSize: 0, FileOptions.RandomAccess);
    }

    private static string ExtendedPath(string fullPath) =>
        fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? fullPath
        : fullPath.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + fullPath[2..]
        : @"\\?\" + fullPath;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, FileShare shareMode, nint securityAttributes, FileMode creationDisposition, uint flagsAndAttributes, nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileTime(SafeFileHandle file, nint creationTime, ref long lastAccessTime, nint lastWriteTime);
}
