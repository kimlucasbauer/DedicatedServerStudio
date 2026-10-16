$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile = Join-Path $projectRoot 'DedicatedServerStudio.csproj'
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet-cli-home'
$env:APPDATA = Join-Path $env:DOTNET_CLI_HOME 'appdata'
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
    & dotnet restore $projectFile --configfile .\NuGet.Config
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }
    & dotnet build $projectFile -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

    $builtAssembly = Join-Path $projectRoot 'bin\Release\net10.0\DedicatedServerStudio.dll'
    if (-not (Test-Path -LiteralPath $builtAssembly -PathType Leaf)) { throw 'Built DSS assembly was not found.' }
    $runtimeManifest = [ordered]@{
        assemblyPath = 'bin/Release/net10.0/DedicatedServerStudio.dll'
        sha256 = (Get-FileHash -LiteralPath $builtAssembly -Algorithm SHA256).Hash
        builtAt = (Get-Date).ToString('o')
    }
    [IO.File]::WriteAllText(
        (Join-Path $projectRoot 'dss-runtime.json'),
        ($runtimeManifest | ConvertTo-Json -Depth 3),
        [Text.UTF8Encoding]::new($false)
    )
}
finally { Pop-Location }
