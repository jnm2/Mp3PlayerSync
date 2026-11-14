using Windows.Devices.Enumeration;

namespace Mp3PlayerSync;

/// <summary>
/// Raised when a volume (a drive, a memory card or a mounted disk image) appears, changes or goes away.
/// </summary>
/// <remarks>
/// <see cref="DrivesChanged"/> is raised on the watcher's own callback threads: thread pool threads, possibly several
/// at the same time, and possibly just after <see cref="Dispose"/> has started. Handlers marshal and guard as needed.
/// </remarks>
internal sealed class DriveChangeWatcher : IDisposable
{
    // GUID_DEVINTERFACE_VOLUME
    private const string VolumeSelector =
        "System.Devices.InterfaceClassGuid:=\"{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}\" AND System.Devices.InterfaceEnabled:=System.StructuredQueryType.Boolean#True";

    private readonly DeviceWatcher watcher;

    public DriveChangeWatcher()
    {
        var filter = new VolumeChangeFilter(() => DrivesChanged?.Invoke(this, EventArgs.Empty));
        watcher = DeviceInformation.CreateWatcher(VolumeSelector, null, DeviceInformationKind.DeviceInterface);
        watcher.Added += (_, _) => filter.OnAdded();
        watcher.Updated += (_, _) => filter.OnUpdated();
        watcher.Removed += (_, _) => filter.OnRemoved();
        watcher.EnumerationCompleted += (_, _) => filter.OnEnumerationCompleted();
        watcher.Start();
    }

    /// <summary>Raised on a thread pool thread; see the remarks on <see cref="DriveChangeWatcher"/>.</summary>
    public event EventHandler? DrivesChanged;

    public void Dispose()
    {
        if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
            watcher.Stop();
    }
}