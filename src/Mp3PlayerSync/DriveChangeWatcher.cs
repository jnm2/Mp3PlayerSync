using Windows.Devices.Enumeration;

namespace Mp3PlayerSync;

/// <summary>
/// Raised on the given thread's context when a volume (a drive, a memory card or a mounted disk image) appears,
/// changes or goes away.
/// </summary>
internal sealed class DriveChangeWatcher : IDisposable
{
    // GUID_DEVINTERFACE_VOLUME
    private const string VolumeSelector =
        "System.Devices.InterfaceClassGuid:=\"{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}\" AND System.Devices.InterfaceEnabled:=System.StructuredQueryType.Boolean#True";

    private readonly DeviceWatcher watcher;
    private readonly SynchronizationContext context;
    private volatile bool disposed;

    /// <param name="context">Where <see cref="DrivesChanged"/> is raised, such as the UI thread's.</param>
    public DriveChangeWatcher(SynchronizationContext context)
    {
        this.context = context;
        var filter = new VolumeChangeFilter(OnChanged);
        watcher = DeviceInformation.CreateWatcher(VolumeSelector, null, DeviceInformationKind.DeviceInterface);
        watcher.Added += (_, _) => filter.OnAdded();
        watcher.Updated += (_, _) => filter.OnUpdated();
        watcher.Removed += (_, _) => filter.OnRemoved();
        watcher.EnumerationCompleted += (_, _) => filter.OnEnumerationCompleted();
        watcher.Start();
    }

    public event EventHandler? DrivesChanged;

    // The watcher raises its events on a thread pool thread.
    private void OnChanged()
    {
        if (disposed) return;
        context.Post(_ =>
        {
            if (!disposed) DrivesChanged?.Invoke(this, EventArgs.Empty);
        }, null);
    }

    public void Dispose()
    {
        disposed = true;
        if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
            watcher.Stop();
    }
}