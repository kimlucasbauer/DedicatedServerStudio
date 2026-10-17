param(
    [Parameter(Mandatory)][string]$ProjectRoot,
    [Parameter(Mandatory)][string]$ArchivePath,
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [Parameter(Mandatory)][string]$ExpectedSha256,
    [Parameter(Mandatory)][int]$CurrentProcessId,
    [int]$CurrentLauncherProcessId = 0,
    [Parameter(Mandatory)][ValidateRange(1, 65535)][int]$Port,
    [Parameter(Mandatory)][string]$ProgressPath,
    [Parameter(Mandatory)][Guid]$OperationId
)

$ErrorActionPreference = 'Stop'
# The launcher may inherit a PowerShell 7 module path. Load the Windows PowerShell
# modules by their host paths so update, process, and port commands remain available.
foreach ($moduleName in @('Microsoft.PowerShell.Utility', 'CimCmdlets', 'NetTCPIP')) {
    $moduleManifest = Join-Path (Join-Path (Join-Path $PSHOME 'Modules') $moduleName) ($moduleName + '.psd1')
    Import-Module $moduleManifest -ErrorAction Stop
}
$root = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
$archive = [IO.Path]::GetFullPath($ArchivePath)
$work = Join-Path $root ('.dss-update-work-' + [Guid]::NewGuid().ToString('N'))
$extract = Join-Path $work 'package'
$installStage = Join-Path $work 'install'
$backup = Join-Path $work 'backup'
$logs = Join-Path $root 'logs'
$logPath = Join-Path $logs ('dss-update-' + (Get-Date -Format 'yyyy-MM-dd_HHmmss') + '.log')
$backupManifest = Join-Path $backup 'manifest.json'
$backupReady = $false
$hostStopped = $false
$installationStarted = $false
$restartStarted = $false
$buildDirectory = $null
$archiveHandle = $null
$allowedFiles = @(
    'appsettings.json', 'DedicatedServerStudio.staticwebassets.endpoints.json',
    'dss-runtime.json', 'DSS.cmd', 'DSS-ForceStop.cmd', 'run.ps1', 'apply-update.ps1', 'README.md', 'LICENSE', 'web.config'
)

function Write-UpdateLog([string]$Message) {
    $line = "$(Get-Date -Format o) $Message"
    try { [IO.File]::AppendAllText($logPath, $line + [Environment]::NewLine, [Text.UTF8Encoding]::new($false)) } catch {}
}

function Write-UpdateProgress([string]$Phase, [int]$Percent, [string]$Message, [string]$FailureMessage = $null) {
    try {
        $destination = [IO.Path]::GetFullPath($ProgressPath)
        $temporary = $destination + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        $current = @{
            operationId = $OperationId.ToString('D')
            latestVersion = $ExpectedVersion
            phase = $Phase
            percent = [Math]::Max(0, [Math]::Min(100, $Percent))
            message = $Message
            downloadedBytes = $null
            totalBytes = $null
            error = $FailureMessage
            updatedAt = [DateTimeOffset]::UtcNow.ToString('o')
        } | ConvertTo-Json -Depth 3 -Compress
        [IO.File]::WriteAllText($temporary, $current, [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporary -Destination $destination -Force
    } catch {}
}

function Copy-DssDirectoryWithProgress([string]$Source, [string]$Destination, [string]$Phase, [int]$StartPercent, [int]$EndPercent, [string]$Action) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $sourceRoot = [IO.Path]::GetFullPath($Source).TrimEnd('\') + '\'
    $files = @(Get-ChildItem -LiteralPath $Source -File -Recurse -Force)
    [long]$totalBytes = ($files | Measure-Object -Property Length -Sum).Sum
    [long]$copiedBytes = 0
    $lastReportTick = [Environment]::TickCount64
    $buffer = New-Object byte[] (1024 * 1024)
    foreach ($file in $files) {
        $relativePath = $file.FullName.Substring($sourceRoot.Length)
        $destinationPath = Join-Path $Destination $relativePath
        New-Item -ItemType Directory -Path (Split-Path -Parent $destinationPath) -Force | Out-Null
        $inputStream = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        $outputStream = [IO.File]::Open($destinationPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            while (($read = $inputStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $outputStream.Write($buffer, 0, $read)
                $copiedBytes += $read
                $now = [Environment]::TickCount64
                if ($now - $lastReportTick -ge 500 -or $copiedBytes -eq $totalBytes) {
                    $fraction = if ($totalBytes -gt 0) { $copiedBytes / [double]$totalBytes } else { 1 }
                    $percent = $StartPercent + [int][Math]::Floor(($EndPercent - $StartPercent) * $fraction)
                    Write-UpdateProgress $Phase $percent ("$Action · {0} / {1}" -f ([Math]::Min($copiedBytes, $totalBytes)), $totalBytes)
                    $lastReportTick = $now
                }
            }
        }
        finally { $outputStream.Dispose(); $inputStream.Dispose() }
    }
    Write-UpdateProgress $Phase $EndPercent ("$Action · {0} / {1}" -f $totalBytes, $totalBytes)
}

function Test-DssProcessIdentity([int]$ProcessId) {
    try {
        $processInfo = Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction Stop
        if ($null -eq $processInfo) { return $false }
        $image = [string]$processInfo.ExecutablePath
        $command = [string]$processInfo.CommandLine
        $normalizedRoot = $root.TrimEnd('\') + '\'
        $belongsToDss = ($image -and $image.StartsWith($normalizedRoot, [StringComparison]::OrdinalIgnoreCase)) -or
            ($command -and $command.IndexOf($normalizedRoot, [StringComparison]::OrdinalIgnoreCase) -ge 0)
        $isDssHost = ([IO.Path]::GetFileNameWithoutExtension($image) -in @('DedicatedServerStudio', 'dotnet')) -and
            ($command -match 'DedicatedServerStudio(?:\.dll|\.exe)')
        return $belongsToDss -and $isDssHost
    } catch { return $false }
}

function Test-DssLauncherIdentity([int]$ProcessId) {
    if ($ProcessId -le 0 -or $ProcessId -eq $PID) { return $false }
    try {
        $processInfo = Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction Stop
        if ($null -eq $processInfo -or $processInfo.Name -notin @('powershell.exe', 'pwsh.exe')) { return $false }
        $command = [string]$processInfo.CommandLine
        $expectedScript = [IO.Path]::GetFullPath((Join-Path $root 'run.ps1'))
        return $command -and $command.IndexOf($expectedScript, [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $command -match '(?i)(?:^|\s)-File(?:\s|$)'
    } catch { return $false }
}

function Wait-ForStoppedDssLauncher {
    if ($CurrentLauncherProcessId -le 0 -or $CurrentLauncherProcessId -eq $PID) { return }
    if (-not (Test-DssLauncherIdentity $CurrentLauncherProcessId)) {
        Write-UpdateLog "The original DSS launcher $CurrentLauncherProcessId has already exited or no longer matches run.ps1."
        return
    }

    Write-UpdateProgress 'stopping-launcher' 84 'Liberando o inicializador anterior'
    $deadline = (Get-Date).AddSeconds(2)
    while ((Get-Date) -lt $deadline -and (Test-DssLauncherIdentity $CurrentLauncherProcessId)) {
        Start-Sleep -Milliseconds 150
    }
    if (-not (Test-DssLauncherIdentity $CurrentLauncherProcessId)) { return }

    # The host and its listener are already confirmed stopped. A verified run.ps1 wrapper
    # that remains alive can only keep the launch mutex locked and block the replacement.
    $listener = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($listener) { throw "Port $Port became active again before the old launcher released its lock." }
    Write-UpdateLog "The stopped DSS host's verified launcher $CurrentLauncherProcessId still holds the startup lock; closing the wrapper before restart."
    Stop-Process -Id $CurrentLauncherProcessId -Force -ErrorAction Stop
    $deadline = (Get-Date).AddSeconds(5)
    while ((Get-Date) -lt $deadline -and (Test-DssLauncherIdentity $CurrentLauncherProcessId)) {
        Start-Sleep -Milliseconds 150
    }
    if (Test-DssLauncherIdentity $CurrentLauncherProcessId) {
        throw 'The previous DSS launcher did not release its startup lock.'
    }
}

function Test-StagedDssBuild {
    $assemblyPath = Join-Path $buildDirectory 'DedicatedServerStudio.dll'
    $privateDataPath = Join-Path $work 'preflight-data'
    $standardOutputPath = Join-Path $work 'preflight.stdout.log'
    $standardErrorPath = Join-Path $work 'preflight.stderr.log'
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listenerStarted = $false
    $preflight = $null
    $previousDssUrl = $env:DSS_URL
    $previousDataDirectory = $env:DSS_DATA_DIR
    $previousDisableScan = $env:DSS_DISABLE_UPDATE_SCAN
    try {
        $listener.Start()
        $listenerStarted = $true
        $preflightPort = [int]$listener.LocalEndpoint.Port
        $listener.Stop()
        $listenerStarted = $false
        $env:DSS_URL = "http://127.0.0.1:$preflightPort"
        $env:DSS_DATA_DIR = $privateDataPath
        $env:DSS_DISABLE_UPDATE_SCAN = '1'
        $dotnetPath = (Get-Command dotnet.exe -ErrorAction Stop).Source
        $preflight = Start-Process -FilePath $dotnetPath `
            -ArgumentList @(('"' + $assemblyPath + '"'), '--no-browser') `
            -WorkingDirectory $installStage -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput $standardOutputPath -RedirectStandardError $standardErrorPath
    }
    finally {
        if ($listenerStarted) { $listener.Stop() }
        if ($null -eq $previousDssUrl) { Remove-Item Env:DSS_URL -ErrorAction SilentlyContinue } else { $env:DSS_URL = $previousDssUrl }
        if ($null -eq $previousDataDirectory) { Remove-Item Env:DSS_DATA_DIR -ErrorAction SilentlyContinue } else { $env:DSS_DATA_DIR = $previousDataDirectory }
        if ($null -eq $previousDisableScan) { Remove-Item Env:DSS_DISABLE_UPDATE_SCAN -ErrorAction SilentlyContinue } else { $env:DSS_DISABLE_UPDATE_SCAN = $previousDisableScan }
    }

    try {
        $healthUrl = "http://127.0.0.1:$preflightPort/api/health"
        $pageUrl = "http://127.0.0.1:$preflightPort/"
        $deadline = (Get-Date).AddSeconds(60)
        $healthy = $false
        $lastPreflightError = ''
        while ((Get-Date) -lt $deadline) {
            $preflight.Refresh()
            if ($preflight.HasExited) { break }
            try {
                $health = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 2 -ErrorAction Stop
                if ([string]$health.version -eq $ExpectedVersion) {
                    $page = Invoke-WebRequest -Uri $pageUrl -TimeoutSec 5 -UseBasicParsing -ErrorAction Stop
                    if ([int]$page.StatusCode -ne 200 -or [string]::IsNullOrWhiteSpace([string]$page.Content)) {
                        throw 'The staged dashboard did not serve its new interface.'
                    }
                    $healthy = $true
                    break
                }
            } catch { $lastPreflightError = $_.Exception.Message }
            Start-Sleep -Milliseconds 300
        }

        if (-not $healthy) {
            $output = @()
            foreach ($path in @($standardOutputPath, $standardErrorPath)) {
                if (Test-Path -LiteralPath $path -PathType Leaf) { $output += @(Get-Content -LiteralPath $path -Tail 20) }
            }
            if ($lastPreflightError) { $output += $lastPreflightError }
            $detail = if ($output.Count) { ' ' + ($output -join ' ') } else { '' }
            throw "The staged DSS v$ExpectedVersion did not pass its loopback startup check.$detail"
        }
        Write-UpdateLog "The staged v$ExpectedVersion passed its isolated health and web-page checks on port $preflightPort."
    }
    finally {
        if ($preflight) {
            $preflight.Refresh()
            if (-not $preflight.HasExited) {
                Stop-Process -Id $preflight.Id -Force -ErrorAction SilentlyContinue
                $null = $preflight.WaitForExit(5000)
            }
            $preflight.Dispose()
        }
    }
}

function Start-DssAgain {
    $powerShellPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $scriptPath = Join-Path $root 'run.ps1'
    $arguments = '-NoLogo -NoProfile -ExecutionPolicy Bypass -File "' + $scriptPath + '" -WaitForExistingInstance -PreviousProcessId ' + $CurrentProcessId + ' -PauseOnFailure'
    Start-Process -FilePath $powerShellPath -ArgumentList $arguments -WorkingDirectory $root -WindowStyle Normal | Out-Null
}

function Restore-PreviousBuild {
    if (-not (Test-Path -LiteralPath $backupManifest -PathType Leaf)) { return }
    $manifest = Get-Content -LiteralPath $backupManifest -Raw | ConvertFrom-Json
    foreach ($name in $allowedFiles) {
        $source = Join-Path $backup $name
        $destination = Join-Path $root $name
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            Copy-Item -LiteralPath $source -Destination $destination -Force
        } elseif ($manifest.files -notcontains $name -and (Test-Path -LiteralPath $destination -PathType Leaf)) {
            Remove-Item -LiteralPath $destination -Force
        }
    }
    $oldWebRoot = Join-Path $backup 'wwwroot'
    $newWebRoot = Join-Path $root 'wwwroot'
    if (Test-Path -LiteralPath $oldWebRoot -PathType Container) {
        if (Test-Path -LiteralPath $newWebRoot) { Remove-Item -LiteralPath $newWebRoot -Recurse -Force }
        Copy-Item -LiteralPath $oldWebRoot -Destination $newWebRoot -Recurse -Force
    }
}

try {
    New-Item -ItemType Directory -Path $logs -Force | Out-Null
    New-Item -ItemType Directory -Path $extract -Force | Out-Null
    Write-UpdateLog "Preparing DSS update to v$ExpectedVersion."
    Write-UpdateProgress 'verifying' 47 'Verificando os arquivos do pacote'

    $expectedHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    if (-not $expectedHash.Equals($ExpectedSha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The downloaded package SHA-256 does not match the verified value.'
    }

    Write-UpdateProgress 'extracting' 50 'Extraindo o pacote sem interromper o DSS'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archiveHandle = [System.IO.Compression.ZipFile]::OpenRead($archive)
    $extractRoot = [IO.Path]::GetFullPath($extract).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    [long]$totalArchiveBytes = ($archiveHandle.Entries | Measure-Object -Property Length -Sum).Sum
    if ($totalArchiveBytes -gt 2GB) { throw 'The update archive expands beyond the 2 GB safety limit.' }
    [long]$uncompressedBytes = 0
    [long]$extractedBytes = 0
    $lastExtractReportTick = [Environment]::TickCount64
    foreach ($entry in $archiveHandle.Entries) {
        $relative = $entry.FullName.Replace('\', '/')
        if ([string]::IsNullOrWhiteSpace($relative) -or $relative.StartsWith('/') -or $relative -match '^[A-Za-z]:' -or
            ($relative.Split('/') | Where-Object { $_ -eq '..' -or $_ -eq '.' -or $_ -match ':' }).Count -gt 0) {
            throw 'The update archive contains an unsafe path.'
        }
        $uncompressedBytes += $entry.Length
        $destination = [IO.Path]::GetFullPath((Join-Path $extract ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))))
        if (-not $destination.StartsWith($extractRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'The update archive contains a path outside its staging folder.' }
        if ($relative.EndsWith('/')) { New-Item -ItemType Directory -Path $destination -Force | Out-Null; continue }
        $parent = Split-Path -Parent $destination
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        $entryStream = $entry.Open()
        $fileStream = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $buffer = New-Object byte[] (1024 * 1024)
        try {
            while (($read = $entryStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $fileStream.Write($buffer, 0, $read)
                $extractedBytes += $read
                $now = [Environment]::TickCount64
                if ($now - $lastExtractReportTick -ge 500 -or $extractedBytes -eq $totalArchiveBytes) {
                    $fraction = if ($totalArchiveBytes -gt 0) { $extractedBytes / [double]$totalArchiveBytes } else { 1 }
                    $percent = 50 + [int][Math]::Floor(7 * $fraction)
                    Write-UpdateProgress 'extracting' $percent ("Extraindo pacote · {0} / {1}" -f $extractedBytes, $totalArchiveBytes)
                    $lastExtractReportTick = $now
                }
            }
        } finally { $fileStream.Dispose(); $entryStream.Dispose() }
    }
    $archiveHandle.Dispose(); $archiveHandle = $null
    Write-UpdateProgress 'extracting' 57 'Pacote extraído'

    foreach ($required in @('DedicatedServerStudio.dll', 'DedicatedServerStudio.deps.json', 'DedicatedServerStudio.runtimeconfig.json', 'dss-runtime.json', 'run.ps1', 'DSS.cmd', 'DSS-ForceStop.cmd', 'apply-update.ps1', 'wwwroot\index.html')) {
        if (-not (Test-Path -LiteralPath (Join-Path $extract $required) -PathType Leaf)) { throw "The update archive is missing required file $required." }
    }
    Write-UpdateProgress 'validating' 58 'Validando a versão e os arquivos extraídos'
    $runtime = Get-Content -LiteralPath (Join-Path $extract 'dss-runtime.json') -Raw | ConvertFrom-Json
    if ($runtime.assemblyPath -ne 'DedicatedServerStudio.dll') { throw 'The release runtime manifest does not point to the production assembly.' }
    $assemblyHash = (Get-FileHash -LiteralPath (Join-Path $extract 'DedicatedServerStudio.dll') -Algorithm SHA256).Hash
    if (-not $assemblyHash.Equals([string]$runtime.sha256, [StringComparison]::OrdinalIgnoreCase)) { throw 'The release assembly does not match its runtime manifest.' }
    # Keep every build immutable so a detached console bridge can continue loading the
    # exact assembly and dependency files it started with while the panel is replaced.
    $safeVersion = $ExpectedVersion -replace '[^0-9A-Za-z.-]', '_'
    $buildDirectoryName = '.dss-builds\' + $safeVersion + '-' + [Guid]::NewGuid().ToString('N')
    $buildDirectory = Join-Path $root $buildDirectoryName
    New-Item -ItemType Directory -Path $buildDirectory -Force | Out-Null
    foreach ($name in @('DedicatedServerStudio.dll', 'DedicatedServerStudio.deps.json', 'DedicatedServerStudio.runtimeconfig.json')) {
        Copy-Item -LiteralPath (Join-Path $extract $name) -Destination (Join-Path $buildDirectory $name) -Force
    }
    $newRuntimeManifest = @{
        assemblyPath = ($buildDirectoryName.Replace([IO.Path]::DirectorySeparatorChar, '/') + '/DedicatedServerStudio.dll')
        sha256 = $assemblyHash
        builtAt = [string]$runtime.builtAt
    }
    $runtimeManifestPath = Join-Path $work 'dss-runtime.json'
    [IO.File]::WriteAllText($runtimeManifestPath, ($newRuntimeManifest | ConvertTo-Json -Depth 3), [Text.UTF8Encoding]::new($false))

    # Prepare every replacement file on the same volume as the install. No live DSS file
    # is touched until extraction, manifest verification, staging, and backup all finish.
    Write-UpdateProgress 'staging' 61 'Copiando os novos arquivos para a área de preparação'
    New-Item -ItemType Directory -Path $installStage -Force | Out-Null
    foreach ($name in $allowedFiles) {
        if ($name -eq 'dss-runtime.json') { continue }
        $source = Join-Path $extract $name
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            Copy-Item -LiteralPath $source -Destination (Join-Path $installStage $name) -Force
        }
    }
    Copy-Item -LiteralPath $runtimeManifestPath -Destination (Join-Path $installStage 'dss-runtime.json') -Force
    Copy-DssDirectoryWithProgress (Join-Path $extract 'wwwroot') (Join-Path $installStage 'wwwroot') 'staging' 64 68 'Preparando arquivos do painel'
    if (-not (Test-Path -LiteralPath (Join-Path $installStage 'wwwroot\index.html') -PathType Leaf)) { throw 'The staged frontend is incomplete.' }

    Write-UpdateProgress 'backing-up' 69 'Salvando a instalação atual antes da reinicialização'
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    $existingFiles = @($allowedFiles | Where-Object { Test-Path -LiteralPath (Join-Path $root $_) -PathType Leaf })
    if (Test-Path -LiteralPath (Join-Path $root 'wwwroot') -PathType Container) {
        Copy-DssDirectoryWithProgress (Join-Path $root 'wwwroot') (Join-Path $backup 'wwwroot') 'backing-up' 70 76 'Salvando arquivos atuais'
    }
    @{ files = $existingFiles } | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $backupManifest -Encoding UTF8
    foreach ($name in $existingFiles) { Copy-Item -LiteralPath (Join-Path $root $name) -Destination (Join-Path $backup $name) -Force }
    $backupReady = $true

    Write-UpdateProgress 'preflight' 78 'Validando a inicialização da nova versão em uma porta local isolada'
    Test-StagedDssBuild
    Write-UpdateLog "All v$ExpectedVersion files are extracted, validated, backed up, and the staged build passed its isolated startup check. Waiting for the current DSS host to stop for the final replacement."
    Write-UpdateProgress 'waiting-for-shutdown' 80 'Nova versão validada; aguardando a parada do DSS atual'
    $deadline = (Get-Date).AddMinutes(3)
    $forceStopAfter = (Get-Date).AddSeconds(20)
    $forceStopAttempted = $false
    do {
        $processAlive = Test-DssProcessIdentity $CurrentProcessId
        $listener = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($listener -and [int]$listener.OwningProcess -ne $CurrentProcessId) {
            throw "Port $Port is still owned by another process (PID $($listener.OwningProcess)); the installed files were left unchanged."
        }
        if (-not $processAlive -and -not $listener) { $hostStopped = $true; break }
        if (-not $forceStopAttempted -and $processAlive -and (Get-Date) -ge $forceStopAfter -and
            (-not $listener -or [int]$listener.OwningProcess -eq $CurrentProcessId)) {
            Write-UpdateLog "The verified DSS host $CurrentProcessId did not exit after the shutdown request; stopping it so the staged update can continue."
            Write-UpdateProgress 'stopping-host' 82 'Encerrando o processo antigo do DSS'
            Stop-Process -Id $CurrentProcessId -Force -ErrorAction Stop
            $forceStopAttempted = $true
            Start-Sleep -Milliseconds 500
            continue
        }
        if ((Get-Date) -ge $deadline) { throw 'O DSS não encerrou dentro do prazo. Nenhum arquivo da instalação foi substituído.' }
        Start-Sleep -Milliseconds 400
    } while ($true)

    Wait-ForStoppedDssLauncher

    Write-UpdateProgress 'installing' 86 'Aplicando os arquivos que já foram preparados'
    $installationStarted = $true
    foreach ($name in $allowedFiles) {
        if ($name -eq 'dss-runtime.json') { continue }
        $source = Join-Path $installStage $name
        if (Test-Path -LiteralPath $source -PathType Leaf) { Move-Item -LiteralPath $source -Destination (Join-Path $root $name) -Force }
    }
    $oldWebRoot = Join-Path $root 'wwwroot'
    $stagedOldWebRoot = Join-Path $work 'previous-wwwroot'
    if (Test-Path -LiteralPath $oldWebRoot) { Move-Item -LiteralPath $oldWebRoot -Destination $stagedOldWebRoot -Force }
    Move-Item -LiteralPath (Join-Path $installStage 'wwwroot') -Destination $oldWebRoot -Force
    Move-Item -LiteralPath (Join-Path $installStage 'dss-runtime.json') -Destination (Join-Path $root 'dss-runtime.json') -Force

    Write-UpdateLog "Installed v$ExpectedVersion files. Starting the updated DSS process."
    Write-UpdateProgress 'restarting' 91 'Arquivos instalados. Reiniciando o DSS pelo PowerShell.'
    Start-DssAgain
    $restartStarted = $true
    $healthUrl = "http://127.0.0.1:$Port/api/health"
    $healthDeadline = (Get-Date).AddMinutes(3)
    $healthy = $false
    Write-UpdateProgress 'health-check' 96 'Aguardando o DSS voltar e confirmar a nova versão'
    while ((Get-Date) -lt $healthDeadline) {
        try {
            $health = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 2 -ErrorAction Stop
            if ([string]$health.version -eq $ExpectedVersion) { $healthy = $true; break }
        } catch {}
        Start-Sleep -Seconds 1
    }
    if (-not $healthy) {
        $listener = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $listener) {
            Restore-PreviousBuild
            Write-UpdateLog "The updated build did not start. Restored the previous application files."
            Start-DssAgain
        } else {
            Write-UpdateLog "The update was copied, but the health check did not confirm v$ExpectedVersion. The backup is retained at $backup."
        }
        throw "The updated DSS did not confirm version $ExpectedVersion. Check the startup log."
    }

    Write-UpdateLog "DSS v$ExpectedVersion is responding. Update completed."
    Write-UpdateProgress 'completed' 100 'DSS atualizado e pronto'
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
}
catch {
    Write-UpdateLog "Update failed: $($_.Exception.Message)"
    Write-UpdateProgress 'failed' 100 'A atualização falhou' $_.Exception.Message
    if ($backupReady -and ($hostStopped -or $installationStarted -or $restartStarted)) {
        try {
            if ($installationStarted) { Restore-PreviousBuild; Write-UpdateLog 'Restored the previous application files after an update error.' }
            if (-not $restartStarted) { Start-DssAgain }
        }
        catch { Write-UpdateLog "Automatic file recovery failed: $($_.Exception.Message)" }
    }
    if (-not $installationStarted -and (Test-Path -LiteralPath $work)) {
        try { Remove-Item -LiteralPath $work -Recurse -Force } catch {}
    }
    if (-not $installationStarted -and $buildDirectory -and (Test-Path -LiteralPath $buildDirectory)) {
        try { Remove-Item -LiteralPath $buildDirectory -Recurse -Force } catch {}
    }
    if (Test-Path -LiteralPath $archive) { try { Remove-Item -LiteralPath $archive -Force } catch {} }
    exit 1
}
finally {
    if ($archiveHandle) { $archiveHandle.Dispose() }
}
