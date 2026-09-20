param(
  [string]$Configuration = 'Release',
  [switch]$SkipVerify,
  [switch]$SkipVersionBump,
  [string[]]$VerifyUrls
)
$ErrorActionPreference = 'Stop'
if (-not $SkipVerify -and (-not $VerifyUrls -or $VerifyUrls.Count -eq 0)) {
  throw 'Provide -VerifyUrls with authorized addresses, or use -SkipVerify to publish without site verification.'
}
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
. (Join-Path $root 'scripts\publish-fingerprint.ps1')

# Each publish bumps SemVer patch (e.g. 1.0.0 -> 1.0.1) unless -SkipVersionBump.
# published_at is recorded later in the update manifest and is not part of version comparison.
if ($SkipVersionBump) {
  $propsPath = Join-Path $root 'Directory.Build.props'
  $props = Get-Content -LiteralPath $propsPath -Raw
  if ($props -notmatch '<Version>(?<ver>[^<]+)</Version>') { throw 'Directory.Build.props has no <Version> node' }
  $bumpedVersion = $Matches['ver'].Trim()
  Write-Host "Publishing version $bumpedVersion (SkipVersionBump)"
} else {
  $bumpScript = Join-Path $root 'scripts\bump-release-version.ps1'
  $bumpedVersion = (& $bumpScript | Select-Object -Last 1)
  if (-not $bumpedVersion) { throw 'Version bump failed' }
  $bumpedVersion = $bumpedVersion.ToString().Trim()
  Write-Host "Publishing version $bumpedVersion"
}
if ($bumpedVersion -notmatch '^\d+\.\d+\.\d+') { throw "Version bump returned unexpected value: $bumpedVersion" }

# Fixed staging dirs so unchanged tool builds can reuse fingerprint caches.
$cacheRoot = Join-Path $root 'artifacts\publish-cache'
$stage = Join-Path $root 'artifacts\release-staging'
if (Test-Path -LiteralPath $stage) {
  Remove-Item -LiteralPath $stage -Recurse -Force
}
$appBuild = Join-Path $stage 'app-main'
$package = Join-Path $stage 'VideoDownload'
New-Item -ItemType Directory -Path $stage, $appBuild, $cacheRoot -Force | Out-Null

# --- Main app: side-by-side DLLs under app\main (win-x64 only) ---
dotnet publish (Join-Path $root 'src\VideoDownloader.UI\VideoDownloader.UI.csproj') `
  -c $Configuration -r win-x64 --self-contained false `
  -p:PublishSingleFile=false `
  -p:DebugType=None -p:DebugSymbols=false -p:PublishReadyToRun=false `
  -o $appBuild
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed' }

# Drop non-Windows-x64 native payloads from Whisper/SQLite runtime packs.
foreach ($rid in @('win-arm64', 'win-x86', 'linux-x64', 'osx-arm64', 'osx-x64', 'browser-wasm')) {
  $extra = Join-Path $appBuild "runtimes\$rid"
  if (Test-Path -LiteralPath $extra) {
    Remove-Item -LiteralPath $extra -Recurse -Force
    Write-Host "Removed non-target runtime pack: runtimes\$rid"
  }
}

# --- N_m3u8DL-RE: rebuild only when sources change ---
$m3u8Proj = Join-Path $root 'N_m3u8DL-RE-main\src\N_m3u8DL-RE\N_m3u8DL-RE.csproj'
$m3u8Inputs = @(
  (Join-Path $root 'N_m3u8DL-RE-main\src\N_m3u8DL-RE'),
  (Join-Path $root 'N_m3u8DL-RE-main\src\N_m3u8DL-RE.Common'),
  (Join-Path $root 'N_m3u8DL-RE-main\src\N_m3u8DL-RE.Parser')
)
$m3u8Cache = Invoke-CachedBuild -CacheName 'm3u8' -CacheRoot $cacheRoot -InputPaths $m3u8Inputs -OutputFileName 'N_m3u8DL-RE.exe' -Build {
  param($outDir)
  dotnet publish $m3u8Proj `
    -c $Configuration -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishAot=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false `
    -o $outDir
  if ($LASTEXITCODE -ne 0) { throw 'N_m3u8DL-RE publish failed' }
}

# --- Launcher: rebuild only when sources change ---
$launcherProj = Join-Path $root 'tools\VideoDownloader.Launcher\VideoDownloader.Launcher.csproj'
$launcherInputs = @(
  (Join-Path $root 'tools\VideoDownloader.Launcher'),
  (Join-Path $root 'src\VideoDownloader.UI\Assets\app.ico')
)
$launcherCache = Invoke-CachedBuild -CacheName 'launcher' -CacheRoot $cacheRoot -InputPaths $launcherInputs -OutputFileName 'VideoBrowser.exe' -Build {
  param($outDir)
  dotnet build $launcherProj -c $Configuration -o $outDir --nologo -v q
  if ($LASTEXITCODE -ne 0) { throw 'Launcher build failed' }
  $dlls = @(Get-ChildItem -LiteralPath $outDir -Filter '*.dll' -File -ErrorAction SilentlyContinue)
  if ($dlls.Count -gt 0) {
    throw ("Launcher build must not emit NuGet DLLs next to the exe (found: " +
      (($dlls | ForEach-Object Name) -join ', ') +
      "). Remove PackageReferences from VideoDownloader.Launcher.")
  }
}

# --- Compatibility forwarder at app\VideoDownloader.exe (for old launchers) ---
$forwarderProj = Join-Path $root 'tools\VideoDownloader.AppForwarder\VideoDownloader.AppForwarder.csproj'
$forwarderInputs = @(
  (Join-Path $root 'tools\VideoDownloader.AppForwarder'),
  (Join-Path $root 'src\VideoDownloader.UI\Assets\app.ico')
)
$forwarderCache = Invoke-CachedBuild -CacheName 'app-forwarder' -CacheRoot $cacheRoot -InputPaths $forwarderInputs -OutputFileName 'VideoDownloader.exe' -Build {
  param($outDir)
  dotnet build $forwarderProj -c $Configuration -o $outDir --nologo -v q
  if ($LASTEXITCODE -ne 0) { throw 'App forwarder build failed' }
}

$ffmpegDir = Join-Path $package 'app\ffmpeg'
$m3u8Dir = Join-Path $package 'app\M3u8'
$appDir = Join-Path $package 'app'
$mainDir = Join-Path $package 'app\main'
New-Item -ItemType Directory -Path $package, $appDir, $mainDir, $ffmpegDir, $m3u8Dir -Force | Out-Null

# Main app tree → app\main\
robocopy $appBuild $mainDir /E /R:0 /W:0 /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy main app failed with code $LASTEXITCODE" }
$mainExe = Join-Path $mainDir 'VideoDownloader.exe'
if (-not (Test-Path -LiteralPath $mainExe)) { throw 'Missing required publish artifact: app\main\VideoDownloader.exe' }

# Old-launcher compatibility stub at app\VideoDownloader.exe
Copy-Item -LiteralPath (Join-Path $forwarderCache 'VideoDownloader.exe') -Destination (Join-Path $appDir 'VideoDownloader.exe') -Force

$launcherSrc = Join-Path $launcherCache 'VideoBrowser.exe'
if (-not (Test-Path -LiteralPath $launcherSrc)) { throw 'Missing launcher: VideoBrowser.exe' }
Copy-Item -LiteralPath $launcherSrc -Destination (Join-Path $package 'VideoBrowser.exe') -Force

foreach ($tool in 'ffmpeg.exe', 'ffprobe.exe') {
  $src = Join-Path $root "tools\external\$tool"
  if (-not (Test-Path -LiteralPath $src)) { throw "Missing tool: $src" }
  Copy-Item -LiteralPath $src -Destination (Join-Path $ffmpegDir $tool) -Force
}

$toolsDir = Join-Path $package 'app\tools'
$ytDlpSrc = Join-Path $root 'tools\external\yt-dlp.exe'
if (-not (Test-Path -LiteralPath $ytDlpSrc)) { throw "Missing resolver: $ytDlpSrc" }
New-Item -ItemType Directory -Path $toolsDir -Force | Out-Null
Copy-Item -LiteralPath $ytDlpSrc -Destination (Join-Path $toolsDir 'yt-dlp.exe') -Force

Copy-Item -LiteralPath (Join-Path $m3u8Cache 'N_m3u8DL-RE.exe') -Destination (Join-Path $m3u8Dir 'N_m3u8DL-RE.exe') -Force
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
robocopy $package $target /MIR /R:0 /W:0 /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
$rc = $LASTEXITCODE
if ($rc -ge 8) { throw "robocopy failed with code $rc" }

Write-Host "Published: $target\VideoBrowser.exe (main app under app\main\)"

$updateScript = Join-Path $root 'scripts\publish-update-server.ps1'
if (Test-Path -LiteralPath $updateScript) {
  Write-Host 'Publishing update package to server...'
  & $updateScript -PackageDir $target
  if ($LASTEXITCODE -ne 0) {
    throw "Update server publish failed (exit $LASTEXITCODE)."
  }
}

$verifyScript = Join-Path $root 'scripts\verify-sites.ps1'
if (-not $SkipVerify -and (Test-Path -LiteralPath $verifyScript)) {
  Write-Host 'Running auto site verification...'
  & $verifyScript -Configuration $Configuration -Urls $VerifyUrls
  if ($LASTEXITCODE -ne 0) {
    Write-Host "WARNING: site verification failed (exit $LASTEXITCODE). See %TEMP%\vd-verify-*.log"
    exit $LASTEXITCODE
  }
}
elseif ($SkipVerify) {
  Write-Host 'Skipped auto site verification (-SkipVerify).'
}
