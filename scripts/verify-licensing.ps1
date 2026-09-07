param(
  [string]$Endpoint = 'http://141.164.40.70:11111',
  [string]$MachineId
)

$ErrorActionPreference = 'Stop'

if (-not $MachineId) {
  $serial = (Get-CimInstance Win32_BaseBoard | Select-Object -First 1 -ExpandProperty SerialNumber).Trim().ToUpperInvariant()
  if ([string]::IsNullOrWhiteSpace($serial)) { throw 'No baseboard serial number returned.' }
  $bytes = [Text.Encoding]::UTF8.GetBytes("VideoDownloader/v1`n$serial")
  $MachineId = ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))).ToLowerInvariant()
}

if ($MachineId -notmatch '^[0-9a-f]{64}$') { throw 'MachineId must be a 64-character lowercase SHA-256 hash.' }

$health = Invoke-RestMethod "$Endpoint/health"
if ($health.status -ne 'ok') { throw 'License service health check failed.' }

$licenseResponse = Invoke-WebRequest -Method Post -Uri "$Endpoint/api/v1/license/check" `
  -ContentType 'application/json' -Body (@{ machine_id = $MachineId } | ConvertTo-Json -Compress)
$licenseJson = [Text.Json.JsonDocument]::Parse($licenseResponse.Content).RootElement
$publicKey = (Invoke-RestMethod "$Endpoint/api/v1/public-key").public_key_pem

$machine = $licenseJson.GetProperty('machine_id').GetString()
$edition = $licenseJson.GetProperty('edition').GetString()
$issued = $licenseJson.GetProperty('issued_at').GetString()
$expires = $licenseJson.GetProperty('expires_at').GetString()
$canonical = [Text.Encoding]::UTF8.GetBytes("$machine`n$edition`n$issued`n$expires")
$signatureText = $licenseJson.GetProperty('signature').GetString().Replace('-', '+').Replace('_', '/')
$signatureText += '=' * ((4 - $signatureText.Length % 4) % 4)
$ecdsa = [Security.Cryptography.ECDsa]::Create()
$ecdsa.ImportFromPem($publicKey)
if (-not $ecdsa.VerifyData($canonical, [Convert]::FromBase64String($signatureText), [Security.Cryptography.HashAlgorithmName]::SHA256)) {
  throw 'License signature is not valid P1363 ECDSA data.'
}

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$previousLiveTest = $env:VD_LICENSE_LIVE_TEST
try {
  $env:VD_LICENSE_LIVE_TEST = '1'
  & dotnet test (Join-Path $root 'tests\VideoDownloader.Infrastructure.Tests\VideoDownloader.Infrastructure.Tests.csproj') --filter 'FullyQualifiedName~LicenseProtocolTests'
  if ($LASTEXITCODE -ne 0) { throw 'Actual LicenseService protocol verification failed.' }
}
finally {
  $env:VD_LICENSE_LIVE_TEST = $previousLiveTest
  $ecdsa.Dispose()
}

[pscustomobject]@{
  MachineId = $MachineId
  Edition = $edition
  ExpiresAt = $expires
  SignatureValid = $true
  ClientPolicy = 'Actual LicenseService: server verification on each startup, full license, no download limit'
} | Format-List
