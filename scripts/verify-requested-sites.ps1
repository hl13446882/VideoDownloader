param([switch]$SkipBuild,[string[]]$Sites)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(-not $SkipBuild){
    dotnet build (Join-Path $root 'tools\VideoDownloader.Verify') -c Release --verbosity quiet
    if($LASTEXITCODE -ne 0){throw 'Verifier build failed'}
}
$urls=@(
    'https://www.douyin.com/?recommend=1',
    'https://www.douyin.com/jingxuan?modal_id=7660503158577286451',
    'https://v.douyin.com/9cY9PQIM6HY/',
    'https://www.tiktok.com/',
    'https://www.bilibili.com/video/BV1xMtw6XEAu',
    'https://www.bilibili.com/video/BV1CXWuz3E7V/',
    'https://www.bilibili.com/video/BV1x8g56LEsz/',
    'https://www.xmfyy.com/index.php/vod/play/id/290850/sid/1/nid/1.html'
)
if($Sites){$urls=@($urls|Where-Object{$hostName=([Uri]$_).Host; @($Sites|Where-Object{$hostName.Contains($_)}).Count -gt 0})}
$arguments='--live '+(($urls|ForEach-Object{'"'+$_+'"'}) -join ' ')
$exe=Join-Path $root 'tools\VideoDownloader.Verify\bin\Release\net10.0-windows\VideoDownloader.Verify.exe'
$process=Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru
if(-not $process.WaitForExit(2400000)){
    Stop-Process -Id $process.Id -Force
    throw 'Requested-site acceptance timed out'
}
$latest=Join-Path $root 'artifacts\live-acceptance\latest.txt'
$results=Join-Path $root 'artifacts\live-acceptance\results.json'
if(-not (Test-Path $latest) -or -not (Test-Path $results)){
    Write-Error 'Live acceptance evidence missing (latest.txt / results.json).'
    exit 1
}
$payload=Get-Content -Raw $results | ConvertFrom-Json
if(-not $payload.runComplete){
    Write-Error "Live acceptance results are incomplete for runId=$($payload.runId)"
    exit 1
}
if($payload.actualCount -ne $payload.expectedCount){
    Write-Error "Live acceptance record count mismatch: expected=$($payload.expectedCount) actual=$($payload.actualCount)"
    exit 1
}
Write-Host "Live acceptance evidence runId=$($payload.runId) records=$($payload.actualCount) allPassed=$($payload.allPassed)"
exit $process.ExitCode
