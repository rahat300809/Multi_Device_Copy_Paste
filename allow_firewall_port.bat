@echo off
:: Auto-elevate to Administrator if not already elevated
net session >nul 2>&1
if %errorLevel% neq 0 (
    powershell -Command "Start-Process cmd -ArgumentList '/c \"\"%~f0\"\"' -Verb RunAs"
    exit /b
)

title Allow ClipSync Port in Windows Firewall
echo ========================================================
echo        Allow ClipSync Port 8765 in Windows Firewall
echo ========================================================
echo.
echo Adding inbound firewall rule for TCP port 8765...
netsh advfirewall firewall delete rule name="ClipSync Server" >nul 2>&1
netsh advfirewall firewall add rule name="ClipSync Server" dir=in action=allow protocol=TCP localport=8765
if %errorlevel% equ 0 (
    echo.
    echo [SUCCESS] Port 8765 is now OPEN for Wi-Fi, LAN, and Tailscale!
    echo Android phone and other devices can now connect freely.
) else (
    echo.
    echo [ERROR] Failed to add firewall rule.
)
echo.
pause
