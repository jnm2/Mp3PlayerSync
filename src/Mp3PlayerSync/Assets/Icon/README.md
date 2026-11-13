# App icon

A portable music player whose control ring is a pair of sync arrows, with a music note on its screen.

| Path | What it is |
|------|------------|
| `Source\AppIcon.svg` | Vector master on the 48×48 Windows icon grid; renders every size from 36 px up |
| `Source\AppIcon.<n>.svg` | Hand-hinted artwork for 16, 20, 24, 30 and 32 px (edges on whole pixels; the sync arrows reduce to a ring at 16–24 px) |
| `build-icons.cs` | Regenerates every asset below from the SVGs |
| `Png\` | Each frame of `..\AppIcon.ico` as a PNG |
| `Preview\` | Contact sheet on light and dark, plus taskbar and Start mockups |
| `..\AppIcon.ico` | 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96 and 256 px; the app's `ApplicationIcon` and window icon |
| `..\Msix\` | `Square44x44Logo` target sizes (plated, `altform-unplated`, `altform-lightunplated`) and scale-100–400 sets of `Square44x44Logo`, `Square150x150Logo`, `Wide310x150Logo`, `StoreLogo` and `SplashScreen`, for a future packaged build |

After editing an SVG, regenerate everything with:

```powershell
dotnet run src\Mp3PlayerSync\Assets\Icon\build-icons.cs
```

It also prints how much of the icon has at least 3:1 contrast against light and dark backgrounds.

The design follows [Design guidelines for Windows app icons](https://learn.microsoft.com/windows/apps/design/iconography/app-icon-design)
and [Construct your Windows app's icon](https://learn.microsoft.com/windows/apps/design/iconography/app-icon-construction).
