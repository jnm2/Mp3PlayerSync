using System.ComponentModel;
using Microsoft.UI.Xaml;
using Mp3PlayerSync.Core.Sync;

namespace Mp3PlayerSync.Sync;

/// <summary>What each of the track list's columns needs to show its header and every cell in full, in pixels.</summary>
public readonly record struct TrackContentWidths(double Number, double Title, double Artists, double Album, double Time, double Added, double Status);

/// <summary>
/// The track list's column widths, shared by its header and every row so they line up. #, Time, Added and Status get
/// exactly what their contents need; Title, Artists and Album share the rest (<see cref="ColumnWidths.Fit"/>). All in
/// whole pixels, so the header and rows can't round differently.
/// </summary>
public sealed class TrackColumns : INotifyPropertyChanged
{
    private const double CheckBoxWidth = 32;
    private const double RemoveWidth = 32; // A check box under the waste basket icon
    private const double Spacing = 12; // The grids' ColumnSpacing, between all nine columns

    public static TrackColumns Shared { get; } = new();

    private double width;
    private TrackContentWidths content = new(24, 200, 150, 150, 40, 80, 0);
    private bool showsRemove;

    public bool ShowsAlbum { get; private set; } = true;

    public bool ShowsStatus { get; private set; }

    public GridLength Number { get; private set; } = new(24);

    public GridLength Title { get; private set; } = new(3, GridUnitType.Star);

    public GridLength Artists { get; private set; } = new(2, GridUnitType.Star);

    public GridLength Album { get; private set; } = new(2, GridUnitType.Star);

    public GridLength Time { get; private set; } = new(40);

    public GridLength Added { get; private set; } = new(80);

    public GridLength Status { get; private set; } = new(0);

    public GridLength Remove { get; private set; } = new(0);

    /// <param name="showAlbum">False on album pages, where every row has the same album.</param>
    /// <param name="showStatus">Whether any row has a status to show.</param>
    /// <param name="showRemove">Whether any row is a file that's only on the player.</param>
    public void Update(bool showAlbum, bool showStatus, bool showRemove)
    {
        if (showAlbum != ShowsAlbum)
        {
            ShowsAlbum = showAlbum;
            Changed(nameof(ShowsAlbum));
        }
        ShowsStatus = showStatus;
        showsRemove = showRemove;
        Layout();
    }

    public void SetContentWidths(TrackContentWidths widths)
    {
        if (widths == content) return;
        content = widths;
        Layout();
    }

    /// <summary>The width inside the header's padding, which the rows share.</summary>
    public void SetWidth(double contentWidth)
    {
        if (contentWidth == width) return;
        width = contentWidth;
        Layout();
    }

    private void Layout()
    {
        if (width <= 0) return;
        var status = ShowsStatus ? Math.Ceiling(content.Status) : 0;
        var remove = showsRemove ? RemoveWidth : 0;
        var number = Math.Ceiling(content.Number);
        var time = Math.Ceiling(content.Time);
        var added = Math.Ceiling(content.Added);
        var flexible = ColumnWidths.Fit(width - CheckBoxWidth - number - time - added - status - remove - 8 * Spacing,
            (ValueTuple<double, double>[])[(Math.Ceiling(content.Title), 80), (Math.Ceiling(content.Artists), 60), ShowsAlbum ? (Math.Ceiling(content.Album), 60) : (0, 0)]);

        Number = new(number);
        Title = new(flexible[0]);
        Artists = new(flexible[1]);
        Album = new(flexible[2]);
        Time = new(time);
        Added = new(added);
        Status = new(status);
        Remove = new(remove);
        foreach (var name in (string[])[nameof(Number), nameof(Title), nameof(Artists), nameof(Album), nameof(Time), nameof(Added), nameof(Status), nameof(Remove)])
            Changed(name);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}
