@echo off
chcp 65001 >nul 2>&1
echo ============================================
echo   WireGuard VPN 智能分流 - 一键安装
echo ============================================
echo.
echo  国外流量 -> VPN 隧道 (141.164.40.70)
echo  国内流量 -> 物理网关 (直连)
echo  VPN内网  -> 隧道 (10.0.0.0/24)
echo.
echo 恢复: 双击 restore_network.bat
echo ============================================
echo.

powershell -ExecutionPolicy Bypass -File "%~dp0setup_vpn.ps1"

echo.
pause
