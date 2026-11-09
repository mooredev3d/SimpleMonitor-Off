@echo off
rem Stops Parsec from keeping the monitors awake, so Monitor Off can turn them off.
rem Run with "undo" to put things back:  ignore-parsec-display.bat undo
rem Needs administrator rights - it will ask for them.

net session >nul 2>&1
if errorlevel 1 (
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '%1' -Verb RunAs"
    exit /b
)

if /i "%1"=="undo" (
    powercfg /requestsoverride PROCESS parsecd.exe
    echo Parsec can keep the display awake again.
) else (
    powercfg /requestsoverride PROCESS parsecd.exe DISPLAY
    echo Done. Windows now ignores Parsec's request to keep the display awake.
    echo Parsec itself still works normally.
)

echo.
echo Current overrides:
powercfg /requestsoverride
pause
