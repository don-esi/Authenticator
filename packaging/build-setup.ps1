#Requires -Version 5.1
<#
.SYNOPSIS
  Publishes Authenticator.Wpf (self-contained win-x64) and compiles AuthenticatorSetup-*.exe with Inno Setup.
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..")
$Project = Join-Path $Root "Authenticator.Wpf\Authenticator.Wpf.csproj"
$PublishDir = Join-Path $PSScriptRoot "publish"
$Iss = Join-Path $PSScriptRoot "Authenticator.iss"
$DistDir = Join-Path $PSScriptRoot "dist"

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
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    throw "Inno Setup 6 (ISCC.exe) not found. Install from https://jrsoftware.org/isinfo.php"
}

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
