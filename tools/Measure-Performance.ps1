param([Parameter(Mandatory)][string]$Exe, [string]$OutFile = 'performance.json')
$ErrorActionPreference = 'Stop'
$exePath = (Resolve-Path -LiteralPath $Exe).Path
$reportPath = [IO.Path]::GetFullPath($OutFile)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($reportPath)) | Out-Null
$root = Join-Path ([IO.Path]::GetTempPath()) ('pylon-performance-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$settings = @{'app.onboarded'='true'; 'general.background'='false'; 'general.updates'='false'; 'remote.auto'='false';
    'lyrics.online'='false'; 'library.websearch'='false'; 'ui.realwave'='false'; 'ui.scene'='Off'; 'ui.spectrumstyle'='Off';
    'perf.softwareui'='true'; 'playback.ownengine'='false'; 'ui.tray'='false'; 'playback.softpause'='false'}
[IO.File]::WriteAllText((Join-Path $root 'settings.json'), ($settings | ConvertTo-Json))
$wav = Join-Path $root 'silence.wav'
$writer = [IO.BinaryWriter]::new([IO.File]::Create($wav))
try {
    $writer.Write([Text.Encoding]::ASCII.GetBytes('RIFF')); $writer.Write([int]960036)
    $writer.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt ')); $writer.Write([int]16)
    $writer.Write([int16]1); $writer.Write([int16]1); $writer.Write([int]8000); $writer.Write([int]16000)
    $writer.Write([int16]2); $writer.Write([int16]16); $writer.Write([Text.Encoding]::ASCII.GetBytes('data'))
    $writer.Write([int]960000); $writer.Write([byte[]]::new(960000))
} finally { $writer.Dispose() }
$psi = [Diagnostics.ProcessStartInfo]::new($exePath)
$psi.UseShellExecute = $false
$psi.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$psi.ArgumentList.Add($wav)
$psi.Environment['PYLON_DATA'] = $root
$psi.Environment['PYLON_BENCHMARK'] = $reportPath
$psi.Environment['PYLON_PROFILE'] = (Join-Path $root 'startup.log')
$process = [Diagnostics.Process]::Start($psi)
if (-not $process.WaitForExit(120000)) { $process.Kill($true); throw 'Performance probe timed out.' }
if (-not (Test-Path -LiteralPath $reportPath)) { throw "Performance report was not created. Profile: $root" }
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
Copy-Item -LiteralPath (Join-Path $root 'startup.log') -Destination ($reportPath + '.startup.log')
if (-not $report.passed -or $report.queue10000.bytes -gt 100MB) { throw "Performance regression: $($report.error). See $reportPath" }
Write-Output "Performance probe passed: $reportPath (timings are informational; compare on the same machine)."
