<#
.SYNOPSIS
    End-to-end test of Mp3PlayerSync against the mock Exportify (tests/MockExportify), driven through UI Automation.

.DESCRIPTION
    Starts the mock, then launches the app several times against one fresh data folder, through the test host
    (Mp3PlayerSync.E2EHost), which runs it off-screen without activating it and puts in the fakes:
      1. Signed out: the sign-in screen appears (Exportify's landing page is skipped), the mock signs in by itself,
         then the library page, selector, playlist and album pages, refresh, and a failed export are checked.
      2. Restart: the last opened playlist reopens by itself and the selector choice is remembered.
      2b. The token runs out while an album is open: it's renewed in the background without leaving the album.
      2c. Spotify stops accepting the token during an export: it's renewed and the export retried, unseen.
      3. Expired Exportify token: the session is renewed without showing the sign-in screen, and the last opened
         saved album reopens.
      4. Sign out: the sign-in screen comes back. Links on it open outside the app, and signing in on it works.
    Throughout, Exportify's own page is never shown. Also checks that nothing was written to the Downloads folder.

    Exits with 0 when every check passed, 1 when any failed, and 2 when none failed but some were inconclusive:
    they couldn't look at what they check (say, the app drew no picture of its window). Pixel checks read the window
    as the app's XAML draws it, through the test host, so they work while the PC is locked, when Windows gives
    PrintWindow stale or blank pictures.

    The mock runs the live exportify.net exportify.js and FileSaver.js, so it needs internet access.
    Build first: dotnet build Mp3PlayerSync.slnx

.PARAMETER Only
    Runs just these runs, for example -Only Run2b,Run2c, in a minute or two. Run3 and Run4 run together; Run5 syncs to
    a player drive with a MUSIC folder. A run whose
    earlier runs are skipped starts from what they would have left: signed in, with the expected item last opened.
#>
param(
    [string] $AppPath = (Get-ChildItem "$PSScriptRoot\Mp3PlayerSync.E2EHost\bin\Debug" -Recurse -Filter Mp3PlayerSync.E2EHost.exe | Select-Object -First 1).FullName,
    [string] $ScreenshotFolder,
    [ValidateSet('Run1', 'Run2', 'Run2b', 'Run2c', 'Run3', 'Run4', 'Run5')] [string[]] $Only,
    # Where Invoke-EndToEnd.ps1 runs this. It watches the user's foreground window itself (TEST-1): from here, on a
    # hidden desktop, GetForegroundWindow always returns NULL.
    [ValidateSet('Hidden', 'Current')] [string] $Desktop = 'Hidden'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class E2EWin32 {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
    public struct POINT { public int X, Y; }
    [DllImport("wtsapi32.dll")] static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr memory);
    // WTSSessionInfoEx (25) for this session (-1): its SessionFlags, at offset 16, is WTS_SESSIONSTATE_LOCK (0) while locked.
    public static bool IsLocked() {
        IntPtr buffer; int bytes;
        if (!WTSQuerySessionInformationW(IntPtr.Zero, -1, 25, out buffer, out bytes)) return false;
        try { return Marshal.ReadInt32(buffer, 16) == 0; } finally { WTSFreeMemory(buffer); }
    }
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    public const uint SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int size);
    public static System.Collections.Generic.List<IntPtr> ChildWindows(IntPtr parent, string className) {
        var found = new System.Collections.Generic.List<IntPtr>();
        EnumChildWindows(parent, (hwnd, _) => {
            var name = new System.Text.StringBuilder(256);
            GetClassName(hwnd, name, name.Capacity);
            if (name.ToString() == className) found.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return found;
    }
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    public const uint BM_CLICK = 0xF5, WM_CLOSE = 0x10;
    public static IntPtr ProcessWindow(uint processId, string className) {
        var found = IntPtr.Zero;
        EnumWindows((hwnd, _) => {
            uint owner;
            GetWindowThreadProcessId(hwnd, out owner);
            var name = new System.Text.StringBuilder(256);
            GetClassName(hwnd, name, name.Capacity);
            if (owner != processId || name.ToString() != className || !IsWindowVisible(hwnd)) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }
    public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

$AE = [System.Windows.Automation.AutomationElement]
$TreeScope = [System.Windows.Automation.TreeScope]
$ControlType = [System.Windows.Automation.ControlType]
$failures = [System.Collections.Generic.List[string]]::new()
$inconclusive = [System.Collections.Generic.List[string]]::new()

function Check([bool] $condition, [string] $description) {
    if ($condition) { Write-Host "  PASS  $description" -ForegroundColor Green }
    else { Write-Host "  FAIL  $description" -ForegroundColor Red; $failures.Add($description) }
}

# A check that couldn't look at what it checks, say because there was no picture of the window: it neither passed nor
# failed. The run says so at the end and exits with 2 rather than 0, so it's never taken for a pass.
function Inconclusive([string] $description) {
    Write-Host "  INCONCLUSIVE  $description" -ForegroundColor Yellow
    $inconclusive.Add($description)
}

# Only the app processes this run started, while they run: Windows reuses the IDs of those that have exited.
# The app may never show Exportify's own page: the embedded page is only shown while it's Spotify's sign-in.
$script:appProcesses = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$script:currentWindow = $null
$script:sawExportifyPage = $false
function Watch-Page {
    try {
        if ($script:currentWindow -and (Find-ById $script:currentWindow 'SignInTitle') -and
            (Find-Named $script:currentWindow $ControlType::Pane 'Spotify sign-in') -and
            (Find-WebDocument $script:currentWindow 'Exportify (mock)')) { $script:sawExportifyPage = $true }
    } catch [System.Windows.Automation.ElementNotAvailableException] { } # The window is closing
}

# The page in the WebView2, which UI Automation reaches through the window's input site rather than the XAML tree.
function Find-WebDocument($window, [string] $name) {
    foreach ($hwnd in [E2EWin32]::ChildWindows([IntPtr]$window.Current.NativeWindowHandle, 'InputSiteWindowClass')) {
        $document = $AE::FromHandle($hwnd).FindFirst($TreeScope::Descendants, [System.Windows.Automation.AndCondition]::new(
            [System.Windows.Automation.PropertyCondition]::new($AE::ControlTypeProperty, $ControlType::Document),
            [System.Windows.Automation.PropertyCondition]::new($AE::NameProperty, $name)))
        if ($document) { return $document }
    }
}

# CI runners, with two cores on Arm64, are slower and busier than a developer's PC.
$script:waitScale = if ($env:CI -eq 'true') { 2 } else { 1 }
$script:timeoutShots = 0

function Wait-Until([scriptblock] $probe, [string] $what, [double] $timeoutSeconds = 30) {
    $timeoutSeconds *= $script:waitScale
    $deadline = [DateTime]::UtcNow.AddSeconds($timeoutSeconds)
    do {
        Watch-Page
        $result = & $probe
        if ($result) { return $result }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    # What the window showed, to tell a slow step from a wrong turn
    $shown = ''
    if ($script:currentWindow) {
        try {
            Save-Screenshot $script:currentWindow "timed-out-$((++$script:timeoutShots))"
            $shown = (Get-AllText $script:currentWindow) -replace '\s+', ' '
            if ($shown.Length -gt 500) { $shown = $shown.Substring(0, 500) + '…' }
        } catch { $shown = "(couldn't read the window: $($_.Exception.Message))" }
    }
    throw "Timed out after $timeoutSeconds s waiting for $what$(if ($shown) { ". The window showed: $shown" })"
}

function Find-ById($root, [string] $automationId) {
    $root.FindFirst($TreeScope::Descendants, [System.Windows.Automation.PropertyCondition]::new($AE::AutomationIdProperty, $automationId))
}

function Find-Named($root, $type, [string] $name) {
    $conditions = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new($AE::ControlTypeProperty, $type),
        [System.Windows.Automation.PropertyCondition]::new($AE::NameProperty, $name))
    $root.FindFirst($TreeScope::Descendants, $conditions)
}

# Breadth-first through the raw view, which includes what the control view leaves out. $match tests .Current.
function Find-Raw($root, [scriptblock] $match) {
    $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
    $queue = [System.Collections.Generic.Queue[object]]::new()
    $queue.Enqueue($root)
    while ($queue.Count) {
        $element = $queue.Dequeue()
        try { if ($element -ne $root -and ($element.Current | Where-Object $match)) { return $element } }
        catch [System.Windows.Automation.ElementNotAvailableException] { continue }
        $child = $walker.GetFirstChild($element)
        while ($child) { $queue.Enqueue($child); $child = $walker.GetNextSibling($child) }
    }
}

function Get-Items($list) {
    @($list.FindAll($TreeScope::Children, [System.Windows.Automation.PropertyCondition]::new($AE::ControlTypeProperty, $ControlType::ListItem)))
}

function Get-Text($window, [string] $automationId) { (Find-ById $window $automationId)?.Current.Name }

# Text that's still being filled in (e.g. just after going back to a page): waits briefly for the expected text, then returns what's there.
function Wait-Text($window, [string] $automationId, [string] $expected, [double] $timeoutSeconds = 5) {
    $deadline = [DateTime]::UtcNow.AddSeconds($timeoutSeconds)
    do { $text = Get-Text $window $automationId; if ($text -eq $expected) { break }; Start-Sleep -Milliseconds 100 } while ([DateTime]::UtcNow -lt $deadline)
    $text
}

function Get-AllText($window) {
    (@($window.FindAll($TreeScope::Descendants, [System.Windows.Automation.PropertyCondition]::new($AE::ControlTypeProperty, $ControlType::Text))) |
        ForEach-Object { $_.Current.Name }) -join "`n"
}

# TEXT-1 in docs/design-decisions.md
function Check-NoBrowserText($window, [string] $page) {
    $mentions = [regex]::Matches((Get-AllText $window), '(?im)^.*(browser|exportify).*$') | ForEach-Object Value
    Check (-not $mentions) "[TEXT-1] The $page mentions neither the browser nor Exportify $($mentions -join ' | ')"
}

function Pattern($element, $pattern) { $element.GetCurrentPattern($pattern::Pattern) }

function Find-ListItem($list, [string] $name) {
    $item = Find-Named $list $ControlType::ListItem $name
    if (!$item) {
        # Items outside the viewport are virtualized
        $item = (Pattern $list ([System.Windows.Automation.ItemContainerPattern])).FindItemByProperty($null, $AE::NameProperty, $name)
        if ($item) { (Pattern $item ([System.Windows.Automation.VirtualizedItemPattern])).Realize() }
    }
    $item
}

function Open-LibraryItem($window, [string] $name) {
    $list = Wait-Until { Find-ById $window 'LibraryGridView' } 'the library list'
    $item = Wait-Until { Find-ListItem $list $name } "'$name' in the library"
    (Pattern $item ([System.Windows.Automation.ScrollItemPattern])).ScrollIntoView()
    # Scrolling recycles item containers, so the element found before may now stand for another item.
    $item = Wait-Until { $i = Find-Named $list $ControlType::ListItem $name; if ($i -and !$i.Current.IsOffscreen) { $i } } "'$name' scrolled into view" 10
    (Pattern $item ([System.Windows.Automation.InvokePattern])).Invoke()
    Wait-Until { (Get-Text $window 'PlaylistTitle') -match ('^Spotify (playlist|album): ' + [regex]::Escape($name) + '$') } "the page for '$name' (got '$(Get-Text $window 'PlaylistTitle')')" | Out-Null
}

function Wait-Contents($window, [string] $name, [string] $expectedSummary, [double] $timeoutSeconds = 60) {
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $summary = Wait-Until {
        $s = Get-Text $window 'SummaryText'
        if ($s -like $expectedSummary -and !(Find-ById $window 'PlaylistBusyText')) { $s }
    } "'$name' contents like '$expectedSummary' (now '$(Get-Text $window 'SummaryText')')" $timeoutSeconds
    Check $true "'$name' shows '$summary' after $([int]$stopwatch.Elapsed.TotalMilliseconds) ms"
}

function Click($window, [string] $automationId) {
    (Pattern (Wait-Until { Find-ById $window $automationId } $automationId) ([System.Windows.Automation.InvokePattern])).Invoke()
}

function Select-Filter($window, [string] $name) {
    # Only the tabs' AutomationIds end in FilterItem: library tiles can be selected too, and a playlist could be called "All".
    $item = Wait-Until {
        @($window.FindAll($TreeScope::Descendants, [System.Windows.Automation.PropertyCondition]::new($AE::NameProperty, $name))) |
            Where-Object { $_.Current.AutomationId -like '*FilterItem' } | Select-Object -First 1
    } "selector tab '$name'"
    (Pattern $item ([System.Windows.Automation.SelectionItemPattern])).Select()
}

function First-LibraryItem($window) { (Get-Items (Find-ById $window 'LibraryGridView') | Select-Object -First 1)?.Current.Name }

# Cells of the visible track rows whose text is wider than the cell, so it shows an ellipsis. UI Automation can't
# tell whether text was trimmed, so this measures each cell's text with the list's font and compares it with the cell.
function Truncated-Cells($window, [double] $fromLeft = 0) {
    $font = [System.Drawing.Font]::new('Segoe UI Variable Text', 14, [System.Drawing.GraphicsUnit]::Pixel)
    try {
        foreach ($row in Get-Items (Find-ById $window 'TracksListView')) {
            foreach ($cell in @($row.FindAll($TreeScope::Descendants, [System.Windows.Automation.PropertyCondition]::new($AE::ControlTypeProperty, $ControlType::Text)))) {
                $r = $cell.Current.BoundingRectangle; $text = $cell.Current.Name
                if ($r.IsEmpty -or !$text -or $r.Left -lt $fromLeft) { continue }
                $natural = [System.Windows.Forms.TextRenderer]::MeasureText($text, $font, [System.Drawing.Size]::Empty, [System.Windows.Forms.TextFormatFlags]::NoPadding).Width
                if ($natural -gt $r.Width + 3) { "'$text' ($natural px in $($r.Width))" }
            }
        }
    } finally { $font.Dispose() }
}

# SPACE-1: no two of these elements touch or overlap, and each related pair is at least 8px apart. The rectangles are
# UI Automation's: a button's background, and a text's full line box, descenders included.
function Check-Spacing($window, [string] $where, [string[]] $ids, [string[][]] $related = @()) {
    # What stands for an element that isn't in UI Automation itself: the SelectorBar's items sit in its scroll presenter.
    $standIns = @{ FilterBar = { $item = Find-ById $window 'AllFilterItem'; if ($item) { [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($item) } } }
    $rects = @{}
    $problems = [System.Collections.Generic.List[string]]::new()
    foreach ($id in $ids) {
        $e = Find-ById $window $id
        if (!$e -and $standIns[$id]) { $e = & $standIns[$id] }
        # A renamed or missing element would otherwise just drop out of the check. One that's collapsed has no size.
        if (!$e) { $problems.Add("$id is missing"); continue }
        $r = $e.Current.BoundingRectangle; if (!$r.IsEmpty -and $r.Width -gt 0) { $rects[$id] = $r }
    }
    $names = @($rects.Keys)
    for ($i = 0; $i -lt $names.Count; $i++) {
        for ($j = $i + 1; $j -lt $names.Count; $j++) {
            $a = $rects[$names[$i]]; $b = $rects[$names[$j]]
            $gapX = [Math]::Max($b.Left - $a.Right, $a.Left - $b.Right); $gapY = [Math]::Max($b.Top - $a.Bottom, $a.Top - $b.Bottom)
            if ([Math]::Max($gapX, $gapY) -le 0) { $problems.Add("$($names[$i]) touches $($names[$j])") }
        }
    }
    foreach ($pair in $related) {
        $a = $rects[$pair[0]]; $b = $rects[$pair[1]]
        if (!$a -or !$b) { continue }
        $gap = [Math]::Max([Math]::Max($b.Left - $a.Right, $a.Left - $b.Right), [Math]::Max($b.Top - $a.Bottom, $a.Top - $b.Bottom))
        if ($gap -lt 7.5) { $problems.Add("$($pair[0]) and $($pair[1]) are $gap px apart, not 8") }
    }
    Check (-not $problems.Count) "$where`: nothing touches, and related elements are 8px apart $($problems -join '; ')"
}

# The player to sync to, beside the contents page's heading or under it, never on top of it.
function Check-PlayerBesideHeading($window, [int] $width) {
    $title = (Find-ById $window 'PlaylistTitle').Current.BoundingRectangle
    $player = (Find-ById $window 'ContentsPlayerText').Current.BoundingRectangle
    $sameLine = [Math]::Abs($player.Top - $title.Top) -le 1 -and [Math]::Abs($player.Bottom - $title.Bottom) -le 1 -and $player.Left -ge $title.Right
    $below = $player.Top -ge $title.Bottom
    Check ($sameLine -or $below) "At $width px the player is $(if ($sameLine) { 'on the heading''s line' } elseif ($below) { 'under the heading' } else { "overlapping the heading (title $title, player $player)" })"
}

# Where the back arrow and the banner's title are, relative to the window, and whether a message bar pushes them down.
function Heading-Layout($window, [string] $arrow, [string] $banner) {
    $w = $window.Current.BoundingRectangle
    $a = (Find-ById $window $arrow).Current.BoundingRectangle; $b = (Find-ById $window $banner).Current.BoundingRectangle
    [pscustomobject]@{ ArrowLeft = $a.Left - $w.Left; ArrowTop = $a.Top - $w.Top; BannerLeft = $b.Left - $w.Left; BannerTop = $b.Top - $w.Top; BannerBottom = $b.Bottom - $w.Top; Message = [bool](Find-ById $window 'MessageBar') }
}

# Switching between the playlists page and a playlist moves neither the back arrow nor the banner.
function Check-SameHeadingLayout($library, $contents) {
    $same = [Math]::Abs($library.ArrowLeft - $contents.ArrowLeft) -le 1 -and [Math]::Abs($library.BannerLeft - $contents.BannerLeft) -le 1 -and
        [Math]::Abs(($library.BannerTop - $library.ArrowTop) - ($contents.BannerTop - $contents.ArrowTop)) -le 1 -and
        [Math]::Abs(($library.BannerBottom - $library.BannerTop) - ($contents.BannerBottom - $contents.BannerTop)) -le 1
    # A message bar above the page pushes everything down; otherwise the positions match exactly.
    if (!$library.Message -and !$contents.Message) { $same = $same -and [Math]::Abs($library.ArrowTop - $contents.ArrowTop) -le 1 }
    Check $same "The back arrow and banner sit at the same place on the playlists page and a playlist (playlists $($library | ConvertTo-Json -Compress), playlist $($contents | ConvertTo-Json -Compress))"
}

function Capture-Window($window) {
    $hwnd = [IntPtr]$window.Current.NativeWindowHandle
    $rect = New-Object E2EWin32+RECT
    [E2EWin32]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
    if ($rect.Right -le $rect.Left -or $rect.Bottom -le $rect.Top) { return $null }
    $bitmap = [System.Drawing.Bitmap]::new($rect.Right - $rect.Left, $rect.Bottom - $rect.Top)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    [E2EWin32]::PrintWindow($hwnd, $hdc, 2) | Out-Null
    $graphics.ReleaseHdc($hdc)
    $graphics.Dispose()
    [pscustomobject]@{ Bitmap = $bitmap; Left = $rect.Left; Top = $rect.Top }
}

# The window's content as the app's XAML draws it, through the test host (see WindowCapture there), placed in screen
# pixels like UI Automation's rectangles. Unlike Capture-Window, it works while the PC is locked, but it leaves out
# the title bar, backdrop and web pages. If there's no picture, returns why, as a string.
function Render-Window($window) {
    $request = Join-Path $env:MP3PLAYERSYNC_DATA_DIR 'Capture.request'
    $png = Join-Path $env:MP3PLAYERSYNC_DATA_DIR 'Capture.png'
    # One left over from an app that has since closed would never be noticed: the host waits for one to be created.
    Remove-Item $request, $png, "$png.error" -ErrorAction SilentlyContinue
    Set-Content $request ''
    $seconds = 10 * $script:waitScale
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    while ((Test-Path $request) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 50 }
    if (Test-Path $request) { Remove-Item $request -ErrorAction SilentlyContinue; return "the app didn't draw the window within $seconds s" }
    if (Test-Path "$png.error") { return "the app couldn't draw the window: $((Get-Content "$png.error" -TotalCount 1))" }
    if (!(Test-Path $png)) { return 'the app had no window content to draw' }
    $file = [System.Drawing.Bitmap]::new($png)
    try { $bitmap = [System.Drawing.Bitmap]::new($file) } finally { $file.Dispose() } # A copy, so the file isn't held open
    if (Test-Blank $bitmap) { $bitmap.Dispose(); return "the app's drawing of the window was blank" }
    $origin = New-Object E2EWin32+POINT
    [E2EWin32]::ClientToScreen([IntPtr]$window.Current.NativeWindowHandle, [ref]$origin) | Out-Null
    [pscustomobject]@{ Bitmap = $bitmap; Left = $origin.X; Top = $origin.Y }
}

# Whether a picture shows nothing, as Windows sometimes gives PrintWindow while the PC is locked: one color, perhaps
# with a border.
function Test-Blank($bitmap) {
    $colors = [System.Collections.Generic.HashSet[int]]::new()
    for ($x = 0; $x -lt $bitmap.Width; $x += 8) {
        for ($y = 0; $y -lt $bitmap.Height; $y += 8) { if ($colors.Add($bitmap.GetPixel($x, $y).ToArgb()) -and $colors.Count -gt 2) { return $false } }
    }
    $true
}

function Save-Screenshot($window, [string] $name) {
    if (!$ScreenshotFolder) { return }
    # While the PC is locked, PrintWindow gets a blank picture, or the window as it was before (say, a page ago).
    $why = if ([E2EWin32]::IsLocked()) { 'the PC is locked, when Windows gives stale or blank pictures' }
    $capture = if (!$why) { Capture-Window $window }
    if (!$why -and !$capture) { Write-Host "  No screenshot of $name (window not laid out)"; return }
    if (!$why -and (Test-Blank $capture.Bitmap)) { $capture.Bitmap.Dispose(); $why = 'Windows gave a blank picture' }
    if ($why) {
        # The app's own drawing is the next best thing.
        $capture = Render-Window $window
        if ($capture -is [string]) { Write-Host "  No screenshot of $name ($why, and $capture)"; return }
    }
    $path = Join-Path $ScreenshotFolder "$name.png"
    $capture.Bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $capture.Bitmap.Dispose()
    Write-Host "  Screenshot: $path$(if ($why) { " (as the app draws it, without the title bar, backdrop or web pages: $why)" })"
}

# The color of the icon just left of a status text in a track row: Success (green), Caution (yellow), Critical (red),
# Neutral (grey), or None; or "Unknown (why)" without a picture of the window. Status icons aren't in UI Automation
# (Narrator reads the text instead), so this reads the pixels, as the app draws them, which works while the PC is locked.
function Status-Tone($window, $row, [string] $status) {
    $text = $row.FindFirst($TreeScope::Descendants, [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new($AE::NameProperty, $status),
        [System.Windows.Automation.PropertyCondition]::new($AE::ControlTypeProperty, $ControlType::Text))).Current.BoundingRectangle
    $capture = Render-Window $window
    if ($capture -is [string]) { return "Unknown ($capture)" }
    $counts = @{ Success = 0; Caution = 0; Critical = 0; Neutral = 0 }
    try {
        # The icon's 18px slot and 4px gap are just left of the text; the gap shows the row's background.
        $right = [int]($text.Left - $capture.Left) - 2
        $top = [int]($text.Top - $capture.Top)
        $background = $capture.Bitmap.GetPixel($right, $top).GetBrightness()
        for ($x = $right - 20; $x -lt $right; $x++) {
            for ($y = $top; $y -lt [int]($text.Bottom - $capture.Top); $y++) {
                $c = $capture.Bitmap.GetPixel($x, $y)
                if ($c.G -gt $c.R + 40 -and $c.G -gt $c.B + 20) { $counts.Success++ }
                elseif ($c.R -gt 120 -and $c.G -gt 100 -and $c.B -lt $c.G - 60 -and [Math]::Abs($c.R - $c.G) -lt 60) { $counts.Caution++ }
                elseif ($c.R -gt $c.G + 40 -and $c.R -gt $c.B + 30) { $counts.Critical++ }
                elseif ([Math]::Max($c.R, [Math]::Max($c.G, $c.B)) - [Math]::Min($c.R, [Math]::Min($c.G, $c.B)) -lt 30 -and [Math]::Abs($c.GetBrightness() - $background) -gt 0.25) { $counts.Neutral++ }
            }
        }
    } finally { $capture.Bitmap.Dispose() }
    $best = $counts.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 1
    if ($best.Value -ge 5) { $best.Key } else { 'None' }
}

# Checks the tones Status-Tone found, which is inconclusive rather than a pass or a failure if any is Unknown.
function Check-Tones([string[]] $tones, [string[]] $expected, [string] $description) {
    $found = $tones -join ', '
    if ($tones -like 'Unknown*') { Inconclusive "$description`: $found" }
    else { Check ($found -eq ($expected -join ', ')) "$description`: $found" }
}

# The player page comes first on every start; the folder chosen last time is pre-selected, so Enter continues.
function Choose-Player($window) {
    $grid = Wait-Until { Find-ById $window 'PlayerGridView' } 'the MP3 player page'
    Check ((Get-Text $window 'PlayerTitle') -eq 'Select an MP3 player to sync') 'MP3 player page comes first'
    $tile = Wait-Until { $t = Find-ListItem $grid $playerName; if ($t -and (Pattern $t ([System.Windows.Automation.SelectionItemPattern])).Current.IsSelected) { $t } } "the '$playerName' player, pre-selected" 10
    Check $true 'Player chosen last time is pre-selected'
    Check ($null -eq (Find-ById $window 'SignInTitle') -and $null -eq (Find-ById $window 'LibraryGridView')) 'Nothing else shows behind the player page'
    if (!$script:checkedPlayerPage) {
        $script:checkedPlayerPage = $true
        # The title can reach UI Automation a moment after the window does, so wait for it rather than read it once.
        $hwnd = [IntPtr]$window.Current.NativeWindowHandle
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        $title = $AE::FromHandle($hwnd).Current.Name
        while ($title -ne 'MP3 Player Sync' -and [DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 200
            $title = $AE::FromHandle($hwnd).Current.Name
        }
        Check ($title -eq 'MP3 Player Sync') "[TEXT-3] The window is called 'MP3 Player Sync': '$title'"
        $refresh = @($window.FindAll($TreeScope::Descendants, [System.Windows.Automation.PropertyCondition]::new($AE::ControlTypeProperty, $ControlType::Button))) |
            Where-Object { $_.Current.Name -match 'Refresh' -or $_.Current.AutomationId -match 'Refresh' }
        Check (-not $refresh) '[PLAYER-5] The player page has no Refresh button'
        Check-NoBrowserText $window 'player page'
    }
    Save-Screenshot $window 'player'
    (Pattern $tile ([System.Windows.Automation.InvokePattern])).Invoke()
    Wait-Until { $null -eq (Find-ById $window 'PlayerGridView') } 'the player page to close' | Out-Null
}

function Start-App([string] $url) {
    $script:currentWindow = $null
    $env:MP3PLAYERSYNC_EXPORTIFY_URL = $url
    $process = Start-Process $AppPath -PassThru
    $script:appProcesses.Add($process)
    # The app's own window, by its class: the process can have other top-level windows (popups, helpers), which have
    # no title or size of their own.
    $condition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new($AE::ProcessIdProperty, $process.Id),
        [System.Windows.Automation.PropertyCondition]::new($AE::ClassNameProperty, 'WinUIDesktopWin32WindowClass'))
    $window = Wait-Until { $AE::RootElement.FindFirst($TreeScope::Children, $condition) } 'the app window'
    $others = @($AE::RootElement.FindAll($TreeScope::Children, [System.Windows.Automation.PropertyCondition]::new($AE::ProcessIdProperty, $process.Id))) |
        Where-Object { $_.Current.NativeWindowHandle -ne $window.Current.NativeWindowHandle }
    foreach ($other in $others) {
        $r = $other.Current.BoundingRectangle
        Write-Host "  The app also has a top-level '$($other.Current.Name)' $($other.Current.ClassName) at $([int]$r.Left),$([int]$r.Top) $([int]$r.Width)x$([int]$r.Height)"
    }
    $script:currentWindow = $window
    # For Stop-App
    $script:appWindows[$process.Id] = [IntPtr]$window.Current.NativeWindowHandle
    $script:appDataDirs[$process.Id] = $env:MP3PLAYERSYNC_DATA_DIR
    return $process, $window
}

# Closes the app, and waits until it's gone, with its WebView2 processes: the next launch on the same data folder would
# otherwise hand over to the closing copy (APP-1), or meet its WebView2 profile still in use.
$script:appWindows = @{}
$script:appDataDirs = @{}
function Stop-App($process) {
    $script:currentWindow = $null
    $seconds = 5 * $script:waitScale
    if (!$process.HasExited) {
        # To the window Start-App found: CloseMainWindow uses Process.MainWindowHandle, which can be a helper window.
        $hwnd = $script:appWindows[$process.Id]
        if ($hwnd) { [E2EWin32]::PostMessage($hwnd, [E2EWin32]::WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }
        else { $process.CloseMainWindow() | Out-Null }
        if (!$process.WaitForExit([int]($seconds * 1000))) {
            Write-Host "  The app didn't close within $seconds s; ending it"
            # It may still exit by itself between the wait and the kill.
            try { $process.Kill() } catch [System.InvalidOperationException] { }
            $process.WaitForExit(10000) | Out-Null # Kill only starts it
        }
    }
    $dataDir = $script:appDataDirs[$process.Id]
    if (!$dataDir) { return }
    $webViewData = (Join-Path $dataDir 'WebView2') + '\'
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    do {
        $left = @(Get-CimInstance Win32_Process -Filter "Name = 'msedgewebview2.exe'" -Property ProcessId, CommandLine |
            Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($webViewData, [StringComparison]::OrdinalIgnoreCase) -ge 0 })
        if (!$left) { return }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    Write-Host "  The app's WebView2 processes were still running $seconds s after it closed; ending them"
    $left | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

# Waits for the library while watching whether the sign-in screen was ever shown.
function Wait-Library($window, [string] $expectedSummary, [ref] $sawSignIn) {
    Wait-Until {
        if (Find-ById $window 'SignInTitle') { $sawSignIn.Value = $true }
        (Get-Text $window 'LibrarySummaryText') -like $expectedSummary -and !(Find-ById $window 'LibraryBusyText')
    } "library summary like '$expectedSummary' (now '$(Get-Text $window 'LibrarySummaryText')')" 60 | Out-Null
}

# How many calls to the fake Spotify API matched the pattern, from the mock's log.
function Get-ApiCalls([string] $pattern) {
    $calls = Invoke-RestMethod "$($url)api-log" # The whole list as one array, so filter it rather than pipe it
    @(@($calls) -match $pattern).Count
}

# Clicks Refresh and waits until the app has asked Spotify again: the busy text alone can come and go between polls.
function Click-Refresh($window, [string] $apiPattern, [string] $what) {
    $before = Get-ApiCalls $apiPattern
    Click $window 'RefreshPlaylistButton'
    Wait-Until { (Get-ApiCalls $apiPattern) -gt $before } "$what to ask Spotify again" 30 | Out-Null
}
function Test-Selected([string[]] $names) { -not $Only -or @($names | Where-Object { $Only -contains $_ }).Count -gt 0 }

# Gets to the state that the runs before a selected one leave behind, when they're skipped: signed in, with
# everything listed, and the given item opened last.
$lastOpened = $null
function Prepare-State([string] $lastItem) {
    if ($script:lastOpened -eq $lastItem) { return }
    Write-Host "Preparing: signed in, '$lastItem' opened last"
    $process, $window = Start-App "$($url)?autoSignIn"
    try {
        Choose-Player $window
        # The item opened last reopens by itself; go back to the library from it.
        Wait-Until { ((Get-Text $window 'LibrarySummaryText') -and !(Find-ById $window 'LibraryBusyText')) -or (Get-Text $window 'PlaylistTitle') } 'the library or the item opened last' 90 | Out-Null
        if (Get-Text $window 'PlaylistTitle') { Click $window 'BackButton' }
        Wait-Until { (Get-Text $window 'LibrarySummaryText') -and !(Find-ById $window 'LibraryBusyText') } 'the library' 90 | Out-Null
        Select-Filter $window 'All'
        Wait-Until { (Get-Text $window 'LibrarySummaryText') -like '*playlists and albums' -and !(Find-ById $window 'LibraryBusyText') } 'the whole library' 60 | Out-Null
        Open-LibraryItem $window $lastItem
        Wait-Until { (Get-Text $window 'SummaryText') -and !(Find-ById $window 'PlaylistBusyText') } "'$lastItem' to load" 60 | Out-Null
    } finally { Stop-App $process }
    $script:lastOpened = $lastItem
}

# Everything this run uses is its own, so runs can go side by side: its data folder (and so the app's settings,
# WebView2 profile, caches, staging and single-instance key, and the test's player and drive folders), and the mock's port.
$env:MP3PLAYERSYNC_DATA_DIR = Join-Path ([IO.Path]::GetTempPath()) "Mp3PlayerSyncE2E-$PID-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
# Stands in for YouTube Music: see FakeTrackFinder for which songs need review
$env:MP3PLAYERSYNC_TEST_FAKE_AUDIO = Join-Path $PSScriptRoot 'Mp3PlayerSync.Tests\Tagging\Assets\silence.m4a'
# No removable drive can be assumed, so the test's player is a folder, as if chosen with "Other folder" last time.
$playerName = 'E2E Player'
$playerFolder = Join-Path $env:MP3PLAYERSYNC_DATA_DIR $playerName
New-Item -ItemType Directory -Force $playerFolder | Out-Null
# Playlists sync into a folder of their own. One Road Trip track is already there, one file there is no longer in
# the playlist, and another track is elsewhere on the player (which doesn't count for the playlist).
$roadTripFolder = Join-Path $playerFolder 'Road Trip, Vol. 2'
$removedFile = Join-Path $roadTripFolder '03. Gone - Removed song.mp3'
foreach ($file in (Join-Path $roadTripFolder '02. Artist 1 - roadtrip song 2.mp3'), $removedFile, (Join-Path $playerFolder 'Music\Deep\Artist 4 - roadtrip song 5.mp3')) {
    New-Item -ItemType Directory -Force (Split-Path $file) | Out-Null
    [IO.File]::WriteAllBytes($file, [byte[]](1..64))
}
@{ player = @{ folderPath = $playerFolder } } | ConvertTo-Json | Set-Content (Join-Path $env:MP3PLAYERSYNC_DATA_DIR 'settings.json')
# A port that's free now; should another process take it before the mock does, the mock fails and another is tried.
function Start-Mock {
    for ($attempt = 1; ; $attempt++) {
        $probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
        $probe.Start(); $port = $probe.LocalEndpoint.Port; $probe.Stop()
        $job = Start-Job { param($script, $port) & $script -Port $port } -ArgumentList "$PSScriptRoot\Start-MockExportify.ps1", $port
        $deadline = (Get-Date).AddSeconds(30)
        while ($job.State -eq 'Running' -and (Get-Date) -lt $deadline) {
            if ($(try { (Invoke-WebRequest "http://localhost:$port/" -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200 } catch { $false })) { return $job, $port }
            Start-Sleep -Milliseconds 200
        }
        $why = "$($job.ChildJobs[0].JobStateInfo.Reason)"
        Stop-Job $job; Remove-Job $job -Force
        if ($attempt -ge 5) { throw "The mock server didn't start: $why" }
        Write-Host "  The mock server couldn't use port $port ($why); trying another"
    }
}
$downloads = (New-Object -ComObject Shell.Application).NameSpace('shell:Downloads').Self.Path
$startTime = Get-Date

try {
    $server, $port = Start-Mock
    $url = "http://localhost:$port/"
    # The mock's fake Spotify sign-in page, on another origin like accounts.spotify.com
    $env:MP3PLAYERSYNC_SPOTIFY_ACCOUNTS_URL = "http://127.0.0.1:$port/"

    if (Test-Selected 'Run1') {
        Write-Host "Run 1: signed out ($url, data in $env:MP3PLAYERSYNC_DATA_DIR)"
        # The mock's automatic sign-in waits until the test has seen the sign-in screen, which would otherwise be up
        # for only a moment.
        Invoke-RestMethod "$($url)sign-in-hold" -Method Post -Body 'hold' | Out-Null
        $process, $window = Start-App "$($url)?autoSignIn"
        try {
            Choose-Player $window
            # A [ref], like Wait-Library's: Wait-Until runs the probe in a scope of its own, where assigning a variable
            # would only make a new one.
            $sawLibraryEarly = [ref]$false
            Wait-Until {
                if (Find-ById $window 'LibraryGridView') { $sawLibraryEarly.Value = $true }
                Find-ById $window 'SignInTitle'
            } 'the sign-in screen' 60 | Out-Null
            Check (!$sawLibraryEarly.Value) 'No library page before signing in'
            Save-Screenshot $window 'signin'
            $signInText = "$(Get-Text $window 'SignInTitle') $(Get-Text $window 'SignInText')"
            Check ($signInText -notmatch 'browser|exportify') "Sign-in text mentions neither browser nor Exportify: '$signInText'"
            Invoke-RestMethod "$($url)sign-in-hold" -Method Post -Body 'release' | Out-Null

            $sawSignIn = $true
            Wait-Library $window '40 playlists' ([ref]$sawSignIn)
            Check ($null -eq (Find-ById $window 'SignInTitle')) 'Sign-in screen is hidden after signing in'
            Check ((First-LibraryItem $window) -eq 'Liked Songs') "Liked Songs is first (got '$(First-LibraryItem $window)')"
            $firstTab = @($window.FindAll($TreeScope::Descendants, [System.Windows.Automation.PropertyCondition]::new($AE::NameProperty, 'Created playlists'))) |
                Where-Object { $_.GetSupportedPatterns() -contains [System.Windows.Automation.SelectionItemPattern]::Pattern } | Select-Object -First 1
            $tabs = @([System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($firstTab).FindAll($TreeScope::Children, [System.Windows.Automation.Condition]::TrueCondition)) |
                Where-Object { $_.GetSupportedPatterns() -contains [System.Windows.Automation.SelectionItemPattern]::Pattern } |
                Sort-Object { $_.Current.BoundingRectangle.Left } | ForEach-Object { $_.Current.Name }
            Check (($tabs -join ' | ') -eq 'Created playlists | Saved playlists | Saved albums | All') "[GRID-6] Tabs: $($tabs -join ' | ')"
            Check ((Get-Text $window 'SignOutButton') -eq 'Sign out of Spotify') "[TEXT-4] Sign-out button: '$(Get-Text $window 'SignOutButton')'"
            Check-NoBrowserText $window 'library page'

            Save-Screenshot $window 'library'
            $libraryLayout = Heading-Layout $window 'ChangePlayerButton' 'LibraryTitle'
            Check-Spacing $window 'Playlists page' @('ChangePlayerButton', 'SelectedPlayerTitle', 'LibraryTitle', 'FilterBar', 'RefreshLibraryButton', 'SignOutButton', 'LibrarySummaryText') @(, @('RefreshLibraryButton', 'SignOutButton'))

            # The window can't be made so narrow that a tab, Refresh or Sign out is cut off.
            (Pattern $window ([System.Windows.Automation.TransformPattern])).Resize(400, 900)
            Start-Sleep -Milliseconds 700
            if ($process.HasExited) { throw "The app exited when the window was narrowed (exit code $($process.ExitCode))" }
            $tabs = 'CreatedFilterItem', 'SavedPlaylistsFilterItem', 'SavedAlbumsFilterItem', 'AllFilterItem' | ForEach-Object { Wait-Until { Find-ById $window $_ } $_ 10 }
            $clippedTabs = $tabs | Where-Object { $_.Current.IsOffscreen -or $_.Current.BoundingRectangle.Width -le 0 } | ForEach-Object { $_.Current.Name }
            $refresh = (Find-ById $window 'RefreshLibraryButton').Current.BoundingRectangle
            $signOut = (Find-ById $window 'SignOutButton').Current.BoundingRectangle
            $windowRect = $window.Current.BoundingRectangle
            Check (@($tabs).Count -eq 4 -and -not $clippedTabs -and ($tabs | ForEach-Object { $_.Current.BoundingRectangle.Right } | Measure-Object -Maximum).Maximum -le $refresh.Left -and $signOut.Right -le $windowRect.Right - 8) "At the narrowest window ($($windowRect.Width) px) the tabs, Refresh and Sign out all fit $($clippedTabs -join ', ')"
            Save-Screenshot $window 'library-narrowest'
            (Pattern $window ([System.Windows.Automation.TransformPattern])).Resize(1280, 900)
            Start-Sleep -Milliseconds 500

            # TEXT-15: one line counts the songs found while they're looked for, then goes away. The fake finder would be
            # done in a second or two, so it's held after its first few songs until the line has been checked.
            $holdFinding = Join-Path $env:MP3PLAYERSYNC_DATA_DIR 'HoldFinding.txt'
            Set-Content $holdFinding ''
            try {
                Open-LibraryItem $window 'Road Trip, Vol. 2'
                Wait-Contents $window 'Road Trip, Vol. 2' '250 tracks, 21:08:45'
                $findingText = Wait-Until { $t = Get-Text $window 'FindingProgressText'; if ($t -match '^Finding sources… \d+ of \d+$') { $t } } "the finding progress line (now '$(Get-Text $window 'FindingProgressText')')" 30
                Save-Screenshot $window 'finding-progress'
                Check ($findingText -match '^Finding sources… \d+ of 2\d\d$') "[TEXT-15] One line counts the songs being looked for: '$findingText'"
            } finally { Remove-Item $holdFinding -ErrorAction SilentlyContinue }
            Wait-Until { $null -eq (Find-ById $window 'FindingProgressText') } 'the finding progress line to go away' 30 | Out-Null
            Check $true '[TEXT-15] The finding progress line goes away when finding finishes'
            Check ((Get-AllText $window) -like '*Some details could not be loaded*403*') "Exportify's audio-features 403 is surfaced as a warning"
            Check ((Get-AllText $window) -like '*Spotify refused some requests (HTTP 403)*' -and (Get-AllText $window) -notmatch '(?i)browser|exportify') "[TEXT-1] The warning is in the app's words: '$(((Get-AllText $window) -split "`n" | Where-Object { $_ -match '403' }) -join ' ')'"
            $firstRow = Get-Items (Find-ById $window 'TracksListView') | Select-Object -First 1
            Check ($firstRow.Current.Name -eq 'roadtrip song 1 — Earth, Wind & Fire, Chaka Khan') "First track row: '$($firstRow.Current.Name)'"
            Check ((Get-Text $window 'ContentsBannerTitle') -eq 'Choose songs to sync') "[TEXT-5] Contents banner: '$(Get-Text $window 'ContentsBannerTitle')'"
            # Above the track list, only the heading (RichTextBlock, named "Spotify playlist: …") carries the name, and only it has art.
            $listTop = (Find-ById $window 'TracksListView').Current.BoundingRectangle.Top
            $bannerBottom = (Find-ById $window 'ContentsBannerTitle').Current.BoundingRectangle.Bottom
            $repeats = @($window.FindAll($TreeScope::Descendants, [System.Windows.Automation.PropertyCondition]::new($AE::NameProperty, 'Road Trip, Vol. 2'))) |
                Where-Object { $_.Current.BoundingRectangle.Bottom -le $listTop }
            # Images are art, not icons or a busy ring's animation (which UI Automation also calls an image).
            $hero = @($window.FindAll($TreeScope::Descendants, [System.Windows.Automation.PropertyCondition]::new($AE::ControlTypeProperty, $ControlType::Image))) |
                Where-Object { $r = $_.Current.BoundingRectangle; !$r.IsEmpty -and $r.Width -ge 32 -and $r.Height -ge 32 -and $r.Top -ge $bannerBottom -and $r.Bottom -le $listTop }
            $describe = { param($e) $r = $e.Current.BoundingRectangle; "$($e.Current.ControlType.ProgrammaticName -replace '^ControlType\.') '$($e.Current.Name)' $($e.Current.ClassName) #$($e.Current.AutomationId) at $([int]$r.Left),$([int]$r.Top) $([int]$r.Width)x$([int]$r.Height)" }
            $found = @(@($repeats) + @($hero) | Where-Object { $_ } | ForEach-Object { & $describe $_ })
            Check (-not $repeats -and -not $hero) "[CONTENTS-2] No hero art or repeated name under the banner (banner bottom $([int]$bannerBottom), list top $([int]$listTop)) $($found -join '; ')"
            Check-NoBrowserText $window 'contents page'
            Save-Screenshot $window 'playlist'
            Check-SameHeadingLayout $libraryLayout (Heading-Layout $window 'BackButton' 'ContentsBannerTitle')

            # The header's columns line up with the rows' at every width, and nothing is cut off.
            $firstRow = Get-Items (Find-ById $window 'TracksListView') | Select-Object -First 1
            function Cell($row, [string] $text) { $row.FindFirst($TreeScope::Descendants, [System.Windows.Automation.AndCondition]::new([System.Windows.Automation.PropertyCondition]::new($AE::NameProperty, $text), [System.Windows.Automation.PropertyCondition]::new($AE::ControlTypeProperty, $ControlType::Text))).Current.BoundingRectangle }
            # UI Automation's TransformPattern when the window offers it (it doesn't always), otherwise SetWindowPos.
            $hwnd = [IntPtr]$window.Current.NativeWindowHandle
            function Resize-Window([int] $width, [int] $height) {
                $transform = $null
                if ($window.TryGetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern, [ref]$transform) -and $transform.Current.CanResize) {
                    $transform.Resize($width, $height)
                } else {
                    [E2EWin32]::SetWindowPos($hwnd, [IntPtr]::Zero, 0, 0, $width, $height, [E2EWin32]::SWP_NOMOVE -bor [E2EWin32]::SWP_NOZORDER -bor [E2EWin32]::SWP_NOACTIVATE) | Out-Null
                }
            }
            foreach ($width in 800, 1200, 1900) {
                Resize-Window $width 900
                Start-Sleep -Milliseconds 700
                $edges = @(
                    @('TitleHeader', 'roadtrip song 1', 'Left'),
                    @('ArtistsHeader', 'Earth, Wind & Fire, Chaka Khan', 'Left'),
                    @('AlbumHeader', 'Album 0', 'Left'), # The first track's album in fake-spotify.js
                    @('NumberHeader', '1', 'Right'),
                    @('TimeHeader', ($firstRow.FindAll($TreeScope::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -match '^\d+:\d\d$' } | Select-Object -First 1).Current.Name, 'Right'))
                $off = foreach ($edge in $edges) {
                    $header = (Find-ById $window $edge[0]).Current.BoundingRectangle
                    $cell = Cell $firstRow $edge[1]
                    $delta = $cell.($edge[2]) - $header.($edge[2])
                    if ([Math]::Abs($delta) -gt 1) { "$($edge[0]) off by $delta" }
                }
                Check (-not $off) "At $width px the columns line up with the header $($off -join ', ')"
                $windowRight = $window.Current.BoundingRectangle.Right
                $clipped = 'SyncButton', 'RefreshPlaylistButton', 'SyncSummaryText', 'OpenPlayerFolderButton', 'RemoveHeader' | Where-Object { $r = (Find-ById $window $_).Current.BoundingRectangle; $r.IsEmpty -or $r.Right -gt $windowRight - 8 }
                Check (-not $clipped) "At $width px nothing in the header is cut off $($clipped -join ', ')"
                Check-PlayerBesideHeading $window $width
                Check-Spacing $window "At $width px the playlist's header" @('BackButton', 'PlaylistTitle', 'ContentsPlayerText', 'OpenPlayerFolderButton', 'ContentsBannerTitle', 'SummaryText', 'RefreshPlaylistButton', 'SyncSummaryText', 'SyncButton', 'SelectAllCheckBox', 'NumberHeader', 'TitleHeader', 'ArtistsHeader', 'AlbumHeader', 'TimeHeader', 'AddedHeader', 'StatusHeader', 'RemoveHeader') @(@('ContentsPlayerText', 'OpenPlayerFolderButton'), @('RefreshPlaylistButton', 'SyncSummaryText'))
                if ($width -eq 1200) { $cut = Truncated-Cells $window (Find-ById $window 'StatusHeader').Current.BoundingRectangle.Left; Check (-not $cut) "At 1200 px no status is cut off $($cut -join ', ')" }
                if ($width -eq 1900) { $cut = Truncated-Cells $window; Check (-not $cut) "At 1900 px no cell is cut off $($cut -join ', ')" }
                Save-Screenshot $window "playlist-$width"
            }
            Resize-Window 1200 900

            # Sync controls: every track starts selected except the one already on the player.
            $selectAll = Find-ById $window 'SelectAllCheckBox'
            $toggle = Pattern $selectAll ([System.Windows.Automation.TogglePattern])
            Wait-Until { $toggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::Indeterminate } 'all but one track selected by default' 15 | Out-Null
            Check $true 'Tracks not on the player are selected by default'
            $tracksList = Find-ById $window 'TracksListView'
            function Row-Texts($row) { @($row.FindAll($TreeScope::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) | ForEach-Object { $_.Current.Name } }
            function Row-Box($row) { Pattern ($row.FindFirst($TreeScope::Descendants, [System.Windows.Automation.PropertyCondition]::new($AE::ControlTypeProperty, $ControlType::CheckBox))) ([System.Windows.Automation.TogglePattern]) }
            $onPlayer = Find-ListItem $tracksList 'roadtrip song 2 — Artist 1'
            Check ((Row-Texts $onPlayer) -contains 'On player') "The track in the playlist's folder shows ""On player"""
            Check-Tones (Status-Tone $window $onPlayer 'On player') 'Success' '[STATUS-1] "On player" has a success icon'
            Check (@(Row-Texts $onPlayer | Where-Object { $_ -match '[\uE000-\uF8FF]' }).Count -eq 0) '[STATUS-1] Narrator never reads an icon glyph'
            Check ((Row-Box $onPlayer).Current.ToggleState -eq [System.Windows.Automation.ToggleState]::Off) '...and starts unselected'
            $elsewhere = Find-ListItem $tracksList 'roadtrip song 5 — Artist 4'
            Check ((Row-Box $elsewhere).Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) 'A track elsewhere on the player is still selected for the playlist'
            $playerOnly = Wait-Until { Find-ListItem $tracksList 'Removed song — Gone' } 'the player-only file in the list' 10
            Check ((Row-Texts $playerOnly) -contains 'Not in playlist') 'A file no longer in the playlist is listed as not in the playlist'
            $removeBox = Row-Box $playerOnly
            Check ($removeBox.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::Off) '...and is not marked for removal'
            Check ((Get-AllText $window) -like '*Sync to player*') "Sync button reads 'Sync to player'"
            Check ((Get-Text $window 'SyncSummaryText') -eq 'Adding 249 songs') "Summary: '$(Get-Text $window 'SyncSummaryText')'"
            $shown = Wait-Text $window 'ContentsPlayerText' "Player: Folder ($playerFolder)"
            Check ($shown -eq "Player: Folder ($playerFolder)") "The contents page names the player: '$shown'"
            # CONTENTS-1: heading with the Player line right-justified beside it and Open folder under that; the banner;
            # then the count on the left and Refresh, the summary and Sync to player on the right. (Open folder may share
            # the banner's line, which ALIGN-3 keeps where it is on the other pages.)
            $box = @{}
            foreach ($id in 'PlaylistTitle', 'ContentsPlayerText', 'OpenPlayerFolderButton', 'ContentsBannerTitle', 'SummaryText', 'RefreshPlaylistButton', 'SyncSummaryText', 'SyncButton') {
                $box[$id] = (Find-ById $window $id).Current.BoundingRectangle
            }
            $misplaced = @(
                if (!($box.ContentsPlayerText.Top -lt $box.PlaylistTitle.Bottom -and $box.ContentsPlayerText.Bottom -gt $box.PlaylistTitle.Top)) { 'the Player line is not beside the heading' }
                if ([Math]::Abs($box.ContentsPlayerText.Right - $box.SyncButton.Right) -gt 24) { "the Player line is not right-justified (right edge $($box.ContentsPlayerText.Right), Sync to player's $($box.SyncButton.Right))" }
                if ($box.OpenPlayerFolderButton.Top -lt $box.ContentsPlayerText.Bottom - 1) { 'Open folder is not under the Player line' }
                if ($box.ContentsBannerTitle.Top -lt $box.PlaylistTitle.Bottom - 1) { 'the banner is not under the heading' }
                if ($box.SummaryText.Top -lt [Math]::Max($box.ContentsBannerTitle.Bottom, $box.OpenPlayerFolderButton.Bottom) - 1) { 'the toolbar is not under the banner and Open folder' }
                if (!($box.SummaryText.Right -le $box.RefreshPlaylistButton.Left -and $box.RefreshPlaylistButton.Right -le $box.SyncSummaryText.Left -and $box.SyncSummaryText.Right -le $box.SyncButton.Left)) { 'the toolbar is not count, Refresh, summary, Sync to player' }
            )
            Check (-not $misplaced) "[CONTENTS-1] Contents page layout $($misplaced -join '; ')"
            # Refreshing keeps what the user ticked and unticked.
            (Row-Box $elsewhere).Toggle()
            $removeBox.Toggle()
            Click-Refresh $window '^/v1/playlists/roadtrip/tracks' 'the refresh'
            Wait-Contents $window 'Road Trip, Vol. 2 (refreshed with choices)' '250 tracks, *'
            $tracksList = Find-ById $window 'TracksListView'
            $elsewhere = Find-ListItem $tracksList 'roadtrip song 5 — Artist 4'
            $playerOnly = Wait-Until { Find-ListItem $tracksList 'Removed song — Gone' } 'the player-only file after refreshing' 10
            $removeBox = Row-Box $playerOnly
            Check ((Row-Box $elsewhere).Current.ToggleState -eq [System.Windows.Automation.ToggleState]::Off) 'A song the user unticked stays unticked after refreshing'
            Check ($removeBox.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) 'A file the user chose to remove stays chosen after refreshing'
            Check ((Row-Box (Find-ListItem $tracksList 'roadtrip song 3 — Artist 2')).Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) 'Songs the user left alone keep their defaults'
            (Row-Box $elsewhere).Toggle()
            $removeBox.Toggle()
            $toggle = Pattern (Find-ById $window 'SelectAllCheckBox') ([System.Windows.Automation.TogglePattern])
            Wait-Until { $toggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::Indeterminate } 'select all to be indeterminate again' 5 | Out-Null

            $syncButton = Find-ById $window 'SyncButton'
            Check $syncButton.Current.IsEnabled 'Sync button is enabled with tracks selected'
            $toggle.Toggle() # indeterminate -> unchecked
            Wait-Until { -not $syncButton.Current.IsEnabled } 'the Sync button to disable with nothing selected' 5 | Out-Null
            Check ($toggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::Off) 'Select all clears every track'

            # Removing the player-only file, and syncing nothing else
            Check ([string]::IsNullOrEmpty((Get-Text $window 'SyncSummaryText'))) 'No summary when nothing would change'
            $removeBox.Toggle()
            Wait-Until { $syncButton.Current.IsEnabled } 'the Sync button to enable for a removal' 5 | Out-Null
            Check ((Get-Text $window 'SyncSummaryText') -eq 'Removing 1 song') "Summary: '$(Get-Text $window 'SyncSummaryText')'"
            (Pattern $syncButton ([System.Windows.Automation.InvokePattern])).Invoke()
            Wait-Until { -not (Test-Path $removedFile) -and -not (Find-Named $tracksList $ControlType::ListItem 'Removed song — Gone') } 'the removed file to be deleted and leave the list' 20 | Out-Null
            Check $true 'Syncing deletes a player-only file marked for removal'

            $toggle.Toggle()
            Wait-Until { $syncButton.Current.IsEnabled } 'the Sync button to enable again' 5 | Out-Null
            Check $true 'Select all selects every track again'

            Click-Refresh $window '^/v1/playlists/roadtrip/tracks' 'the playlist refresh'
            Wait-Contents $window 'Road Trip, Vol. 2 (refreshed)' '250 tracks, 21:08:45'

            # Cancelling part way through the downloads leaves the player as it was.
            $filesBefore = @(Get-ChildItem $playerFolder -Recurse -File -Force | ForEach-Object FullName)
            $syncButton = Find-ById $window 'SyncButton'
            Wait-Until { $syncButton.Current.IsEnabled } 'the Sync button before cancelling' 15 | Out-Null
            (Pattern $syncButton ([System.Windows.Automation.InvokePattern])).Invoke()
            Wait-Until { (Get-AllText $window) -like '*Downloading*' } 'the downloads to start' 30 | Out-Null
            Check ((Get-AllText $window) -like '*Cancel sync*') 'The Sync button becomes Cancel sync'
            Check ((Get-Text $window 'SyncProgressText') -match '^Syncing \d+ of 249 songs$') "Progress: '$(Get-Text $window 'SyncProgressText')'"
            Check (-not (Find-ById $window 'BackButton').Current.IsEnabled) 'The playlist cannot be left while syncing'
            (Pattern $syncButton ([System.Windows.Automation.InvokePattern])).Invoke()
            Wait-Until { (Get-AllText $window) -like '*Sync to player*' } 'the sync to stop' 15 | Out-Null
            Check ((Get-AllText $window) -like '*Sync cancelled.*Nothing on the player changed.*') 'Cancelling says nothing on the player changed'
            $filesAfter = @(Get-ChildItem $playerFolder -Recurse -File -Force | ForEach-Object FullName)
            Check ($null -eq (Compare-Object $filesBefore $filesAfter)) "Cancelling during the downloads leaves the player's files as they were $((Compare-Object $filesBefore $filesAfter | ForEach-Object InputObject) -join ', ')"
            Check ((Get-AllText $window) -notmatch '(?m)^(Downloading|Waiting|Saving)') 'No song is left looking busy after cancelling'
            Wait-Until { (Get-Text $window 'SyncSummaryText') -eq 'Adding 249 songs' } "the summary after cancelling (now '$(Get-Text $window 'SyncSummaryText')')" 10 | Out-Null
            Check $true 'Every song is still to be added after cancelling'

            Check ($null -ne (Find-ById $window 'OpenPlayerFolderButton')) 'The playlist offers to open its folder on the player'
            Check ($null -eq (Find-ById $window 'ContentsChangePlayerButton')) 'The player is changed from the player page, not the playlist'
            Click $window 'BackButton'

            # Syncing finds every song first, has the user settle uncertain matches, then downloads.
            function Press($element) { (Pattern $element ([System.Windows.Automation.InvokePattern])).Invoke() }
            function In-Item($item, $type, [string] $name) { Wait-Until { Find-Named $item $type $name } "'$name'" 10 }
            function Item-Text($item) { (@($item.FindAll($TreeScope::Descendants, [System.Windows.Automation.PropertyCondition]::new($AE::ControlTypeProperty, $ControlType::Text))) | ForEach-Object { $_.Current.Name }) -join "`n" }
            Open-LibraryItem $window 'Café ☕ "Quotes"'
            Wait-Contents $window 'Café' '3 tracks, *'
            $syncButton = Find-ById $window 'SyncButton'
            Wait-Until { $syncButton.Current.IsEnabled } 'the Sync button for Café' 15 | Out-Null
            Check ((Get-Text $window 'SyncSummaryText') -eq 'Adding 3 songs') "Café summary: '$(Get-Text $window 'SyncSummaryText')'"
            $cafeList = Find-ById $window 'TracksListView'
            $cafe1, $cafe2, $cafe3 = 'cafe song 1 — Earth, Wind & Fire, Chaka Khan', 'cafe song 2 — Artist 1', 'cafe song 3 — Artist 2'
            Wait-Until {
                (Row-Texts (Find-ListItem $cafeList $cafe1)) -contains 'Source found' -and
                (Row-Texts (Find-ListItem $cafeList $cafe2)) -contains 'Needs review' -and
                (Row-Texts (Find-ListItem $cafeList $cafe3)) -contains "Couldn't search"
            } 'the songs to be looked for before syncing' 15 | Out-Null
            Check $true 'Songs are looked for as soon as the playlist opens'
            $tones = (Status-Tone $window (Find-ListItem $cafeList $cafe1) 'Source found'), (Status-Tone $window (Find-ListItem $cafeList $cafe2) 'Needs review'), (Status-Tone $window (Find-ListItem $cafeList $cafe3) "Couldn't search")
            Check-Tones $tones 'None', 'Caution', 'Critical' "[STATUS-1] Source found has no icon, Needs review a caution icon, Couldn't search a critical icon"
            Save-Screenshot $window 'status-icons'
            Check ((Get-Text $window 'ReviewMatchesButton') -eq 'Review 2 songs') "Review button: '$(Get-Text $window 'ReviewMatchesButton')'"
            Click $window 'ReviewMatchesButton'
            $review = Wait-Until { Find-ById $window 'ReviewListView' } 'the review before syncing' 15
            Check ((Get-Text $window 'ReviewTitle') -eq 'Choose a match for 2 songs') "Review title: '$(Get-Text $window 'ReviewTitle')'"
            Check (-not (Find-ById $window 'ReviewContinueButton').Current.IsEnabled) 'Continue waits until every song is settled'
            Check ($null -eq (Find-ById $window 'TracksListView') -and $null -eq (Find-ById $window 'SyncButton')) 'The track list makes way for the review'

            $song2 = Wait-Until { Find-ListItem $review 'cafe song 2 — Artist 1' } 'cafe song 2 under review' 10
            Check ((Item-Text $song2) -like '*Only Opus audio is available.*') 'A result without usable audio says why'
            Check (-not (In-Item $song2 $ControlType::Button 'Use cafe song 2 (Remix) for cafe song 2').Current.IsEnabled) '...and cannot be used'
            Check ($null -ne (In-Item $song2 $ControlType::Hyperlink 'Open cafe song 2 (Live) on YouTube Music')) 'Each result links to YouTube Music'
            (Pattern (In-Item $song2 $ControlType::Edit 'Link to cafe song 2') ([System.Windows.Automation.ValuePattern])).SetValue('https://example.com/song')
            Press (In-Item $song2 $ControlType::Button 'Use the link for cafe song 2')
            Wait-Until { (Item-Text $song2) -like "*isn't a YouTube or YouTube Music link*" } 'the bad link error' 10 | Out-Null
            Check $true 'A link that is not to YouTube is rejected with a message'
            Press (In-Item $song2 $ControlType::Button 'Use cafe song 2 (Live) for cafe song 2')
            Wait-Until { (Item-Text $song2) -like '*Using cafe song 2*' } 'cafe song 2 to be settled' 10 | Out-Null
            Check $true 'Choosing a result settles the song'

            # Backing out keeps the choice; syncing then only asks about what's still unsettled.
            Click $window 'ReviewCancelButton'
            $cafeList = Wait-Until { Find-ById $window 'TracksListView' } 'the track list after the review' 10
            Wait-Until { (Row-Texts (Find-ListItem $cafeList $cafe2)) -contains 'Source found' } 'the chosen match in the list' 10 | Out-Null
            Check ((Get-Text $window 'ReviewMatchesButton') -eq 'Review 1 song') "A choice made before backing out is kept: '$(Get-Text $window 'ReviewMatchesButton')'"
            Press (Find-ById $window 'SyncButton')
            $review = Wait-Until { Find-ById $window 'ReviewListView' } 'the review step when syncing' 15
            Check ((Get-Text $window 'ReviewTitle') -eq 'Choose a match for 1 song') "Syncing only asks about the unsettled song: '$(Get-Text $window 'ReviewTitle')'"

            $song3 = Wait-Until { Find-ListItem $review 'cafe song 3 — Artist 2' } 'cafe song 3 under review' 10
            Check ((Item-Text $song3) -like "*Couldn't search YouTube Music*") 'A failed search says so'
            Check ($null -ne (In-Item $song3 $ControlType::Button 'Search again for cafe song 3')) '...and offers to search again'
            Press (In-Item $song3 $ControlType::Button 'Skip cafe song 3')
            Wait-Until { (Get-Text $window 'ReviewTitle') -eq 'Ready to sync' } 'every song to be settled' 10 | Out-Null
            $continue = Find-ById $window 'ReviewContinueButton'
            Wait-Until { $continue.Current.IsEnabled } 'Continue to enable' 5 | Out-Null
            Press $continue

            # cafe song 1 matched automatically, but its video has gone by the time it downloads.
            Wait-Until { (Get-Text $window 'ReviewTitle') -eq 'Choose a match for 1 song' } 'the vanished video to come back for review' 20 | Out-Null
            $review = Find-ById $window 'ReviewListView'
            $song1 = Wait-Until { Find-ListItem $review 'cafe song 1 — Earth, Wind & Fire, Chaka Khan' } 'cafe song 1 under review' 10
            Check $true 'A song whose video has gone goes back for review'
            Press (In-Item $song1 $ControlType::Button 'Use cafe song 1 (Live) for cafe song 1')
            $continue = Find-ById $window 'ReviewContinueButton'
            Wait-Until { $continue.Current.IsEnabled } 'Continue to enable again' 10 | Out-Null
            Press $continue

            $tracksList = Wait-Until { Find-ById $window 'TracksListView' } 'the track list after syncing' 20
            Wait-Until { (Row-Texts (Find-ListItem $tracksList 'cafe song 1 — Earth, Wind & Fire, Chaka Khan')) -contains 'On player' -and (Row-Texts (Find-ListItem $tracksList 'cafe song 2 — Artist 1')) -contains 'On player' } 'both songs on the player' 30 | Out-Null
            $cafeFiles = @(Get-ChildItem $playerFolder -Recurse -Filter *.m4a | Where-Object { $_.DirectoryName -like '*Caf*' })
            Check ($cafeFiles.Count -eq 2) "Both settled songs were copied into the playlist's folder ($($cafeFiles.Name -join ', '))"
            # Tagged on the way (the fake audio has none of its own), as read by Windows itself
            $shell = New-Object -ComObject Shell.Application
            $tags = foreach ($file in $cafeFiles) {
                $item = $shell.Namespace($file.DirectoryName).ParseName($file.Name)
                [pscustomobject]@{ Title = $item.ExtendedProperty('System.Title'); Artist = @($item.ExtendedProperty('System.Music.Artist')) -join '; ' }
            }
            Check ($cafeFiles.Count -eq 2 -and @($tags | Where-Object { $_.Title -like 'cafe song *' -and $_.Artist }).Count -eq 2) "The copied songs are tagged ($(($tags | ForEach-Object { "$($_.Title) by $($_.Artist)" }) -join ', '))"
            $result = (Get-AllText $window) -split "`n" | Where-Object { $_ -match 'Synced|Sync cancelled|Updated|Nothing was synced|skipped|couldn''t' }
        Check (($result -join ' ') -like "*Synced 2 songs to Folder (*)*1 song skipped.*") "The sync says what it did: '$($result -join ' | ')'"
            $skippedRow = Find-ListItem $tracksList 'cafe song 3 — Artist 2'
            Check ((Row-Texts $skippedRow) -contains 'Skipped' -and (Row-Box $skippedRow).Current.ToggleState -eq [System.Windows.Automation.ToggleState]::Off) 'The skipped song shows as skipped and unselected'
            Check-Tones (Status-Tone $window $skippedRow 'Skipped') 'Neutral' '[STATUS-1] Skipped has a neutral icon'
            Press (In-Item $skippedRow $ControlType::Hyperlink 'Undo skipping cafe song 3')
            Wait-Until { (Row-Box $skippedRow).Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On } 'the undone skip to be selected again' 5 | Out-Null
            Check ((Row-Texts $skippedRow) -notcontains 'Skipped') 'Undoing a skip selects the song again'

            # Deleting a synced song's file on the player is noticed, like any other change on the player.
            $synced = Find-ListItem $tracksList 'cafe song 2 — Artist 1'
            $syncedFile = $cafeFiles | Where-Object Name -like '*cafe song 2*' | Select-Object -First 1
            Remove-Item -LiteralPath $syncedFile.FullName
            Wait-Until { (Row-Texts $synced) -notcontains 'On player' -and (Row-Box $synced).Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On } 'the deleted song to be back to adding' 20 | Out-Null
            Check $true 'A synced song deleted from the player goes back to being added'
            Save-Screenshot $window 'synced'
            Click $window 'BackButton'
            Select-Filter $window 'Saved playlists'
            Wait-Library $window '19 playlists' ([ref]$sawSignIn)

            Select-Filter $window 'Saved albums'
            Wait-Library $window '5 albums' ([ref]$sawSignIn)
            $albumNames = (Get-Items (Find-ById $window 'LibraryGridView')) | ForEach-Object { $_.Current.Name }
            Check (($albumNames -join '|') -eq 'Playlist 5|Playlist 14|Mock Album One|Big Album|Old Album') "Saved albums: album-like playlists, then saved albums newest first ($($albumNames -join ', '))"
            Save-Screenshot $window 'saved-albums'
            Open-LibraryItem $window 'Big Album'
            Wait-Contents $window 'Big Album' 'Prolific Band · 2010 — 60 tracks, 3:29:30'
            Check ($null -eq (Find-ById $window 'AlbumHeader') -and $null -ne (Find-ById $window 'ArtistsHeader')) 'Album pages leave out the Album column'
            # The summary already matches, so wait until the refresh has asked Spotify (the Saved Albums export's lookup).
            Click-Refresh $window '^/v1/tracks\?' 'the album refresh'
            Wait-Contents $window 'Big Album (refreshed)' '*60 tracks, *'
            Click $window 'BackButton'

            # Playlist 5's cover looks like an album, but its export shows four albums.
            Open-LibraryItem $window 'Playlist 5'
            Wait-Contents $window 'Playlist 5' '6 tracks, *'
            Click $window 'BackButton'
            Wait-Library $window '4 albums' ([ref]$sawSignIn)
            Check $true 'Opening Playlist 5 moved it out of Saved albums'
            Open-LibraryItem $window 'Playlist 14'
            Wait-Contents $window 'Playlist 14' '1 track, *'
            Click $window 'BackButton'
            Wait-Library $window '4 albums' ([ref]$sawSignIn)
            Check $true 'Playlist 14, a single album, stays in Saved albums'
            Select-Filter $window 'Created playlists'
            Wait-Library $window '41 playlists' ([ref]$sawSignIn)

            Select-Filter $window 'All'
            Wait-Library $window '64 playlists and albums' ([ref]$sawSignIn)
            Open-LibraryItem $window 'Broken'
            Wait-Until { (Get-AllText $window) -like '*Could not load*Broken*' } 'the failure for Broken' 30 | Out-Null
            Check $true 'Failed export is reported'
            Check ((Get-AllText $window) -notmatch '(?i)browser|exportify') "[TEXT-1] The failure is in the app's words: '$(((Get-AllText $window) -split "`n" | Select-String -Pattern 'Could not load' -Context 0,1) -replace '\s+', ' ')'"
            Check ($null -ne (Find-ById $window 'TryAgainButton') -and $null -eq (Find-ById $window 'SyncButton') -and [string]::IsNullOrEmpty((Get-Text $window 'SyncSummaryText'))) 'A playlist that did not load offers Try again, and nothing to sync'
            Click $window 'BackButton'
            Open-LibraryItem $window 'Liked Songs'
            Wait-Contents $window 'Liked Songs' '7 tracks, *'
            Click $window 'BackButton'
            Open-LibraryItem $window 'Playlist 60'
            Wait-Contents $window 'Playlist 60' '5 tracks, *'
            Click $window 'BackButton'

            Open-LibraryItem $window 'Road Trip, Vol. 2'
            Wait-Contents $window 'Road Trip, Vol. 2' '250 tracks, *'
        } finally {
            Stop-App $process
            Invoke-RestMethod "$($url)sign-in-hold" -Method Post -Body 'release' | Out-Null
        }
        $lastOpened = 'Road Trip, Vol. 2'
    }

    if (Test-Selected 'Run2') {
        Prepare-State 'Road Trip, Vol. 2'
        Write-Host 'Run 2: restart while signed in'
        $process, $window = Start-App $url
        try {
            # Only one copy per data folder: launching again hands over to the running copy and exits.
            $second = Start-Process $AppPath -PassThru
            $script:appProcesses.Add($second)
            Check ($second.WaitForExit(30000) -and $second.ExitCode -eq 0) "A second launch on the same data folder exits by itself (exit code $(if ($second.HasExited) { $second.ExitCode } else { 'none, still running' }))"
            Check (!$process.HasExited -and $null -ne $AE::RootElement.FindFirst($TreeScope::Children, [System.Windows.Automation.PropertyCondition]::new($AE::ProcessIdProperty, $process.Id))) '...and the first copy stays open'
            # A different data folder, like another test run's, is a separate copy.
            $otherData = "$env:MP3PLAYERSYNC_DATA_DIR-other"
            $env:MP3PLAYERSYNC_DATA_DIR, $sharedData = $otherData, $env:MP3PLAYERSYNC_DATA_DIR
            try { $other, $otherWindow = Start-App $url } finally { $env:MP3PLAYERSYNC_DATA_DIR = $sharedData }
            $script:currentWindow = $window
            Check ($null -ne $otherWindow -and !$other.HasExited) 'A launch on another data folder runs alongside'
            Stop-App $other
            Remove-Item $otherData -Recurse -Force -ErrorAction SilentlyContinue

            Choose-Player $window
            $sawSignIn = $false
            $signInShown = [ref]$false
            Wait-Until {
                if (Find-ById $window 'SignInTitle') { $signInShown.Value = $true }
                (Get-Text $window 'PlaylistTitle') -eq 'Spotify playlist: Road Trip, Vol. 2'
            } 'the last opened playlist to reopen' 60 | Out-Null
            Wait-Contents $window 'Road Trip, Vol. 2 (restored)' '250 tracks, *'
            Check (!$signInShown.Value) 'Sign-in screen is never shown when already signed in'
            Click $window 'BackButton'
            Wait-Library $window '64 playlists and albums' ([ref]$sawSignIn)
            Check $true 'Selector choice (All) was remembered'
            $shown = Wait-Text $window 'SelectedPlayerTitle' "Player: Folder ($playerFolder)"
            Check ($shown -eq "Player: Folder ($playerFolder)") "Library shows the chosen folder: '$shown'"
            Check ((Get-Text $window 'LibraryTitle') -eq 'Select a playlist to sync') "Library banner: '$(Get-Text $window 'LibraryTitle')'"
            Click $window 'ChangePlayerButton'
            Choose-Player $window
            Wait-Library $window '64 playlists and albums' ([ref]$sawSignIn)
            Check $true 'Changing the MP3 player returns to the library'

            # "Other folder" opens Windows' folder dialog in the player's folder, where Select Folder picks that folder.
            Click $window 'ChangePlayerButton'
            $grid = Wait-Until { Find-ById $window 'PlayerGridView' } 'the MP3 player page'
            $hwnd = $window.Current.NativeWindowHandle
            # The dialog is modal, so invoke the tile from another thread in case Invoke waits for it. The app's window
            # is passed by its handle: the process can have other top-level windows too (see Start-App).
            $invoke = Start-ThreadJob {
                Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
                $AE = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]
                $app = $AE::FromHandle([IntPtr]$using:hwnd)
                $tile = $app.FindFirst($TS::Descendants, [System.Windows.Automation.AndCondition]::new(
                    [System.Windows.Automation.PropertyCondition]::new($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem),
                    [System.Windows.Automation.PropertyCondition]::new($AE::NameProperty, 'Other folder')))
                $tile.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            }
            # Found through Win32: UI Automation searches from the root can time out while the app waits on the dialog.
            # The thread's errors would otherwise go unseen, leaving only this timeout.
            $dialog = try { Wait-Until { $h = [E2EWin32]::ProcessWindow([uint32]$process.Id, '#32770'); if ($h -ne [IntPtr]::Zero) { $AE::FromHandle($h) } } 'the folder dialog' 20 }
                catch { Receive-Job $invoke *>&1 | ForEach-Object { Write-Host "  Invoking 'Other folder': $_" }; throw }
            # On the test desktop the dialog's Win32 buttons appear as panes without InvokePattern, so the raw view is searched.
            $selectFolder = Wait-Until { Find-Raw $dialog { $_.ClassName -eq 'Button' -and $_.Name -eq 'Select Folder' } } "the dialog's Select Folder button" 10
            $address = Wait-Until { (Find-Raw $dialog { $_.Name -like 'Address: *' })?.Current.Name } "the dialog's address bar" 10
            Check ($address -eq "Address: $playerFolder") "[PLAYER-6] The folder dialog opens in the player's folder: '$address'"
            $items = Wait-Until { Find-Raw $dialog { $_.Name -eq 'Items View' } } "the dialog's folder contents" 10
            Check ((Pattern $items ([System.Windows.Automation.SelectionPattern])).Current.GetSelection().Count -eq 0) '[PLAYER-6] Nothing is selected in the folder dialog'
            Check $selectFolder.Current.IsEnabled '[PLAYER-6] Select Folder is enabled with nothing selected'
            [E2EWin32]::PostMessage([IntPtr]$selectFolder.Current.NativeWindowHandle, [E2EWin32]::BM_CLICK, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
            Wait-Library $window '64 playlists and albums' ([ref]$sawSignIn)
            $invoke | Wait-Job -Timeout 10 | Remove-Job -Force
            $settings = Get-Content (Join-Path $env:MP3PLAYERSYNC_DATA_DIR 'settings.json') -Raw | ConvertFrom-Json
            $shown = Wait-Text $window 'SelectedPlayerTitle' "Player: Folder ($playerFolder)"
            Check ($settings.lastPickedFolder -eq $playerFolder -and $shown -eq "Player: Folder ($playerFolder)") "[PLAYER-6] Select Folder picked the folder shown: '$shown', last picked '$($settings.lastPickedFolder)'"
            Open-LibraryItem $window 'Big Album'
            Wait-Contents $window 'Big Album' '*60 tracks, *'
        } finally { Stop-App $process }
        $lastOpened = 'Big Album'
    }

    if (Test-Selected 'Run2b') {
        Prepare-State 'Big Album'
        Write-Host 'Run 2b: the token runs out while the app is open'
        $process, $window = Start-App "$($url)?expireAfter=20"
        try {
            Choose-Player $window
            Wait-Until { (Get-Text $window 'PlaylistTitle') -eq 'Spotify album: Big Album' } 'the last opened album to reopen' 60 | Out-Null
            Wait-Contents $window 'Big Album (restored)' '*60 tracks, *'
            # The token runs out 20 s after the page loaded; the fake API then rejects it, so only a renewed one works.
            $interrupted = @()
            $stopwatch = [Diagnostics.Stopwatch]::StartNew()
            while ($stopwatch.Elapsed.TotalSeconds -lt 30) {
                Watch-Page
                if (Find-ById $window 'SignInTitle') { $interrupted += 'sign-in' }
                if (Find-ById $window 'LoadingText') { $interrupted += 'loading' }
                $title = Get-Text $window 'PlaylistTitle'
                if ($title -ne 'Spotify album: Big Album') { $interrupted += "left the album ('$title')" }
                Start-Sleep -Milliseconds 200
            }
            Check (-not $interrupted) "The album stays on screen while the token is renewed $(($interrupted | Select-Object -Unique) -join ', ')"
            Click-Refresh $window '^/v1/tracks\?' 'the refresh'
            Wait-Contents $window 'Big Album (after renewal)' '*60 tracks, *'
            Check ((Get-AllText $window) -notlike '*Could not load*') 'Exports work with the renewed token'
        } finally { Stop-App $process }
        $lastOpened = 'Big Album'
    }

    if (Test-Selected 'Run2c') {
        Prepare-State 'Big Album'
        Write-Host 'Run 2c: Spotify stops accepting the token during an export'
        $process, $window = Start-App "$($url)?expireOnExport"
        try {
            Choose-Player $window
            $signInShown = [ref]$false
            try {
                Wait-Until {
                    if (Find-ById $window 'SignInTitle') { $signInShown.Value = $true }
                    (Get-Text $window 'PlaylistTitle') -eq 'Spotify album: Big Album' -and (Get-Text $window 'SummaryText') -like '*60 tracks*'
                } 'the album after renewing the token mid-export' 90 | Out-Null
            } catch { Write-Host "  Showing: $((Get-AllText $window) -replace "`n", ' | ')"; throw }
            Check (!$signInShown.Value) 'A token rejected mid-export is renewed without showing the sign-in screen'
            Check ((Get-AllText $window) -notlike '*Could not load*') 'The interrupted export is retried after renewing'
        } finally { Stop-App $process }
        $lastOpened = 'Big Album'
    }

    if (Test-Selected 'Run3', 'Run4') {
        Prepare-State 'Big Album'
        Write-Host 'Run 3: expired Exportify token, last opened a saved album'
        $process, $window = Start-App "$($url)?expireToken"
        try {
            Choose-Player $window
            $signInShown = [ref]$false
            Wait-Until {
                if (Find-ById $window 'SignInTitle') { $signInShown.Value = $true }
                (Get-Text $window 'PlaylistTitle') -eq 'Spotify album: Big Album' -and (Get-Text $window 'SummaryText') -like '*60 tracks*'
            } 'the saved album after renewing the session' 90 | Out-Null
            Check (!$signInShown.Value) 'Expired token is renewed without showing the sign-in screen'
            Check $true 'The last opened saved album reopened'

            Write-Host 'Run 4: sign out'
            Click $window 'BackButton'
            $loginLoads = Get-ApiCalls '^fake-login loaded$'
            Click $window 'SignOutButton'
            Wait-Until { Find-ById $window 'SignInTitle' } 'the sign-in screen after signing out' 30 | Out-Null
            Check $true 'Signing out shows the sign-in screen'

            # Links to elsewhere open outside the app; the sign-in page stays, and signing in still works.
            # The page reports loading to the mock, which tells a page that never loaded from one UI Automation can't see.
            Wait-Until { (Get-ApiCalls '^fake-login loaded$') -gt $loginLoads } "Spotify's sign-in page to load" 60 | Out-Null
            $page = try { Wait-Until { Find-WebDocument $window 'Spotify login (mock)' } "Spotify's sign-in page in UI Automation" 60 }
                catch { Write-Host "  The page loaded; the sign-in button instead of the page is $(if (Find-ById $window 'SignInPromptButton') { '' } else { 'not ' })showing"; throw }
            $opened = Join-Path $env:MP3PLAYERSYNC_DATA_DIR 'OpenedExternally.txt'
            foreach ($link in @(@('Sign up for Spotify', 'https://www.spotify.com/signup'), @('Help', 'https://support.spotify.com/'))) {
                (Pattern (Wait-Until { Find-Named $page $ControlType::Hyperlink $link[0] } "the '$($link[0])' link" 10) ([System.Windows.Automation.InvokePattern])).Invoke()
                Wait-Until { (Test-Path $opened) -and (Get-Content $opened) -contains $link[1] } "'$($link[0])' to open outside the app" 10 | Out-Null
                # Asks the page found above, whose name is its title: it changes if the WebView goes elsewhere. Looking
                # it up again through the window doesn't work once the click has put focus inside the WebView.
                $stayed = $true
                try {
                    Wait-Until {
                        $title = try { $page.Current.Name } catch [System.Windows.Automation.ElementNotAvailableException] { $null }
                        $title -eq 'Spotify login (mock)' -and (Find-ById $window 'SignInTitle')
                    } "the sign-in page after clicking '$($link[0])'" 10 | Out-Null
                } catch {
                    $stayed = $false
                    Write-Host "  $($_.Exception.Message)"
                }
                Check $stayed "Clicking '$($link[0])' opens it outside the app and stays on the sign-in page"
            }
            (Pattern (Find-Named $page $ControlType::Button 'Sign in') ([System.Windows.Automation.InvokePattern])).Invoke()
            $sawSignIn = $true
            Wait-Library $window '*playlists*' ([ref]$sawSignIn)
            Check ($null -eq (Find-ById $window 'SignInTitle')) 'Signing in on the page returns to the library'
        } finally { Stop-App $process }
        $lastOpened = $null
    }

    if (Test-Selected 'Run5') {
        Prepare-State 'Playlist 5'
        Write-Host 'Run 5: player drives with a MUSIC folder'
        # Drives listed as removable, each backed by a folder. Both have an empty MUSIC folder (in its own casing) and a
        # file of the user's; the old card also has a playlist folder the app synced to its root before.
        $newCard = Join-Path $env:MP3PLAYERSYNC_DATA_DIR 'E2E Walkman'
        $oldCard = Join-Path $env:MP3PLAYERSYNC_DATA_DIR 'E2E Old Card'
        foreach ($card in $newCard, $oldCard) { New-Item -ItemType Directory -Force (Join-Path $card $(if ($card -eq $newCard) { 'Music' } else { 'MUSIC' })) | Out-Null; Set-Content (Join-Path $card 'Notes.txt') 'mine' }
        New-Item -ItemType Directory -Force (Join-Path $oldCard 'Road Trip') | Out-Null
        Set-Content (Join-Path $oldCard 'Road Trip\.mp3playersync-playlist.json') '{}'
        $env:MP3PLAYERSYNC_TEST_DRIVES = ConvertTo-Json -Compress @(
            @{ root = 'W:\'; label = 'E2E WALKMAN'; bus = 'Usb'; totalBytes = 16GB; freeBytes = 8GB; folder = $newCard },
            @{ root = 'X:\'; label = 'E2E OLD CARD'; bus = 'SecureDigital'; totalBytes = 16GB; freeBytes = 8GB; folder = $oldCard })

        # Chooses the drive, opens Playlist 5 (six songs, and a playlist, unlike one-song playlists) and syncs it, then returns where its song went, relative to the card.
        function Sync-To([string] $label, [string] $card) {
            $process, $window = Start-App $url
            try {
                $grid = Wait-Until { Find-ById $window 'PlayerGridView' } 'the MP3 player page'
                $drive = Wait-Until { Find-ListItem $grid $label } "the $label drive" 10
                (Pattern $drive ([System.Windows.Automation.InvokePattern])).Invoke()
                # A newly chosen player may show the library rather than reopen the last playlist.
                Wait-Until { (Get-Text $window 'PlaylistTitle') -eq 'Spotify playlist: Playlist 5' -or (Find-ById $window 'LibraryGridView') } 'the library or the last opened playlist' 60 | Out-Null
                if ((Get-Text $window 'PlaylistTitle') -ne 'Spotify playlist: Playlist 5') {
                    Select-Filter $window 'All'
                    Open-LibraryItem $window 'Playlist 5'
                }
                Wait-Contents $window "Playlist 5 on $label" '6 tracks, *'
                $syncButton = Find-ById $window 'SyncButton'
                Wait-Until { $syncButton.Current.IsEnabled } 'the Sync button' 15 | Out-Null
                (Pattern $syncButton ([System.Windows.Automation.InvokePattern])).Invoke()
                $synced = Wait-Until { $s = @(Get-ChildItem $card -Recurse -Filter *.m4a -ErrorAction SilentlyContinue); if ($s.Count -eq 6) { $s } } "the six songs on $label" 60
                Wait-Until { (Get-AllText $window) -match '(?m)^On player$' } 'the synced song to show as on the player' 30 | Out-Null
                # The sync goes on after the last file is in place (the playlist folder's marker, then the result), and
                # closing the window before then asks "Stop syncing and close?" (SYNC-10).
                Wait-Until { (Get-AllText $window) -match 'Synced 6 songs to' } "the sync to $label to finish" 30 | Out-Null
                Check $true "[PLAYER-7] The scan reads where $label was synced: the synced song is on the player"
                return @($synced | ForEach-Object { [IO.Path]::GetRelativePath($card, $_.FullName) })
            } finally { Stop-App $process }
        }

        try {
            $synced = Sync-To 'E2E WALKMAN' $newCard
            Check (@($synced | Where-Object { $_ -notlike 'Music\Playlist 5\*.m4a' }).Count -eq 0) "[PLAYER-7] A card with a MUSIC folder and nothing from the app is synced into it: $($synced -join ', ')"
            Check ((@(Get-ChildItem $newCard -Force | ForEach-Object Name) -join ', ') -eq 'Music, Notes.txt') "[PLAYER-7] Nothing is added at that card's root: $(@(Get-ChildItem $newCard -Force | ForEach-Object Name) -join ', ')"

            $synced = Sync-To 'E2E OLD CARD' $oldCard
            Check (@($synced | Where-Object { $_ -notlike 'Playlist 5\*.m4a' }).Count -eq 0) "[PLAYER-7] A card the app synced to at its root stays at its root, despite its empty MUSIC folder: $($synced -join ', ')"
            Check (@(Get-ChildItem (Join-Path $oldCard 'MUSIC') -Force).Count -eq 0) '[PLAYER-7] ...and its MUSIC folder is left empty'

            $settings = Get-Content (Join-Path $env:MP3PLAYERSYNC_DATA_DIR 'settings.json') -Raw
            Check ($settings -notmatch '"musicFolders"') '[PLAYER-7] Where to sync is decided from the card, not remembered'
        } finally {
            Remove-Item Env:\MP3PLAYERSYNC_TEST_DRIVES -ErrorAction SilentlyContinue
        }
        $lastOpened = $null
    }
    $newDownloads = Get-ChildItem $downloads -File -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -gt $startTime }
    Check (@($newDownloads).Count -eq 0) "Nothing was written to $downloads"
    Check (!$script:sawExportifyPage) "Exportify's own page was never shown"
}
catch {
    $failures.Add($_.ToString())
    Write-Host "  ERROR $_ $($_.ScriptStackTrace)" -ForegroundColor Red
}
finally {
    if ($server) {
        Stop-Job $server
        # Requests the mock couldn't answer, which can explain a failure that shows up far from them
        Receive-Job $server *>&1 | Where-Object { "$_" -like 'Mock: *' } | ForEach-Object { Write-Host "  $_" }
        Remove-Job $server -Force
    }
    Remove-Item Env:\MP3PLAYERSYNC_EXPORTIFY_URL -ErrorAction SilentlyContinue
    Remove-Item Env:\MP3PLAYERSYNC_SPOTIFY_ACCOUNTS_URL -ErrorAction SilentlyContinue
    Remove-Item Env:\MP3PLAYERSYNC_TEST_FAKE_AUDIO -ErrorAction SilentlyContinue
    foreach ($process in $script:appProcesses) { if (!$process.HasExited) { $process.Kill() } }
    Remove-Item $env:MP3PLAYERSYNC_DATA_DIR -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item Env:\MP3PLAYERSYNC_DATA_DIR -ErrorAction SilentlyContinue
}

if ($failures.Count) { Write-Host "$($failures.Count) failure(s)$(if ($inconclusive.Count) { ", and $($inconclusive.Count) inconclusive check(s)" })" -ForegroundColor Red; exit 1 }
if ($inconclusive.Count) {
    Write-Host "No failures, but $($inconclusive.Count) check(s) were inconclusive, neither passed nor failed:" -ForegroundColor Yellow
    $inconclusive | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
    exit 2
}
Write-Host 'All end-to-end checks passed' -ForegroundColor Green
