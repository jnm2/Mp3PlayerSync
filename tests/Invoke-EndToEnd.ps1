<#
.SYNOPSIS
    Runs Test-MockEndToEnd.ps1, or another script, kept apart from whatever the user is doing.

.DESCRIPTION
    Each run has its own data folder, mock port and processes, so runs can't disturb each other, and everything it
    started is cleaned up afterwards, including on failure.

    By default (-Desktop Hidden) it runs on a separate, hidden desktop on the interactive window station (the script,
    the mock server and the app all live on that desktop, which UI Automation requires), so the app under test can
    never take focus from, or appear over, the user's windows; meanwhile it logs the user's foreground window to
    confirm nothing changes it. -Desktop Current runs on this session's own desktop instead, where the app can cover
    and take focus from whatever else is there: for CI, on a throwaway VM with nobody to disturb, or for someone who
    asks for it on purpose. Agents never use it on the user's PC.
#>
param(
    [string] $ScreenshotFolder,
    # The test host to run, such as the trimmed one from Test-PublishedExe.ps1; otherwise the Debug build
    [string] $AppPath,
    # Just these runs of Test-MockEndToEnd.ps1, for example Run2b,Run2c (see its -Only)
    [string[]] $Only,
    # Another script to run there instead, such as a quick experiment
    [string] $TestScript = (Join-Path $PSScriptRoot 'Test-MockEndToEnd.ps1'),
    # What the script prints when everything went well
    [string] $PassedText = 'All end-to-end checks passed',
    [int] $TimeoutMinutes = 15,
    # Hidden: a desktop of its own. Current: this session's desktop, for CI or when asked for on purpose (see above);
    # the script is told with -Desktop Current.
    [ValidateSet('Hidden', 'Current')]
    [string] $Desktop = 'Hidden'
)

$ErrorActionPreference = 'Stop'
$hidden = $Desktop -eq 'Hidden'

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class TestDesktop
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr devmode, int flags, uint access, IntPtr security);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CreateProcessW(string application, string commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] public static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    public const uint GENERIC_ALL = 0x10000000;
    public const uint CREATE_NO_WINDOW = 0x08000000;
}
'@

# Runs go in parallel, each on its own desktop with its own data folder, mock port and processes, but only so many
# at once, or they starve each other of CPU and time out. A run takes one of a few slots, each a machine-wide mutex:
# a run that dies holding one leaves it abandoned, which is fine to take, so no slot is ever lost. Waiting here
# doesn't count towards the timeout. Each run notes itself in a queue folder so the oldest waiters go first and a
# waiting run can say what it's waiting for, instead of looking hung.
$slotCount = [Math]::Max(2, [int][Math]::Floor([Environment]::ProcessorCount / 4))
$queue = Join-Path ([IO.Path]::GetTempPath()) 'Mp3PlayerSyncEndToEndQueue'
New-Item -ItemType Directory -Force $queue | Out-Null
$entry = Join-Path $queue "$PID.json"
$me = @{ pid = $PID; since = (Get-Date).ToString('o'); from = (Resolve-Path "$PSScriptRoot\..").Path; only = $Only -join ','; running = $false }
$me | ConvertTo-Json -Compress | Set-Content $entry

function Get-QueueEntries {
    foreach ($file in Get-ChildItem $queue -Filter *.json -ErrorAction SilentlyContinue) {
        $other = try { Get-Content $file.FullName -Raw | ConvertFrom-Json } catch { $null }
        $process = if ($other) { Get-Process -Id $other.pid -ErrorAction SilentlyContinue }
        # Gone, or the ID now belongs to another process: a run that ended without cleaning up.
        if (!$process -or $process.StartTime -gt [DateTime]$other.since) { Remove-Item $file.FullName -ErrorAction SilentlyContinue; continue }
        $other
    }
}

function Enter-Slot {
    for ($i = 0; $i -lt $slotCount; $i++) {
        $slot = [System.Threading.Mutex]::new($false, "Global\Mp3PlayerSyncEndToEndSlot-$i")
        $taken = try { $slot.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $true }
        if ($taken) { return $slot }
        $slot.Dispose()
    }
}

$waitStarted = Get-Date
$lastReport = $null
$lock = $null
while (!$lock) {
    $entries = @(Get-QueueEntries)
    $ahead = @($entries | Where-Object { !$_.running -and $_.pid -ne $PID -and [DateTime]$_.since -lt [DateTime]$me.since }).Count
    # Only the longest-waiting runs try, so a later run can't take a slot first.
    if ($ahead -lt $slotCount) { $lock = Enter-Slot }
    if ($lock) { break }
    if (!$lastReport -or ((Get-Date) - $lastReport).TotalSeconds -ge 30) {
        $lastReport = Get-Date
        $running = @($entries | Where-Object running | ForEach-Object { "$(Split-Path $_.from -Leaf)$(if ($_.only) { " ($($_.only))" }) since $(([DateTime]$_.since).ToString('HH:mm:ss'))" })
        Write-Host "$((Get-Date).ToString('HH:mm:ss')) Waiting $(((Get-Date) - $waitStarted).ToString('m\:ss')) for one of $slotCount slots; running: $(if ($running) { $running -join ', ' } else { 'runs from older scripts' }); $ahead queued ahead of this one"
    }
    Start-Sleep -Seconds 2
}
$me.running = $true
$me.since = (Get-Date).ToString('o')
$me | ConvertTo-Json -Compress | Set-Content $entry
if ($lastReport) { Write-Host "$((Get-Date).ToString('HH:mm:ss')) Starting after waiting $(((Get-Date) - $waitStarted).ToString('m\:ss'))" }
$desktopName = "Mp3PlayerSyncTests-$PID"
$log = Join-Path ([IO.Path]::GetTempPath()) "$desktopName.log"
$desktopHandle = if (!$hidden) { [IntPtr]::Zero } else { [TestDesktop]::CreateDesktopW($desktopName, [IntPtr]::Zero, [IntPtr]::Zero, 0, [TestDesktop]::GENERIC_ALL, [IntPtr]::Zero) }
if ($hidden -and $desktopHandle -eq [IntPtr]::Zero) { Remove-Item $entry -ErrorAction SilentlyContinue; $lock.ReleaseMutex(); throw "CreateDesktop failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())" }

$testProcess = $null
$processHandle = [IntPtr]::Zero
$exitCode = 1
$testProcessStarts = @{}
$currentTree = @{}
try {
    $script = $TestScript
    $arguments = if ($ScreenshotFolder) { " -ScreenshotFolder '$ScreenshotFolder'" } else { '' }
    if ($AppPath) { $arguments += " -AppPath '$((Resolve-Path $AppPath).Path)'" }
    if ($Only) { $arguments += " -Only $($Only -join ',')" }
    if (!$hidden) { $arguments += ' -Desktop Current' }
    $pwsh = [Environment]::ProcessPath
    $commandLine = "`"$pwsh`" -NoProfile -Command `"& '$script'$arguments *> '$log'; exit `$LASTEXITCODE`""

    $startup = New-Object TestDesktop+STARTUPINFO
    $startup.cb = [Runtime.InteropServices.Marshal]::SizeOf($startup)
    if ($hidden) { $startup.lpDesktop = "WinSta0\$desktopName" }
    $info = New-Object TestDesktop+PROCESS_INFORMATION
    if (![TestDesktop]::CreateProcessW($pwsh, $commandLine, [IntPtr]::Zero, [IntPtr]::Zero, $false, [TestDesktop]::CREATE_NO_WINDOW, [IntPtr]::Zero, $PSScriptRoot, [ref]$startup, [ref]$info)) {
        throw "CreateProcess failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())"
    }
    [TestDesktop]::CloseHandle($info.hThread) | Out-Null
    # Kept open: Get-Process objects for processes the script didn't start report no exit code.
    $processHandle = $info.hProcess
    $testProcess = Get-Process -Id $info.dwProcessId
    Write-Host "Running the end-to-end tests on $(if ($hidden) { "desktop '$desktopName'" } else { 'this desktop' }) (process $($testProcess.Id))"

    # Watch the user's desktop: its foreground window may change because of the user (who may well be running the
    # app themselves), but never to a process started by the test run. The test run's processes are worked out
    # afresh each time from the live process tree, matching start times, so a process ID that Windows reuses
    # after a test process exits (say, for an Explorer window the user opens) never counts.
    $foregroundChanges = [System.Collections.Generic.List[string]]::new()
    $rootStart = (Get-CimInstance Win32_Process -Filter "ProcessId = $($testProcess.Id)").CreationDate
    $testProcessStarts = @{ [uint32]$testProcess.Id = $rootStart }
    $stolen = [System.Collections.Generic.List[string]]::new()
    $last = [IntPtr]::Zero
    $nextTreeRefresh = Get-Date
    # The test run's processes as they are now: the script and everything descended from it, with their start times.
    function Update-TestTree {
        $all = Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, CreationDate
        $tree = @{ [uint32]$testProcess.Id = $rootStart }
        do {
            $added = 0
            foreach ($process in $all) {
                $parentStart = $tree[[uint32]$process.ParentProcessId]
                if ($null -ne $parentStart -and !$tree.ContainsKey([uint32]$process.ProcessId) -and $process.CreationDate -ge $parentStart) {
                    $tree[[uint32]$process.ProcessId] = $process.CreationDate; $added++
                }
            }
        } while ($added)
        foreach ($id in $tree.Keys) { $testProcessStarts[$id] = $tree[$id] } # Kept for the cleanup afterwards
        $script:currentTree = $tree
        $script:nextTreeRefresh = (Get-Date).AddSeconds(2)
    }
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    while (!$testProcess.HasExited -and (Get-Date) -lt $deadline) {
        if ((Get-Date) -ge $nextTreeRefresh) { Update-TestTree }
        $foreground = [TestDesktop]::GetForegroundWindow()
        if ($foreground -ne $last) {
            $owner = [uint32]0
            [TestDesktop]::GetWindowThreadProcessId($foreground, [ref]$owner) | Out-Null
            $ownerProcess = Get-Process -Id $owner -ErrorAction SilentlyContinue
            $foregroundChanges.Add("$(Get-Date -Format HH:mm:ss.fff) $($ownerProcess.ProcessName) ($owner)")
            # A process the test run started since the last refresh, such as an app that takes the foreground as soon
            # as it shows, isn't in the tree yet.
            if ($null -eq $currentTree[$owner]) { Update-TestTree }
            $recordedStart = $currentTree[$owner]
            if ($hidden -and $null -ne $recordedStart -and $ownerProcess -and [Math]::Abs(($ownerProcess.StartTime - $recordedStart).TotalSeconds) -lt 1) {
                $stolen.Add("$($ownerProcess.ProcessName) ($owner)")
            }
            $last = $foreground
        }
        Start-Sleep -Milliseconds 100
    }
    if (!$testProcess.HasExited) { Write-Host "Timed out after $TimeoutMinutes minutes" -ForegroundColor Red }

    $logText = Get-Content $log -Raw -ErrorAction SilentlyContinue
    $logText
    Write-Host "Foreground windows on the user's desktop during the run:"
    $foregroundChanges | ForEach-Object { Write-Host "  $_" }
    # The check for TEST-1. The script can't make it: off the input desktop, GetForegroundWindow is always NULL.
    if ($hidden) {
        if ($stolen.Count) { Write-Host "  FAIL  The app never took the foreground: $($stolen -join ', ')" -ForegroundColor Red }
        else { Write-Host '  PASS  The app never took the foreground' -ForegroundColor Green }
    }

    $processExitCode = [uint32]0
    $exited = $testProcess.HasExited -and [TestDesktop]::GetExitCodeProcess($processHandle, [ref]$processExitCode)
    # The exit code and the log have to agree that everything passed, so neither can hide a failure.
    $passed = $exited -and $processExitCode -eq 0 -and $logText -match [regex]::Escape($PassedText)
    if ($stolen.Count) { Write-Host "A test process took the foreground: $($stolen -join ', ')" -ForegroundColor Red }
    elseif ($exited -and $processExitCode -eq 0 -and !$passed) { Write-Host "The script exited with 0 but did not print '$PassedText'" -ForegroundColor Red }
    $exitCode = if ($passed -and !$stolen.Count) { 0 } elseif ($exited -and $processExitCode -ne 0) { [int]$processExitCode } else { 1 }
}
finally {
    # Everything started on the test desktop goes with it.
    # Only what the test run started; the user may have their own copy of the app open.
    if ($testProcess -and !$testProcess.HasExited) { $testProcess.Kill($true) }
    foreach ($id in @($testProcessStarts.Keys)) {
        Get-Process -Id $id -ErrorAction SilentlyContinue |
            Where-Object { [Math]::Abs(($_.StartTime - $testProcessStarts[$id]).TotalSeconds) -lt 1 } | ForEach-Object { $_.Kill($true) }
    }
    if ($processHandle -ne [IntPtr]::Zero) { [TestDesktop]::CloseHandle($processHandle) | Out-Null }
    if ($desktopHandle -ne [IntPtr]::Zero) { [TestDesktop]::CloseDesktop($desktopHandle) | Out-Null }
    Remove-Item $log -ErrorAction SilentlyContinue
    Remove-Item $entry -ErrorAction SilentlyContinue
    $lock.ReleaseMutex()
    $lock.Dispose()
}

exit $exitCode
