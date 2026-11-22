#Requires -Version 5.1
<#
.SYNOPSIS
  Generates Authenticator.Browser\chrome-extension.pem and updates public key + Chromium extension id.
  Replaces any previous id — reload/reinstall the extension and re-register the native host after running.
#>
[CmdletBinding()]
param(
    [string]$BrowserDir = ""
)

$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..")
if (-not $BrowserDir) {
    $BrowserDir = Join-Path $Root "Authenticator.Browser"
}

$project = Join-Path $PSScriptRoot "GenerateChromePem\GenerateChromePem.csproj"
dotnet run --project $project -c Release -- $BrowserDir
if ($LASTEXITCODE -ne 0) {
    throw "GenerateChromePem failed with exit code $LASTEXITCODE"
}

$idFile = Join-Path $BrowserDir ".chrome-extension-id.txt"
$id = (Get-Content $idFile -Raw).Trim()
Write-Host ""
Write-Host "Remember to update hard-coded extension ids in:"
Write-Host "  packaging\build-setup.ps1"
Write-Host "  packaging\Authenticator.iss"
Write-Host "  Authenticator.Browser\register-native-host.ps1"
Write-Host "  Authenticator.Browser\README.md / packaging\README.md"
Write-Host "Current id: $id"
