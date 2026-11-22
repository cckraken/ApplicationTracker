@echo off
rem Starts Application Tracker with made-up demo data, to look around.
rem It never touches your own data. Close this window to stop it.
setlocal
title Application Tracker DEMO - close this window to stop it
cd /d "%~dp0"
set "URL=http://localhost:5181"
set "DATA=%LOCALAPPDATA%\ApplicationTracker\data"

if not exist "%~dp0ApplicationTracker.App.exe" (
    echo The demo can't start from inside the zip file.
    echo Close this window, right-click the zip, choose "Extract All",
    echo and double-click "Try the demo.cmd" in the extracted folder.
    pause
    exit /b 1
)

echo Starting the Application Tracker demo at %URL% ...
echo This uses made-up demo data (demo.db), kept separately from your own.
echo Your browser opens in a few seconds. If it doesn't, go to %URL%
echo Close this window to stop the demo.
echo.

"%~dp0ApplicationTracker.App.exe" --environment Production --urls %URL% --Tracker:DataDirectory "%DATA%" --Tracker:DatabaseFile demo.db --Tracker:SeedDemoData true --Tracker:OpenBrowser true
if not "%errorlevel%"=="0" (
    echo.
    echo The demo stopped with an error ^(see the message above^).
    echo If it says the address is already in use, the demo is probably already running
    echo in another window: use that one, or close it and start again.
    pause
)
endlocal
