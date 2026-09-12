param(
  [string]$PropsPath = '',
  [string]$IssPath = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $PropsPath) { $PropsPath = Join-Path $root 'Directory.Build.props' }
if (-not $IssPath) { $IssPath = Join-Path $root 'installer\VideoDownloader.iss' }
if (-not (Test-Path -LiteralPath $PropsPath)) { throw "Missing $PropsPath" }

$props = Get-Content -LiteralPath $PropsPath -Raw
if ($props -notmatch '<Version>(?<ver>[^<]+)</Version>') {
  throw 'Directory.Build.props has no <Version> node'
}
$current = $Matches['ver'].Trim()
if ($current -notmatch '^(?<maj>\d+)\.(?<min>\d+)\.(?<pat>\d+)(?<pre>-[0-9A-Za-z\.-]+)?$') {
  throw "Unsupported version format: $current"
}
$maj = [int]$Matches['maj']
$min = [int]$Matches['min']
$pat = [int]$Matches['pat'] + 1
$pre = $Matches['pre']
$next = "$maj.$min.$pat$pre"

$utf8NoBom = New-Object System.Text.UTF8Encoding $false
$props = [regex]::Replace($props, '<Version>[^<]+</Version>', "<Version>$next</Version>")
[System.IO.File]::WriteAllText($PropsPath, $props, $utf8NoBom)

if (Test-Path -LiteralPath $IssPath) {
  $iss = Get-Content -LiteralPath $IssPath -Raw
  $iss = [regex]::Replace($iss, '(?m)^#define AppVersion\s+".*"', "#define AppVersion `"$next`"")
  $iss = [regex]::Replace($iss, 'OutputBaseFilename=VideoDownloader-[^\r\n]*-win-x64-setup', "OutputBaseFilename=VideoDownloader-$next-win-x64-setup")
  [System.IO.File]::WriteAllText($IssPath, $iss, $utf8NoBom)
}

Write-Host "Version bumped: $current -> $next"
# Sole pipeline output for callers.
$next
