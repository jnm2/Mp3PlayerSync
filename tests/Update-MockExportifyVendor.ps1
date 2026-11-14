<#
.SYNOPSIS
    Updates the mock Exportify's copies of exportify.net's files and of the libraries its page loads, or with -Check,
    says whether exportify.net has changed since.

.DESCRIPTION
    tests\MockExportify serves these from its vendor folder rather than from the internet, so that a slow or failed
    download, or a change on exportify.net, can't fail an end-to-end run.

    -Check, which .github\workflows\exportify-drift.yml runs every week, fails when exportify.net's exportify.js,
    FileSaver.js or style.css differ from the copies here, or when its page loads anything else. Then set
    $exportifyCommit to the commit exportify.net now runs, update any library that changed, run this without -Check,
    and open a pull request, where CI runs the end-to-end tests on the new copies.
#>
param([switch] $Check)

$ErrorActionPreference = 'Stop'
$vendor = Join-Path $PSScriptRoot 'MockExportify' 'vendor'
# https://github.com/pavelkomarov/exportify, which is exportify.net
$exportifyCommit = 'a461de305717b625495fa77ef1f462d8cfa2f8d5'
$exportify = "https://raw.githubusercontent.com/pavelkomarov/exportify/$exportifyCommit"

# Everything exportify.net's page loads, in its order (Page), and where each copy comes from. Live: what to compare the
# copy with for -Check.
$sources = @(
    @{ Page = '//maxcdn.bootstrapcdn.com/bootstrap/3.3.4/css/bootstrap.min.css'; File = 'bootstrap.min.css'; Url = 'https://maxcdn.bootstrapcdn.com/bootstrap/3.3.4/css/bootstrap.min.css' }
    @{ Page = '//maxcdn.bootstrapcdn.com/font-awesome/4.3.0/css/font-awesome.min.css'; File = 'font-awesome.min.css'; Url = 'https://maxcdn.bootstrapcdn.com/font-awesome/4.3.0/css/font-awesome.min.css' }
    # The page asks for the latest React 16, which is 16.14.0.
    @{ Page = '//unpkg.com/react@16/umd/react.production.min.js'; File = 'react.production.min.js'; Url = 'https://unpkg.com/react@16.14.0/umd/react.production.min.js' }
    @{ Page = '//unpkg.com/react-dom@16/umd/react-dom.production.min.js'; File = 'react-dom.production.min.js'; Url = 'https://unpkg.com/react-dom@16.14.0/umd/react-dom.production.min.js' }
    @{ Page = 'FileSaver.js'; File = 'exportify/FileSaver.js'; Url = "$exportify/FileSaver.js"; Live = 'https://exportify.net/FileSaver.js' }
    @{ Page = '//cdnjs.cloudflare.com/ajax/libs/jszip/2.5.0/jszip.min.js'; File = 'jszip.min.js'; Url = 'https://cdnjs.cloudflare.com/ajax/libs/jszip/2.5.0/jszip.min.js' }
    @{ Page = 'style.css'; File = 'exportify/style.css'; Url = "$exportify/style.css"; Live = 'https://exportify.net/style.css' }
    @{ Page = 'exportify.js'; File = 'exportify/exportify.js'; Url = "$exportify/exportify.js"; Live = 'https://exportify.net/exportify.js' }
    # exportify.js has no license header of its own.
    @{ File = 'exportify/LICENSE'; Url = "$exportify/LICENSE" }
)

if (!$Check) {
    foreach ($source in $sources) {
        $path = Join-Path $vendor $source.File
        New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
        Invoke-WebRequest $source.Url -OutFile $path
        Write-Host "$($source.File) from $($source.Url)"
    }
    exit
}

$problems = [System.Collections.Generic.List[string]]::new()
$page = (Invoke-WebRequest 'https://exportify.net/').Content
$loaded = [regex]::Matches($page, '<(?:script[^>]*?\bsrc|link[^>]*?\brel="stylesheet"[^>]*?\bhref)="([^"]+)"') | ForEach-Object { $_.Groups[1].Value }
$expected = $sources | Where-Object Page | ForEach-Object Page
if (($loaded -join "`n") -ne ($expected -join "`n")) {
    $problems.Add("exportify.net's page loads $($loaded -join ', '), not $($expected -join ', ')")
}
foreach ($source in $sources | Where-Object Live) {
    $live = (Invoke-WebRequest $source.Live).RawContentStream.ToArray()
    $copy = [IO.File]::ReadAllBytes((Join-Path $vendor $source.File))
    if ([Convert]::ToHexString($live) -ne [Convert]::ToHexString($copy)) { $problems.Add("$($source.File): $($source.Live) differs from the copy") }
}
if ($problems.Count) {
    $steps = @(
        "1. Set `$exportifyCommit in tests/Update-MockExportifyVendor.ps1 to the commit exportify.net now runs (https://github.com/pavelkomarov/exportify/commits), and any library version that changed."
        '2. Run: pwsh tests/Update-MockExportifyVendor.ps1'
        '3. Open a pull request with the result, where CI runs the end-to-end tests on the new copies.'
    )
    Write-Host "Exportify changed, so the mock's copies in tests/MockExportify/vendor need updating:" -ForegroundColor Red
    $problems | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Write-Host 'To update them:'
    $steps | ForEach-Object { Write-Host "  $_" }
    if ($env:GITHUB_ACTIONS) {
        # On the run's page, where a failure email's link goes
        $problems | ForEach-Object { Write-Host "::error title=Exportify changed::$_" }
        @(
            "## Exportify changed, so the mock's copies need updating"
            ''
            'What differs from the copies in `tests/MockExportify/vendor`:'
            $problems | ForEach-Object { "- $_" }
            ''
            'To update them:'
            ''
            $steps -replace 'pwsh tests/Update-MockExportifyVendor.ps1', '`pwsh tests/Update-MockExportifyVendor.ps1`' -replace '\$exportifyCommit', '`$exportifyCommit`'
        ) | Add-Content $env:GITHUB_STEP_SUMMARY
    }
    exit 1
}
Write-Host "exportify.net matches the mock's copies (Exportify $exportifyCommit)"
