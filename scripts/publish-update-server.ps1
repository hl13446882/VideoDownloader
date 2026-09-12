param(
  [string]$PackageDir = '',
  [string]$Channel = 'beta',
  [string]$Version = '',
  [string]$ReleaseNotes = '',
  [string]$SshHost = $(if ($env:UPDATE_SSH_HOST) { $env:UPDATE_SSH_HOST } else { '141.164.40.70' }),
  [string]$SshUser = $(if ($env:UPDATE_SSH_USER) { $env:UPDATE_SSH_USER } else { 'root' }),
  [string]$RemotePath = $(if ($env:UPDATE_REMOTE_PATH) { $env:UPDATE_REMOTE_PATH } else { '/var/lib/videodownloader-updates' }),
  [switch]$SkipUpload
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $PackageDir) {
  $PackageDir = Join-Path $root 'publish\VideoDownload'
}
if (-not (Test-Path -LiteralPath $PackageDir)) {
  throw "Package directory not found: $PackageDir"
}
if (-not $Version) {
  $exe = Join-Path $PackageDir 'app\VideoDownloader.exe'
  if (-not (Test-Path -LiteralPath $exe)) { $exe = Join-Path $PackageDir 'VideoDownloader.exe' }
  if (Test-Path -LiteralPath $exe) {
    $Version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion
    if ($Version -and $Version.Contains('+')) { $Version = $Version.Split('+')[0] }
  }
}
if (-not $Version) { $Version = '0.0.0' }

$stage = Join-Path $root ('artifacts\update-' + [Guid]::NewGuid().ToString('N'))
$filesDir = Join-Path $stage 'files'
New-Item -ItemType Directory -Path $filesDir -Force | Out-Null
robocopy $PackageDir $filesDir /E /R:1 /W:1 /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Failed to stage package files (robocopy $LASTEXITCODE)" }

$entries = New-Object System.Collections.Generic.List[object]
Get-ChildItem -LiteralPath $filesDir -Recurse -File | ForEach-Object {
  $relative = $_.FullName.Substring($filesDir.Length).TrimStart('\', '/').Replace('\', '/')
  $sha = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
  $entries.Add([pscustomobject]@{
    path = $relative
    sha256 = $sha
    size = $_.Length
  })
}

$manifest = [ordered]@{
  channel = $Channel
  version = $Version
  published_at = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
  release_notes = $ReleaseNotes
  files = $entries
}
$manifestPath = Join-Path $stage 'manifest.json'
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
Write-Host "Update manifest: $Version ($($entries.Count) files)"

if ($SkipUpload) {
  Write-Host "SkipUpload set. Staged at $stage"
  exit 0
}

$sshTarget = "$SshUser@$SshHost"
$remoteChannel = "$RemotePath/$Channel"
$remoteFiles = "$remoteChannel/files"
$remoteTmp = "$remoteChannel/manifest.json.tmp"
$remoteManifest = "$remoteChannel/manifest.json"

Write-Host "Uploading to $sshTarget:$remoteChannel ..."
ssh -o BatchMode=yes $sshTarget "mkdir -p '$remoteFiles'"
if ($LASTEXITCODE -ne 0) { throw 'ssh mkdir failed' }

# Prefer tar|ssh for atomic-ish tree sync without requiring rsync on Windows.
$tar = Get-Command tar -ErrorAction SilentlyContinue
if (-not $tar) { throw 'tar is required to upload update files' }

Push-Location $filesDir
try {
  tar -cf - . | ssh -o BatchMode=yes $sshTarget "mkdir -p '$remoteFiles' && rm -rf '$remoteFiles'/* && tar -C '$remoteFiles' -xf -"
  if ($LASTEXITCODE -ne 0) { throw 'upload of files tree failed' }
}
finally {
  Pop-Location
}

scp -o BatchMode=yes $manifestPath "${sshTarget}:${remoteTmp}"
if ($LASTEXITCODE -ne 0) { throw 'scp manifest failed' }
ssh -o BatchMode=yes $sshTarget "mv -f '$remoteTmp' '$remoteManifest'"
if ($LASTEXITCODE -ne 0) { throw 'remote manifest publish failed' }

Write-Host "Published update $Version to $sshTarget:$remoteManifest"
Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
