using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Mp3PlayerSync.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Ioctl;

namespace Mp3PlayerSync;

/// <summary>
/// Lists mounted drives with what Windows reports about the device behind each one.
/// </summary>
internal static class DriveProbe
{
    public static List<DriveCandidate> GetDrives()
    {
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
        var drives = new List<DriveCandidate>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue; // e.g. a card reader with no card in it

                drives.Add(new DriveCandidate(
                    drive.RootDirectory.FullName,
                    drive.VolumeLabel,
                    drive.DriveType,
                    GetBus(drive.RootDirectory.FullName),
                    string.Equals(drive.RootDirectory.FullName, systemRoot, StringComparison.OrdinalIgnoreCase),
                    GetVolumeId(drive.RootDirectory.FullName),
                    GetVolumeSerial(drive.RootDirectory.FullName),
                    drive.TotalSize,
                    drive.AvailableFreeSpace,
                    IsWriteProtected(drive.RootDirectory.FullName)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Ejected while being read
            }
        }

        return drives;
    }

    /// <summary>
    /// Asks the storage driver which bus the drive's device is on (USB, SD, NVMe, …).
    /// </summary>
    private static unsafe StorageBus GetBus(string rootPath)
    {
        // Opening the volume with no access rights is enough for this query and needs no elevation.
        using SafeFileHandle volume = PInvoke.CreateFile(
            @"\\.\" + rootPath.TrimEnd('\\'),
            0,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE,
            null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            0,
            null);
        if (volume.IsInvalid) return StorageBus.Unknown;

        var query = new STORAGE_PROPERTY_QUERY
        {
            PropertyId = STORAGE_PROPERTY_ID.StorageDeviceProperty,
            QueryType = STORAGE_QUERY_TYPE.PropertyStandardQuery,
        };

        // The descriptor is followed by variable-length vendor and product strings; only the fixed part is read.
        var buffer = stackalloc byte[1024];
        uint returned;
        if (!PInvoke.DeviceIoControl((HANDLE)volume.DangerousGetHandle(), PInvoke.IOCTL_STORAGE_QUERY_PROPERTY, &query, (uint)sizeof(STORAGE_PROPERTY_QUERY), buffer, 1024, &returned, null)
            || returned < sizeof(STORAGE_DEVICE_DESCRIPTOR))
        {
            return StorageBus.Unknown;
        }

        return ((STORAGE_DEVICE_DESCRIPTOR*)buffer)->BusType switch
        {
            STORAGE_BUS_TYPE.BusTypeUsb => StorageBus.Usb,
            STORAGE_BUS_TYPE.BusTypeSd => StorageBus.SecureDigital,
            STORAGE_BUS_TYPE.BusTypeMmc => StorageBus.Mmc,
            STORAGE_BUS_TYPE.BusTypeUnknown => StorageBus.Unknown,
            _ => StorageBus.Other,
        };
    }

    /// <summary>The volume GUID path, which identifies the volume whatever letter it's given.</summary>
    private static unsafe string? GetVolumeId(string rootPath)
    {
        const int Length = 64; // \\?\Volume{GUID}\ is 49 characters
        var name = stackalloc char[Length];
        fixed (char* root = rootPath)
        {
            return PInvoke.GetVolumeNameForVolumeMountPoint(new PCWSTR(root), new PWSTR(name), Length)
                ? new string(name)
                : null;
        }
    }

    private const int ErrorWriteProtect = 19;

    /// <summary>
    /// Whether the volume a path is on can't be written to: a read-only volume, or a card whose lock switch is on.
    /// Only asks; nothing is written.
    /// </summary>
    public static unsafe bool IsWriteProtected(string path)
    {
        string? root;
        try
        {
            root = Path.GetPathRoot(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        if (string.IsNullOrEmpty(root)) return false;

        uint flags;
        fixed (char* rootChars = root)
        {
            if (PInvoke.GetVolumeInformation(new PCWSTR(rootChars), default, 0, null, null, &flags, default, 0)
                && (flags & PInvoke.FILE_READ_ONLY_VOLUME) != 0)
            {
                return true;
            }
        }

        // A drive letter's device can say it's write-protected even when the file system doesn't (yet).
        if (root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        using SafeFileHandle volume = PInvoke.CreateFile(
            @"\\.\" + root.TrimEnd('\\'),
            0,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE,
            null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            0,
            null);
        if (volume.IsInvalid) return false;

        uint returned;
        return !PInvoke.DeviceIoControl((HANDLE)volume.DangerousGetHandle(), PInvoke.IOCTL_DISK_IS_WRITABLE, null, 0, null, 0, &returned, null)
            && Marshal.GetLastPInvokeError() == ErrorWriteProtect;
    }

    private static unsafe uint? GetVolumeSerial(string rootPath)
    {
        uint serial;
        fixed (char* root = rootPath)
        {
            return PInvoke.GetVolumeInformation(new PCWSTR(root), default, 0, &serial, null, null, default, 0) ? serial : null;
        }
    }
}
