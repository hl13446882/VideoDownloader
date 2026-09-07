param(
    [string]$Configuration = 'Release',
    [switch]$SkipBuild,
    [string[]]$Urls
)

$ErrorActionPreference = 'Stop'
if (-not $Urls -or $Urls.Count -eq 0) {
    throw 'Pass explicit authorized test addresses with -Urls. No default videos are used.'
}
foreach ($url in $Urls) {
    $parsed = $null
    if (-not [Uri]::TryCreate($url, [UriKind]::Absolute, [ref]$parsed) -or
        $parsed.Scheme -notin @('http', 'https') -or $url.Contains('"')) {
        throw 'Test addresses must be valid HTTP(S) URLs without literal quotes.'
    }
}
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$publish = Join-Path $root 'publish\VideoDownload'
$project = Join-Path $root 'tools\VideoDownloader.Verify\VideoDownloader.Verify.csproj'
$outDir = Join-Path $root 'artifacts\verify'

if (-not (Test-Path (Join-Path $publish 'tools\yt-dlp.exe'))) {
    throw "Missing published app at $publish. Run scripts\publish-beta.ps1 first."
}

if (-not $SkipBuild) {
    dotnet build $project -c $Configuration --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Verify project build failed' }
}

New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$exe = Join-Path $root "tools\VideoDownloader.Verify\bin\$Configuration\net10.0-windows\VideoDownloader.Verify.exe"
if (-not (Test-Path $exe)) {
    throw "Verify exe not found: $exe"
}

if (Get-Process VideoDownloader, VideoDownloader.Verify -ErrorAction SilentlyContinue) {
    throw 'Close VideoDownloader and any existing verifier before testing; active sessions will not be terminated automatically.'
}

Write-Host "Running site verification (WebView2 + pipeline)..."
$arguments = ($Urls | ForEach-Object { '"' + $_ + '"' }) -join ' '
$p = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory (Split-Path $exe -Parent) -WindowStyle Hidden -PassThru -Wait
$code = $p.ExitCode
Write-Host "Verify exit code: $code"
$repoLog = Join-Path $root 'artifacts\vd-verify-latest.log'
if (Test-Path $repoLog) {
  Write-Host "----- artifacts/vd-verify-latest.log -----"
  Get-Content -LiteralPath $repoLog
}
$latestLog = Get-ChildItem -Path $env:TEMP -Filter 'vd-verify-*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($latestLog -and -not (Test-Path $repoLog)) {
  Write-Host "----- verify log: $($latestLog.FullName) -----"
  Get-Content -LiteralPath $latestLog.FullName -Tail 80
}
if ($code -ne 0) {
  Write-Host "FAIL: detection/switch verification did not pass."
  exit $code
}

Write-Host "PASS: all sample sites produced addresses and switching updated detection."
exit 0
