param(
    [switch]$Lan,
    [switch]$Public,
    [ValidateSet('local', 'lan', 'public')]
    [string]$ListenMode,
    [string]$AssemblyPath,
    [switch]$WaitForExistingInstance,
    [int]$PreviousProcessId = 0,
    [string]$StartupSignalPath,
    [switch]$RequireNonAdministrator,
    [switch]$PauseOnFailure
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$launchMutex = $null
$ownsLaunchMutex = $false
$launchLog = $null
$logWriteFailed = $false
$exitCode = 1
$script:startupSignalPath = $StartupSignalPath

function Resolve-DssAssembly([string]$ConfiguredPath, [string]$ExpectedHash) {
    if ([string]::IsNullOrWhiteSpace($ConfiguredPath) -or [IO.Path]::IsPathRooted($ConfiguredPath)) {
        throw 'The active build path in dss-runtime.json must be relative to the DSS project folder.'
    }
    $resolvedPath = [IO.Path]::GetFullPath((Join-Path $projectRoot $ConfiguredPath))
    $rootPrefix = [IO.Path]::GetFullPath($projectRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The active build path resolves outside the DSS project folder.'
    }
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) { throw "The active DSS build is missing: $resolvedPath" }
    $actualHash = (Get-FileHash -LiteralPath $resolvedPath -Algorithm SHA256).Hash
    if ($actualHash -ne $ExpectedHash) { throw 'The active build was replaced. Restore the build listed in dss-runtime.json or select a newly approved build.' }
    return $resolvedPath
}

function Test-DssProcessIdentity([int]$ProcessId) {
    if ($ProcessId -le 0) { return $false }
    try {
        $processInfo = Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction Stop
        if ($null -eq $processInfo) { return $false }
        $image = [string]$processInfo.ExecutablePath
        $command = [string]$processInfo.CommandLine
        $rootPrefix = [IO.Path]::GetFullPath($projectRoot).TrimEnd('\') + '\'
        $belongsToDss = ($image -and $image.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) -or
            ($command -and $command.IndexOf($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -ge 0)
        $isDssHost = ([IO.Path]::GetFileNameWithoutExtension($image) -in @('DedicatedServerStudio', 'dotnet')) -and
            ($command -match 'DedicatedServerStudio(?:\.dll|\.exe)')
        return $belongsToDss -and $isDssHost
    } catch { return $false }
}

function Stop-StaleDssLauncher([datetime]$OlderThan) {
    $expectedScript = [IO.Path]::GetFullPath((Join-Path $projectRoot 'run.ps1'))
    try {
        $candidates = Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {
            [int]$_.ProcessId -ne $PID -and
            $_.Name -in @('powershell.exe', 'pwsh.exe') -and
            $_.CommandLine -and
            $_.CommandLine.IndexOf($expectedScript, [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $_.CommandLine -match '(?i)(?:^|\s)-File(?:\s|$)' -and
            $_.CommandLine -notmatch '(?i)-WaitForExistingInstance'
        }
        foreach ($candidate in $candidates) {
            if ($candidate.CreationDate -and [datetime]$candidate.CreationDate -ge $OlderThan) { continue }
            $candidateId = [int]$candidate.ProcessId
            if (-not (Get-Process -Id $candidateId -ErrorAction SilentlyContinue)) { continue }
            Write-LaunchMessage "The DSS host and listener are closed, but the verified launcher (PID $candidateId) still holds the startup lock. Closing that stale launcher so startup can continue."
            Stop-Process -Id $candidateId -Force -ErrorAction Stop
            return $true
        }
    }
    catch { Write-LaunchMessage "Could not inspect a stale DSS launcher: $($_.Exception.Message)" }
    return $false
}

function Write-StartupSignal([bool]$Ready, [bool]$IsAdministrator, [string]$Message = '') {
    if ([string]::IsNullOrWhiteSpace($script:startupSignalPath)) { return }
    $signalPath = $script:startupSignalPath
    $temporaryPath = "$signalPath.$PID.tmp"
    try {
        $signal = @{ ready = $Ready; isAdministrator = $IsAdministrator; message = $Message } | ConvertTo-Json -Compress
        [IO.File]::WriteAllText($temporaryPath, $signal, [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporaryPath -Destination $signalPath -Force | Out-Null
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
    }
}

function Write-LaunchMessage([string]$Message) {
    Write-Host $Message
    if ($script:launchLog -and -not $script:logWriteFailed) {
        try {
            [IO.File]::AppendAllText($script:launchLog, "$(Get-Date -Format o) $Message$([Environment]::NewLine)", [Text.UTF8Encoding]::new($false))
        }
        catch {
            $script:logWriteFailed = $true
            Write-Warning "Could not continue writing the startup log: $($_.Exception.Message)"
        }
    }
}

try {
    # A parent PowerShell 7 process can supply an incompatible module search path.
    Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1') -ErrorAction Stop
    $logsDirectory = Join-Path $projectRoot 'logs'
    New-Item -ItemType Directory -Path $logsDirectory -Force | Out-Null
    $launchLog = Join-Path $logsDirectory "dss-startup-$(Get-Date -Format yyyy-MM-dd).log"
    $restartHandoffMarker = Join-Path $logsDirectory 'dss-restart-handoff.json'

    $dssConfigPath = Join-Path $projectRoot 'dss-config.json'
    $runAsAdministrator = $false
    $configuredListenMode = 'local'
    if (Test-Path -LiteralPath $dssConfigPath -PathType Leaf) {
        try {
            $dssConfig = Get-Content -LiteralPath $dssConfigPath -Raw | ConvertFrom-Json
            if ($null -ne $dssConfig.PSObject.Properties['runAsAdministrator']) {
                $runAsAdministrator = [bool]$dssConfig.runAsAdministrator
            }
            if ($dssConfig.listenMode -in @('local', 'lan', 'public')) { $configuredListenMode = [string]$dssConfig.listenMode }
        }
        catch { throw "The DSS configuration in dss-config.json is invalid: $($_.Exception.Message)" }
    }
    else {
        $defaultConfig = [ordered]@{ runAsAdministrator = $false; listenMode = 'local' }
        [IO.File]::WriteAllText($dssConfigPath, ($defaultConfig | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    }

    if ($Public) { $ListenMode = 'public' }
    elseif ($Lan) { $ListenMode = 'lan' }
    elseif ([string]::IsNullOrWhiteSpace($ListenMode)) { $ListenMode = $configuredListenMode }

    $configuredUrl = $env:DSS_URL
    if ([string]::IsNullOrWhiteSpace($configuredUrl)) { $configuredUrl = $env:VALHEIM_STUDIO_URL }
    if ([string]::IsNullOrWhiteSpace($configuredUrl)) { $configuredUrl = 'http://127.0.0.1:5089' }
    $listenUri = [Uri]$configuredUrl
    if (-not $listenUri.IsAbsoluteUri -or $listenUri.Scheme -notin @('http', 'https')) { throw 'The DSS listening URL is invalid.' }
    $listenBuilder = [UriBuilder]::new($listenUri)
    $listenBuilder.Host = if ($ListenMode -eq 'local') { '127.0.0.1' } else { '0.0.0.0' }
    $listenBuilder.Path = '/'
    $listenBuilder.Query = ''
    $listenBuilder.Fragment = ''
    $listenUrl = $listenBuilder.Uri.GetLeftPart([UriPartial]::Authority)
    $listenUri = [Uri]$listenUrl

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        $isAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    }
    finally { $identity.Dispose() }

    if ($RequireNonAdministrator -and $isAdministrator) {
        $message = 'The standard-permission restart was launched with administrator privileges.'
        Write-StartupSignal $false $true $message
        throw $message
    }
    if ($RequireNonAdministrator) {
        $null = Get-Command dotnet.exe -ErrorAction Stop
        if ([string]::IsNullOrWhiteSpace($AssemblyPath)) {
            $runtimeFile = Join-Path $projectRoot 'dss-runtime.json'
            $runtime = Get-Content -LiteralPath $runtimeFile -Raw | ConvertFrom-Json
            $AssemblyPath = Resolve-DssAssembly ([string]$runtime.assemblyPath) ([string]$runtime.sha256)
        }
        elseif (-not [IO.Path]::IsPathRooted($AssemblyPath)) { $AssemblyPath = Join-Path $projectRoot $AssemblyPath }
        if (-not (Test-Path -LiteralPath $AssemblyPath -PathType Leaf)) { throw "DSS assembly was not found: $AssemblyPath" }
        if (-not (Test-Path -LiteralPath (Join-Path $projectRoot 'wwwroot\index.html'))) { throw 'Compiled frontend is missing. Run build.ps1 before starting DSS.' }
        Write-StartupSignal $true $false 'The standard-permission process is ready and waiting for the current instance to close.'
    }
    elseif (-not [string]::IsNullOrWhiteSpace($StartupSignalPath)) {
        try {
            $null = Get-Command dotnet.exe -ErrorAction Stop
            if ([string]::IsNullOrWhiteSpace($AssemblyPath)) {
                $runtimeFile = Join-Path $projectRoot 'dss-runtime.json'
                $runtime = Get-Content -LiteralPath $runtimeFile -Raw | ConvertFrom-Json
                $AssemblyPath = Resolve-DssAssembly ([string]$runtime.assemblyPath) ([string]$runtime.sha256)
            }
            elseif (-not [IO.Path]::IsPathRooted($AssemblyPath)) { $AssemblyPath = Join-Path $projectRoot $AssemblyPath }
            if (-not (Test-Path -LiteralPath $AssemblyPath -PathType Leaf)) { throw "DSS assembly was not found: $AssemblyPath" }
            if (-not (Test-Path -LiteralPath (Join-Path $projectRoot 'wwwroot\index.html'))) { throw 'Compiled frontend is missing. Run build.ps1 before starting DSS.' }
            Write-StartupSignal $true $isAdministrator 'The replacement DSS launcher passed its startup checks and is waiting for the current instance to close.'
        }
        catch {
            Write-StartupSignal $false $isAdministrator $_.Exception.Message
            throw
        }
    }

    $launchMutex = [Threading.Mutex]::new($false, "Local\DedicatedServerStudio-Launcher-$($listenUri.Port)")
    if ($WaitForExistingInstance) {
        $waitStarted = Get-Date
        $waitUntil = $waitStarted.AddSeconds(120)
        $forceStopAfter = $waitStarted.AddSeconds(8)
        $nextStaleLauncherCheck = $waitStarted.AddSeconds(20)
        $forceStopAttempted = $false
        $previousProcess = $null
        if ($PreviousProcessId -gt 0) {
            if (Test-DssProcessIdentity $PreviousProcessId) {
                try { $previousProcess = Get-Process -Id $PreviousProcessId -ErrorAction Stop }
                catch { $previousProcess = $null }
            }
            else { Write-LaunchMessage "PID $PreviousProcessId no longer identifies the previous DSS host; waiting only for the DSS listener and startup lock." }
        }
        Write-LaunchMessage 'Waiting for the current DSS instance to close before restarting with the selected permission mode.'
        do {
            $previousProcessExited = $true
            if ($previousProcess) {
                try { $previousProcess.Refresh(); $previousProcessExited = $previousProcess.HasExited }
                catch { $previousProcessExited = $true }
                if (-not $previousProcessExited -and -not (Test-DssProcessIdentity $PreviousProcessId)) {
                    Write-LaunchMessage "PID $PreviousProcessId no longer identifies the previous DSS host. Leaving that process untouched and continuing the handoff."
                    $previousProcessExited = $true
                }
            }
            if (-not $previousProcessExited) {
                if (-not $forceStopAttempted -and $PreviousProcessId -gt 0 -and (Get-Date) -ge $forceStopAfter -and
                    (Test-DssProcessIdentity $PreviousProcessId)) {
                    $listener = Get-NetTCPConnection -LocalPort $listenUri.Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
                    if (-not $listener -or [int]$listener.OwningProcess -eq $PreviousProcessId) {
                        Write-LaunchMessage "The previous DSS host (PID $PreviousProcessId) remained open after shutdown. Stopping that verified process so the restart can continue."
                        try {
                            $handoff = @{ previousProcessId = $PreviousProcessId; listenPort = $listenUri.Port; requestedAtUtc = [DateTimeOffset]::UtcNow.ToString('o') } | ConvertTo-Json -Compress
                            [IO.File]::WriteAllText($restartHandoffMarker, $handoff, [Text.UTF8Encoding]::new($false))
                        }
                        catch { Write-LaunchMessage "Could not record the verified restart handoff: $($_.Exception.Message)" }
                        try { Stop-Process -Id $PreviousProcessId -Force -ErrorAction Stop }
                        catch {
                            $stopError = $_
                            try {
                                $handoff = Get-Content -LiteralPath $restartHandoffMarker -Raw | ConvertFrom-Json
                                if ([int]$handoff.previousProcessId -eq $PreviousProcessId) { Remove-Item -LiteralPath $restartHandoffMarker -Force -ErrorAction SilentlyContinue }
                            }
                            catch { }

                            $previousProcessExitedAfterStopRace = $false
                            if ($previousProcess) {
                                try { $previousProcess.Refresh(); $previousProcessExitedAfterStopRace = $previousProcess.HasExited }
                                catch { $previousProcessExitedAfterStopRace = $true }
                            }
                            if ($previousProcessExitedAfterStopRace) {
                                Write-LaunchMessage "The previous DSS host (PID $PreviousProcessId) exited before the stop request completed. Continuing the restart."
                            }
                            elseif (-not (Test-DssProcessIdentity $PreviousProcessId)) {
                                Write-LaunchMessage "PID $PreviousProcessId no longer identifies the previous DSS host. Leaving that process untouched and continuing the handoff."
                            }
                            else {
                                throw $stopError
                            }
                        }
                        $forceStopAttempted = $true
                        Start-Sleep -Milliseconds 100
                    }
                }
                if ((Get-Date) -ge $waitUntil) { throw 'The current DSS instance did not close in time. Close the previous instance and try again.' }
                if ($previousProcess) { $null = $previousProcess.WaitForExit(150) }
                else { Start-Sleep -Milliseconds 150 }
                continue
            }

            # Avoid repeated TCP table scans while the old host is still shutting down.
            # Its process handle and the launcher mutex give us cheap, reliable wait signals.
            $listener = Get-NetTCPConnection -LocalPort $listenUri.Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($listener) {
                $listenerProcessId = [int]$listener.OwningProcess
                if (-not (Test-DssProcessIdentity $listenerProcessId)) {
                    throw "Port $($listenUri.Port) is still owned by another application (PID $listenerProcessId). The updater left the application files unchanged until the port is free."
                }
                if (-not $forceStopAttempted -and (Get-Date) -ge $waitStarted.AddSeconds(20)) {
                    Write-LaunchMessage "The previous verified DSS host (PID $listenerProcessId) is still listening during the restart handoff. Stopping that host so the update can continue."
                    Stop-Process -Id $listenerProcessId -Force -ErrorAction Stop
                    $forceStopAttempted = $true
                    Start-Sleep -Milliseconds 250
                    continue
                }
            }
            else {
                try { $ownsLaunchMutex = $launchMutex.WaitOne(150) }
                catch [Threading.AbandonedMutexException] { $ownsLaunchMutex = $true }
                if ($ownsLaunchMutex) { break }
                if ((Get-Date) -ge $nextStaleLauncherCheck) {
                    $null = Stop-StaleDssLauncher $waitStarted.AddSeconds(-15)
                    $nextStaleLauncherCheck = (Get-Date).AddSeconds(5)
                }
            }
            if ((Get-Date) -ge $waitUntil) { throw 'The current DSS instance did not close in time. Close the previous instance and try again.' }
            Start-Sleep -Milliseconds 100
        } while ($true)
        if ($previousProcess) { $previousProcess.Dispose() }
    }
    else {
        try { $ownsLaunchMutex = $launchMutex.WaitOne(0) }
        catch [Threading.AbandonedMutexException] { $ownsLaunchMutex = $true }
        if (-not $ownsLaunchMutex) {
            Write-LaunchMessage 'DSS is already starting or open in another window. Use the existing console.'
            exit 0
        }
    }

    $listener = Get-NetTCPConnection -LocalPort $listenUri.Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($listener) {
        $healthHost = if ($listenUri.Host -in @('0.0.0.0', '::', '*', '+')) { '127.0.0.1' } else { $listenUri.Host }
        $healthUrl = '{0}://{1}:{2}/api/health' -f $listenUri.Scheme, $healthHost, $listenUri.Port
        try { $health = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 4 -ErrorAction Stop }
        catch { throw "Port $($listenUri.Port) is occupied by PID $($listener.OwningProcess), but the dashboard is not responding. Check that process before trying again." }
        if ($health.app -eq 'Dedicated Server Studio') {
            Write-LaunchMessage "DSS is already active at $listenUrl. No duplicate instance was started."
            exit 0
        }
        throw "Port $($listenUri.Port) is in use by another application (PID $($listener.OwningProcess))."
    }

    if ($runAsAdministrator -and -not $isAdministrator) {
        if ($ownsLaunchMutex) { $launchMutex.ReleaseMutex(); $ownsLaunchMutex = $false }
        $elevatedPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        if (-not (Test-Path -LiteralPath $elevatedPowerShell -PathType Leaf)) { throw 'Windows PowerShell was not found to start DSS as administrator.' }
        $scriptPath = $MyInvocation.MyCommand.Path
        $arguments = @('-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $scriptPath + '"'))
        $arguments += @('-ListenMode', $ListenMode)
        if ($WaitForExistingInstance) { $arguments += '-WaitForExistingInstance' }
        if ($PreviousProcessId -gt 0) { $arguments += @('-PreviousProcessId', [string]$PreviousProcessId) }
        if (-not [string]::IsNullOrWhiteSpace($AssemblyPath)) { $arguments += @('-AssemblyPath', ('"' + $AssemblyPath + '"')) }
        if ($RequireNonAdministrator) { $arguments += '-RequireNonAdministrator' }
        if (-not [string]::IsNullOrWhiteSpace($StartupSignalPath)) { $arguments += @('-StartupSignalPath', ('"' + $StartupSignalPath + '"')) }
        $arguments += '-PauseOnFailure'
        Write-LaunchMessage 'Requesting permission to start DSS as administrator.'
        try {
            Start-Process -FilePath $elevatedPowerShell -ArgumentList ($arguments -join ' ') -WorkingDirectory $projectRoot -WindowStyle Normal -Verb RunAs | Out-Null
            Write-LaunchMessage 'DSS will start in the elevated window. You can change this preference in dss-config.json.'
            exit 0
        }
        catch {
            Write-LaunchMessage "Could not start DSS as administrator: $($_.Exception.Message)"
            exit 1
        }
    }
    if ($runAsAdministrator) { Write-LaunchMessage 'DSS is running with administrator privileges.' }
    else { Write-LaunchMessage 'runAsAdministrator is disabled in dss-config.json.' }

    $dotnetCommand = Get-Command dotnet.exe -ErrorAction Stop
    if ([string]::IsNullOrWhiteSpace($AssemblyPath)) {
        $runtimeFile = Join-Path $projectRoot 'dss-runtime.json'
        $runtime = Get-Content -LiteralPath $runtimeFile -Raw | ConvertFrom-Json
        $AssemblyPath = Resolve-DssAssembly ([string]$runtime.assemblyPath) ([string]$runtime.sha256)
    }
    elseif (-not [IO.Path]::IsPathRooted($AssemblyPath)) { $AssemblyPath = Join-Path $projectRoot $AssemblyPath }
    if (-not (Test-Path -LiteralPath $AssemblyPath -PathType Leaf)) { throw "DSS assembly was not found: $AssemblyPath" }
    if (-not (Test-Path -LiteralPath (Join-Path $projectRoot 'wwwroot\index.html'))) { throw 'Compiled frontend is missing. Run build.ps1 before starting DSS.' }

    $appArgs = @('--no-browser')
    if ($ListenMode -eq 'lan') { $appArgs += '--lan' }
    elseif ($ListenMode -eq 'public') { $appArgs += '--public' }
    Write-LaunchMessage "Starting DSS at $listenUrl"
    Write-LaunchMessage "Active build: $AssemblyPath"
    Write-LaunchMessage "Startup log: $launchLog"
    Push-Location $projectRoot
    try {
        # Log native CLR errors even when the application cannot load its own logger.
        $ErrorActionPreference = 'Continue'
        & $dotnetCommand.Source $AssemblyPath @appArgs 2>&1 | ForEach-Object { Write-LaunchMessage $_.ToString() }
        $exitCode = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'
    }
    finally { Pop-Location }
    if ($exitCode -ne 0 -and (Test-Path -LiteralPath $restartHandoffMarker -PathType Leaf)) {
        try {
            $handoff = Get-Content -LiteralPath $restartHandoffMarker -Raw | ConvertFrom-Json
            $handoffAge = [DateTimeOffset]::UtcNow - [DateTimeOffset]::Parse([string]$handoff.requestedAtUtc)
            if ([int]$handoff.listenPort -eq $listenUri.Port -and $handoffAge -ge [TimeSpan]::Zero -and $handoffAge -lt [TimeSpan]::FromSeconds(45)) {
                Write-LaunchMessage "The DSS host exited with code $exitCode during the verified permission-mode handoff. The replacement instance is starting in the other window."
                Remove-Item -LiteralPath $restartHandoffMarker -Force -ErrorAction SilentlyContinue
                $exitCode = 0
            }
        }
        catch { Write-LaunchMessage "Could not validate the DSS restart handoff marker: $($_.Exception.Message)" }
    }
    Write-LaunchMessage "DSS exited with code $exitCode."
}
catch {
    Write-LaunchMessage "DSS startup failed: $($_.Exception.Message)"
    $exitCode = 1
}
finally {
    if ($ownsLaunchMutex) { $launchMutex.ReleaseMutex() }
    if ($launchMutex) { $launchMutex.Dispose() }
}
if ($PauseOnFailure -and $exitCode -ne 0) {
    Write-Host ''
    if ($launchLog) { Write-Host "Startup log: $launchLog" }
    [void](Read-Host 'DSS could not start. Press Enter to close this window')
}
exit $exitCode
