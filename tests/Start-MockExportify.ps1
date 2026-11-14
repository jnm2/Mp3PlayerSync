<#
.SYNOPSIS
    Serves tests/MockExportify on http://localhost:<Port>/ until stopped (Ctrl+C).
#>
param([int] $Port = 5178)

$root = Join-Path $PSScriptRoot 'MockExportify'
$contentTypes = @{ '.html' = 'text/html; charset=utf-8'; '.js' = 'text/javascript; charset=utf-8'; '.css' = 'text/css' }

# Made-up playlist covers for the demo library (fake-spotify.js ?demo): a gradient with soft circles, the same for the same name.
Add-Type -AssemblyName System.Drawing
function New-DemoCover([string] $name) {
    $palettes = @(
        @('#FF7E5F', '#FEB47B'), @('#43C6AC', '#191654'), @('#F7971E', '#FFD200'), @('#4568DC', '#B06AB3'),
        @('#11998E', '#38EF7D'), @('#EE0979', '#FF6A00'), @('#2193B0', '#6DD5ED'), @('#834D9B', '#D04ED6'),
        @('#556270', '#FF6B6B'), @('#1D976C', '#93F9B9'))
    $hash = [uint32]7; foreach ($c in $name.ToCharArray()) { $hash = [uint32](($hash * 31 + [int]$c) % 4294967296) }
    $random = [Random]::new([int]($hash % 2147483647))
    $from, $to = $palettes[$hash % $palettes.Count] | ForEach-Object { [System.Drawing.ColorTranslator]::FromHtml($_) }
    $size = 300
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = 'AntiAlias'
    $gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new([System.Drawing.Point]::new(0, 0), [System.Drawing.Point]::new($size, $size), $from, $to)
    $graphics.FillRectangle($gradient, 0, 0, $size, $size)
    foreach ($i in 1..4) {
        $diameter = $random.Next(90, 260)
        $brush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb($random.Next(25, 60), 255, 255, 255))
        $graphics.FillEllipse($brush, $random.Next(-80, $size - 40), $random.Next(-80, $size - 40), $diameter, $diameter)
        $brush.Dispose()
    }
    $stream = [IO.MemoryStream]::new()
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $gradient.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
    $stream.ToArray()
}

$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("http://localhost:$Port/")
# The mock Spotify login page is served from here, so it is a different origin from the mock Exportify.
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
# It answers one request at a time, so one that stalls while sending its body mustn't hold up the rest for long.
$listener.TimeoutManager.EntityBody = [TimeSpan]::FromSeconds(10)
$listener.Start()
Write-Host "Mock Exportify listening on http://localhost:$Port/"

$apiLog = [System.Collections.Generic.List[string]]::new()
$signInHeld = $false

function Send-Response($context) {
    $path = [Uri]::UnescapeDataString($context.Request.Url.AbsolutePath.TrimStart('/'))

    # For fingerprint.html: what the browser sends, and somewhere to put its findings.
    if ($path -eq 'echo-headers') {
        $headers = @{}; foreach ($name in $context.Request.Headers.AllKeys) { $headers[$name.ToLowerInvariant()] = $context.Request.Headers[$name] }
        $bytes = [Text.Encoding]::UTF8.GetBytes(($headers | ConvertTo-Json))
        $context.Response.ContentType = 'application/json'
        $context.Response.Headers['Access-Control-Allow-Origin'] = '*'
        $context.Response.OutputStream.Write($bytes, 0, $bytes.Length); $context.Response.Close(); return
    }
    # The fake Spotify API answers inside the page, so the page reports each call here; the tests read the list
    # to tell when the app asked Spotify for something, such as a refresh, however quickly it was answered.
    if ($path -eq 'api-log') {
        if ($context.Request.HttpMethod -eq 'POST') { $apiLog.Add([IO.StreamReader]::new($context.Request.InputStream).ReadToEnd()) }
        else {
            $bytes = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -Compress -InputObject @($apiLog)))
            $context.Response.ContentType = 'application/json'
            $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        }
        $context.Response.Close(); return
    }
    if ($path -eq 'report' -and $context.Request.HttpMethod -eq 'POST') {
        $body = [IO.StreamReader]::new($context.Request.InputStream).ReadToEnd()
        Add-Content (Join-Path ([IO.Path]::GetTempPath()) "mock-exportify-report-$Port.jsonl") $body
        $context.Response.Headers['Access-Control-Allow-Origin'] = '*'
        $context.Response.Close(); return
    }
    # The tests can hold the fake login page's ?auto sign-in (POST 'hold') until they've seen the sign-in screen,
    # then let it go on (POST 'release'); the page asks here (GET) while it waits.
    if ($path -eq 'sign-in-hold') {
        if ($context.Request.HttpMethod -eq 'POST') { $script:signInHeld = [IO.StreamReader]::new($context.Request.InputStream).ReadToEnd() -eq 'hold' }
        else {
            $bytes = [Text.Encoding]::UTF8.GetBytes($(if ($signInHeld) { 'held' } else { 'released' }))
            $context.Response.ContentType = 'text/plain'
            $context.Response.Headers['Cache-Control'] = 'no-store'
            $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        }
        $context.Response.Close(); return
    }
    # The tests can make the page's token run out once they're watching (POST); the page, opened with
    # ?expireWhenAsked, asks here (GET) and is told once.
    if ($path -eq 'expire') {
        if ($context.Request.HttpMethod -eq 'POST') { $script:expireAsked = $true }
        else {
            $bytes = [Text.Encoding]::UTF8.GetBytes($(if ($script:expireAsked) { 'expire' } else { 'wait' }))
            $script:expireAsked = $false
            $context.Response.ContentType = 'text/plain'
            $context.Response.Headers['Cache-Control'] = 'no-store'
            $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        }
        $context.Response.Close(); return
    }
    if ($path -match '^demo-cover/(.+)\.png$') {
        $bytes = New-DemoCover $Matches[1]
        $context.Response.ContentType = 'image/png'
        $context.Response.OutputStream.Write($bytes, 0, $bytes.Length); $context.Response.Close(); return
    }
    if ($path -eq '') { $path = 'index.html' }
    $file = [IO.Path]::GetFullPath((Join-Path $root $path))

    if ($file.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path $file -PathType Leaf)) {
        $bytes = [IO.File]::ReadAllBytes($file)
        $context.Response.ContentType = $contentTypes[[IO.Path]::GetExtension($file)] ?? 'application/octet-stream'
        $context.Response.Headers['Cache-Control'] = 'no-store'
        $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    } else {
        $context.Response.StatusCode = 404
    }
    $context.Response.Close()
}

$failed = 0
try {
    while ($listener.IsListening) {
        # Waiting in short steps rather than in GetContext lets Stop-Job end the mock at once: it can't interrupt
        # GetContext, so it used to give up after 2 minutes.
        $next = $listener.GetContextAsync()
        while (!$next.Wait(250)) { }
        $context = $next.Result
        # A client that goes away mid-request, as the app's page does when it closes or navigates, makes reading or
        # writing throw. That's only this request's problem: the mock goes on serving the others, and says so for
        # the test to show (see Test-MockEndToEnd.ps1).
        try { Send-Response $context }
        catch {
            $failed++
            Write-Host "Mock: $($context.Request.HttpMethod) $($context.Request.Url.PathAndQuery) failed ($failed so far): $($_.Exception.Message)"
            try { $context.Response.Abort() } catch { }
        }
    }
} finally {
    $listener.Stop()
}
