$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet-cli-home'
$env:APPDATA = Join-Path $projectRoot '.dotnet-cli-home\appdata'
New-Item -ItemType Directory -Force $env:APPDATA | Out-Null

Push-Location $projectRoot
try {
    if (-not (Get-Command npm -ErrorAction SilentlyContinue)) { throw 'Node.js/npm was not found. Install Node.js LTS and run npm ci in the frontend folder.' }
    if (-not (Test-Path (Join-Path $projectRoot 'frontend\node_modules'))) { throw 'Frontend dependencies were not found. Run npm ci in the frontend folder.' }
    Push-Location (Join-Path $projectRoot 'frontend')
    try {
        & npm run build
        if ($LASTEXITCODE -ne 0) { throw "npm run build failed with exit code $LASTEXITCODE" }
    }
    finally { Pop-Location }
    & dotnet restore .\DedicatedServerStudio.csproj --configfile .\NuGet.Config
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }
    $publishRoot = Join-Path $projectRoot 'publish'
    $webOutput = [IO.Path]::GetFullPath((Join-Path $publishRoot 'web'))
    $resolvedProjectRoot = [IO.Path]::GetFullPath($projectRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not $webOutput.StartsWith($resolvedProjectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Publish folder is outside the project root: $webOutput"
    }
    if (Test-Path -LiteralPath $webOutput) { Remove-Item -LiteralPath $webOutput -Recurse -Force }
    & dotnet publish .\DedicatedServerStudio.csproj -c Release --no-restore -p:RestoreConfigFile=NuGet.Config `
        -p:UseAppHost=false -o .\publish\web
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

    $builtAssembly = Join-Path $projectRoot 'bin\Release\net10.0\DedicatedServerStudio.dll'
    if (-not (Test-Path -LiteralPath $builtAssembly -PathType Leaf)) { throw 'Built DSS assembly was not found.' }
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

    # The source manifest identifies the development build. A production package runs
    # the published assembly from its root, so generate a matching manifest here.
    $publishedAssembly = Join-Path $webOutput 'DedicatedServerStudio.dll'
    if (-not (Test-Path -LiteralPath $publishedAssembly -PathType Leaf)) { throw 'Published DSS assembly was not found.' }
    $publishedHash = (Get-FileHash -LiteralPath $publishedAssembly -Algorithm SHA256).Hash
    $runtimeManifest = [ordered]@{
        assemblyPath = 'DedicatedServerStudio.dll'
        sha256 = $publishedHash
        builtAt = (Get-Date).ToString('o')
    }
    [IO.File]::WriteAllText(
        (Join-Path $webOutput 'dss-runtime.json'),
        ($runtimeManifest | ConvertTo-Json -Depth 3),
        [Text.UTF8Encoding]::new($false)
    )

    foreach ($file in @('apply-update.ps1', 'CHANGELOG.md', 'DSS.cmd', 'DSS-ForceStop.cmd', 'LICENSE', 'README.md', 'run.ps1')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot $file) -Destination (Join-Path $webOutput $file) -Force
    }
}
finally { Pop-Location }
