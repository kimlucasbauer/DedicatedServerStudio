param([switch]$LiveCatalog, [string]$AppPath)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($AppPath)) { $AppPath = Join-Path $projectRoot 'bin\Debug\net10.0\DedicatedServerStudio.dll' }
if (-not (Test-Path -LiteralPath $AppPath)) { throw "Assembly do DSS não encontrada: $AppPath. Rode dotnet build primeiro." }
$appPath = (Resolve-Path -LiteralPath $AppPath).Path

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('ValheimStudio-smoke-' + [Guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $tempRoot 'data'
$serverRoot = Join-Path $tempRoot 'server-fixture'
$configRoot = Join-Path $serverRoot 'BepInEx\config'
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()
$baseUrl = "http://127.0.0.1:$port"
$process = $null
$http = $null
$anonymousHttp = $null

function Invoke-SmokeApi([string]$Path, [string]$Method = 'GET', [object]$Body = $null, [Net.Http.HttpClient]$Client = $http) {
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method), "$baseUrl$Path")
    if ($null -ne $Body) {
        $json = if ($Body -is [string]) { $Body } else { $Body | ConvertTo-Json -Depth 8 -Compress }
        $request.Content = [Net.Http.StringContent]::new($json, [Text.Encoding]::UTF8, 'application/json')
    }
    try { $response = $Client.SendAsync($request).GetAwaiter().GetResult() }
    catch { throw "HTTP request failed for $Method $Path at $baseUrl; inner=$($_.Exception.InnerException)" }
    $content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $statusCode = [int]$response.StatusCode
    $request.Dispose()
    $response.Dispose()
    if ($statusCode -lt 200 -or $statusCode -ge 300) { throw "HTTP $statusCode $Path`: $content" }
    if ([string]::IsNullOrWhiteSpace($content)) { throw "HTTP $statusCode $Path returned empty content; headers=$($response.Headers | ConvertTo-Json -Compress)" }
    return $content | ConvertFrom-Json
}

try {
    New-Item -ItemType Directory -Force $dataRoot, $configRoot | Out-Null
    [IO.File]::WriteAllText((Join-Path $configRoot 'Smoke.cfg'), "before=true`n")

    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = (Get-Command dotnet).Source
    $start.Arguments = '"' + $appPath + '" --no-browser'
    $start.WorkingDirectory = $projectRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['DSS_URL'] = $baseUrl
    $start.Environment['DSS_DATA_DIR'] = $dataRoot
    $start.Environment['DSS_NO_BROWSER'] = '1'
    $start.Environment['DSS_DISABLE_UPDATE_SCAN'] = '1'
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    [void]$process.Start()
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()

    $ready = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        try {
            $health = Invoke-RestMethod "$baseUrl/api/health" -TimeoutSec 2
            if ($health.status -eq 'ready') { $ready = $true; break }
        } catch { Start-Sleep -Milliseconds 250 }
        if ($process.HasExited) { break }
    }
    if (-not $ready) { throw "Painel não iniciou. $($stderrTask.GetAwaiter().GetResult())" }

    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.CookieContainer = [Net.CookieContainer]::new()
    $handler.UseCookies = $true
    $http = [Net.Http.HttpClient]::new($handler)
    $anonymousHttp = [Net.Http.HttpClient]::new()
    $status = Invoke-SmokeApi '/api/auth/status'
    if (-not $status.setupRequired) { throw "Setup inicial não apareceu; resposta=$($status | ConvertTo-Json -Compress)" }
    Invoke-SmokeApi '/api/auth/setup' 'POST' @{ password = 'smoke-test-password-2026' } | Out-Null
    $games = Invoke-SmokeApi '/api/games'
    if ($games.Count -ne 1 -or $games[0].id -ne 'valheim' -or $games[0].portSpan -ne 2 -or $games[0].steamAppId -ne '896660') { throw 'O registro de jogos não corresponde ao adaptador Valheim.' }
    $steamSettings = Invoke-SmokeApi '/api/settings/steamcmd'
    $expectedSteamDefault = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $appPath) 'steamcmd'))
    if ($steamSettings.defaultDirectory -ne $expectedSteamDefault) { throw "A pasta padrão do SteamCMD não fica ao lado do DSS. Esperado=$expectedSteamDefault; recebido=$($steamSettings.defaultDirectory)" }
    $customSteamDirectory = Join-Path $tempRoot 'custom-steamcmd'
    $savedSteamSettings = Invoke-SmokeApi '/api/settings/steamcmd' 'PUT' @{ directory = $customSteamDirectory }
    $reloadedSteamSettings = Invoke-SmokeApi '/api/settings/steamcmd'
    if ($savedSteamSettings.directory -ne $customSteamDirectory -or $reloadedSteamSettings.directory -ne $customSteamDirectory) { throw 'A preferência de pasta do SteamCMD não foi salva.' }
    New-Item -ItemType Directory -Force $customSteamDirectory | Out-Null
    [IO.File]::WriteAllText((Join-Path $customSteamDirectory 'steamcmd.exe'), 'fixture')
    $existingSteamSettings = Invoke-SmokeApi '/api/settings/steamcmd'
    if (-not $existingSteamSettings.executableFound) { throw 'A pasta escolhida não reconheceu uma instalação existente do SteamCMD.' }
    $relativeSteamPathRejected = $false
    try { Invoke-SmokeApi '/api/settings/steamcmd' 'PUT' @{ directory = 'steamcmd' } | Out-Null }
    catch { $relativeSteamPathRejected = $_.Exception.Message -match 'HTTP 400' }
    if (-not $relativeSteamPathRejected) { throw 'A preferência do SteamCMD aceitou um caminho relativo.' }
    $pathCheck = Invoke-SmokeApi "/api/games/valheim/server-path?installPath=$([Uri]::EscapeDataString($serverRoot))"
    if ($pathCheck.executableFound -or $pathCheck.message -notmatch 'SteamCMD') { throw 'A ausência do servidor não encaminhou para a instalação SteamCMD.' }

    $anonymousRejected = $false
    try { Invoke-SmokeApi '/api/instances' 'GET' $null $anonymousHttp | Out-Null } catch { $anonymousRejected = $true }
    if (-not $anonymousRejected) { throw 'A rota de instâncias aceitou uma sessão anônima.' }

    $profile = Invoke-SmokeApi '/api/instances' 'POST' @{
        name = 'Smoke fixture'; installPath = $serverRoot; serverName = 'Smoke'; worldName = 'Fixture';
        saveDirectory = ''; gamePassword = 'valid-password'; port = 26000;
        isPublic = $false; crossplay = $false; extraArguments = @()
    }
    if ($profile.gameId -ne 'valheim' -or $profile.portSpan -ne 2) { throw 'O perfil não retornou o jogo e a faixa de portas.' }
    if (-not $profile.useInstallPathSaves -or $profile.resolvedSaveDirectory -ne (Join-Path $serverRoot 'Saves')) { throw 'A pasta Saves padrão do perfil não aponta para a raiz da instalação.' }
    $export = Invoke-SmokeApi "/api/instances/$($profile.id)/export"
    if ($export.passwordConfigured -ne $true -or ($export | ConvertTo-Json -Depth 8) -match 'valid-password|protectedPassword') { throw 'O JSON exportado incluiu dados secretos.' }
    $import = $export | ConvertTo-Json -Depth 8 | ConvertFrom-Json
    $import.name = 'Imported fixture'
    $import.port = 26010
    $import.saveDirectory = ''
    $import.worldName = 'ImportedWorld'
    $imported = Invoke-SmokeApi '/api/instances/import' 'POST' $import
    if ($imported.hasPassword -or $imported.name -ne 'Imported fixture') { throw 'A importação não omitiu a senha do perfil.' }
    $console = Invoke-SmokeApi "/api/instances/$($profile.id)/console?after=0"
    if ($console.running -or $null -eq $console.entries) { throw 'O endpoint de console não retornou o estado do perfil parado.' }
    $portCollision = $false
    try {
        Invoke-SmokeApi '/api/instances' 'POST' @{
            name = 'Port collision'; installPath = ''; serverName = 'Other'; worldName = 'Other';
            gamePassword = 'valid-password'; port = 26001; isPublic = $false; crossplay = $false; extraArguments = @()
        } | Out-Null
    } catch { $portCollision = $_.Exception.Message -match 'HTTP 409' }
    if (-not $portCollision) { throw 'A validação não detectou conflito entre portas sobrepostas.' }
    $saveCollision = $false
    try {
        Invoke-SmokeApi '/api/instances' 'POST' @{
            name = 'Save collision'; installPath = $serverRoot; saveDirectory = '';
            serverName = 'Other'; worldName = 'Fixture'; gamePassword = 'valid-password'; port = 26020;
            isPublic = $false; crossplay = $false; extraArguments = @()
        } | Out-Null
    } catch { $saveCollision = $_.Exception.Message -match 'HTTP 409' }
    if (-not $saveCollision) { throw 'A validação permitiu compartilhar pasta de saves entre perfis.' }
    $configUrl = "/api/instances/$($profile.id)/configs/Smoke.cfg"
    $saved = Invoke-SmokeApi $configUrl 'PUT' @{ content = "after=true`n" }
    $history = Invoke-SmokeApi "/api/history?instanceId=$($profile.id)"
    $revision = $history.revisions | Where-Object { $_.id -eq $saved.id } | Select-Object -First 1
    if (-not $revision.canRollback) { throw 'A revisão de BepInEx não está marcada para rollback.' }
    Invoke-SmokeApi "/api/instances/$($profile.id)/history/$($saved.id)/restore-lines" 'POST' '{}' | Out-Null
    $restored = Invoke-SmokeApi $configUrl
    if ($restored.content -ne "before=true`n") { throw 'O rollback não restaurou o conteúdo inicial.' }

    $operation = Invoke-SmokeApi "/api/instances/$($profile.id)/game/update-status"
    if ($null -ne $operation.operation) { throw 'O perfil de teste deveria iniciar sem operação SteamCMD.' }
    $providers = Invoke-SmokeApi '/api/providers'
    if ($providers.providers.Count -lt 3) { throw 'Os três conectores esperados não foram encontrados.' }
    $html = $http.GetStringAsync("$baseUrl/").GetAwaiter().GetResult()
    if ($html -notmatch 'Dedicated Server Studio' -or $html -notmatch 'dss-mark.svg') { throw 'A marca web do DSS não foi servida.' }
    if ($LiveCatalog) {
        foreach ($provider in @('thunderstore', 'hexium')) {
            try {
                $catalog = Invoke-SmokeApi "/api/catalog?provider=$provider&q=BepInExPack"
                Write-Output "LIVE PASS: $provider retornou $($catalog.items.Count) resultado(s)."
            } catch {
                Write-Output "LIVE UNVERIFIED: $provider — $($_.Exception.Message)"
            }
        }
    }
    Invoke-SmokeApi '/api/auth/logout' 'POST' '{}' | Out-Null
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        try { Invoke-SmokeApi '/api/auth/login' 'POST' @{ password = 'senha-incorreta-para-teste' } | Out-Null; throw 'Senha incorreta foi aceita.' }
        catch { if ($_.Exception.Message -notmatch 'HTTP 401') { throw } }
    }
    $rateLimited = $false
    try { Invoke-SmokeApi '/api/auth/login' 'POST' @{ password = 'senha-incorreta-para-teste' } | Out-Null }
    catch { $rateLimited = $_.Exception.Message -match 'HTTP 429' }
    if (-not $rateLimited) { throw 'O endpoint não limitou tentativas repetidas de login.' }

    Write-Output 'PASS: autenticação, pasta padrão/configurável do SteamCMD, verificação de executável, App ID Steam, Saves padrão, perfis JSON/portas, console, histórico/rollback, conectores e marca DSS.'
}
finally {
    if ($process -and -not $process.HasExited) {
        try { $process.Kill($true); $process.WaitForExit(5000) | Out-Null } catch { }
    }
    if ($process) { $process.Dispose() }
    if ($http) { $http.Dispose() }
    if ($anonymousHttp) { $anonymousHttp.Dispose() }
    if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
}
