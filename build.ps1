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
    & dotnet build $projectFile --no-restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }
}
finally { Pop-Location }
