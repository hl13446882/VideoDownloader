@echo off
chcp 65001 >nul 2>&1
echo ============================================
echo   WireGuard VPN 智能分流 安装/卸载
echo ============================================
echo.
echo  模式: 国外流量走VPN, 国内/局域网直连
echo  VPN 内网 (10.0.0.0/24) 走VPN
echo.

sc query "WireGuardTunnel$local-windows" >nul 2>&1
if %errorlevel%==0 (
    echo [检测到已安装的隧道服务]
    echo.
    set /p ACTION="卸载隧道? (Y/N): "
    if /i "!ACTION!"=="Y" (
        echo 正在卸载...
        sc stop "WireGuardTunnel$local-windows" >nul 2>&1
        sc delete "WireGuardTunnel$local-windows" >nul 2>&1
        echo 已卸载.
    )
) else (
    echo [安装隧道服务]
    echo.
    "%ProgramFiles%\WireGuard\wireguard.exe" /installtunnelservice "%~dp0..\..\Program Files\WireGuard\Data\local-windows.conf"
    if %errorlevel%==0 (
        echo 安装成功! VPN 隧道已启动.
    ) else (
        echo 安装失败. 请确保以管理员身份运行.
    )
)

echo.
pause
