@echo off
cd /d "%~dp0"
if not exist "%~dp0aap-headtracker.exe" (
    echo aap-headtracker.exe is missing.
    pause
    exit /b 1
)
"%~dp0aap-headtracker.exe" list
if errorlevel 1 (
    echo Pair and connect the AirPods in Windows, and check the MagicAAP driver.
    pause
    exit /b 1
)
start "" "%~dp0AirCursorWindows.exe"
echo Wear both AirPods and look forward. Press Ctrl+C here to stop tracking.
"%~dp0aap-headtracker.exe" bridge --init-format librepods --start-format airpods5 --start-delay-ms 750
pause
