@echo off
rem Lets phones on your Wi-Fi reach the Monitor Off phone remote (TCP port 8765).
rem Needs administrator rights - it will ask for them.

net session >nul 2>&1
if errorlevel 1 (
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

rem Remove any earlier rules (including ones Windows created from the firewall pop-up,
rem which can block the app if "Cancel" or the wrong network type was chosen).
netsh advfirewall firewall delete rule name="Monitor Off phone remote" >nul 2>&1
netsh advfirewall firewall delete rule name="MonitorOff.exe" >nul 2>&1
netsh advfirewall firewall delete rule name="MonitorOff" >nul 2>&1

netsh advfirewall firewall add rule name="Monitor Off phone remote" dir=in action=allow protocol=TCP localport=8765 profile=private,domain,public remoteip=localsubnet
if errorlevel 1 (
    echo.
    echo Could not add the firewall rule.
    pause
    exit /b 1
)

echo.
echo Done. Phones on the same Wi-Fi can now reach the remote on port 8765.
echo (Only devices on your local network are allowed, and the PIN is still required.)
pause
