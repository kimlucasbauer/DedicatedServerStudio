@echo off
setlocal EnableExtensions
title Dedicated Server Studio - Manual Update

set "projectRoot=%~dp0"
set "powershellPath=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%powershellPath%" (
    echo Windows PowerShell was not found at "%powershellPath%".
    pause
    exit /b 1
)

"%powershellPath%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%projectRoot%DSS-ManualUpdate.ps1" %*
set "dssExit=%errorlevel%"
if not "%dssExit%"=="0" (
    echo.
    echo The manual update did not complete. Review the message and the logs folder.
    pause
)
exit /b %dssExit%
