$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $MyInvocation.MyCommand.Path)).TrimEnd('\')
$buildsDirectory = Join-Path $projectRoot '.dss-builds'
$applyScript = $null
$bootstrapDirectory = $null
$progressPath = Join-Path $buildsDirectory 'manual-update-progress.json'
$lockPath = Join-Path $buildsDirectory 'manual-update.lock'
$updateLock = $null

function Write-ManualMessage([string]$Message) { Write-Host "DSS manual update: $Message" }

try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        $isAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    }
    finally { $identity.Dispose() }

    if (-not $isAdministrator) {
        $powerShellPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $scriptPath = [IO.Path]::GetFullPath($MyInvocation.MyCommand.Path)
        $arguments = '-NoLogo -NoProfile -ExecutionPolicy Bypass -File "' + $scriptPath + '"'
        Write-ManualMessage 'Requesting administrator permission to replace the installed application files.'
        Start-Process -FilePath $powerShellPath -ArgumentList $arguments -WorkingDirectory $projectRoot -WindowStyle Normal -Verb RunAs | Out-Null
        exit 0
    }

    foreach ($moduleName in @('Microsoft.PowerShell.Utility', 'CimCmdlets', 'NetTCPIP')) {
        $moduleManifest = Join-Path (Join-Path (Join-Path $PSHOME 'Modules') $moduleName) ($moduleName + '.psd1')
        Import-Module $moduleManifest -ErrorAction Stop
    }

    New-Item -ItemType Directory -Path $buildsDirectory -Force | Out-Null
    $updateLock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)

    $archives = @(Get-ChildItem -LiteralPath $buildsDirectory -File -Filter 'DedicatedServerStudio-*.zip' |
        Where-Object { $_.Name -match '^DedicatedServerStudio-(\d+\.\d+\.\d+)-(production|nexus)\.zip$' } |
        Sort-Object LastWriteTime -Descending)
    if ($archives.Count -eq 0) {
        throw "No DSS update ZIP was found. Place a file named DedicatedServerStudio-<version>-production.zip or DedicatedServerStudio-<version>-nexus.zip in '$buildsDirectory'."
    }

    if ($archives.Count -gt 1) {
        Write-ManualMessage 'Choose the ZIP to install:'
        for ($index = 0; $index -lt $archives.Count; $index++) {
            Write-Host ('  {0}. {1}  ({2})' -f ($index + 1), $archives[$index].Name, $archives[$index].LastWriteTime)
        }
        $selection = 0
        while ($selection -lt 1 -or $selection -gt $archives.Count) {
            $rawSelection = Read-Host ('Enter a number from 1 to ' + $archives.Count)
            if (-not [int]::TryParse($rawSelection, [ref]$selection)) { $selection = 0 }
        }
        $archive = $archives[$selection - 1]
    }
    else { $archive = $archives[0] }

    $expectedVersion = [regex]::Match($archive.Name, '^DedicatedServerStudio-(\d+\.\d+\.\d+)-(?:production|nexus)\.zip$').Groups[1].Value
    $expectedHash = (Get-FileHash -LiteralPath $archive.FullName -Algorithm SHA256).Hash
    $bootstrapDirectory = Join-Path $env:TEMP ('DSS-manual-update-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $bootstrapDirectory -Force | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $packageArchive = [System.IO.Compression.ZipFile]::OpenRead($archive.FullName)
    try {
        $helperEntry = $packageArchive.Entries | Where-Object { $_.FullName -eq 'apply-update.ps1' } | Select-Object -First 1
        if (-not $helperEntry -or $helperEntry.Length -gt 2MB) { throw 'The selected ZIP does not contain a valid local update helper.' }
        $applyScript = Join-Path $bootstrapDirectory 'apply-update.ps1'
        $sourceStream = $helperEntry.Open()
        $destinationStream = [IO.File]::Open($applyScript, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $sourceStream.CopyTo($destinationStream) }
        finally { $destinationStream.Dispose(); $sourceStream.Dispose() }
    }
    finally { $packageArchive.Dispose() }
    $configuredUrl = $env:DSS_URL
    if ([string]::IsNullOrWhiteSpace($configuredUrl)) { $configuredUrl = $env:VALHEIM_STUDIO_URL }
    if ([string]::IsNullOrWhiteSpace($configuredUrl)) { $configuredUrl = 'http://127.0.0.1:5089' }
    $configuredUri = [Uri]$configuredUrl
    $port = $configuredUri.Port
    $currentProcessId = 0
    $currentLauncherProcessId = 0
    $rootPrefix = $projectRoot.TrimEnd('\') + '\'
    $hostProcesses = @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {
        $_.Name -in @('dotnet.exe', 'DedicatedServerStudio.exe') -and
        $_.CommandLine -match 'DedicatedServerStudio(?:\.dll|\.exe)' -and
        $_.CommandLine.IndexOf($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -ge 0
    })
    foreach ($hostProcess in $hostProcesses) {
        $listeners = @(Get-NetTCPConnection -State Listen -OwningProcess ([int]$hostProcess.ProcessId) -ErrorAction SilentlyContinue)
        if ($listeners.Count -gt 0) {
            $currentProcessId = [int]$hostProcess.ProcessId
            $port = [int]$listeners[0].LocalPort
            break
        }
    }

    if ($currentProcessId -gt 0) {
        $runScript = [IO.Path]::GetFullPath((Join-Path $projectRoot 'run.ps1'))
        $launcher = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
            $_.Name -in @('powershell.exe', 'pwsh.exe') -and $_.CommandLine -and
            $_.CommandLine.IndexOf($runScript, [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $_.CommandLine -match '(?i)(?:^|\s)-File(?:\s|$)' -and
            $_.CommandLine -notmatch '(?i)-WaitForExistingInstance'
        } | Select-Object -First 1
        if ($launcher) { $currentLauncherProcessId = [int]$launcher.ProcessId }
    }

    Write-Host ''
    Write-ManualMessage ("Selected {0} (SHA-256: {1})." -f $archive.Name, $expectedHash)
    Write-ManualMessage 'The package will be verified and prepared before DSS is stopped. Game servers with a persistent console connection remain running during the dashboard restart.'
    $confirmation = Read-Host 'Continue with the update? (Y/N)'
    if ($confirmation -notmatch '^(?i:y|yes|s|sim)$') { Write-ManualMessage 'Cancelled.'; exit 0 }

    $operationId = [Guid]::NewGuid()
    $powerShellPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $arguments = @(
        '-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $applyScript,
        '-ProjectRoot', $projectRoot, '-ArchivePath', $archive.FullName,
        '-ExpectedVersion', $expectedVersion, '-ExpectedSha256', $expectedHash,
        '-CurrentProcessId', [string]$currentProcessId, '-CurrentLauncherProcessId', [string]$currentLauncherProcessId,
        '-Port', [string]$port, '-ProgressPath', $progressPath, '-OperationId', $operationId.ToString('D'),
        '-KeepArchive'
    )
    $quotedArguments = foreach ($argument in $arguments) { '"' + ([string]$argument).Replace('"', '\"') + '"' }
    Write-ManualMessage ("Preparing version {0}. The dashboard remains available until the final replacement step." -f $expectedVersion)
    $updater = Start-Process -FilePath $powerShellPath -ArgumentList ($quotedArguments -join ' ') `
        -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru
    $lastProgress = ''
    while ($true) {
        $updater.Refresh()
        if ($updater.HasExited) { break }
        if (Test-Path -LiteralPath $progressPath -PathType Leaf) {
            try {
                $progress = Get-Content -LiteralPath $progressPath -Raw | ConvertFrom-Json
                if ($progress.operationId -eq $operationId.ToString('D')) {
                    $progressLine = ('[{0,3}%] {1}' -f [int]$progress.percent, [string]$progress.message)
                    if ($progressLine -ne $lastProgress) { Write-Host $progressLine; $lastProgress = $progressLine }
                }
            }
            catch { }
        }
        Start-Sleep -Seconds 1
    }
    $updater.Refresh()
    $exitCode = $updater.ExitCode
    $updater.Dispose()
    if ($exitCode -ne 0) {
        $details = ''
        if (Test-Path -LiteralPath $progressPath -PathType Leaf) {
            try {
                $progress = Get-Content -LiteralPath $progressPath -Raw | ConvertFrom-Json
                if ($progress.operationId -eq $operationId.ToString('D') -and $progress.error) { $details = [string]$progress.error }
            }
            catch { }
        }
        if ($details) { throw "The manual update failed: $details" }
        throw "The manual update failed with exit code $exitCode. Review logs\dss-update-*.log."
    }

    Write-ManualMessage ("Version {0} was installed. The update ZIP was kept in .dss-builds." -f $expectedVersion)
}
catch {
    Write-Host ('ERROR: ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
finally {
    if ($updateLock) {
        $updateLock.Dispose()
        if (Test-Path -LiteralPath $lockPath -PathType Leaf) { Remove-Item -LiteralPath $lockPath -Force -ErrorAction SilentlyContinue }
    }
    if ($bootstrapDirectory -and (Test-Path -LiteralPath $bootstrapDirectory -PathType Container)) {
        Remove-Item -LiteralPath $bootstrapDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
