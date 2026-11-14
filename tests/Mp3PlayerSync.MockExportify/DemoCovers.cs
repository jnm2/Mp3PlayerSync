using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Mp3PlayerSync.MockExportify;

/// <summary>
/// Made-up playlist covers for the demo library (fake-spotify.js <c>?demo</c>): a gradient with soft circles, the same
/// for the same name.
/// </summary>
internal static class DemoCovers
{
    private static readonly string[][] Palettes =
    [
        ["#FF7E5F", "#FEB47B"], ["#43C6AC", "#191654"], ["#F7971E", "#FFD200"], ["#4568DC", "#B06AB3"],
        ["#11998E", "#38EF7D"], ["#EE0979", "#FF6A00"], ["#2193B0", "#6DD5ED"], ["#834D9B", "#D04ED6"],
        ["#556270", "#FF6B6B"], ["#1D976C", "#93F9B9"],
    ];

    public static byte[] Draw(string name)
    {
        var hash = 7u;
        foreach (var c in name) hash = unchecked(hash * 31 + c);
        var random = new Random((int)(hash % int.MaxValue));
        var palette = Palettes[hash % Palettes.Length];
        var from = ColorTranslator.FromHtml(palette[0]);
        var to = ColorTranslator.FromHtml(palette[1]);

        const int size = 300;
        using var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var gradient = new LinearGradientBrush(new Point(0, 0), new Point(size, size), from, to);
        graphics.FillRectangle(gradient, 0, 0, size, size);
        for (var i = 0; i < 4; i++)
        {
            var diameter = random.Next(90, 260);
            using var brush = new SolidBrush(Color.FromArgb(random.Next(25, 60), 255, 255, 255));
            graphics.FillEllipse(brush, random.Next(-80, size - 40), random.Next(-80, size - 40), diameter, diameter);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
