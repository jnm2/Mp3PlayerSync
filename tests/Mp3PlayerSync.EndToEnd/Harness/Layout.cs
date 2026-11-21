using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Shouldly;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Mp3PlayerSync.EndToEnd.Harness;

/// <summary>Where the back arrow and the banner's title are, relative to the window, and whether a message bar pushes them down.</summary>
public sealed record HeadingPosition(double ArrowLeft, double ArrowTop, double BannerLeft, double BannerTop, double BannerBottom, bool Message);

/// <summary>
/// Checks of where the app puts things (spacing, alignment, columns and what fits), from UI Automation's rectangles and
/// from the pixels the app draws.
/// </summary>
public static class Layout
{
    /// <summary>Where the element with <paramref name="automationId"/> is, in screen pixels, or an empty rectangle if it isn't there.</summary>
    public static Rectangle Bounds(this AppUnderTest app, string automationId) => app.FindById(automationId)?.BoundingRectangle ?? Rectangle.Empty;

    /// <summary>
    /// Resizes the window, through UI Automation when the window offers it (it doesn't always) or else SetWindowPos, then
    /// waits for the layout to settle (see <see cref="WaitLayoutAsync"/>).
    /// </summary>
    public static async Task ResizeAsync(this AppUnderTest app, int width, int height, string[] settledIds, string what, Func<object?>? more = null)
    {
        var before = app.Window.BoundingRectangle.Width;
        if (app.Window.Patterns.Transform.PatternOrDefault is { } transform && transform.CanResize.Value)
            transform.Resize(width, height);
        else
            PInvoke.SetWindowPos((HWND)app.Window.Properties.NativeWindowHandle.Value, HWND.Null, 0, 0, width, height,
                SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        if (app.Process.HasExited) throw new InvalidOperationException($"The app exited when the window was resized (exit code {app.Process.ExitCode})");
        await app.WaitLayoutAsync(before, settledIds, what, more);
    }

    /// <summary>
    /// A resize settles over several layout passes, and the window's minimum size can change it again, so after one this
    /// waits until the window's width has changed and the window and these elements read the same twice in a row. A
    /// window that can't change width, such as one already as wide as the screen (CI's is 1024 px), has settled once they've
    /// read the same five times in a row.
    /// </summary>
    private static async Task WaitLayoutAsync(this AppUnderTest app, int widthBefore, string[] ids, string what, Func<object?>? more)
    {
        string? last = null;
        var same = 0;
        await app.UntilAsync(() =>
        {
            var bounds = app.Window.BoundingRectangle;
            var now = string.Join(';', [bounds, .. ids.Select(id => (object?)app.FindById(id)?.BoundingRectangle), more?.Invoke()]);
            same = now == last ? same + 1 : 0;
            last = now;
            return (bounds.Width != widthBefore && same >= 1) || same >= 4;
        }, $"the layout {what} to settle", 10);
    }

    /// <summary>
    /// SPACE-1: no two of these elements touch or overlap, and each related pair is at least 8px apart. The rectangles are
    /// UI Automation's: a button's background, and a text's full line box, descenders included.
    /// </summary>
    public static void CheckSpacing(this AppUnderTest app, Checks checks, string where, string[] ids, (string, string)[] related)
    {
        var problems = new List<string>();
        var rectangles = new Dictionary<string, Rectangle>();
        foreach (var id in ids)
        {
            // What stands for an element that isn't in UI Automation itself: the SelectorBar's items sit in its scroll presenter.
            var element = app.FindById(id) ?? (id == "FilterBar" ? app.FindById("AllFilterItem")?.Parent : null);
            // A renamed or missing element would otherwise just drop out of the check. One that's collapsed has no size.
            if (element is null)
            {
                problems.Add($"{id} is missing");
                continue;
            }
            if (element.BoundingRectangle is { IsEmpty: false, Width: > 0 } bounds) rectangles[id] = bounds;
        }
        var names = rectangles.Keys.ToList();
        for (var i = 0; i < names.Count; i++)
        {
            for (var j = i + 1; j < names.Count; j++)
            {
                if (Gap(rectangles[names[i]], rectangles[names[j]]) <= 0) problems.Add($"{names[i]} touches {names[j]}");
            }
        }
        foreach (var (a, b) in related)
        {
            if (!rectangles.TryGetValue(a, out var first) || !rectangles.TryGetValue(b, out var second)) continue;
            var gap = Gap(first, second);
            if (gap < 8) problems.Add($"{a} and {b} are {gap} px apart, not 8");
        }
        checks.Check($"{where}: nothing touches, and related elements are 8px apart", () => problems.ShouldBeEmpty());
    }

    private static int Gap(Rectangle a, Rectangle b) =>
        Math.Max(Math.Max(b.Left - a.Right, a.Left - b.Right), Math.Max(b.Top - a.Bottom, a.Top - b.Bottom));

    /// <summary>The player to sync to, beside the contents page's heading or under it, never on top of it.</summary>
    public static void CheckPlayerBesideHeading(this AppUnderTest app, Checks checks, int width)
    {
        var title = app.Bounds("PlaylistTitle");
        var player = app.Bounds("ContentsPlayerText");
        var sameLine = Math.Abs(player.Top - title.Top) <= 1 && Math.Abs(player.Bottom - title.Bottom) <= 1 && player.Left >= title.Right;
        var below = player.Top >= title.Bottom;
        checks.Check($"At {width} px the player is on the heading's line or under it, not over it",
            () => (sameLine || below).ShouldBeTrue($"The heading is at {title} and the player at {player}"));
    }

    public static HeadingPosition HeadingLayout(this AppUnderTest app, string arrowId, string bannerId)
    {
        var window = app.Window.BoundingRectangle;
        var arrow = app.Bounds(arrowId);
        var banner = app.Bounds(bannerId);
        return new(arrow.Left - window.Left, arrow.Top - window.Top, banner.Left - window.Left, banner.Top - window.Top, banner.Bottom - window.Top,
            app.FindById("MessageBar") is not null);
    }

    /// <summary>Switching between the playlists page and a playlist moves neither the back arrow nor the banner.</summary>
    public static void CheckSameHeadingLayout(Checks checks, HeadingPosition library, HeadingPosition contents) =>
        checks.Check("The back arrow and banner sit at the same place on the playlists page and a playlist", () => contents.ShouldSatisfyAllConditions(
            () => contents.ArrowLeft.ShouldBe(library.ArrowLeft, 1),
            () => contents.BannerLeft.ShouldBe(library.BannerLeft, 1),
            () => (contents.BannerTop - contents.ArrowTop).ShouldBe(library.BannerTop - library.ArrowTop, 1),
            () => (contents.BannerBottom - contents.BannerTop).ShouldBe(library.BannerBottom - library.BannerTop, 1),
            // A message bar above the page pushes everything down; otherwise the positions match exactly.
            () => { if (!library.Message && !contents.Message) contents.ArrowTop.ShouldBe(library.ArrowTop, 1); }));

    /// <summary>
    /// Cells of the visible track rows whose text is wider than the cell, so it shows an ellipsis. UI Automation can't
    /// tell whether text was trimmed, so this measures each cell's text with the list's font and compares it with the cell.
    /// </summary>
    public static List<string> TruncatedCells(this AppUnderTest app, int fromLeft = int.MinValue)
    {
        using var font = new Font("Segoe UI Variable Text", 14, GraphicsUnit.Pixel);
        var cut = new List<string>();
        foreach (var row in app.FindById("TracksListView")?.Items() ?? [])
        {
            foreach (var cell in row.FindAllDescendants(c => c.ByControlType(ControlType.Text)))
            {
                var bounds = cell.BoundingRectangle;
                var text = cell.Name();
                if (bounds.IsEmpty || text.Length == 0 || bounds.Left < fromLeft) continue;
                var natural = TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding).Width;
                if (natural > bounds.Width + 3) cut.Add($"'{text}' ({natural} px in {bounds.Width})");
            }
        }
        return cut;
    }

    /// <summary>
    /// A heading beside a back arrow, measured from the pixels the app drew (so it holds while the PC is locked): the
    /// arrow's ink is centered on the capitals' (the heading starts with a capital), and its descenders are drawn in full.
    /// The text's layout box ends at its baseline (HeadingTextStyle), so the descenders are looked for just below it.
    /// </summary>
    public static void CheckHeading(this AppUnderTest app, Checks checks, string arrowId, string textId, string what)
    {
        var centered = $"[ALIGN-1] {what}: the back arrow is centered on the capitals";
        var descenders = $"[ALIGN-2] {what}: descenders are drawn in full";
        using var picture = WindowPictures.Render(app, out var why);
        if (picture is null)
        {
            checks.Inconclusive(centered, why);
            checks.Inconclusive(descenders, why);
            return;
        }
        var arrow = app.Bounds(arrowId);
        var text = app.Bounds(textId);
        const int below = 8;

        var ink = ReadInk(picture, arrow.Left, Math.Min(arrow.Top, text.Top), text.Right, Math.Max(arrow.Bottom, text.Bottom) + below);
        var runs = InkRuns(ink);
        checks.Check(centered, () =>
        {
            runs.Count.ShouldBeGreaterThanOrEqualTo(2, "Expected the shapes of an arrow and a letter");
            var arrowRows = InkRows(ink, runs[0]);
            var capitalRows = InkRows(ink, runs[1]);
            var offset = (arrowRows.Top + arrowRows.Bottom) / 2.0 - (capitalRows.Top + capitalRows.Bottom) / 2.0;
            offset.ShouldBe(0, 1, $"The arrow's ink is in rows {arrowRows} and the capital's in rows {capitalRows}");
        });

        ink = ReadInk(picture, text.Left, text.Top, text.Right, text.Bottom + below);
        runs = InkRuns(ink);
        checks.Check(descenders, () =>
        {
            runs.ShouldNotBeEmpty("No text was drawn");
            var baseline = InkRows(ink, runs[0]).Bottom;
            var lowest = InkRows(ink, (runs[0].First, runs[^1].Last)).Bottom;
            var height = ink.GetLength(1);
            var where = $"The baseline is row {baseline} and the lowest ink row {lowest}, of {height} looked at";
            (lowest - baseline).ShouldBeGreaterThanOrEqualTo(3, where);
            lowest.ShouldBeLessThan(height - 1, where);
        });
    }

    /// <summary>
    /// Where the app drew ink in a rectangle of a picture (in screen pixels, like UI Automation's). Ink is any pixel at
    /// least half as far from the background (the commonest color) as the strongest ink there.
    /// </summary>
    private static bool[,] ReadInk(WindowPicture picture, int left, int top, int right, int bottom)
    {
        var area = Rectangle.FromLTRB(left - picture.Left, top - picture.Top, right - picture.Left, bottom - picture.Top);
        area.Intersect(new Rectangle(0, 0, picture.Bitmap.Width, picture.Bitmap.Height));
        var data = picture.Bitmap.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var bytes = new byte[data.Stride * area.Height];
        try
        {
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
        }
        finally
        {
            picture.Bitmap.UnlockBits(data);
        }
        int Pixel(int x, int y) => (bytes[y * data.Stride + x * 4 + 2] << 16) | (bytes[y * data.Stride + x * 4 + 1] << 8) | bytes[y * data.Stride + x * 4];

        var counts = new Dictionary<int, int>();
        for (var y = 0; y < area.Height; y++)
        {
            for (var x = 0; x < area.Width; x++) counts[Pixel(x, y)] = counts.GetValueOrDefault(Pixel(x, y)) + 1;
        }
        var background = counts.MaxBy(pair => pair.Value).Key;
        var distance = new int[area.Width, area.Height];
        var strongest = 0;
        for (var y = 0; y < area.Height; y++)
        {
            for (var x = 0; x < area.Width; x++)
            {
                var color = Pixel(x, y);
                distance[x, y] = Math.Max(Math.Abs(((color >> 16) & 255) - ((background >> 16) & 255)),
                    Math.Max(Math.Abs(((color >> 8) & 255) - ((background >> 8) & 255)), Math.Abs((color & 255) - (background & 255))));
                strongest = Math.Max(strongest, distance[x, y]);
            }
        }
        var ink = new bool[area.Width, area.Height];
        for (var y = 0; y < area.Height; y++)
        {
            for (var x = 0; x < area.Width; x++) ink[x, y] = strongest > 0 && distance[x, y] * 2 >= strongest;
        }
        return ink;
    }

    /// <summary>The first and last columns of each run of inked columns, left to right.</summary>
    private static List<(int First, int Last)> InkRuns(bool[,] ink)
    {
        var runs = new List<(int, int)>();
        var start = -1;
        for (var x = 0; x <= ink.GetLength(0); x++)
        {
            var any = false;
            for (var y = 0; x < ink.GetLength(0) && y < ink.GetLength(1) && !any; y++) any = ink[x, y];
            if (any && start < 0) start = x;
            else if (!any && start >= 0)
            {
                runs.Add((start, x - 1));
                start = -1;
            }
        }
        return runs;
    }

    /// <summary>The top and bottom inked rows between two columns, or -1 for none.</summary>
    private static (int Top, int Bottom) InkRows(bool[,] ink, (int First, int Last) columns)
    {
        var (top, bottom) = (-1, -1);
        for (var y = 0; y < ink.GetLength(1); y++)
        {
            for (var x = columns.First; x <= columns.Last; x++)
            {
                if (!ink[x, y]) continue;
                if (top < 0) top = y;
                bottom = y;
                break;
            }
        }
        return (top, bottom);
    }
}
