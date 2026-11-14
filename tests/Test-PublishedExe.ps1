<#
.SYNOPSIS
    Runs the end-to-end tests against the app's code as released: trimmed.

.DESCRIPTION
    Trimming can break things that work in the Debug build. This publishes the release exe as CI does (which fails on
    trimming warnings), then publishes the tests' host (Mp3PlayerSync.E2EHost) trimmed with the same settings
    (src\Mp3PlayerSync\Publishing.targets) and runs Test-MockEndToEnd.ps1 against it (through Invoke-EndToEnd.ps1), with
    the mock Exportify built in Release.
    The host is published as a folder rather than a single file, which would leave out the app's XAML (in
    Mp3PlayerSync.pri).
#>
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',
    # Just these runs of Test-MockEndToEnd.ps1 (see its -Only)
    [string[]] $Only,
    [string] $ScreenshotFolder,
    # Where to keep the publishes' MSBuild binlogs
    [string] $BinlogFolder,
    # See Invoke-EndToEnd.ps1
    [ValidateSet('Hidden', 'Current')]
    [string] $Desktop = 'Hidden'
)

$ErrorActionPreference = 'Stop'
$output = Join-Path ([IO.Path]::GetTempPath()) "Mp3PlayerSyncPublished-$PID"
$exitCode = 1
$exeLog = if ($BinlogFolder) { "-bl:$BinlogFolder\publish-exe-$Runtime.binlog" } else { '-nologo' }
$hostLog = if ($BinlogFolder) { "-bl:$BinlogFolder\publish-e2e-host-$Runtime.binlog" } else { '-nologo' }
$mockLog = if ($BinlogFolder) { "-bl:$BinlogFolder\publish-mock.binlog" } else { '-nologo' }
try {
    dotnet publish "$PSScriptRoot\..\src\Mp3PlayerSync\Mp3PlayerSync.csproj" -c Release -r $Runtime -o "$output\exe" -p:PublishSingleFile=true $exeLog
    $exitCode = $LASTEXITCODE
    if (!$exitCode) {
        Write-Host ('Published the exe: {0:N1} MB' -f ((Get-Item "$output\exe\Mp3PlayerSync.exe").Length / 1MB))
        dotnet publish "$PSScriptRoot\Mp3PlayerSync.E2EHost\Mp3PlayerSync.E2EHost.csproj" -c Release -r $Runtime -o "$output\host" -p:SelfContained=true -p:WindowsAppSDKSelfContained=true $hostLog
        $exitCode = $LASTEXITCODE
        # The app's XAML is in Mp3PlayerSync.pri, which Windows only looks for beside Mp3PlayerSync.exe; resources.pri
        # is found beside any exe.
        if (!$exitCode) { Copy-Item "$output\host\Mp3PlayerSync.pri" "$output\host\resources.pri" }
    }
    if (!$exitCode) {
        # The mock Exportify runs on this PC, whatever $Runtime is.
        dotnet publish "$PSScriptRoot\Mp3PlayerSync.MockExportify\Mp3PlayerSync.MockExportify.csproj" -c Release -o "$output\mock" $mockLog
        $exitCode = $LASTEXITCODE
    }
    if (!$exitCode) {
        # CI doubles every wait (see Test-MockEndToEnd.ps1), so it gets twice as long in all too.
        $timeout = if ($env:CI -eq 'true') { 30 } else { 15 }
        & "$PSScriptRoot\Invoke-EndToEnd.ps1" -AppPath "$output\host\Mp3PlayerSync.E2EHost.exe" -MockPath "$output\mock\Mp3PlayerSync.MockExportify.exe" -Only $Only -ScreenshotFolder $ScreenshotFolder -Desktop $Desktop -TimeoutMinutes $timeout
        $exitCode = $LASTEXITCODE
    }
}
finally {
    Remove-Item $output -Recurse -Force -ErrorAction SilentlyContinue
}

exit $exitCode

