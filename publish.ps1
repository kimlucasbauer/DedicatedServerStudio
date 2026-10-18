param(
    [ValidateSet('GitHub', 'Nexus', 'Both')]
    [string]$PackageFlavor = 'GitHub'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile = Join-Path $projectRoot 'DedicatedServerStudio.csproj'
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet-cli-home'
$env:APPDATA = Join-Path $env:DOTNET_CLI_HOME 'appdata'
New-Item -ItemType Directory -Force $env:APPDATA | Out-Null

$publishRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'publish'))
$resolvedProjectRoot = [IO.Path]::GetFullPath($projectRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
if (-not $publishRoot.StartsWith($resolvedProjectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Publish folder is outside the project root: $publishRoot"
}

[xml]$project = Get-Content -LiteralPath $projectFile -Raw
$versionNode = Select-Xml -LiteralPath $projectFile -XPath '/Project/PropertyGroup/Version' | Select-Object -First 1
$version = [string]$versionNode.Node.InnerText
if ([string]::IsNullOrWhiteSpace($version)) { throw 'Could not read the DSS version from the project file.' }
$flavors = if ($PackageFlavor -eq 'Both') { @('GitHub', 'Nexus') } else { @($PackageFlavor) }
$originalUpdaterFlag = $env:VITE_DSS_UPDATER_ENABLED

Push-Location $projectRoot
try {
    if (-not (Get-Command npm -ErrorAction SilentlyContinue)) { throw 'Node.js/npm was not found. Install Node.js LTS and run npm ci in the frontend folder.' }
    if (-not (Test-Path (Join-Path $projectRoot 'frontend\node_modules'))) { throw 'Frontend dependencies were not found. Run npm ci in the frontend folder.' }
    & dotnet restore $projectFile --configfile .\NuGet.Config
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }

    foreach ($flavor in $flavors) {
        $updaterEnabled = $flavor -eq 'GitHub'
        $updaterValue = $updaterEnabled.ToString().ToLowerInvariant()
        $env:VITE_DSS_UPDATER_ENABLED = $updaterValue
        $outputName = if ($flavor -eq 'GitHub') { 'web' } else { 'nexus' }
        $archiveSuffix = if ($flavor -eq 'GitHub') { 'production' } else { 'nexus' }
        $webOutput = [IO.Path]::GetFullPath((Join-Path $publishRoot $outputName))
        if (-not $webOutput.StartsWith($publishRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Package folder is outside the publish folder: $webOutput"
        }

        Push-Location (Join-Path $projectRoot 'frontend')
        try {
            & npm run build
            if ($LASTEXITCODE -ne 0) { throw "Frontend build failed for $flavor with exit code $LASTEXITCODE" }
        }
        finally { Pop-Location }

        if (Test-Path -LiteralPath $webOutput) { Remove-Item -LiteralPath $webOutput -Recurse -Force }
        & dotnet publish $projectFile -c Release --no-restore -p:RestoreConfigFile=NuGet.Config `
            -p:UseAppHost=false -p:DssUpdaterEnabled=$updaterValue -o $webOutput
        if ($LASTEXITCODE -ne 0) { throw "DSS publish failed for $flavor with exit code $LASTEXITCODE" }

        $builtAssembly = Join-Path $webOutput 'DedicatedServerStudio.dll'
        if (-not (Test-Path -LiteralPath $builtAssembly -PathType Leaf)) { throw "Published $flavor DSS assembly was not found." }
        $runtimeManifest = [ordered]@{
            assemblyPath = 'DedicatedServerStudio.dll'
            sha256 = (Get-FileHash -LiteralPath $builtAssembly -Algorithm SHA256).Hash
            builtAt = (Get-Date).ToString('o')
        }
        [IO.File]::WriteAllText(
            (Join-Path $webOutput 'dss-runtime.json'),
            ($runtimeManifest | ConvertTo-Json -Depth 3),
            [Text.UTF8Encoding]::new($false)
        )

        $commonFiles = @('CHANGELOG.md', 'DSS.cmd', 'DSS-ForceStop.cmd', 'DSS-ManualUpdate.cmd', 'DSS-ManualUpdate.ps1', 'LICENSE', 'README.md', 'run.ps1')
        foreach ($file in $commonFiles) {
            Copy-Item -LiteralPath (Join-Path $projectRoot $file) -Destination (Join-Path $webOutput $file) -Force
        }
        # This helper only applies a ZIP already placed by the user; it makes no network requests.
        Copy-Item -LiteralPath (Join-Path $projectRoot 'apply-update.ps1') -Destination (Join-Path $webOutput 'apply-update.ps1') -Force
        $manualBuildsDirectory = Join-Path $webOutput '.dss-builds'
        New-Item -ItemType Directory -Path $manualBuildsDirectory -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\manual-update\README.txt') -Destination (Join-Path $manualBuildsDirectory 'README.txt') -Force

        $archivePath = Join-Path $publishRoot "DedicatedServerStudio-$version-$archiveSuffix.zip"
        if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
        Compress-Archive -Path (Join-Path $webOutput '*') -DestinationPath $archivePath -CompressionLevel Optimal
        Write-Host "Created $archivePath"
    }

    if ($PackageFlavor -eq 'Both') {
        # Leave the developer checkout in its normal GitHub-enabled build state.
        $env:VITE_DSS_UPDATER_ENABLED = 'true'
        Push-Location (Join-Path $projectRoot 'frontend')
        try {
            & npm run build
            if ($LASTEXITCODE -ne 0) { throw "Could not restore the default frontend build (exit code $LASTEXITCODE)" }
        }
        finally { Pop-Location }
        & dotnet build $projectFile -c Release --no-restore -p:DssUpdaterEnabled=true
        if ($LASTEXITCODE -ne 0) { throw "Could not restore the default DSS build (exit code $LASTEXITCODE)" }

        $builtAssembly = Join-Path $projectRoot 'bin\Release\net10.0\DedicatedServerStudio.dll'
        if (-not (Test-Path -LiteralPath $builtAssembly -PathType Leaf)) { throw 'Default DSS assembly was not found after restoring the developer build.' }
        $developmentManifest = [ordered]@{
            assemblyPath = 'bin/Release/net10.0/DedicatedServerStudio.dll'
            sha256 = (Get-FileHash -LiteralPath $builtAssembly -Algorithm SHA256).Hash
            builtAt = (Get-Date).ToString('o')
        }
        [IO.File]::WriteAllText(
            (Join-Path $projectRoot 'dss-runtime.json'),
            ($developmentManifest | ConvertTo-Json -Depth 3),
            [Text.UTF8Encoding]::new($false)
        )
    }
}
finally {
    if ($null -eq $originalUpdaterFlag) { Remove-Item Env:VITE_DSS_UPDATER_ENABLED -ErrorAction SilentlyContinue }
    else { $env:VITE_DSS_UPDATER_ENABLED = $originalUpdaterFlag }
    Pop-Location
}
