// Regenerates every app icon asset from the SVG sources in .\Source.
// Run from anywhere:  dotnet run src\Mp3PlayerSync\Assets\Icon\build-icons.cs
#:package Svg.Skia@5.2.3

using System.Buffers.Binary;
using SkiaSharp;
using Svg.Skia;

var iconDir = AppContext.GetData("EntryPointFileDirectoryPath") as string ?? Directory.GetCurrentDirectory();
var sourceDir = Path.Combine(iconDir, "Source");
var pngDir = Path.Combine(iconDir, "Png");
var previewDir = Path.Combine(iconDir, "Preview");
var assetsDir = Path.GetFullPath(Path.Combine(iconDir, ".."));
var msixDir = Path.Combine(assetsDir, "Msix");

// Sizes Windows may request for a Win32 icon (title bar, taskbar, Start at 100%-400% scale).
int[] icoSizes = [16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256];
int[] scales = [100, 125, 150, 200, 400];

foreach (var dir in new[] { pngDir, previewDir, msixDir })
{
    if (Directory.Exists(dir))
    {
        foreach (var file in Directory.GetFiles(dir, "*.png")) File.Delete(file);
    }
    Directory.CreateDirectory(dir);
}

var master = LoadSvg(Path.Combine(sourceDir, "AppIcon.svg"));
var hinted = new Dictionary<int, SKPicture>();
foreach (var file in Directory.GetFiles(sourceDir, "AppIcon.*.svg"))
{
    var size = int.Parse(Path.GetFileNameWithoutExtension(file).Split('.')[1]);
    hinted[size] = LoadSvg(file);
}

// Hand-hinted artwork wins at its exact pixel size; everything else scales the master.
SKBitmap RenderIcon(int size) =>
    RenderPicture(hinted.TryGetValue(size, out var picture) ? picture : master, size);

// Win32 .ico and the per-size PNG frames.
var frames = icoSizes.Select(size => (size, bitmap: RenderIcon(size))).ToList();
foreach (var (size, bitmap) in frames) SavePng(bitmap, Path.Combine(pngDir, $"AppIcon-{size}.png"));
WriteIco(Path.Combine(assetsDir, "AppIcon.ico"), frames);

// MSIX visual assets, in case the app is packaged later. The art reads on light and dark backgrounds,
// so the plated, dark (unplated) and light (lightunplated) variants share it.
foreach (var (size, bitmap) in frames)
{
    foreach (var suffix in new[] { "", "_altform-unplated", "_altform-lightunplated" })
        SavePng(bitmap, Path.Combine(msixDir, $"Square44x44Logo.targetsize-{size}{suffix}.png"));
}
foreach (var scale in scales)
{
    int Px(int baseSize) => (int)Math.Round(baseSize * scale / 100.0, MidpointRounding.AwayFromZero);

    SavePng(RenderIcon(Px(44)), Path.Combine(msixDir, $"Square44x44Logo.scale-{scale}.png"));
    SavePng(RenderIcon(Px(50)), Path.Combine(msixDir, $"StoreLogo.scale-{scale}.png"));
    SavePng(RenderTile(Px(150), Px(150), Px(75), 0.46f), Path.Combine(msixDir, $"Square150x150Logo.scale-{scale}.png"));
    SavePng(RenderTile(Px(310), Px(150), Px(75), 0.46f), Path.Combine(msixDir, $"Wide310x150Logo.scale-{scale}.png"));
    SavePng(RenderTile(Px(620), Px(300), Px(150), 0.5f), Path.Combine(msixDir, $"SplashScreen.scale-{scale}.png"));
}

SavePng(RenderContactSheet(), Path.Combine(previewDir, "contact-sheet.png"));
SavePng(RenderMockup(), Path.Combine(previewDir, "taskbar-start-mockup.png"));

Console.WriteLine($"Wrote {Path.Combine(assetsDir, "AppIcon.ico")} ({icoSizes.Length} sizes), PNG frames, MSIX assets and previews.");
Console.WriteLine("Share of opaque icon area with >= 3:1 contrast against the background (guideline: at least half):");
foreach (var size in new[] { 16, 24, 32, 48, 256 })
{
    var bitmap = frames.Single(f => f.size == size).bitmap;
    Console.WriteLine($"  {size,3} px  light #F3F3F3: {ContrastShare(bitmap, new SKColor(0xF3, 0xF3, 0xF3)),4:P0}   dark #202020: {ContrastShare(bitmap, new SKColor(0x20, 0x20, 0x20)),4:P0}");
}

static SKPicture LoadSvg(string path)
{
    var svg = new SKSvg();
    return svg.Load(path) ?? throw new InvalidOperationException($"Could not load {path}");
}

static SKBitmap RenderPicture(SKPicture picture, int size)
{
    var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(SKColors.Transparent);
    canvas.Scale(size / picture.CullRect.Width, size / picture.CullRect.Height);
    canvas.DrawPicture(picture);
    return bitmap;
}

SKBitmap RenderTile(int width, int height, int iconSize, float centerY)
{
    var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(SKColors.Transparent);
    using var icon = RenderIcon(iconSize);
    Blit(canvas, icon, (width - iconSize) / 2, (int)Math.Round(height * centerY - iconSize / 2.0));
    return bitmap;
}

static void SavePng(SKBitmap bitmap, string path)
{
    using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(path, data.ToArray());
}

// Frames under 256 px are stored as 32-bit DIBs (most compatible); 256 px is stored as PNG.
static void WriteIco(string path, IReadOnlyList<(int size, SKBitmap bitmap)> frames)
{
    var images = frames.Select(f => f.size >= 256 ? f.bitmap.Encode(SKEncodedImageFormat.Png, 100).ToArray() : ToDib(f.bitmap)).ToList();
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    writer.Write((ushort)0);
    writer.Write((ushort)1);
    writer.Write((ushort)frames.Count);
    var offset = 6 + 16 * frames.Count;
    for (var i = 0; i < frames.Count; i++)
    {
        var size = frames[i].size;
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(images[i].Length);
        writer.Write(offset);
        offset += images[i].Length;
    }
    foreach (var image in images) writer.Write(image);
}

static byte[] ToDib(SKBitmap bitmap)
{
    var size = bitmap.Width;
    using var unpremul = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Unpremul));
    bitmap.PeekPixels().ReadPixels(unpremul.Info, unpremul.GetPixels(), unpremul.RowBytes, 0, 0);
    var pixels = unpremul.GetPixelSpan();
    var maskStride = (size + 31) / 32 * 4;
    var result = new byte[40 + size * size * 4 + maskStride * size];
    var span = result.AsSpan();
    BinaryPrimitives.WriteInt32LittleEndian(span[0..], 40);
    BinaryPrimitives.WriteInt32LittleEndian(span[4..], size);
    BinaryPrimitives.WriteInt32LittleEndian(span[8..], size * 2);
    BinaryPrimitives.WriteInt16LittleEndian(span[12..], 1);
    BinaryPrimitives.WriteInt16LittleEndian(span[14..], 32);
    BinaryPrimitives.WriteInt32LittleEndian(span[20..], size * size * 4 + maskStride * size);
    var maskStart = 40 + size * size * 4;
    for (var y = 0; y < size; y++)
    {
        var sourceRow = size - 1 - y;
        pixels.Slice(sourceRow * unpremul.RowBytes, size * 4).CopyTo(span[(40 + y * size * 4)..]);
        for (var x = 0; x < size; x++)
        {
            if (pixels[sourceRow * unpremul.RowBytes + x * 4 + 3] == 0)
                result[maskStart + y * maskStride + x / 8] |= (byte)(0x80 >> (x % 8));
        }
    }
    return result;
}

static double ContrastShare(SKBitmap bitmap, SKColor background)
{
    double opaque = 0, passing = 0;
    var backgroundLuminance = Luminance(background);
    for (var y = 0; y < bitmap.Height; y++)
    for (var x = 0; x < bitmap.Width; x++)
    {
        var c = bitmap.GetPixel(x, y);
        if (c.Alpha < 230) continue;
        opaque++;
        var l = Luminance(c);
        var ratio = (Math.Max(l, backgroundLuminance) + 0.05) / (Math.Min(l, backgroundLuminance) + 0.05);
        if (ratio >= 3) passing++;
    }
    return passing / opaque;

    static double Luminance(SKColor c)
    {
        static double Channel(byte v) { var s = v / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Channel(c.Red) + 0.7152 * Channel(c.Green) + 0.0722 * Channel(c.Blue);
    }
}

static SKFont Font(float size, bool bold = false) =>
    new(SKTypeface.FromFamilyName("Segoe UI Variable Text", bold ? SKFontStyle.Bold : SKFontStyle.Normal) ?? SKTypeface.Default, size);

static void Blit(SKCanvas canvas, SKBitmap bitmap, float x, float y) => DrawNearest(canvas, bitmap, x, y, 1);

static void DrawNearest(SKCanvas canvas, SKBitmap bitmap, float x, float y, int zoom)
{
    using var image = SKImage.FromBitmap(bitmap);
    canvas.DrawImage(image, SKRect.Create(x, y, bitmap.Width * zoom, bitmap.Height * zoom), new SKSamplingOptions(SKFilterMode.Nearest));
}

SKBitmap RenderContactSheet()
{
    const int width = 1360, panelHeight = 700, gap = 24;
    var bitmap = new SKBitmap(new SKImageInfo(width, panelHeight * 2, SKColorType.Bgra8888, SKAlphaType.Premul));
    using var canvas = new SKCanvas(bitmap);
    var themes = new[] { ("Light", new SKColor(0xF3, 0xF3, 0xF3), new SKColor(0x1A, 0x1A, 0x1A)), ("Dark", new SKColor(0x20, 0x20, 0x20), new SKColor(0xF0, 0xF0, 0xF0)) };
    for (var t = 0; t < themes.Length; t++)
    {
        var (name, background, foreground) = themes[t];
        var top = t * panelHeight;
        canvas.DrawRect(0, top, width, panelHeight, new SKPaint { Color = background });
        using var text = new SKPaint { Color = foreground, IsAntialias = true };
        using var subtle = new SKPaint { Color = foreground.WithAlpha(150), IsAntialias = true };
        canvas.DrawText($"Mp3PlayerSync app icon — {name} theme", 40, top + 48, SKTextAlign.Left, Font(22, bold: true), text);
        canvas.DrawText("Every .ico frame at 1:1", 40, top + 84, SKTextAlign.Left, Font(15), subtle);

        float x = 40, baseline = top + 100 + 256;
        foreach (var (size, frame) in frames)
        {
            Blit(canvas, frame, x, baseline - size);
            canvas.DrawText($"{size}", x + size / 2f, baseline + 22, SKTextAlign.Center, Font(13), subtle);
            x += size + gap;
        }

        canvas.DrawText("Hand-hinted small sizes, zoomed 4x (nearest neighbour)", 40, top + 420, SKTextAlign.Left, Font(15), subtle);
        x = 40;
        baseline = top + 440 + 48 * 4;
        foreach (var size in new[] { 16, 20, 24, 30, 32, 48 })
        {
            DrawNearest(canvas, frames.Single(f => f.size == size).bitmap, x, baseline - size * 4, 4);
            canvas.DrawText($"{size}", x + size * 2, baseline + 22, SKTextAlign.Center, Font(13), subtle);
            x += size * 4 + 36;
        }
    }
    return bitmap;
}

SKBitmap RenderMockup()
{
    const int width = 1440, sectionHeight = 370;
    var bitmap = new SKBitmap(new SKImageInfo(width, sectionHeight * 2, SKColorType.Bgra8888, SKAlphaType.Premul));
    using var canvas = new SKCanvas(bitmap);
    var themes = new[]
    {
        new Theme("Light", new SKColor(0xD9, 0xE4, 0xF2), new SKColor(0xEE, 0xEE, 0xEE), new SKColor(0xF3, 0xF3, 0xF3), new SKColor(0x1A, 0x1A, 0x1A), new SKColor(0xB8, 0xBE, 0xC8)),
        new Theme("Dark", new SKColor(0x1B, 0x26, 0x38), new SKColor(0x1C, 0x1C, 0x1C), new SKColor(0x24, 0x24, 0x24), new SKColor(0xF0, 0xF0, 0xF0), new SKColor(0x5A, 0x60, 0x6A)),
    };
    for (var t = 0; t < themes.Length; t++)
    {
        var theme = themes[t];
        var top = t * sectionHeight;
        canvas.DrawRect(0, top, width, sectionHeight, new SKPaint { Color = theme.Desktop });
        using var text = new SKPaint { Color = theme.Text, IsAntialias = true };
        canvas.DrawText($"{theme.Name} theme — Start pins (left) and taskbar (right) at 100% and 150% scale", 24, top + 32, SKTextAlign.Left, Font(15, bold: true), text);

        // Start menu pinned grid, 100% (32 px icons) and 150% (48 px icons).
        DrawStart(canvas, theme, 24, top + 56, 1f, 32);
        DrawStart(canvas, theme, 400, top + 56, 1.5f, 48);

        // Taskbars, 100% (24 px icons, 48 px bar) and 150% (36 px icons, 72 px bar).
        DrawTaskbar(canvas, theme, 976, top + 120, 1f, 24);
        DrawTaskbar(canvas, theme, 976, top + 240, 1.5f, 36);
    }
    return bitmap;

    void DrawStart(SKCanvas canvas, Theme theme, float left, float top, float scale, int iconSize)
    {
        var cell = 84 * scale;
        var panel = SKRect.Create(left, top, cell * 4 + 16 * scale, cell * 2 + 16 * scale + 8);
        canvas.DrawRoundRect(panel, 8 * scale, 8 * scale, new SKPaint { Color = theme.Panel, IsAntialias = true });
        using var text = new SKPaint { Color = theme.Text, IsAntialias = true };
        using var placeholder = new SKPaint { Color = theme.Placeholder, IsAntialias = true };
        for (var i = 0; i < 8; i++)
        {
            var cx = left + 8 * scale + (i % 4) * cell + cell / 2;
            var cy = top + 8 * scale + (i / 4) * cell + 12 * scale;
            var isOurs = i == 1;
            if (isOurs)
                Blit(canvas, RenderIcon(iconSize), (int)(cx - iconSize / 2f), (int)cy);
            else
                canvas.DrawRoundRect(SKRect.Create(cx - iconSize / 2f + 2 * scale, cy + 2 * scale, iconSize - 4 * scale, iconSize - 4 * scale), 4 * scale, 4 * scale, placeholder);
            canvas.DrawText(isOurs ? "Mp3PlayerSync" : "App", cx, cy + iconSize + 18 * scale, SKTextAlign.Center, Font(12 * scale), text);
        }
    }

    void DrawTaskbar(SKCanvas canvas, Theme theme, float left, float top, float scale, int iconSize)
    {
        var barHeight = 48 * scale;
        var slot = 44 * scale;
        canvas.DrawRect(SKRect.Create(left, top, 6 * slot + 24 * scale, barHeight), new SKPaint { Color = theme.Bar });
        using var placeholder = new SKPaint { Color = theme.Placeholder, IsAntialias = true };
        for (var i = 0; i < 6; i++)
        {
            var cx = left + 12 * scale + i * slot + slot / 2;
            var iconTop = (int)(top + (barHeight - iconSize) / 2);
            if (i == 3)
            {
                canvas.DrawRoundRect(SKRect.Create(cx - slot / 2 + 2 * scale, top + 4 * scale, slot - 4 * scale, barHeight - 8 * scale), 4 * scale, 4 * scale,
                    new SKPaint { Color = (theme.Text).WithAlpha(18), IsAntialias = true });
                Blit(canvas, RenderIcon(iconSize), (int)(cx - iconSize / 2f), iconTop);
                canvas.DrawRoundRect(SKRect.Create(cx - 8 * scale, top + barHeight - 6 * scale, 16 * scale, 3 * scale), 1.5f * scale, 1.5f * scale,
                    new SKPaint { Color = new SKColor(0x6A, 0x58, 0xF0), IsAntialias = true });
            }
            else
            {
                canvas.DrawRoundRect(SKRect.Create(cx - iconSize / 2f + 2 * scale, iconTop + 2 * scale, iconSize - 4 * scale, iconSize - 4 * scale), 3 * scale, 3 * scale, placeholder);
            }
        }
    }
}

record Theme(string Name, SKColor Desktop, SKColor Bar, SKColor Panel, SKColor Text, SKColor Placeholder);
