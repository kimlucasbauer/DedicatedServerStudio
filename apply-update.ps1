param(
    [Parameter(Mandatory)][string]$ProjectRoot,
    [Parameter(Mandatory)][string]$ArchivePath,
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [Parameter(Mandatory)][string]$ExpectedSha256,
    [Parameter(Mandatory)][int]$CurrentProcessId,
    [Parameter(Mandatory)][ValidateRange(1, 65535)][int]$Port,
    [Parameter(Mandatory)][string]$ProgressPath,
    [Parameter(Mandatory)][Guid]$OperationId
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
$archive = [IO.Path]::GetFullPath($ArchivePath)
$work = Join-Path $root ('.dss-update-work-' + [Guid]::NewGuid().ToString('N'))
$extract = Join-Path $work 'package'
$backup = Join-Path $work 'backup'
$logs = Join-Path $root 'logs'
$logPath = Join-Path $logs ('dss-update-' + (Get-Date -Format 'yyyy-MM-dd_HHmmss') + '.log')
$backupManifest = Join-Path $backup 'manifest.json'
$backupReady = $false
$restartStarted = $false
$archiveHandle = $null
$allowedFiles = @(
    'appsettings.json', 'DedicatedServerStudio.staticwebassets.endpoints.json',
    'dss-runtime.json', 'DSS.cmd', 'run.ps1', 'apply-update.ps1', 'README.md', 'LICENSE', 'web.config'
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

function Start-DssAgain {
    $powerShellPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $scriptPath = Join-Path $root 'run.ps1'
    $arguments = '-NoLogo -NoProfile -ExecutionPolicy Bypass -File "' + $scriptPath + '" -WaitForExistingInstance -PreviousProcessId ' + $CurrentProcessId
    Start-Process -FilePath $powerShellPath -ArgumentList $arguments -WorkingDirectory $root -WindowStyle Hidden | Out-Null
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
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    Write-UpdateLog "Preparing DSS update to v$ExpectedVersion."
    Write-UpdateProgress 'verifying' 47 'Verificando os arquivos do pacote'

    $expectedHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    if (-not $expectedHash.Equals($ExpectedSha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The downloaded package SHA-256 does not match the verified value.'
    }

    Write-UpdateProgress 'waiting-for-shutdown' 50 'Aguardando o painel encerrar'
    $deadline = (Get-Date).AddMinutes(3)
    $forceStopAttempted = $false
    do {
        $processAlive = Get-Process -Id $CurrentProcessId -ErrorAction SilentlyContinue
        $listener = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $processAlive -and -not $listener) { break }
        if (-not $forceStopAttempted -and (Get-Date) -ge $deadline.AddMinutes(-2.5) -and
            $processAlive -and -not $listener -and (Test-DssProcessIdentity $CurrentProcessId)) {
            Write-UpdateLog "The previous DSS host remained alive without its listener; stopping verified process $CurrentProcessId."
            Write-UpdateProgress 'stopping-host' 52 'Encerrando o host antigo do DSS'
            Stop-Process -Id $CurrentProcessId -Force -ErrorAction Stop
            $forceStopAttempted = $true
            Start-Sleep -Milliseconds 500
            continue
        }
        if ((Get-Date) -ge $deadline) { throw 'O processo do DSS não encerrou dentro do prazo; os arquivos permaneceram intactos.' }
        Start-Sleep -Milliseconds 400
    } while ($true)

    Write-UpdateProgress 'extracting' 56 'Extraindo e validando o pacote'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archiveHandle = [System.IO.Compression.ZipFile]::OpenRead($archive)
    $extractRoot = [IO.Path]::GetFullPath($extract).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    [long]$uncompressedBytes = 0
    foreach ($entry in $archiveHandle.Entries) {
        $relative = $entry.FullName.Replace('\', '/')
        if ([string]::IsNullOrWhiteSpace($relative) -or $relative.StartsWith('/') -or $relative -match '^[A-Za-z]:' -or
            ($relative.Split('/') | Where-Object { $_ -eq '..' -or $_ -eq '.' -or $_ -match ':' }).Count -gt 0) {
            throw 'The update archive contains an unsafe path.'
        }
        $uncompressedBytes += $entry.Length
        if ($uncompressedBytes -gt 2GB) { throw 'The update archive expands beyond the 2 GB safety limit.' }
        $destination = [IO.Path]::GetFullPath((Join-Path $extract ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))))
        if (-not $destination.StartsWith($extractRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'The update archive contains a path outside its staging folder.' }
        if ($relative.EndsWith('/')) { New-Item -ItemType Directory -Path $destination -Force | Out-Null; continue }
        $parent = Split-Path -Parent $destination
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        $entryStream = $entry.Open()
        $fileStream = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $entryStream.CopyTo($fileStream) } finally { $fileStream.Dispose(); $entryStream.Dispose() }
    }
    $archiveHandle.Dispose(); $archiveHandle = $null

    foreach ($required in @('DedicatedServerStudio.dll', 'DedicatedServerStudio.deps.json', 'DedicatedServerStudio.runtimeconfig.json', 'dss-runtime.json', 'run.ps1', 'DSS.cmd', 'apply-update.ps1', 'wwwroot\index.html')) {
        if (-not (Test-Path -LiteralPath (Join-Path $extract $required) -PathType Leaf)) { throw "The update archive is missing required file $required." }
    }
    $runtime = Get-Content -LiteralPath (Join-Path $extract 'dss-runtime.json') -Raw | ConvertFrom-Json
    if ($runtime.assemblyPath -ne 'DedicatedServerStudio.dll') { throw 'The release runtime manifest does not point to the production assembly.' }
    $assemblyHash = (Get-FileHash -LiteralPath (Join-Path $extract 'DedicatedServerStudio.dll') -Algorithm SHA256).Hash
    if (-not $assemblyHash.Equals([string]$runtime.sha256, [StringComparison]::OrdinalIgnoreCase)) { throw 'The release assembly does not match its runtime manifest.' }
    Write-UpdateProgress 'backing-up' 68 'Salvando uma cópia da versão atual'

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

    $existingFiles = @($allowedFiles | Where-Object { Test-Path -LiteralPath (Join-Path $root $_) -PathType Leaf })
    if (Test-Path -LiteralPath (Join-Path $root 'wwwroot') -PathType Container) {
        Copy-Item -LiteralPath (Join-Path $root 'wwwroot') -Destination (Join-Path $backup 'wwwroot') -Recurse -Force
    }
    @{ files = $existingFiles } | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $backupManifest -Encoding UTF8
    foreach ($name in $existingFiles) { Copy-Item -LiteralPath (Join-Path $root $name) -Destination (Join-Path $backup $name) -Force }
    $backupReady = $true

    Write-UpdateProgress 'installing' 76 'Instalando os arquivos da nova versão'
    foreach ($name in $allowedFiles) {
        if ($name -eq 'dss-runtime.json') { continue }
        $source = Join-Path $extract $name
        if (Test-Path -LiteralPath $source -PathType Leaf) { Copy-Item -LiteralPath $source -Destination (Join-Path $root $name) -Force }
    }
    Move-Item -LiteralPath $runtimeManifestPath -Destination (Join-Path $root 'dss-runtime.json') -Force
    $oldWebRoot = Join-Path $root 'wwwroot'
    if (Test-Path -LiteralPath $oldWebRoot) { Remove-Item -LiteralPath $oldWebRoot -Recurse -Force }
    Copy-Item -LiteralPath (Join-Path $extract 'wwwroot') -Destination $oldWebRoot -Recurse -Force

    Write-UpdateLog "Installed v$ExpectedVersion files. Starting the updated DSS process."
    Write-UpdateProgress 'restarting' 87 'Reiniciando o painel'
    Start-DssAgain
    $restartStarted = $true
    $healthUrl = "http://127.0.0.1:$Port/api/health"
    $healthDeadline = (Get-Date).AddMinutes(3)
    $healthy = $false
    Write-UpdateProgress 'health-check' 93 'Confirmando que a nova versão iniciou'
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
    if (-not $restartStarted -and $backupReady) {
        try { Restore-PreviousBuild; Write-UpdateLog 'Restored the previous application files after an update error.'; Start-DssAgain }
        catch { Write-UpdateLog "Automatic file recovery failed: $($_.Exception.Message)" }
    }
    if (-not $backupReady -and (Test-Path -LiteralPath $work)) {
        try { Remove-Item -LiteralPath $work -Recurse -Force } catch {}
    }
    if (Test-Path -LiteralPath $archive) { try { Remove-Item -LiteralPath $archive -Force } catch {} }
    exit 1
}
finally {
    if ($archiveHandle) { $archiveHandle.Dispose() }
}
