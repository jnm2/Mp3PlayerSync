<#
.SYNOPSIS
    Regenerates the README's screenshots in docs/images from the mock Exportify's made-up demo library.

.DESCRIPTION
    Runs the app through its end-to-end test host (Mp3PlayerSync.E2EHost) on a hidden desktop
    (Invoke-EndToEnd.ps1), so it never appears over or takes focus from your windows. It runs against the mock
    Exportify with ?demo (tests/MockExportify/demo-library.js), a fake "MP3 PLAYER (E:)" drive backed by a temporary
    folder (MP3PLAYERSYNC_TEST_DRIVES) and the fake YouTube Music. No real Spotify account, library, drive or folder
    path can appear in the pictures.

    Build first: dotnet build Mp3PlayerSync.slnx
    Keep the workstation unlocked while it runs: windows capture as black while it's locked, and then nothing is saved.
#>
param(
    # Set when this script runs itself on the hidden desktop
    [string] $ScreenshotFolder,
    [string] $AppPath = (Get-ChildItem "$PSScriptRoot\Mp3PlayerSync.E2EHost\bin\Debug" -Recurse -Filter Mp3PlayerSync.E2EHost.exe | Select-Object -First 1).FullName,
    [string] $OutputFolder = (Join-Path $PSScriptRoot '..\docs\images'),
    # The window's size in device-independent pixels
    [int] $Width = 1100,
    [int] $Height = 760
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$names = 'hero', 'step-1-player', 'step-2-playlist', 'step-3-synced'
$passedText = 'All screenshots taken'

if (!$ScreenshotFolder) {
    $staging = Join-Path ([IO.Path]::GetTempPath()) "Mp3PlayerSyncScreenshots-$PID"
    New-Item -ItemType Directory -Force $staging | Out-Null
    try {
        & (Join-Path $PSScriptRoot 'Invoke-EndToEnd.ps1') -TestScript $PSCommandPath -ScreenshotFolder $staging -PassedText $passedText -TimeoutMinutes 5
        if ($LASTEXITCODE) { throw "Taking the screenshots failed ($LASTEXITCODE)" }

        # While the PC is locked, Windows gives blank pictures (one color, black or white, perhaps with a border) or
        # stale ones (refused as they're taken, above); never keep those.
        foreach ($name in $names) {
            $bitmap = [System.Drawing.Bitmap]::new((Join-Path $staging "$name.png"))
            try {
                $bright = 0; $samples = 0; $colors = [System.Collections.Generic.HashSet[int]]::new()
                for ($x = 0; $x -lt $bitmap.Width; $x += 17) { for ($y = 0; $y -lt $bitmap.Height; $y += 17) { $samples++; $pixel = $bitmap.GetPixel($x, $y); $colors.Add($pixel.ToArgb()) | Out-Null; if ($pixel.GetBrightness() -gt 0.08) { $bright++ } } }
                if ($bright / $samples -lt 0.2 -or $colors.Count -le 2) { throw "$name.png came out blank. Is the PC locked? Nothing was saved." }
            } finally { $bitmap.Dispose() }
        }
        New-Item -ItemType Directory -Force $OutputFolder | Out-Null
        foreach ($name in $names) {
            $from = Join-Path $staging "$name.png"; $to = Join-Path $OutputFolder "$name.png"
            if (Get-Command magick -ErrorAction SilentlyContinue) {
                # Flat UI loses nothing visible at 256 colors; the covers' gradients would dither, so that one stays lossless.
                $colors = if ($name -eq 'step-2-playlist') { @() } else { '-colors', '256' }
                magick $from -strip @colors -define png:compression-level=9 $to
            } else {
                Copy-Item $from $to -Force
            }
        }
        Write-Host "Saved $($names.Count) screenshots to $((Resolve-Path $OutputFolder).Path)"
        Remove-Item $staging -Recurse -Force
    } catch {
        Write-Host "What was taken, and the window when it failed, are in $staging"
        throw
    }
    exit 0
}

# From here on, this runs on the hidden desktop.
trap { Write-Host "ERROR $_ $($_.ScriptStackTrace)"; exit 1 }
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class ScreenshotWin32 {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT rect, int size);
    public const uint SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("wtsapi32.dll")] static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr memory);
    // WTSSessionInfoEx (25) for this session (-1): its SessionFlags, at offset 16, is WTS_SESSIONSTATE_LOCK (0) while locked.
    public static bool IsLocked() {
        IntPtr buffer; int bytes;
        if (!WTSQuerySessionInformationW(IntPtr.Zero, -1, 25, out buffer, out bytes)) return false;
        try { return Marshal.ReadInt32(buffer, 16) == 0; } finally { WTSFreeMemory(buffer); }
    }
}
'@

$AE = [System.Windows.Automation.AutomationElement]
$TreeScope = [System.Windows.Automation.TreeScope]
$ControlType = [System.Windows.Automation.ControlType]

function Wait-Until([scriptblock] $probe, [string] $what, [double] $timeoutSeconds = 30) {
    $deadline = [DateTime]::UtcNow.AddSeconds($timeoutSeconds)
    do {
        $result = & $probe
        if ($result) { return $result }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out after $timeoutSeconds s waiting for $what"
}
function Find-ById($root, [string] $automationId) { $root.FindFirst($TreeScope::Descendants, [System.Windows.Automation.PropertyCondition]::new($AE::AutomationIdProperty, $automationId)) }
function Find-ListItem($root, [string] $name) {
    $root.FindFirst($TreeScope::Descendants, [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new($AE::ControlTypeProperty, $ControlType::ListItem),
        [System.Windows.Automation.PropertyCondition]::new($AE::NameProperty, $name)))
}
function Get-Text($window, [string] $automationId) { (Find-ById $window $automationId)?.Current.Name }
function Get-AllText($window) {
    (@($window.FindAll($TreeScope::Descendants, [System.Windows.Automation.PropertyCondition]::new($AE::ControlTypeProperty, $ControlType::Text))) | ForEach-Object { $_.Current.Name }) -join "`n"
}
function Pattern($element, $pattern) { $element.GetCurrentPattern($pattern::Pattern) }
function Invoke-Element($element) { (Pattern $element ([System.Windows.Automation.InvokePattern])).Invoke() }

# The window without its invisible resize borders, at the window's own DPI.
function Save-Screenshot($window, [string] $name) {
    Start-Sleep -Milliseconds 800 # Lets animations and covers settle
    # Windows gives PrintWindow stale or blank pictures while the PC is locked.
    if ([ScreenshotWin32]::IsLocked()) { throw "The PC is locked, so $name.png can't be taken. Run this again once it's unlocked." }
    $hwnd = [IntPtr]$window.Current.NativeWindowHandle
    $rect = New-Object ScreenshotWin32+RECT; $frame = New-Object ScreenshotWin32+RECT
    [ScreenshotWin32]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
    if ([ScreenshotWin32]::DwmGetWindowAttribute($hwnd, [ScreenshotWin32]::DWMWA_EXTENDED_FRAME_BOUNDS, [ref]$frame, 16) -ne 0) { $frame = $rect }
    $whole = [System.Drawing.Bitmap]::new($rect.Right - $rect.Left, $rect.Bottom - $rect.Top)
    $graphics = [System.Drawing.Graphics]::FromImage($whole)
    $hdc = $graphics.GetHdc()
    [ScreenshotWin32]::PrintWindow($hwnd, $hdc, 2) | Out-Null
    $graphics.ReleaseHdc($hdc); $graphics.Dispose()
    $crop = [System.Drawing.Rectangle]::new($frame.Left - $rect.Left, $frame.Top - $rect.Top, $frame.Right - $frame.Left, $frame.Bottom - $frame.Top)
    $cropped = $whole.Clone($crop, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)

    # Saved at the DIP size, whatever the display scaling, so the pictures are the same size for everyone.
    $scale = $Width / $cropped.Width
    if ([Math]::Abs($scale - 1) -gt 0.01) {
        $resized = [System.Drawing.Bitmap]::new($Width, [int][Math]::Round($cropped.Height * $scale), [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
        $g = [System.Drawing.Graphics]::FromImage($resized)
        $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
        $g.DrawImage($cropped, 0, 0, $resized.Width, $resized.Height); $g.Dispose()
        $cropped.Dispose(); $cropped = $resized
    }
    $path = Join-Path $ScreenshotFolder "$name.png"
    $cropped.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $cropped.Dispose(); $whole.Dispose()
    Write-Host "Screenshot: $name ($((Get-Item $path).Length) bytes)"
}

# The demo library, and its tracks as fake-spotify.js makes them
$libraryScript = Get-Content (Join-Path $PSScriptRoot 'MockExportify\demo-library.js') -Raw
$library = $libraryScript.Substring($libraryScript.IndexOf('window.demoLibrary =') + 'window.demoLibrary ='.Length).Trim().TrimEnd(';') | ConvertFrom-Json
function Get-DemoTrack([string] $playlistId, [int] $index) {
    $seed = [uint64]7
    foreach ($c in $playlistId.ToCharArray()) { $seed = ($seed * 31 + [int]$c) % 4294967296 }
    $artist = $library.artists[($seed + $index * 7) % $library.artists.Count]
    [pscustomobject]@{ Title = $library.titles[($seed + $index * 5) % $library.titles.Count]; Artist = $artist[0] }
}

$port = Get-Random -Minimum 20000 -Maximum 40000
$url = "http://localhost:$port/"
$dataDir = Join-Path ([IO.Path]::GetTempPath()) "Mp3PlayerSyncScreenshots-$port"
$env:MP3PLAYERSYNC_EXPORTIFY_URL = "$($url)?autoSignIn&demo"
$env:MP3PLAYERSYNC_SPOTIFY_ACCOUNTS_URL = "http://127.0.0.1:$port/"
$env:MP3PLAYERSYNC_DATA_DIR = $dataDir
$env:MP3PLAYERSYNC_TEST_FAKE_AUDIO = Join-Path $PSScriptRoot 'Mp3PlayerSync.Tests\Tagging\Assets\silence.m4a'

# The player: a USB MP3 player and a memory card, each a folder standing in for a drive
$playerFolder = Join-Path $dataDir 'MP3 PLAYER'
$cardFolder = Join-Path $dataDir 'Memory card'
New-Item -ItemType Directory -Force $playerFolder, $cardFolder | Out-Null
$env:MP3PLAYERSYNC_TEST_DRIVES = ConvertTo-Json -Compress @(
    @{ root = 'E:\'; label = 'MP3 PLAYER'; bus = 'Usb'; totalBytes = 16GB; freeBytes = [long]9.4GB; folder = $playerFolder },
    @{ root = 'F:\'; label = ''; bus = 'SecureDigital'; totalBytes = 32GB; freeBytes = [long]31.2GB; folder = $cardFolder })
@{ player = @{ volumeId = '\\?\Volume{test-E}\'; label = 'MP3 PLAYER' } } | ConvertTo-Json | Set-Content (Join-Path $dataDir 'settings.json')

# Synced before: the playlist's songs up to those added since
$playlist = $library.playlists | Where-Object id -eq 'roadtrip'
$playlistFolder = Join-Path $playerFolder $playlist.name
New-Item -ItemType Directory -Force $playlistFolder | Out-Null
foreach ($i in 0..($playlist.total - $playlist.newSinceLastSync - 1)) {
    $track = Get-DemoTrack $playlist.id $i
    # Placeholder bytes: with no tags to read, the app goes by the file name.
    [IO.File]::WriteAllBytes((Join-Path $playlistFolder ('{0:00}. {1} - {2}.m4a' -f ($i + 1), $track.Artist, $track.Title)), [byte[]](1..64))
}

$server = Start-Job { param($script, $port) & $script -Port $port } -ArgumentList "$PSScriptRoot\Start-MockExportify.ps1", $port
$process = $null
try {
    Wait-Until { try { (Invoke-WebRequest $url -UseBasicParsing).StatusCode -eq 200 } catch { $false } } 'the mock server' | Out-Null
    $process = Start-Process $AppPath -PassThru
    $window = Wait-Until { $AE::RootElement.FindFirst($TreeScope::Children, [System.Windows.Automation.PropertyCondition]::new($AE::ProcessIdProperty, $process.Id)) } 'the app window'
    $hwnd = [IntPtr]$window.Current.NativeWindowHandle
    $dpiScale = [ScreenshotWin32]::GetDpiForWindow($hwnd) / 96
    [ScreenshotWin32]::SetWindowPos($hwnd, [IntPtr]::Zero, 0, 0, [int]($Width * $dpiScale), [int]($Height * $dpiScale),
        [ScreenshotWin32]::SWP_NOMOVE -bor [ScreenshotWin32]::SWP_NOZORDER -bor [ScreenshotWin32]::SWP_NOACTIVATE) | Out-Null

    # 1. Pick your player
    $players = Wait-Until { Find-ById $window 'PlayerGridView' } 'the player page'
    $tile = Wait-Until { $t = Find-ListItem $players 'MP3 PLAYER'; if ($t -and (Pattern $t ([System.Windows.Automation.SelectionItemPattern])).Current.IsSelected) { $t } } 'MP3 PLAYER, pre-selected'
    Save-Screenshot $window 'step-1-player'
    Invoke-Element $tile

    # 2. Pick a playlist (the mock signs in by itself)
    Wait-Until { (Get-Text $window 'LibrarySummaryText') -like '*playlists*' -and !(Find-ById $window 'LibraryBusyText') } 'the library' 90 | Out-Null
    Save-Screenshot $window 'step-2-playlist'
    $grid = Find-ById $window 'LibraryGridView'
    Invoke-Element (Wait-Until { Find-ListItem $grid $playlist.name } $playlist.name)

    # The payoff: songs already on the player, the new ones found and ready to go
    $newSongs = $playlist.newSinceLastSync
    Wait-Until { (Get-Text $window 'SyncSummaryText') -eq "Adding $newSongs songs" -and !(Find-ById $window 'PlaylistBusyText') } "'Adding $newSongs songs' (now '$(Get-Text $window 'SyncSummaryText')')" 60 | Out-Null
    $tracks = Find-ById $window 'TracksListView'
    $last = Get-DemoTrack $playlist.id ($playlist.total - 1)
    Wait-Until { (Get-AllText $window) -match 'Source found' -and (Find-ListItem $tracks "$($last.Title) — $($last.Artist)") } 'the new songs to be found' 30 | Out-Null
    Save-Screenshot $window 'hero'

    # 3. Sync
    Invoke-Element (Find-ById $window 'SyncButton')
    Wait-Until { [string]::IsNullOrEmpty((Get-Text $window 'SyncSummaryText')) -and (Get-AllText $window) -match 'Sync to player' -and (Get-AllText $window) -notmatch 'Source found|Downloading|Queued|Saving' } 'the sync to finish' 60 | Out-Null
    $copied = @(Get-ChildItem $playlistFolder -File).Count
    if ($copied -ne $playlist.total) { throw "Expected $($playlist.total) songs in the playlist's folder, found $copied" }
    Save-Screenshot $window 'step-3-synced'

    Write-Host $passedText
} catch {
    # Terminating errors would otherwise skip the hidden desktop's log.
    Write-Host "ERROR $_ $($_.ScriptStackTrace)"
    if ($window) { try { Save-Screenshot $window 'failure' } catch { } }
    exit 1
} finally {
    if ($process -and !$process.HasExited) { $process.CloseMainWindow() | Out-Null; if (!$process.WaitForExit(5000)) { $process.Kill() } }
    Stop-Job $server -ErrorAction SilentlyContinue; Remove-Job $server -Force -ErrorAction SilentlyContinue
    Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
}
