@echo off
rem Builds SimpleMonitorOff.exe into the "build" folder next to this file.
cd /d "%~dp0"

rem Close a running copy so the .exe can be replaced (also the old MonitorOff.exe name).
taskkill /im SimpleMonitorOff.exe /f >nul 2>&1
taskkill /im MonitorOff.exe /f >nul 2>&1

dotnet publish MonitorOff.csproj -c Release
if errorlevel 1 (
    echo.
    echo Build FAILED - see the errors above.
    pause
    exit /b 1
)

echo.
echo Build succeeded: "%~dp0build\SimpleMonitorOff.exe"
pause
