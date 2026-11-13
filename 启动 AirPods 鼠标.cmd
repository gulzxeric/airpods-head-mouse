@echo off
setlocal
cd /d "%~dp0"
if not exist "%~dp0aap-headtracker.exe" (
    echo aap-headtracker.exe is missing.
    pause
    exit /b 1
)
if not exist "%~dp0AirCursorWindows.exe" (
    echo AirCursorWindows.exe is missing.
    pause
    exit /b 1
)
"%~dp0aap-headtracker.exe" list
if errorlevel 1 (
    echo Pair and connect your AirPods in Windows, and check the MagicAAP driver.
    pause
    exit /b 1
)

echo Choose the connected AirPods model:
echo   1. AirPods 5 ^(both cases^)
echo   2. AirPods Pro 2
echo   3. AirPods Max 2
echo   4. AirPods 4 ^(with or without ANC^)
echo   5. AirPods 3
echo   6. AirPods Pro 1
echo   7. AirPods Pro 3
echo   8. AirPods Max 1
echo   0. Exit
choice /C 123456780 /N /M "Model [1-8, 0 to exit]: "
if errorlevel 9 exit /b
if errorlevel 8 (set "model=max1") else if errorlevel 7 (set "model=pro3") else if errorlevel 6 (set "model=pro1") else if errorlevel 5 (set "model=airpods3") else if errorlevel 4 (set "model=airpods4") else if errorlevel 3 (set "model=max2") else if errorlevel 2 (set "model=pro2") else if errorlevel 1 (set "model=airpods5") else exit /b

echo   T. Test tracking for 12 seconds
echo   S. Start mouse control
echo   Q. Exit
choice /C TSQ /N /M "Action [T/S/Q]: "
if errorlevel 3 exit /b
if errorlevel 2 goto start
if errorlevel 1 goto test
exit /b

:test
echo Wear the AirPods and look forward. Then turn your head to check the angles.
"%~dp0aap-headtracker.exe" bridge --model %model% --seconds 12
if errorlevel 3 echo No pose samples received. Check the headset model or try the protocol override in README.md.
pause
exit /b

:start
start "" "%~dp0AirCursorWindows.exe"
echo Wear the AirPods and look forward. Press Ctrl+C here to stop tracking.
"%~dp0aap-headtracker.exe" bridge --model %model%
pause
