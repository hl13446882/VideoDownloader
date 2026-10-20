@echo off
chcp 65001 >nul 2>&1
echo ============================================
echo   WireGuard VPN 网络恢复工具
echo ============================================
echo.

echo [1/4] 停止 WireGuard 隧道服务...
sc stop "WireGuardTunnel$local-windows" >nul 2>&1
timeout /t 2 /nobreak >nul
echo       完成.

echo [2/4] 删除 WireGuard 隧道服务...
sc delete "WireGuardTunnel$local-windows" >nul 2>&1
timeout /t 1 /nobreak >nul
echo       完成.

echo [3/4] 刷新 DNS 缓存...
ipconfig /flushdns >nul 2>&1
echo       完成.

echo [4/4] 验证网络连通性...
ping -n 1 -w 3000 192.168.1.1 >nul 2>&1
if %errorlevel%==0 (
    echo       局域网: 正常
) else (
    echo       局域网: 不通 - 请检查网络连接
)
ping -n 1 -w 5000 baidu.com >nul 2>&1
if %errorlevel%==0 (
    echo       互联网: 正常
) else (
    echo       互联网: 不通 - 请尝试禁用再启用网络适配器
)

echo.
echo ============================================
echo   网络已恢复正常!
echo ============================================
echo.
pause
