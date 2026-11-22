#Requires -Version 5.1
<#
.SYNOPSIS
  Publishes Authenticator.Wpf (self-contained win-x64), NativeHost, Browser extension,
  optionally packs a CRX, and compiles AuthenticatorSetup-*.exe with Inno Setup.
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Version = "",
    [string]$ChromeExtensionPem = ""
)

$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..")
$Project = Join-Path $Root "Authenticator.Wpf\Authenticator.Wpf.csproj"
$NativeHostProject = Join-Path $Root "Authenticator.NativeHost\Authenticator.NativeHost.csproj"
$BrowserSrc = Join-Path $Root "Authenticator.Browser"
$PublishDir = Join-Path $PSScriptRoot "publish"
$BrowserPublish = Join-Path $PublishDir "Browser"
$NativeHostPublish = Join-Path $PublishDir "NativeHost"
$Iss = Join-Path $PSScriptRoot "Authenticator.iss"
$DistDir = Join-Path $PSScriptRoot "dist"
$ChromeExtensionId = "bfcajmeodjdgaomgpanlihipagjkmlih"

if (-not $Version) {
    [xml]$csproj = Get-Content $Project
    $Version = $csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $Version) { $Version = "1.0.0" }
}

Write-Host "Version: $Version"
Write-Host "Publishing self-contained win-x64 to $PublishDir ..."

if (Test-Path $PublishDir) {
    Remove-Item $PublishDir -Recurse -Force
}

dotnet publish $Project `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $PublishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish (WPF) failed with exit code $LASTEXITCODE"
}

Write-Host "Publishing NativeHost to $NativeHostPublish ..."
New-Item -ItemType Directory -Force -Path $NativeHostPublish | Out-Null
dotnet publish $NativeHostProject `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $NativeHostPublish

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish (NativeHost) failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path (Join-Path $NativeHostPublish "Authenticator.NativeHost.exe"))) {
    throw "NativeHost publish missing Authenticator.NativeHost.exe"
}

Write-Host "Copying Browser extension to $BrowserPublish ..."
New-Item -ItemType Directory -Force -Path $BrowserPublish | Out-Null
$exclude = @(".chrome-key.b64", "chrome-extension.pem", "private-key.pem", "*.crx", "*.pem")
Get-ChildItem $BrowserSrc -Force | Where-Object {
    $name = $_.Name
    -not ($exclude | Where-Object { $name -like $_ })
} | ForEach-Object {
    $dest = Join-Path $BrowserPublish $_.Name
    if ($_.PSIsContainer) {
        Copy-Item $_.FullName $dest -Recurse -Force
    } else {
        Copy-Item $_.FullName $dest -Force
    }
}

if (-not (Test-Path (Join-Path $BrowserPublish "manifest.json"))) {
    throw "Browser publish missing manifest.json"
}

# Patch manifest version to match app version (major.minor.patch).
$manifestPath = Join-Path $BrowserPublish "manifest.json"
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$manifest.version = ($Version -split '[^0-9.]')[0]
$manifest | ConvertTo-Json -Depth 8 | Set-Content -Path $manifestPath -Encoding utf8

function Find-ChromeExe {
    $candidates = @(
        "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
        "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
        "$env:LocalAppData\Google\Chrome\Application\chrome.exe",
        "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
        "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
        "$env:LocalAppData\Microsoft\Edge\Application\msedge.exe"
    )
    return $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not $ChromeExtensionPem) {
    if ($env:CHROME_EXTENSION_PEM -and (Test-Path $env:CHROME_EXTENSION_PEM)) {
        $ChromeExtensionPem = $env:CHROME_EXTENSION_PEM
    } else {
        # Prefer packaging\ (outside the unpacked extension folder — Chrome rejects *.pem under Load unpacked).
        foreach ($cand in @(
            (Join-Path $PSScriptRoot "chrome-extension.pem"),
            (Join-Path $BrowserSrc "chrome-extension.pem"),
            (Join-Path $BrowserSrc "private-key.pem")
        )) {
            if (Test-Path $cand) { $ChromeExtensionPem = $cand; break }
        }
    }
}

$crxPath = Join-Path $BrowserPublish "AuthenticatorVault.crx"
$packed = $false
if ($ChromeExtensionPem -and (Test-Path $ChromeExtensionPem)) {
    $chrome = Find-ChromeExe
    if (-not $chrome) {
        Write-Warning "Chrome/Edge not found — skipping CRX pack. Unpacked extension will still ship."
    } else {
        Write-Host "Packing CRX with $chrome ..."
        $packArgs = @(
            "--pack-extension=`"$BrowserPublish`"",
            "--pack-extension-key=`"$ChromeExtensionPem`""
        )
        # Chrome writes AuthenticatorVault.crx next to the folder when packing a directory named Browser —
        # actually it writes <foldername>.crx beside the folder. Our folder is Browser → Browser.crx in publish/.
        $null = & $chrome @packArgs
        $siblingCrx = Join-Path $PublishDir "Browser.crx"
        Start-Sleep -Seconds 1
        if (Test-Path $siblingCrx) {
            Move-Item $siblingCrx $crxPath -Force
            $packed = $true
            Write-Host "CRX packed: $crxPath"
        } elseif (Test-Path $crxPath) {
            $packed = $true
            Write-Host "CRX packed: $crxPath"
        } else {
            Write-Warning "CRX pack did not produce Browser.crx — force-install may fall back to Load unpacked."
        }
    }
} else {
    Write-Warning "No chrome-extension.pem (or CHROME_EXTENSION_PEM). Shipping unpacked only; store listing / CRX later."
}

# updates.xml for ExtensionInstallForcelist — codebase path rewritten at install time by Inno.
$updatesPath = Join-Path $BrowserPublish "updates.xml"
$updates = @"
<?xml version='1.0' encoding='UTF-8'?>
<gupdate xmlns='http://www.google.com/update2/response' protocol='2.0'>
  <app appid='$ChromeExtensionId'>
    <updatecheck codebase='__CRX_CODEBASE__' version='$($manifest.version)' />
  </app>
</gupdate>
"@
Set-Content -Path $updatesPath -Value $updates -Encoding utf8

# Marker for Inno: whether a CRX was produced.
$marker = Join-Path $BrowserPublish "pack-status.txt"
@(
    "extensionId=$ChromeExtensionId"
    "version=$($manifest.version)"
    "crxPacked=$($packed.ToString().ToLowerInvariant())"
) | Set-Content -Path $marker -Encoding utf8

$iscc = @(
    "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
    "$env:LocalAppData\Programs\Inno Setup 7\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:LocalAppData\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    $fromPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($fromPath) { $iscc = $fromPath.Source }
}

if (-not $iscc) {
    throw "Inno Setup (ISCC.exe) not found. Install Inno Setup 6+ from https://jrsoftware.org/isinfo.php"
}

Write-Host "Using ISCC: $iscc"

New-Item -ItemType Directory -Force -Path $DistDir | Out-Null

Write-Host "Compiling Inno script with ISCC ..."
& $iscc "/DMyAppVersion=$Version" $Iss
if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed with exit code $LASTEXITCODE"
}

$setup = Get-ChildItem $DistDir -Filter "AuthenticatorSetup-*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($setup) {
    Write-Host "Setup built: $($setup.FullName)"
} else {
    Write-Warning "ISCC completed but no AuthenticatorSetup-*.exe was found under packaging\dist"
}
