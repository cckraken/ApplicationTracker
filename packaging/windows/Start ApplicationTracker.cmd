@echo off
rem Starts Application Tracker with your own data and opens it in your browser.
rem Close this window to stop it.
setlocal
title Application Tracker - close this window to stop it
cd /d "%~dp0"
set "URL=http://localhost:5180"
set "DATA=%LOCALAPPDATA%\ApplicationTracker\data"

if not exist "%~dp0ApplicationTracker.App.exe" (
    echo Application Tracker can't start from inside the zip file.
    echo Close this window, right-click the zip, choose "Extract All",
    echo and double-click "Start ApplicationTracker.cmd" in the extracted folder.
    pause
    exit /b 1
)

echo Starting Application Tracker at %URL% ...
echo Your data is kept in %DATA%
echo Your browser opens in a few seconds. If it doesn't, go to %URL%
echo Close this window to stop Application Tracker.
echo.

"%~dp0ApplicationTracker.App.exe" --environment Production --urls %URL% --Tracker:DataDirectory "%DATA%" --Tracker:DatabaseFile tracker.db --Tracker:OpenBrowser true
if not "%errorlevel%"=="0" (
    echo.
    echo Application Tracker stopped with an error ^(see the message above^).
    echo If it says the address is already in use, Application Tracker is probably already
    echo running in another window: use that one, or close it and start again.
    pause
)
endlocal
