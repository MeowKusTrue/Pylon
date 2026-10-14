# Builds a release: self-contained x64 app, a portable zip and the installer.
#   .\build.ps1                 # version from Pylon.csproj
#   .\build.ps1 -Version 1.1.0
param([string]$Version)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $Version) { $Version = ([xml](Get-Content Pylon\Pylon.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
$out = 'dist\Pylon'
Remove-Item dist -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish Pylon\Pylon.csproj -c Release -o $out -p:Version=$Version -p:DebugType=none -p:PublishReadyToRun=true
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

# libVLC's plugin cache (plugins.dat): without it every start scans ~300 plugin DLLs
Start-Process "$out\Pylon.exe" -ArgumentList '--build-plugin-cache' -Wait
if (-not (Test-Path "$out\libvlc\win-x64\plugins\plugins.dat")) { throw 'plugin cache was not generated' }

Compress-Archive -Path "$out\*" -DestinationPath "dist\Pylon-$Version-win-x64-portable.zip"

$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    & $iscc /Q "/DAppVersion=$Version" installer\Pylon.iss
    if ($LASTEXITCODE) { throw 'Inno Setup failed' }
} else {
    Write-Warning 'Inno Setup 6 not found - skipped the installer (https://jrsoftware.org/isdl.php)'
}
Get-ChildItem dist -File | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
