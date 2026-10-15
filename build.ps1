# Builds a release with Velopack: self-contained x64 app, installer, portable zip, full + delta update packages.
#   .\build.ps1                 # version from Pylon.csproj
#   .\build.ps1 -Version 1.3.0
# Needs vpk (dotnet tool install -g vpk). With GITHUB_TOKEN set, the previous release is downloaded first so a delta
# package (only what changed) can be made. Inno Setup 6, when present, builds the bridge for 1.0-1.2 installs.
param([string]$Version)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $Version) { $Version = ([xml](Get-Content Pylon\Pylon.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
$out = 'dist\Pylon'
$releases = 'dist\releases'
Remove-Item dist -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish Pylon\Pylon.csproj -c Release -o $out -p:Version=$Version -p:DebugType=none -p:PublishReadyToRun=true
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

# libVLC's plugin cache (plugins.dat): without it every start scans ~300 plugin DLLs
Start-Process "$out\Pylon.exe" -ArgumentList '--build-plugin-cache' -Wait
if (-not (Test-Path "$out\libvlc\win-x64\plugins\plugins.dat")) { throw 'plugin cache was not generated' }

# release notes = this version's section of CHANGELOG.md (both languages; the app shows one)
$notes = 'dist\notes.md'
$section = [regex]::Match((Get-Content CHANGELOG.md -Raw), "(?ms)^## $([regex]::Escape($Version))\s*\r?\n(.*?)(?=^## |\z)")
Set-Content $notes ($(if ($section.Success) { $section.Groups[1].Value.Trim() } else { "Pylon $Version" })) -Encoding utf8

if ($env:GITHUB_TOKEN) {
    # the previous release's full package is the base for this version's delta (fails harmlessly on the first one)
    vpk download github --repoUrl https://github.com/MeowKusTrue/Pylon --token $env:GITHUB_TOKEN -o $releases
    if ($LASTEXITCODE) { Write-Warning 'no previous Velopack release: this one ships without a delta' }
}
vpk pack -u Pylon -v $Version -p $out -e Pylon.exe -o $releases --packTitle Pylon --packAuthors 'Pylon contributors' `
    -i Pylon\Assets\pylon.ico --releaseNotes $notes --shortcuts Desktop,StartMenuRoot
if ($LASTEXITCODE) { throw 'vpk pack failed' }

$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    & $iscc /Q "/DAppVersion=$Version" installer\Bridge.iss
    if ($LASTEXITCODE) { throw 'Inno Setup (bridge) failed' }
} else {
    Write-Warning 'Inno Setup 6 not found - skipped the bridge for 1.0-1.2 installs (https://jrsoftware.org/isdl.php)'
}
Get-ChildItem $releases -File | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
