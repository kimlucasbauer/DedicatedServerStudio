@echo off
setlocal EnableExtensions
title Dedicated Server Studio

set "projectRoot=%~dp0"
set "powershellPath=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%powershellPath%" (
    echo Windows PowerShell was not found at "%powershellPath%".
    pause
    exit /b 1
)

pushd "%projectRoot%"
"%powershellPath%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%projectRoot%run.ps1" %*
set "dssExit=%errorlevel%"
popd
if not "%dssExit%"=="0" (
    echo.
    echo DSS did not start successfully. Review the startup error and log path above.
    pause
)
exit /b %dssExit%
