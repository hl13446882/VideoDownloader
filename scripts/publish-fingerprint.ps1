# Shared helpers for publish-beta.ps1: content fingerprints and cached tool builds.
function Get-PathFingerprint {
  param(
    [Parameter(Mandatory = $true)]
    [string[]]$Paths
  )

  $sha = [System.Security.Cryptography.SHA256]::Create()
  $buffer = New-Object System.IO.MemoryStream
  try {
    foreach ($path in ($Paths | Sort-Object -Unique)) {
      if (-not (Test-Path -LiteralPath $path)) { continue }
      $item = Get-Item -LiteralPath $path
      if ($item.PSIsContainer) {
        Get-ChildItem -LiteralPath $path -Recurse -File -ErrorAction SilentlyContinue |
          Sort-Object FullName |
          ForEach-Object {
            $rel = $_.FullName.Substring($path.Length).TrimStart('\', '/')
            $fileHash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            $line = [System.Text.Encoding]::UTF8.GetBytes(($rel + '|' + $fileHash + "`n").ToLowerInvariant())
            $buffer.Write($line, 0, $line.Length)
          }
      }
      else {
        $fileHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        $line = [System.Text.Encoding]::UTF8.GetBytes(($item.Name + '|' + $fileHash + "`n").ToLowerInvariant())
        $buffer.Write($line, 0, $line.Length)
      }
    }

    $buffer.Position = 0
    $hash = $sha.ComputeHash($buffer)
    return ([System.BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
  }
  finally {
    $buffer.Dispose()
    $sha.Dispose()
  }
}

function Invoke-CachedBuild {
  param(
    [Parameter(Mandatory = $true)][string]$CacheName,
    [Parameter(Mandatory = $true)][string]$CacheRoot,
    [Parameter(Mandatory = $true)][string[]]$InputPaths,
    [Parameter(Mandatory = $true)][string]$OutputFileName,
    [Parameter(Mandatory = $true)][scriptblock]$Build
  )

  $cacheDir = Join-Path $CacheRoot $CacheName
  $fingerprintPath = Join-Path $cacheDir 'fingerprint.txt'
  $outputPath = Join-Path $cacheDir $OutputFileName
  $fp = Get-PathFingerprint -Paths $InputPaths

  if ((Test-Path -LiteralPath $outputPath) -and (Test-Path -LiteralPath $fingerprintPath)) {
    $prev = (Get-Content -LiteralPath $fingerprintPath -Raw).Trim()
    if ($prev -eq $fp) {
      Write-Host "Cache hit: $CacheName (fingerprint unchanged)"
      return $cacheDir
    }
  }

  Write-Host "Cache miss: $CacheName — rebuilding…"
  New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null
  Get-ChildItem -LiteralPath $cacheDir -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne 'fingerprint.txt' } |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

  & $Build $cacheDir
  if (-not (Test-Path -LiteralPath $outputPath)) {
    throw "Cached build '$CacheName' did not produce $OutputFileName"
  }

  Set-Content -LiteralPath $fingerprintPath -Value $fp -Encoding ascii -NoNewline
  return $cacheDir
}
