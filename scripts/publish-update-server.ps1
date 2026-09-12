param(
  [string]$PackageDir = '',
  [string]$Channel = 'beta',
  [string]$Version = '',
  [string]$ReleaseNotes = '',
  [string]$SshHost = $(if ($env:UPDATE_SSH_HOST) { $env:UPDATE_SSH_HOST } else { '141.164.40.70' }),
  [string]$SshUser = $(if ($env:UPDATE_SSH_USER) { $env:UPDATE_SSH_USER } else { 'root' }),
  [string]$RemotePath = $(if ($env:UPDATE_REMOTE_PATH) { $env:UPDATE_REMOTE_PATH } else { '/var/lib/videodownloader-updates' }),
  [switch]$SkipUpload,
  [switch]$ForceFullUpload
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

$publishedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
$stage = Join-Path $root ('artifacts\update-' + [Guid]::NewGuid().ToString('N'))
$filesDir = Join-Path $stage 'files'
New-Item -ItemType Directory -Path $filesDir -Force | Out-Null
robocopy $PackageDir $filesDir /E /R:1 /W:1 /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Failed to stage package files (robocopy $LASTEXITCODE)" }

$localMap = @{}
$entries = New-Object System.Collections.Generic.List[object]
Get-ChildItem -LiteralPath $filesDir -Recurse -File | ForEach-Object {
  $relative = $_.FullName.Substring($filesDir.Length).TrimStart('\', '/').Replace('\', '/')
  $sha = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
  $localMap[$relative] = $sha
  $entries.Add([pscustomobject]@{
    path = $relative
    sha256 = $sha
    size = $_.Length
  })
}

$manifest = [ordered]@{
  channel = $Channel
  version = $Version
  published_at = $publishedAt
  release_notes = $ReleaseNotes
  files = $entries
}
$manifestPath = Join-Path $stage 'manifest.json'
$json = $manifest | ConvertTo-Json -Depth 6
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText($manifestPath, $json, $utf8NoBom)
Write-Host "Update manifest: $Version ($($entries.Count) files) published_at=$publishedAt"

if ($SkipUpload) {
  Write-Host "SkipUpload set. Staged at $stage"
  exit 0
}

$sshTarget = "$SshUser@$SshHost"
$remoteChannel = "$RemotePath/$Channel"
$remoteFiles = "$remoteChannel/files"
$remoteTmp = "$remoteChannel/manifest.json.tmp"
$remoteManifest = "$remoteChannel/manifest.json"
$tar = Get-Command tar -ErrorAction SilentlyContinue
if (-not $tar) { throw 'tar is required to upload update files' }

ssh -o BatchMode=yes $sshTarget "mkdir -p '$remoteFiles'"
if ($LASTEXITCODE -ne 0) { throw 'ssh mkdir failed' }

# Pull remote manifest for differential upload (version check uses SemVer only; published_at is metadata).
$remoteMap = @{}
$remoteManifestLocal = Join-Path $stage 'remote-manifest.json'
$hasRemote = $false
if (-not $ForceFullUpload) {
  scp -o BatchMode=yes "${sshTarget}:${remoteManifest}" $remoteManifestLocal 2>$null
  if (($LASTEXITCODE -eq 0) -and (Test-Path -LiteralPath $remoteManifestLocal)) {
    try {
      $remoteJson = Get-Content -LiteralPath $remoteManifestLocal -Raw -Encoding UTF8
      if ($remoteJson.Length -gt 0 -and [int][char]$remoteJson[0] -eq 0xFEFF) {
        $remoteJson = $remoteJson.Substring(1)
      }
      $remoteObj = $remoteJson | ConvertFrom-Json
      foreach ($f in @($remoteObj.files)) {
        if ($f.path -and $f.sha256) {
          $remoteMap[[string]$f.path] = ([string]$f.sha256).ToLowerInvariant()
        }
      }
      $hasRemote = $true
      Write-Host "Remote manifest loaded: version=$($remoteObj.version) files=$($remoteMap.Count)"
    }
    catch {
      Write-Host "Remote manifest unreadable; falling back to full upload."
      $remoteMap = @{}
      $hasRemote = $false
    }
  }
  else {
    Write-Host "No remote manifest; full upload for first publish."
  }
}

$changed = New-Object System.Collections.Generic.List[string]
foreach ($path in $localMap.Keys) {
  if (-not $remoteMap.ContainsKey($path) -or $remoteMap[$path] -ne $localMap[$path]) {
    $changed.Add($path)
  }
}
$deletes = New-Object System.Collections.Generic.List[string]
foreach ($path in $remoteMap.Keys) {
  if (-not $localMap.ContainsKey($path)) {
    $deletes.Add($path)
  }
}

Write-Host "Diff: changed=$($changed.Count) deleted=$($deletes.Count) total=$($entries.Count)"

if ($ForceFullUpload -or -not $hasRemote) {
  $bundle = Join-Path $stage 'files.tar.gz'
  Write-Host "Full upload to ${sshTarget}:${remoteChannel} ..."
  Push-Location $filesDir
  try {
    & tar -czf $bundle .
    if ($LASTEXITCODE -ne 0) { throw 'local tar create failed' }
  }
  finally { Pop-Location }

  scp -o BatchMode=yes $bundle "${sshTarget}:/tmp/vd-update-files.tar.gz"
  if ($LASTEXITCODE -ne 0) { throw 'scp bundle failed' }

  ssh -o BatchMode=yes $sshTarget "rm -rf '$remoteFiles' && mkdir -p '$remoteFiles' && tar -C '$remoteFiles' -xzf /tmp/vd-update-files.tar.gz && rm -f /tmp/vd-update-files.tar.gz && chown -R videodownloader:videodownloader '$remoteChannel' && chmod -R u+rwX,go+rX '$remoteChannel'"
  if ($LASTEXITCODE -ne 0) { throw 'remote extract failed' }
}
else {
  if ($changed.Count -gt 0) {
    $deltaDir = Join-Path $stage 'delta'
    New-Item -ItemType Directory -Path $deltaDir -Force | Out-Null
    foreach ($rel in $changed) {
      $src = Join-Path $filesDir ($rel.Replace('/', '\'))
      $dst = Join-Path $deltaDir ($rel.Replace('/', '\'))
      $dstParent = Split-Path -Parent $dst
      if (-not (Test-Path -LiteralPath $dstParent)) {
        New-Item -ItemType Directory -Path $dstParent -Force | Out-Null
      }
      Copy-Item -LiteralPath $src -Destination $dst -Force
    }

    $bundle = Join-Path $stage 'delta.tar.gz'
    Write-Host "Differential upload ($($changed.Count) files) to ${sshTarget}:${remoteChannel} ..."
    Push-Location $deltaDir
    try {
      & tar -czf $bundle .
      if ($LASTEXITCODE -ne 0) { throw 'local delta tar create failed' }
    }
    finally { Pop-Location }

    scp -o BatchMode=yes $bundle "${sshTarget}:/tmp/vd-update-delta.tar.gz"
    if ($LASTEXITCODE -ne 0) { throw 'scp delta failed' }

    ssh -o BatchMode=yes $sshTarget "mkdir -p '$remoteFiles' && tar -C '$remoteFiles' -xzf /tmp/vd-update-delta.tar.gz && rm -f /tmp/vd-update-delta.tar.gz"
    if ($LASTEXITCODE -ne 0) { throw 'remote delta extract failed' }
  }
  else {
    Write-Host "No file content changes; uploading manifest only."
  }

  if ($deletes.Count -gt 0) {
    $deleteList = Join-Path $stage 'deletes.txt'
    ($deletes | ForEach-Object { $_.Replace('\', '/') }) | Set-Content -LiteralPath $deleteList -Encoding ascii
    scp -o BatchMode=yes $deleteList "${sshTarget}:/tmp/vd-update-deletes.txt"
    if ($LASTEXITCODE -ne 0) { throw 'scp deletes list failed' }
    ssh -o BatchMode=yes $sshTarget "while IFS= read -r p; do [ -n `"`$p`" ] || continue; case `"`$p`" in *..*) continue ;; esac; rm -f `"$remoteFiles/`$p`"; done < /tmp/vd-update-deletes.txt; rm -f /tmp/vd-update-deletes.txt"
    if ($LASTEXITCODE -ne 0) { throw 'remote deletes failed' }
    Write-Host "Removed $($deletes.Count) obsolete remote file(s)."
  }

  ssh -o BatchMode=yes $sshTarget "chown -R videodownloader:videodownloader '$remoteChannel' && chmod -R u+rwX,go+rX '$remoteChannel'"
  if ($LASTEXITCODE -ne 0) { throw 'remote chown failed' }
}

scp -o BatchMode=yes $manifestPath "${sshTarget}:${remoteTmp}"
if ($LASTEXITCODE -ne 0) { throw 'scp manifest failed' }
ssh -o BatchMode=yes $sshTarget "mv -f '$remoteTmp' '$remoteManifest' && chown videodownloader:videodownloader '$remoteManifest'"
if ($LASTEXITCODE -ne 0) { throw 'remote manifest publish failed' }

Write-Host "Published update $Version to ${sshTarget}:${remoteManifest} (changed=$($changed.Count), deleted=$($deletes.Count))"
Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
