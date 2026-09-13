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

# net48 launcher: detects/installs .NET 10 Desktop Runtime, then starts the app.
$launcherProj = Join-Path $root 'tools\VideoDownloader.Launcher\VideoDownloader.Launcher.csproj'
$launcherOut = Join-Path $stage 'launcher'
dotnet build $launcherProj -c $Configuration -o $launcherOut --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Launcher build failed' }

$ffmpegDir = Join-Path $package 'app\ffmpeg'
$m3u8Dir = Join-Path $package 'app\M3u8'
$appDir = Join-Path $package 'app'
New-Item -ItemType Directory -Path $package, $appDir, $ffmpegDir, $m3u8Dir -Force | Out-Null

# Root entry: net48 launcher only. Real app + tools live under app\ (same BaseDirectory layout).
$appSrc = Join-Path $appBuild 'VideoDownloader.exe'
if (-not (Test-Path -LiteralPath $appSrc)) { throw 'Missing required publish artifact: VideoDownloader.exe' }
Copy-Item -LiteralPath $appSrc -Destination (Join-Path $appDir 'VideoDownloader.exe') -Force

$launcherSrc = Join-Path $launcherOut 'VideoDownloader.exe'
if (-not (Test-Path -LiteralPath $launcherSrc)) { throw 'Missing launcher: VideoDownloader.exe' }
# Root stays minimal: only the net48 launcher exe (no NuGet side-by-side DLLs).
Copy-Item -LiteralPath $launcherSrc -Destination (Join-Path $package 'VideoDownloader.exe') -Force
$launcherDlls = @(Get-ChildItem -LiteralPath $launcherOut -Filter '*.dll' -File -ErrorAction SilentlyContinue)
if ($launcherDlls.Count -gt 0) {
  throw ("Launcher build must not emit NuGet DLLs next to the exe (found: " +
    (($launcherDlls | ForEach-Object Name) -join ', ') +
    "). Remove PackageReferences from VideoDownloader.Launcher.")
}

foreach ($tool in 'ffmpeg.exe', 'ffprobe.exe') {
  $src = Join-Path $root "tools\external\$tool"
  if (-not (Test-Path -LiteralPath $src)) { throw "Missing tool: $src" }
  Copy-Item -LiteralPath $src -Destination (Join-Path $ffmpegDir $tool) -Force
}

# yt-dlp is an application resolver, not a .NET runtime dependency. Keep it
# separate from the main executable so its absence is visible and replaceable.
$toolsDir = Join-Path $package 'app\tools'
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

# Upload differential update package to the licensing/web host.
$updateScript = Join-Path $root 'scripts\publish-update-server.ps1'
if (Test-Path -LiteralPath $updateScript) {
  Write-Host 'Publishing update package to server...'
  & $updateScript -PackageDir $target
  if ($LASTEXITCODE -ne 0) {
    throw "Update server publish failed (exit $LASTEXITCODE)."
  }
}

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
