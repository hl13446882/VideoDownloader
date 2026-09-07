param([switch]$Publish,[switch]$RealMachine)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$results = Join-Path $root 'artifacts\acceptance'
New-Item -ItemType Directory -Path $results -Force | Out-Null
foreach ($name in 'VideoDownloader.Core.Tests','VideoDownloader.Infrastructure.Tests') {
    dotnet test (Join-Path $root "tests\$name") -c Release --logger "trx;LogFileName=$name.trx" --results-directory $results
    if ($LASTEXITCODE -ne 0) { throw "$name failed; publication blocked" }
}
dotnet build (Join-Path $root 'tools\VideoDownloader.Verify') -c Release --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Real-machine verifier build failed; publication blocked' }
if ($Publish) {
    & (Join-Path $PSScriptRoot 'publish-beta.ps1') -SkipVerify
    if ($LASTEXITCODE -ge 8) { throw 'Publish failed' }
}
if ($RealMachine) {
    $exe = Join-Path $root 'tools\VideoDownloader.Verify\bin\Release\net10.0-windows\VideoDownloader.Verify.exe'
    $process = Start-Process -FilePath $exe -ArgumentList '--local' -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(240000)) {
        Stop-Process -Id $process.Id
        throw 'Real-machine acceptance timed out'
    }
    if ($process.ExitCode -ne 0) { throw "Real-machine acceptance failed: $($process.ExitCode); see artifacts/vd-verify-latest.log" }
}
Write-Host 'Acceptance passed for requested stages. Reports: artifacts/acceptance and artifacts/vd-verify-latest.log'
