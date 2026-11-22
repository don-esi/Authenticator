# Registers Authenticator.NativeHost for Chrome, Edge, and Firefox (HKCU).
# Prefers published self-contained win-x64 output (required for Chrome's minimal PATH).
param(
    [string]$NativeHostExe = "",
    [string]$ChromeExtensionId = "apolfgjbnoelcpahldnnlmbjnbfjleil",
    [string]$FirefoxExtensionId = "authenticator@local"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent $root

if (-not $NativeHostExe) {
    $candidates = @(
        Join-Path $repo "Authenticator.NativeHost\bin\Release\net11.0-windows\win-x64\publish\Authenticator.NativeHost.exe"
        Join-Path $repo "Authenticator.NativeHost\bin\Release\net11.0-windows\win-x64\Authenticator.NativeHost.exe"
        Join-Path $repo "Authenticator.NativeHost\bin\Release\net11.0\win-x64\publish\Authenticator.NativeHost.exe"
        Join-Path $repo "Authenticator.NativeHost\bin\Release\net11.0\win-x64\Authenticator.NativeHost.exe"
    )
    $NativeHostExe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not $NativeHostExe -or -not (Test-Path $NativeHostExe)) {
    throw "Native host exe not found. Run: dotnet publish D:\Source\Authenticator\Authenticator.NativeHost -c Release"
}

$NativeHostExe = (Resolve-Path $NativeHostExe).Path
$hostDir = Join-Path $env:LOCALAPPDATA "Authenticator\native-messaging"
New-Item -ItemType Directory -Force -Path $hostDir | Out-Null

function Write-HostManifest([string]$path, [hashtable]$manifest) {
    $json = $manifest | ConvertTo-Json -Depth 6
    $utf8NoBom = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText($path, $json, $utf8NoBom)
}

$chromiumManifestPath = Join-Path $hostDir "com.authenticator.native.chromium.json"
Write-HostManifest $chromiumManifestPath @{
    name = "com.authenticator.native"
    description = "Authenticator vault native messaging host"
    path = $NativeHostExe
    type = "stdio"
    allowed_origins = @(
        "chrome-extension://$ChromeExtensionId/"
    )
}

$firefoxManifestPath = Join-Path $hostDir "com.authenticator.native.firefox.json"
Write-HostManifest $firefoxManifestPath @{
    name = "com.authenticator.native"
    description = "Authenticator vault native messaging host"
    path = $NativeHostExe
    type = "stdio"
    allowed_extensions = @($FirefoxExtensionId)
}

function Set-NativeHostKey([string]$regPath, [string]$manifestPath) {
    if (-not (Test-Path $regPath)) {
        New-Item -Path $regPath -Force | Out-Null
    }
    New-ItemProperty -Path $regPath -Name "(Default)" -Value $manifestPath -PropertyType String -Force | Out-Null
}

Set-NativeHostKey "HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.authenticator.native" $chromiumManifestPath
Set-NativeHostKey "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.authenticator.native" $chromiumManifestPath
Set-NativeHostKey "HKCU:\Software\Mozilla\NativeMessagingHosts\com.authenticator.native" $firefoxManifestPath

Write-Host "Registered com.authenticator.native"
Write-Host "  Host:     $NativeHostExe"
Write-Host "  Size:     $((Get-Item $NativeHostExe).Length) bytes"
Write-Host "  Chromium: $chromiumManifestPath"
Write-Host "  Firefox:  $firefoxManifestPath"
Write-Host ""
Write-Host "Reload the extension, unlock Authenticator, then Test connection."
