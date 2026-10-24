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
echo Adding inbound firewall rules for TCP 8765 and UDP 8766 (Private, Public, Domain)...
netsh advfirewall firewall delete rule name="ClipSync Server" >nul 2>&1
netsh advfirewall firewall add rule name="ClipSync Server" dir=in action=allow protocol=TCP localport=8765 profile=any
netsh advfirewall firewall delete rule name="ClipSync Discovery" >nul 2>&1
netsh advfirewall firewall add rule name="ClipSync Discovery" dir=in action=allow protocol=UDP localport=8766 profile=any
if %errorlevel% equ 0 (
    echo.
    echo [SUCCESS] Ports 8765 (TCP) and 8766 (UDP) are now OPEN for all networks!
    echo Phones and laptops can now discover and connect freely.
) else (
    echo.
    echo [ERROR] Failed to add firewall rules. Please ensure you clicked Yes on UAC.
)
echo.
pause
