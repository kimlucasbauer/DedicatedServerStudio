@echo off
setlocal EnableExtensions
title Dedicated Server Studio - Force Stop

set "powershellPath=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%powershellPath%" (
    echo Windows PowerShell was not found at "%powershellPath%".
    pause
    exit /b 1
)

"%powershellPath%" -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference = 'Stop'; $owners = @(Get-NetTCPConnection -LocalPort 5089 -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique); if ($owners.Count -eq 0) { Write-Host 'No process is listening on the DSS port 5089.'; exit 1 }; $stopped = 0; foreach ($owner in $owners) { $processInfo = Get-CimInstance Win32_Process -Filter ('ProcessId = ' + $owner) -ErrorAction SilentlyContinue; if (-not $processInfo -or ($processInfo.CommandLine -notmatch 'DedicatedServerStudio\.dll' -and $processInfo.Name -ne 'DedicatedServerStudio.exe')) { Write-Warning ('PID ' + $owner + ' owns port 5089 but could not be verified as DSS; leaving it running.'); continue }; Stop-Process -Id $owner -Force; Write-Host ('Force-stopped DSS PID ' + $owner + '.'); $stopped++ }; if ($stopped -eq 0) { Write-Error 'No verified DSS process was stopped.'; exit 2 }"
set "dssExit=%errorlevel%"

if not "%dssExit%"=="0" (
    echo.
    echo DSS was not stopped. Run this file as Administrator if access was denied.
    pause
    exit /b %dssExit%
)

echo DSS was force-stopped.
pause
exit /b 0
