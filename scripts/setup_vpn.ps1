# VPN Smart Split-Tunnel Setup Script
# Routes: Foreign + VPN internal -> VPN tunnel | Chinese + LAN -> Physical gateway
# Requires: Run as Administrator

$ErrorActionPreference = "Stop"

Write-Host "=== VPN Smart Split-Tunnel Setup ===" -ForegroundColor Cyan

# Step 1: Record current physical gateway before tunnel changes routing
$defaultRoute = Get-NetRoute -DestinationPrefix "0.0.0.0/0" -ErrorAction SilentlyContinue |
    Sort-Object RouteMetric | Select-Object -First 1
if (-not $defaultRoute) {
    Write-Host "ERROR: No default route found. Are you connected to the network?" -ForegroundColor Red
    exit 1
}
$physicalGateway = $defaultRoute.NextHop
$physicalIfIndex = $defaultRoute.ifIndex
$physicalIP = (Get-NetIPConfiguration -InterfaceIndex $physicalIfIndex).IPv4Address.IPAddress
Write-Host "Physical gateway: $physicalGateway (interface #$physicalIfIndex, IP: $physicalIP)" -ForegroundColor Green

# Step 2: Clean up any existing tunnel
$existingTunnel = Get-NetAdapter | Where-Object { $_.Name -like "*local-windows*" -or $_.Name -like "*WireGuard*" }
if ($existingTunnel) {
    Write-Host "Removing existing tunnel service..." -ForegroundColor Yellow
    & wireguard.exe /uninstalltunnelservice local-windows 2>$null
    Start-Sleep -Seconds 2
}

# Step 3: Add critical route for VPN server BEFORE tunnel starts (prevent routing loop)
$vpnServerIP = "141.164.40.70"
Write-Host "Adding VPN server route: $vpnServerIP via $physicalGateway..." -ForegroundColor Cyan
try {
    New-NetRoute -DestinationPrefix "$vpnServerIP/32" -NextHop $physicalGateway `
        -InterfaceIndex $physicalIfIndex -RouteMetric 1 -ErrorAction Stop | Out-Null
    Write-Host "VPN server route added." -ForegroundColor Green
} catch {
    Write-Host "Warning: VPN server route may already exist: $_" -ForegroundColor Yellow
}

# Step 4: Install tunnel service
Write-Host "Installing WireGuard tunnel..." -ForegroundColor Cyan
$wgPath = (Get-Command wireguard.exe -ErrorAction SilentlyContinue).Source
if (-not $wgPath) {
    $wgPath = "C:\Program Files\WireGuard\wireguard.exe"
    if (-not (Test-Path $wgPath)) {
        Write-Host "ERROR: WireGuard not found. Install from https://www.wireguard.com/install/" -ForegroundColor Red
        exit 1
    }
}
& $wgPath /installtunnelservice "C:\Program Files\WireGuard\Data\local-windows.conf"
Start-Sleep -Seconds 3

# Step 5: Find the WireGuard tunnel interface
$tunnelAdapter = Get-NetAdapter | Where-Object { $_.Name -like "*local-windows*" }
if (-not $tunnelAdapter) {
    Write-Host "ERROR: Tunnel interface not found after installation" -ForegroundColor Red
    Write-Host "Attempting cleanup..." -ForegroundColor Yellow
    & $wgPath /uninstalltunnelservice local-windows 2>$null
    exit 1
}
$tunnelIfIndex = $tunnelAdapter.ifIndex
Write-Host "Tunnel interface: $($tunnelAdapter.Name) (#$tunnelIfIndex)" -ForegroundColor Green

# Step 6: Add routes for Chinese IPs through physical gateway
Write-Host "Adding Chinese IP routes via physical gateway..." -ForegroundColor Cyan
$chinaIPsFile = Join-Path $PSScriptRoot "china_ips.txt"
if (-not (Test-Path $chinaIPsFile)) {
    Write-Host "ERROR: china_ips.txt not found in $($PSScriptRoot)" -ForegroundColor Red
    exit 1
}

$chinaIPs = Get-Content $chinaIPsFile | Where-Object { $_ -match '^\d+\.\d+\.\d+\.\d+/\d+$' }
$added = 0
$failed = 0
foreach ($cidr in $chinaIPs) {
    try {
        New-NetRoute -DestinationPrefix $cidr -NextHop $physicalGateway `
            -InterfaceIndex $physicalIfIndex -RouteMetric 100 -ErrorAction Stop | Out-Null
        $added++
    } catch {
        $failed++
    }
}
Write-Host "Chinese IP routes: $added added, $failed failed (out of $($chinaIPs.Count))" -ForegroundColor Green

# Step 7: Add private network routes through physical gateway (LAN traffic)
$privateRoutes = @("172.16.0.0/12", "192.168.0.0/16")
foreach ($cidr in $privateRoutes) {
    try {
        New-NetRoute -DestinationPrefix $cidr -NextHop $physicalGateway `
            -InterfaceIndex $physicalIfIndex -RouteMetric 50 -ErrorAction Stop | Out-Null
        Write-Host "Private route $cidr via physical gateway" -ForegroundColor Green
    } catch {}
}

# Step 8: Flush DNS
ipconfig /flushdns | Out-Null

Write-Host ""
Write-Host "=== Setup Complete ===" -ForegroundColor Green
Write-Host "Foreign traffic -> VPN tunnel (141.164.40.70)" -ForegroundColor White
Write-Host "Chinese traffic -> Physical gateway ($physicalGateway)" -ForegroundColor White
Write-Host "VPN internal    -> Tunnel (10.0.0.0/24)" -ForegroundColor White
Write-Host ""
Write-Host "To restore: run restore_network.bat" -ForegroundColor Yellow

# Save gateway info for diagnostics
@{
    Gateway = $physicalGateway
    IfIndex = $physicalIfIndex
    PhysicalIP = $physicalIP
    SetupTime = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
} | ConvertTo-Json | Set-Content (Join-Path $PSScriptRoot "vpn_state.json")
