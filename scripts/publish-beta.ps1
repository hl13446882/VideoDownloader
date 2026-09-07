param(
  [string]$Configuration = 'Release',
  [switch]$SkipVerify,
  [string[]]$VerifyUrls
)
$ErrorActionPreference = 'Stop'
if (-not $SkipVerify -and (-not $VerifyUrls -or $VerifyUrls.Count -eq 0)) {
  throw 'Provide -VerifyUrls with authorized addresses, or use -SkipVerify to publish without site verification.'
}
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$stage = Join-Path $root ('artifacts\release-' + [Guid]::NewGuid().ToString('N'))
$appBuild = Join-Path $stage 'app'
$m3u8Build = Join-Path $stage 'm3u8-build'
$package = Join-Path $stage 'VideoDownload'

dotnet publish (Join-Path $root 'src\VideoDownloader.UI\VideoDownloader.UI.csproj') `
  -c $Configuration -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=None -p:DebugSymbols=false -p:PublishReadyToRun=false `
  -o $appBuild
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed' }

dotnet publish (Join-Path $root 'N_m3u8DL-RE-main\src\N_m3u8DL-RE\N_m3u8DL-RE.csproj') `
  -c $Configuration -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:PublishAot=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false `
  -o $m3u8Build
if ($LASTEXITCODE -ne 0) { throw 'N_m3u8DL-RE publish failed' }

$ffmpegDir = Join-Path $package 'ffmpeg'
$m3u8Dir = Join-Path $package 'M3u8'
New-Item -ItemType Directory -Path $package, $ffmpegDir, $m3u8Dir -Force | Out-Null

# Root: framework-dependent single-file host. Do not ship the .NET runtime,
# loose framework/package assemblies, or WebView2 native loader duplicates.
$rootKeep = @(
  'VideoDownloader.exe'
)
foreach ($name in $rootKeep) {
  $src = Join-Path $appBuild $name
  if (-not (Test-Path -LiteralPath $src)) { throw "Missing required publish artifact: $name" }
  Copy-Item -LiteralPath $src -Destination (Join-Path $package $name) -Force
}

foreach ($tool in 'ffmpeg.exe', 'ffprobe.exe') {
  $src = Join-Path $root "tools\external\$tool"
  if (-not (Test-Path -LiteralPath $src)) { throw "Missing tool: $src" }
  Copy-Item -LiteralPath $src -Destination (Join-Path $ffmpegDir $tool) -Force
}

# yt-dlp is an application resolver, not a .NET runtime dependency. Keep it
# separate from the main executable so its absence is visible and replaceable.
$toolsDir = Join-Path $package 'tools'
$ytDlpSrc = Join-Path $root 'tools\external\yt-dlp.exe'
if (-not (Test-Path -LiteralPath $ytDlpSrc)) { throw "Missing resolver: $ytDlpSrc" }
New-Item -ItemType Directory -Path $toolsDir -Force | Out-Null
Copy-Item -LiteralPath $ytDlpSrc -Destination (Join-Path $toolsDir 'yt-dlp.exe') -Force

foreach ($name in 'N_m3u8DL-RE.exe') {
  $src = Join-Path $m3u8Build $name
  if (Test-Path -LiteralPath $src) {
    Copy-Item -LiteralPath $src -Destination (Join-Path $m3u8Dir $name) -Force
  }
}
$license = Join-Path $root 'N_m3u8DL-RE-main\LICENSE'
if (Test-Path -LiteralPath $license) {
  Copy-Item -LiteralPath $license -Destination (Join-Path $m3u8Dir 'LICENSE') -Force
}

$publishRoot = Join-Path $root 'publish'
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
$target = Join-Path $publishRoot 'VideoDownload'
if ([IO.Path]::GetFullPath($target) -ne [IO.Path]::GetFullPath((Join-Path $root 'publish\VideoDownload'))) {
  throw 'Invalid publish target'
}

New-Item -ItemType Directory -Path $target -Force | Out-Null
# Replace in-place so a running previous build locking the folder does not block publish.
robocopy $package $target /MIR /R:0 /W:0 /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
$rc = $LASTEXITCODE
if ($rc -ge 8) { throw "robocopy failed with code $rc" }

Write-Host "Published: $target\VideoDownloader.exe"

# Auto site verification (YouTube / Douyin / Bilibili detect + switch).
$verifyScript = Join-Path $root 'scripts\verify-sites.ps1'
if (-not $SkipVerify -and (Test-Path -LiteralPath $verifyScript)) {
  Write-Host 'Running auto site verification...'
  & $verifyScript -Configuration $Configuration -Urls $VerifyUrls
  if ($LASTEXITCODE -ne 0) {
    Write-Host "WARNING: site verification failed (exit $LASTEXITCODE). See %TEMP%\vd-verify-*.log"
    # Non-zero so agents notice, but do not delete the published package.
    exit $LASTEXITCODE
  }
}
elseif ($SkipVerify) {
  Write-Host 'Skipped auto site verification (-SkipVerify).'
}
