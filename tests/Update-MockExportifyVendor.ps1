<#
.SYNOPSIS
    Updates the mock Exportify's copies of exportify.net's files and of the libraries its page loads.

.DESCRIPTION
    tests\MockExportify serves these from its vendor folder rather than from the internet, so that a slow or failed
    download, or a change on exportify.net, can't fail an end-to-end run. To update them, set $exportifyCommit to the
    commit exportify.net now runs, and any library's version that changed, run this, and open a pull request, where CI
    runs the end-to-end tests on the new copies.
#>

$ErrorActionPreference = 'Stop'
$vendor = Join-Path $PSScriptRoot 'MockExportify' 'vendor'
# https://github.com/pavelkomarov/exportify, which is exportify.net
$exportifyCommit = 'a461de305717b625495fa77ef1f462d8cfa2f8d5'
$exportify = "https://raw.githubusercontent.com/pavelkomarov/exportify/$exportifyCommit"

# Everything exportify.net's page loads, in its order, and where each copy comes from
$sources = @(
    @{ File = 'bootstrap.min.css'; Url = 'https://maxcdn.bootstrapcdn.com/bootstrap/3.3.4/css/bootstrap.min.css' }
    @{ File = 'font-awesome.min.css'; Url = 'https://maxcdn.bootstrapcdn.com/font-awesome/4.3.0/css/font-awesome.min.css' }
    # The page asks for the latest React 16, which is 16.14.0.
    @{ File = 'react.production.min.js'; Url = 'https://unpkg.com/react@16.14.0/umd/react.production.min.js' }
    @{ File = 'react-dom.production.min.js'; Url = 'https://unpkg.com/react-dom@16.14.0/umd/react-dom.production.min.js' }
    @{ File = 'exportify/FileSaver.js'; Url = "$exportify/FileSaver.js" }
    @{ File = 'jszip.min.js'; Url = 'https://cdnjs.cloudflare.com/ajax/libs/jszip/2.5.0/jszip.min.js' }
    @{ File = 'exportify/style.css'; Url = "$exportify/style.css" }
    @{ File = 'exportify/exportify.js'; Url = "$exportify/exportify.js" }
    # exportify.js has no license header of its own.
    @{ File = 'exportify/LICENSE'; Url = "$exportify/LICENSE" }
)

foreach ($source in $sources) {
    $path = Join-Path $vendor $source.File
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    Invoke-WebRequest $source.Url -OutFile $path
    Write-Host "$($source.File) from $($source.Url)"
}
