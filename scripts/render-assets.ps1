# Renders assets\logo.svg / banner.svg to PNG with headless Chrome and rebuilds the multi-size app icon
# (src\Hushwire.App\Assets\hushwire.ico). Run after editing the SVGs:  powershell -File scripts\render-assets.ps1
$ErrorActionPreference = 'Continue'   # Chrome prints harmless warnings to stderr; success is checked by the output file
$root = Split-Path $PSScriptRoot -Parent
$chrome = @("$env:ProgramFiles\Google\Chrome\Application\chrome.exe", "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $chrome) { throw 'Needs Chrome or Edge to rasterize the SVGs.' }

function Render($svg, $png, $w, $h) {
    $uri = ([System.Uri](Join-Path $root $svg)).AbsoluteUri
    & $chrome --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 --default-background-color=00000000 `
        --window-size="$w,$h" --screenshot="$(Join-Path $root $png)" $uri 2>&1 | Out-Null
    if (-not (Test-Path (Join-Path $root $png))) { throw "render failed: $svg" }
}

Render 'assets\logo.svg'   'assets\logo.png'   512 512
Render 'assets\banner.svg' 'assets\banner.png' 1280 320

# multi-size .ico: PNG-compressed images for 16..256 px, scaled down from the 512 px render (the SVG has a fixed 512 size)
Add-Type -AssemblyName System.Drawing
$sizes = 256, 128, 64, 48, 32, 24, 16
$src = [System.Drawing.Image]::FromFile((Join-Path $root 'assets\logo.png'))
$images = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.SmoothingMode = 'HighQuality'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($src, 0, 0, $s, $s); $g.Dispose()
    $m = New-Object IO.MemoryStream; $bmp.Save($m, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    [pscustomobject]@{ Size = $s; Bytes = $m.ToArray() }
}
$src.Dispose()
$ms = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter $ms
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($i in $images) {
    $dim = if ($i.Size -ge 256) { 0 } else { $i.Size }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$i.Bytes.Length); $bw.Write([uint32]$offset)
    $offset += $i.Bytes.Length
}
foreach ($i in $images) { $bw.Write($i.Bytes) }
[IO.File]::WriteAllBytes((Join-Path $root 'src\Hushwire.App\Assets\hushwire.ico'), $ms.ToArray())
Write-Host "assets\logo.png, assets\banner.png and src\Hushwire.App\Assets\hushwire.ico rebuilt"
