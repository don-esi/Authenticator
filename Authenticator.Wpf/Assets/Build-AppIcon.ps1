# Builds Authenticator.Wpf\Assets\app.ico and Authenticator.Browser\icons\*.png
# from app-icon-source.jpg (shield + lock + 2FA on white).
#
# Small sizes use a mild center zoom so the mark stays readable.
# Requires ImageMagick (`magick` on PATH).

$ErrorActionPreference = 'Stop'

$assets = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $assets '..\..')
$browserIcons = Join-Path $repoRoot 'Authenticator.Browser\icons'
$source = Join-Path $assets 'app-icon-source.jpg'

if (-not (Test-Path $source)) {
    throw "Missing source artwork: $source"
}

if (-not (Get-Command magick -ErrorAction SilentlyContinue)) {
    throw 'ImageMagick `magick` not found on PATH.'
}

New-Item -ItemType Directory -Force -Path $assets, $browserIcons | Out-Null

function Invoke-Magick {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$MagickArgs)
    & magick @MagickArgs
    if ($LASTEXITCODE -ne 0) {
        throw "magick failed ($LASTEXITCODE): $($MagickArgs -join ' ')"
    }
}

function New-SizedPng {
    param(
        [Parameter(Mandatory)][int]$Size,
        [Parameter(Mandatory)][string]$OutPath,
        [double]$Zoom = 1.0
    )

    if ($Zoom -gt 1.0) {
        $cropPct = [Math]::Round(100.0 / $Zoom, 2)
        Invoke-Magick $source `
            -colorspace sRGB `
            -gravity Center `
            -crop "${cropPct}%" `
            +repage `
            -filter Lanczos `
            -resize "${Size}x${Size}!" `
            -unsharp '0x0.6+0.6+0.02' `
            -strip `
            $OutPath
    }
    else {
        Invoke-Magick $source `
            -colorspace sRGB `
            -filter Lanczos `
            -resize "${Size}x${Size}!" `
            -unsharp '0x0.5+0.5+0.02' `
            -strip `
            $OutPath
    }
}

$layer16 = Join-Path $assets 'app-icon-16.png'
$layer32 = Join-Path $assets 'app-icon-32.png'
$layer48 = Join-Path $assets 'app-icon-48.png'
$layer256 = Join-Path $assets 'app-icon-256.png'

New-SizedPng -Size 16 -OutPath $layer16 -Zoom 1.2
New-SizedPng -Size 32 -OutPath $layer32 -Zoom 1.12
New-SizedPng -Size 48 -OutPath $layer48 -Zoom 1.06
New-SizedPng -Size 256 -OutPath $layer256 -Zoom 1.0
Write-Host 'Wrote size previews'

$ico = Join-Path $assets 'app.ico'
Invoke-Magick $layer16 $layer32 $layer48 $layer256 $ico
Write-Host "Wrote $ico"

New-SizedPng -Size 16 -OutPath (Join-Path $browserIcons 'icon16.png') -Zoom 1.2
New-SizedPng -Size 48 -OutPath (Join-Path $browserIcons 'icon48.png') -Zoom 1.06
New-SizedPng -Size 128 -OutPath (Join-Path $browserIcons 'icon128.png') -Zoom 1.0
Write-Host "Wrote $browserIcons"

# Drop leftovers from earlier icon experiments
@(
    'app-icon-master.png',
    'app-icon-master-tight.png'
) | ForEach-Object {
    $p = Join-Path $assets $_
    if (Test-Path $p) { Remove-Item $p -Force }
}
Get-ChildItem $assets -Filter '_preview-*' -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem $assets -Filter 'shield-*.png' -ErrorAction SilentlyContinue | Remove-Item -Force

Get-Item $ico | Format-List FullName, Length
Get-ChildItem $assets -Filter 'app-icon-*.png' | Select-Object Name, Length | Format-Table -AutoSize
Get-ChildItem $browserIcons -Filter 'icon*.png' | Select-Object Name, Length | Format-Table -AutoSize
