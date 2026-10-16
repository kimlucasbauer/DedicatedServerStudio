using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DedicatedServerStudio.Models;

namespace DedicatedServerStudio.Services;

public sealed class SteamCmdService(
    StateStore state,
    GameAdapterRegistry adapters,
    InstanceSupervisor supervisor,
    IHttpClientFactory httpClients,
    ILogger<SteamCmdService> logger) : BackgroundService
{
    private const int MaxOperationOutputLines = 1000;
    private const int MaxCapturedOutputChars = 2 * 1024 * 1024;
    private const int MaxSteamCmdLineChars = 16 * 1024;
    private const string SteamCmdDownloadUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";
    private static readonly TimeSpan BuildCacheDuration = TimeSpan.FromHours(6);
    private static readonly TimeSpan SteamCmdBootstrapRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly GameBranchView[] KnownValheimBranches =
    [
        new("public", "Pública atual", "Versão pública atual do servidor Valheim.", ""),
        new("default_old", "Estável anterior", "Versão estável anterior disponibilizada pela Iron Gate.", ""),
        new("default_pre1_0", "Antes da versão 1.0", "Última versão estável anterior à versão 1.0.", ""),
        new("default_precta", "Antes de Call to Arms", "Versão anterior à atualização Call to Arms.", ""),
        new("default_prebw", "Antes de Bog Witch", "Versão anterior à atualização Bog Witch.", ""),
        new("default_preal", "Antes de Ashlands", "Versão anterior à atualização Ashlands.", ""),
        new("default_preml", "Antes de Mistlands", "Versão anterior à atualização Mistlands.", "")
    ];
    private readonly ConcurrentDictionary<Guid, OperationState> _operations = new();
    private readonly ConcurrentDictionary<(Guid ProfileId, bool UseCache), Lazy<Task>> _profileBuildChecks = new();
    private readonly ConcurrentDictionary<string, (string BuildId, DateTimeOffset CheckedAt)> _buildCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (IReadOnlyList<GameBranchView> Branches, DateTimeOffset CheckedAt)> _branchCache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _steamCmdGate = new(1, 1);
    private readonly SemaphoreSlim _buildCheckGate = new(1, 1);
    private CancellationToken _stoppingToken;

    public SteamOperationView? GetOperation(Guid profileId, long afterSequence = 0)
    {
        if (_operations.TryGetValue(profileId, out var operation)) return operation.Snapshot(afterSequence);
        return ReadLatestSavedOperation(profileId, afterSequence);
    }

    public string? GetOperationLogPath(Guid profileId, Guid? operationId = null)
    {
        if (_operations.TryGetValue(profileId, out var operation))
            return operationId.HasValue && operation.Id != operationId.Value || !File.Exists(operation.LogPath) ? null : operation.LogPath;

        var savedOperation = ReadLatestSavedOperation(profileId);
        if (savedOperation is null || operationId.HasValue && savedOperation.OperationId != operationId.Value) return null;
        return FindLatestOperationLogPath(profileId);
    }

    private SteamOperationView? ReadLatestSavedOperation(Guid profileId, long afterSequence = 0)
    {
        var path = FindLatestOperationLogPath(profileId);
        if (path is null) return null;
        var name = Path.GetFileNameWithoutExtension(path);
        var separator = name.LastIndexOf('-');
        if (separator < 0 || !Guid.TryParseExact(name[(separator + 1)..], "N", out var operationId)) return null;

        try
        {
            var output = ReadRecentOperationLines(path);
            var skip = (int)Math.Clamp(afterSequence, 0, output.Length);
            return new SteamOperationView("history", "Último registro salvo",
                "Este é o log mais recente do perfil. Ele não indica uma operação em andamento.",
                output.Skip(skip).ToArray(), false, false, 0, output.Length, null, operationId, output.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "DSS could not read the latest SteamCMD log for profile {ProfileId}", profileId);
            return null;
        }
    }

    private static string[] ReadRecentOperationLines(string path)
    {
        var output = new Queue<string>();
        var characters = 0;
        foreach (var line in File.ReadLines(path))
        {
            var visible = line.Length > MaxSteamCmdLineChars ? line[^MaxSteamCmdLineChars..] : line;
            output.Enqueue(visible);
            characters += visible.Length;
            while (output.Count > MaxOperationOutputLines || characters > MaxCapturedOutputChars)
                characters -= output.Dequeue().Length;
        }
        return output.ToArray();
    }

    private string? FindLatestOperationLogPath(Guid profileId)
    {
        var directory = Path.Combine(state.LogsDirectory, "steamcmd");
        if (!Directory.Exists(directory)) return null;
        try
        {
            return Directory.EnumerateFiles(directory, $"{profileId:N}-*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "DSS could not locate saved SteamCMD logs for profile {ProfileId}", profileId);
            return null;
        }
    }

    public async IAsyncEnumerable<SteamOperationView> StreamOperationAsync(Guid profileId, Guid? knownOperationId, long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        OperationState? operation = null;
        while (operation is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_operations.TryGetValue(profileId, out operation)) break;
            await Task.Delay(TimeSpan.FromMilliseconds(80), cancellationToken);
        }

        if (knownOperationId != operation.Id) afterSequence = 0;
        var lastRevision = -1L;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = operation.Snapshot(afterSequence);
            if (snapshot.Revision != lastRevision)
            {
                yield return snapshot;
                lastRevision = snapshot.Revision;
                afterSequence = snapshot.OutputSequence;
            }
            if (!snapshot.Running) yield break;
            await operation.WaitForChangeAsync(lastRevision, cancellationToken);
        }
    }

    public void CheckProfileInBackground(Guid profileId) => _ = QueueProfileBuildCheck(profileId, useCache: true);

    public Task CheckProfileNowAsync(Guid profileId) => QueueProfileBuildCheck(profileId, useCache: false);

    private Task QueueProfileBuildCheck(Guid profileId, bool useCache)
    {
        var key = (profileId, useCache);
        while (true)
        {
            if (_profileBuildChecks.TryGetValue(key, out var existing)) return existing.Value;
            var check = new Lazy<Task>(() => CheckProfileCoreAsync(profileId, useCache), LazyThreadSafetyMode.ExecutionAndPublication);
            if (!_profileBuildChecks.TryAdd(key, check)) continue;
            var task = check.Value;
            _ = task.ContinueWith(completed => { _profileBuildChecks.TryRemove(key, out _); }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }

    private async Task CheckProfileCoreAsync(Guid profileId, bool useCache)
    {
        try
        {
            var profile = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == profileId));
            if (profile is null || !adapters.TryGet(profile.GameId, out var adapter) ||
                string.IsNullOrWhiteSpace(profile.InstallPath) ||
                !File.Exists(Path.Combine(profile.InstallPath, adapter.ExecutableName)) ||
                _operations.TryGetValue(profileId, out var activeOperation) && activeOperation.IsRunning)
                return;

            await RefreshBuildStateAsync(profileId, adapter, useCache, _stoppingToken);
        }
        catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "DSS could not check the Steam build for profile {ProfileId}", profileId);
            await state.UpdateAsync(data =>
            {
                var current = data.Instances.FirstOrDefault(item => item.Id == profileId);
                if (current is not null) { current.LastUpdateCheck = DateTimeOffset.UtcNow; current.UpdateCheckError = ShortMessage(ex.Message); }
                return true;
            });
        }
    }

    public ServerPathCheck CheckServerPath(string? gameId, string? installPath)
    {
        if (!adapters.TryGet(gameId, out var adapter))
            return new(false, false, "", "Este jogo ainda não tem um adaptador de servidor.");
        if (string.IsNullOrWhiteSpace(installPath) || !Path.IsPathFullyQualified(installPath))
            return new(false, false, adapter.ExecutableName, "Informe um caminho absoluto para a instalação do servidor.");
        try
        {
            var fullPath = Path.GetFullPath(installPath);
            var found = File.Exists(Path.Combine(fullPath, adapter.ExecutableName));
            return new(true, found, adapter.ExecutableName, found
                ? $"{adapter.ExecutableName} encontrado."
                : $"{adapter.ExecutableName} não foi encontrado. O DSS baixará o servidor pelo SteamCMD ao instalar ou iniciar este perfil.");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return new(false, false, adapter.ExecutableName, "O caminho informado não é válido."); }
    }

    public async Task<SteamCmdSettingsView> GetSettingsAsync()
    {
        var configuredDirectory = await state.ReadAsync(data => data.SteamCmdDirectory);
        return ResolveSettings(configuredDirectory);
    }

    public async Task<IReadOnlyList<GameBranchView>> GetAvailableBranchesAsync(string gameId, CancellationToken cancellationToken = default)
    {
        if (!adapters.TryGet(gameId, out var adapter))
            throw new InvalidOperationException("Este jogo ainda não tem integração de versões Steam.");
        if (_branchCache.TryGetValue(adapter.SteamAppId, out var cached) && DateTimeOffset.UtcNow - cached.CheckedAt < BuildCacheDuration)
            return cached.Branches;

        await _buildCheckGate.WaitAsync(cancellationToken);
        try
        {
            if (_branchCache.TryGetValue(adapter.SteamAppId, out cached) && DateTimeOffset.UtcNow - cached.CheckedAt < BuildCacheDuration)
                return cached.Branches;

            var settings = await GetSettingsAsync();
            IReadOnlyList<GameBranchView> branches = KnownValheimBranches;
            if (settings.ExecutableFound)
            {
                var queryArguments = new[] { "+login", "anonymous", "+app_info_update", "1", "+app_info_print", adapter.SteamAppId, "+quit" };
                var result = await RunSteamCmdAsync(Path.Combine(settings.Directory, "steamcmd.exe"), queryArguments, null, cancellationToken);
                branches = result.ExitCode == 0 ? ParseBranches(result.Output) ?? KnownValheimBranches : KnownValheimBranches;
                if (result.ExitCode != 0 && IsSteamCmdBootstrapOutput(result.Output))
                {
                    await Task.Delay(SteamCmdBootstrapRetryDelay, cancellationToken);
                    result = await RunSteamCmdAsync(Path.Combine(settings.Directory, "steamcmd.exe"), queryArguments, null, cancellationToken);
                    branches = result.ExitCode == 0 ? ParseBranches(result.Output) ?? KnownValheimBranches : KnownValheimBranches;
                }
                if (branches.Count == 0) branches = KnownValheimBranches;
            }
            _branchCache[adapter.SteamAppId] = (branches, DateTimeOffset.UtcNow);
            return branches;
        }
        finally { _buildCheckGate.Release(); }
    }

    public async Task<SteamCmdSettingsView> SetDirectoryAsync(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Informe uma pasta absoluta do host para o SteamCMD.");
        if (!Path.IsPathFullyQualified(directory.Trim()))
            throw new ArgumentException("Informe um caminho absoluto da máquina que executa o DSS.");

        string fullPath;
        try { fullPath = Path.GetFullPath(directory.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new ArgumentException("O caminho da pasta SteamCMD não é válido."); }

        if (File.Exists(fullPath))
            throw new ArgumentException("Informe a pasta do SteamCMD, não o caminho do arquivo steamcmd.exe.");

        await state.UpdateAsync(data =>
        {
            data.SteamCmdDirectory = fullPath;
            return true;
        });
        return ResolveSettings(fullPath);
    }

    public async Task<(bool Started, string Message)> BeginInstallAsync(Guid profileId, bool startAfterInstall, Guid? serverId = null)
    {
        var profile = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == profileId));
        if (profile is null) return (false, "O perfil não foi encontrado.");
        var startEndpoint = profile.Servers.FirstOrDefault(server => server.Id == serverId) ?? profile.Servers.FirstOrDefault(server => server.Id == profile.Id) ?? profile.Servers.FirstOrDefault();
        if (startAfterInstall && string.IsNullOrWhiteSpace(startEndpoint?.ProtectedPassword)) return (false, "Defina a senha do servidor selecionado antes de iniciar.");
        if (!adapters.TryGet(profile.GameId, out _)) return (false, "Este jogo ainda não tem integração com SteamCMD.");
        if (string.IsNullOrWhiteSpace(profile.InstallPath) || !Path.IsPathFullyQualified(profile.InstallPath))
            return (false, "Informe um caminho absoluto para a pasta do servidor dedicado.");
        var installPath = Path.GetFullPath(profile.InstallPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var sharingServerIds = await state.ReadAsync(data => data.Instances
            .Where(item => Path.IsPathFullyQualified(item.InstallPath) &&
                string.Equals(Path.GetFullPath(item.InstallPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), installPath, pathComparison))
            .SelectMany(item => item.Servers.Select(server => server.Id)).ToArray());
        if (sharingServerIds.Any(id => supervisor.GetStatus(id) == "running"))
            return (false, "Pare todos os perfis que compartilham esta pasta do servidor antes de instalar ou atualizar os arquivos do jogo.");

        var logDirectory = Path.Combine(state.LogsDirectory, "steamcmd");
        var logPath = Path.Combine(logDirectory, $"{profileId:N}-{Guid.NewGuid():N}.log");
        var operation = new OperationState(startAfterInstall, _stoppingToken, logPath);
        while (true)
        {
            if (_operations.TryGetValue(profileId, out var current))
            {
                if (current.IsRunning) return (false, "Uma instalação ou atualização já está em andamento para este perfil.");
                if (_operations.TryUpdate(profileId, operation, current)) break;
            }
            else if (_operations.TryAdd(profileId, operation)) break;
        }

        if (sharingServerIds.Any(id => supervisor.GetStatus(id) == "running"))
        {
            operation.Set("failed", "Operação bloqueada", "Um perfil que compartilha esta instalação começou a executar. Pare os perfis antes de atualizar os arquivos.");
            return (false, operation.Snapshot().Message);
        }

        _ = Task.Run(() => InstallAsync(profileId, startAfterInstall, serverId, operation, operation.CancellationToken));
        return (true, startAfterInstall
            ? "O servidor não estava instalado. O SteamCMD está baixando os arquivos e iniciará o servidor quando terminar."
            : "O SteamCMD começou a instalar ou atualizar o servidor.");
    }

    public bool AbortInstall(Guid profileId) =>
        _operations.TryGetValue(profileId, out var operation) && operation.TryAbort();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        if (Environment.GetEnvironmentVariable("DSS_DISABLE_UPDATE_SCAN") == "1") return;
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var profiles = await state.ReadAsync(data => data.Instances.ToArray());
                foreach (var profile in profiles)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    if (_operations.TryGetValue(profile.Id, out var activeOperation) && activeOperation.IsRunning) continue;
                    if (!adapters.TryGet(profile.GameId, out var adapter)) continue;
                    if (!File.Exists(Path.Combine(profile.InstallPath, adapter.ExecutableName))) continue;
                    try { await RefreshBuildStateAsync(profile.Id, adapter, useCache: true, stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "DSS could not check Steam build for profile {ProfileId}", profile.Id);
                        await state.UpdateAsync(data =>
                        {
                            var current = data.Instances.FirstOrDefault(item => item.Id == profile.Id);
                            if (current is not null) { current.LastUpdateCheck = DateTimeOffset.UtcNow; current.UpdateCheckError = ShortMessage(ex.Message); }
                            return true;
                        });
                    }
                }
            }
            catch (Exception ex) { logger.LogWarning(ex, "DSS Steam update scan failed"); }

            try { await Task.Delay(TimeSpan.FromHours(6), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task InstallAsync(Guid profileId, bool startAfterInstall, Guid? serverId, OperationState operation, CancellationToken cancellationToken)
    {
        try
        {
            var profile = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == profileId));
            if (profile is null) throw new InvalidOperationException("O perfil foi removido.");
            if (!adapters.TryGet(profile.GameId, out var adapter)) throw new InvalidOperationException("Este jogo não possui integração SteamCMD.");

            operation.Set("running", "Preparando SteamCMD", "Preparando o SteamCMD oficial da Valve.");
            var steamCmd = await EnsureSteamCmdAsync(operation, cancellationToken);

            Directory.CreateDirectory(Path.GetFullPath(profile.InstallPath));
            operation.Set("running", "Instalando arquivos do jogo", $"Baixando/atualizando {adapter.DisplayName} ({adapter.SteamAppId}) na pasta configurada.");
            var selectedBranch = GetSelectedServerBranch(profile);
            // Refresh Steam's app metadata in the same process that applies the update. Otherwise a
            // recently published public build can be reported by the UI while SteamCMD still uses
            // an older app-info cache for app_update.
            var installArguments = new List<string> { "+force_install_dir", Path.GetFullPath(profile.InstallPath), "+login", "anonymous", "+app_info_update", "1", "+app_update", adapter.SteamAppId };
            // SteamCMD remembers the branch currently installed in appmanifest. Omitting -beta for
            // public therefore leaves an installation on a previous branch (for example
            // default_preal) and reports it as already up to date instead of switching to public.
            installArguments.Add("-beta");
            installArguments.Add(selectedBranch);
            if (string.Equals(selectedBranch, "public-test", StringComparison.OrdinalIgnoreCase))
            {
                installArguments.Add("-betapassword");
                installArguments.Add("yesimadebackups");
            }
            installArguments.Add("+quit");
            operation.AddOutput($"DSS: branch selecionada para o servidor: {selectedBranch}; SteamCMD será instruído a aplicar essa branch explicitamente.");
            var result = await RunSteamCmdAsync(steamCmd, installArguments, operation.AddOutput, cancellationToken);
            if (IsSteamCmdBootstrapOutput(result.Output))
            {
                operation.Set("running", "Reiniciando SteamCMD", "A instalação inicial do SteamCMD terminou. Repetindo o comando de instalação do servidor.");
                operation.AddOutput("DSS: SteamCMD concluiu a atualização inicial; aguardando a reinicialização e repetindo app_update.");
                await Task.Delay(SteamCmdBootstrapRetryDelay, cancellationToken);
                result = await RunSteamCmdAsync(steamCmd, installArguments, operation.AddOutput, cancellationToken);
            }
            if (result.ExitCode != 0 && IsAppManifestStateFailure(result.Output, adapter.SteamAppId))
            {
                operation.Set("running", "Recuperando atualização", "O SteamCMD encontrou um estado de instalação inválido. O DSS vai preservar o manifesto atual e tentar a atualização novamente.");
                operation.AddOutput($"DSS: SteamCMD retornou estado 0x6 para o app {adapter.SteamAppId}; verificando o manifesto da instalação e tentando recuperar sem apagar arquivos do servidor.");
                await EnsureNoRunningServersForInstallAsync(profile);
                var manifestBackup = QuarantineSteamAppManifest(Path.GetFullPath(profile.InstallPath), adapter.SteamAppId, profileId);
                if (manifestBackup is not null)
                    operation.AddOutput($"DSS: manifesto anterior preservado em '{manifestBackup}'.");

                await Task.Delay(SteamCmdBootstrapRetryDelay, cancellationToken);
                result = await RunSteamCmdAsync(steamCmd, installArguments, operation.AddOutput, cancellationToken);
                if (IsSteamCmdBootstrapOutput(result.Output))
                {
                    operation.Set("running", "Reiniciando SteamCMD", "O SteamCMD foi atualizado durante a recuperação. Repetindo a instalação do servidor.");
                    operation.AddOutput("DSS: SteamCMD concluiu a atualização inicial; aguardando a reinicialização e repetindo app_update.");
                    await Task.Delay(SteamCmdBootstrapRetryDelay, cancellationToken);
                    result = await RunSteamCmdAsync(steamCmd, installArguments, operation.AddOutput, cancellationToken);
                }
                if (result.ExitCode != 0)
                {
                    RestoreSteamAppManifestIfMissing(manifestBackup);
                    if (IsAppManifestStateFailure(result.Output, adapter.SteamAppId))
                        throw new InvalidOperationException($"O SteamCMD continuou falhando com o estado 0x6 após uma tentativa de recuperação. O manifesto original foi preservado em '{manifestBackup?.BackupPath ?? "nenhum manifesto anterior foi encontrado"}'. Confira espaço livre e permissões da pasta; o log completo do SteamCMD está disponível para download.");
                    throw new InvalidOperationException($"O SteamCMD terminou com código {result.ExitCode} durante a recuperação. {Tail(result.Output)}");
                }
            }
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"O SteamCMD terminou com código {result.ExitCode}. {Tail(result.Output)}");

            var executablePath = Path.Combine(Path.GetFullPath(profile.InstallPath), adapter.ExecutableName);
            if (!File.Exists(executablePath))
                throw new InvalidOperationException($"SteamCMD terminou, mas {adapter.ExecutableName} não apareceu na pasta configurada. Confira se a pasta escolhida é a pasta de instalação esperada pelo SteamCMD.");

            var installedBuild = ReadSteamManifestBuildId(Path.GetFullPath(profile.InstallPath), adapter) ?? "";
            var latestBuild = profile.LatestBuildId;
            var latestBuildRefreshed = false;
            try
            {
                latestBuild = await GetLatestBuildIdAsync(adapter.SteamAppId, steamCmd, useCache: false, cancellationToken, selectedBranch);
                latestBuildRefreshed = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                operation.AddOutput($"DSS: não foi possível confirmar o build mais recente da branch '{selectedBranch}' após a instalação: {ShortMessage(ex.Message)}");
            }
            if (IsInstalledBuildDifferent(installedBuild, latestBuild))
            {
                operation.Set("running", "Sincronizando versão do servidor", $"O manifesto ainda mostra o build {installedBuild}, diferente do build {latestBuild} da branch '{selectedBranch}'. O DSS vai repetir a atualização validando os arquivos.");
                operation.AddOutput($"DSS: build instalado {installedBuild} difere do build {latestBuild}; repetindo app_update com validate.");
                var validatedArguments = new List<string>(installArguments);
                validatedArguments.Insert(validatedArguments.Count - 1, "validate");
                result = await RunSteamCmdAsync(steamCmd, validatedArguments, operation.AddOutput, cancellationToken);
                if (IsSteamCmdBootstrapOutput(result.Output))
                {
                    operation.Set("running", "Reiniciando SteamCMD", "A atualização do SteamCMD terminou. Repetindo a validação do servidor.");
                    operation.AddOutput("DSS: SteamCMD concluiu a atualização inicial; aguardando a reinicialização e repetindo app_update validate.");
                    await Task.Delay(SteamCmdBootstrapRetryDelay, cancellationToken);
                    result = await RunSteamCmdAsync(steamCmd, validatedArguments, operation.AddOutput, cancellationToken);
                }
                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"SteamCMD não conseguiu sincronizar o build {latestBuild} da branch '{selectedBranch}'. {Tail(result.Output)}");

                installedBuild = ReadSteamManifestBuildId(Path.GetFullPath(profile.InstallPath), adapter) ?? "";
                try
                {
                    latestBuild = await GetLatestBuildIdAsync(adapter.SteamAppId, steamCmd, useCache: false, cancellationToken, selectedBranch);
                    latestBuildRefreshed = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    operation.AddOutput($"DSS: não foi possível confirmar novamente o build da branch '{selectedBranch}': {ShortMessage(ex.Message)}");
                }
            }
            var marker = Path.Combine(Path.GetFullPath(profile.InstallPath), ".dss-server.json");
            var markerSaved = true;
            try
            {
                await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(new { appId = adapter.SteamAppId, buildId = installedBuild, installedAt = DateTimeOffset.UtcNow }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                markerSaved = false;
                operation.AddOutput($"DSS: os arquivos do jogo foram atualizados, mas o marcador '{marker}' não pôde ser gravado. Verifique se a conta que executa o DSS tem permissão de gravação na pasta.");
                logger.LogWarning(ex, "SteamCMD updated profile {ProfileId}, but DSS could not write its build marker at {MarkerPath}", profileId, marker);
            }
            var markerWarning = markerSaved ? "" : " O jogo foi atualizado, mas o DSS não conseguiu gravar o arquivo .dss-server.json. Verifique a permissão de gravação da conta do DSS na pasta do servidor; a execução padrão continua sem privilégios de administrador.";
            await state.UpdateAsync(data =>
            {
                var current = data.Instances.FirstOrDefault(item => item.Id == profileId);
                if (current is not null)
                {
                    current.InstalledBuildId = installedBuild;
                    if (!string.IsNullOrWhiteSpace(latestBuild)) current.LatestBuildId = latestBuild;
                    current.LastUpdateCheck = DateTimeOffset.UtcNow;
                    current.UpdateCheckError = IsInstalledBuildDifferent(installedBuild, latestBuild)
                        ? $"SteamCMD concluiu a operação, mas o manifesto continua no build {installedBuild}; a branch '{selectedBranch}' está no build {latestBuild}. Pare o servidor e tente novamente."
                        : "";
                }
                return true;
            });
            var buildMismatch = latestBuildRefreshed && IsInstalledBuildDifferent(installedBuild, latestBuild);
            var buildMismatchMessage = buildMismatch
                ? $"SteamCMD não aplicou o build {latestBuild} da branch '{selectedBranch}'; o manifesto da pasta configurada continua no build {installedBuild}. Pare todos os servidores deste perfil e tente novamente."
                : "";

            if (buildMismatch) throw new InvalidOperationException(buildMismatchMessage);

            if (startAfterInstall)
            {
                var startProfile = await state.ReadAsync(data =>
                {
                    var root = data.Instances.FirstOrDefault(item => item.Id == profileId);
                    var endpoint = root?.Servers.FirstOrDefault(server => server.Id == serverId) ?? root?.Servers.FirstOrDefault(server => server.Id == root.Id) ?? root?.Servers.FirstOrDefault();
                    return root is null || endpoint is null ? null : root.WithServer(endpoint);
                });
                if (startProfile is null) throw new InvalidOperationException("O servidor foi removido do perfil após a instalação.");
                try { startProfile.GamePassword = SecretProtection.Unprotect(startProfile.ProtectedPassword); }
                catch { throw new InvalidOperationException("O servidor foi instalado, mas não foi possível ler a senha salva. Edite o perfil antes de iniciar."); }
                var start = await supervisor.StartAsync(startProfile);
                if (!start.Success) throw new InvalidOperationException($"O servidor foi instalado, mas não iniciou: {start.Message}");
                operation.Set("success", buildMismatch ? "Servidor iniciado · build divergente" : "Servidor iniciado",
                    (buildMismatch ? buildMismatchMessage : $"{adapter.DisplayName} foi instalado/atualizado e iniciado.") + markerWarning);
            }
            else operation.Set("success", buildMismatch ? "Instalação concluída · build divergente" : "Atualização concluída", buildMismatch
                ? buildMismatchMessage
                : string.IsNullOrWhiteSpace(installedBuild)
                    ? $"{adapter.DisplayName} foi instalado/atualizado com sucesso.{markerWarning}"
                    : $"{adapter.DisplayName} está instalado no build {installedBuild}.{markerWarning}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.Set("failed", "Operação cancelada", "A instalação foi interrompida. Os arquivos já baixados permanecem; inicie novamente para retomar pelo SteamCMD.");
        }
        catch (Exception ex)
        {
            operation.Set("failed", "Falha na instalação", ShortMessage(ex.Message));
            operation.AddOutput("DSS: " + ex.Message);
            logger.LogError(ex, "DSS failed to install/update game for profile {ProfileId}", profileId);
        }
        finally
        {
            operation.DisposeCancellation();
            _ = RetireOperationSnapshotAsync(profileId, operation);
        }
    }

    private async Task RetireOperationSnapshotAsync(Guid profileId, OperationState operation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), _stoppingToken);
            if (operation.IsRunning) return;
            ((ICollection<KeyValuePair<Guid, OperationState>>)_operations)
                .Remove(new KeyValuePair<Guid, OperationState>(profileId, operation));
        }
        catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested) { }
    }

    private async Task RefreshBuildStateAsync(Guid profileId, IGameAdapter adapter, bool useCache, CancellationToken cancellationToken)
    {
        var profile = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == profileId));
        if (profile is null) return;
        var selectedBranch = GetSelectedServerBranch(profile);
        var latestBuild = await GetLatestBuildIdAsync(adapter.SteamAppId, null, useCache, cancellationToken, selectedBranch);
        var currentProfile = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == profileId));
        if (currentProfile is null) return;
        var installedBuild = ReadInstalledBuild(currentProfile, adapter);
        await state.UpdateAsync(data =>
        {
            var current = data.Instances.FirstOrDefault(item => item.Id == profileId);
            if (current is not null)
            {
                if (!string.IsNullOrWhiteSpace(installedBuild)) current.InstalledBuildId = installedBuild;
                current.LatestBuildId = latestBuild;
                current.LastUpdateCheck = DateTimeOffset.UtcNow;
                current.UpdateCheckError = "";
            }
            return true;
        });
    }

    private async Task<string> GetLatestBuildIdAsync(string appId, string? steamCmdPath, bool useCache, CancellationToken cancellationToken, string branch = "public")
    {
        await _buildCheckGate.WaitAsync(cancellationToken);
        try
        {
            var cacheKey = $"{appId}:{branch}";
            if (useCache && _buildCache.TryGetValue(cacheKey, out var cached) && DateTimeOffset.UtcNow - cached.CheckedAt < BuildCacheDuration)
                return cached.BuildId;
            steamCmdPath ??= await EnsureSteamCmdAsync(null, cancellationToken);
            var queryArguments = new[] { "+login", "anonymous", "+app_info_update", "1", "+app_info_print", appId, "+quit" };
            var result = await RunSteamCmdAsync(steamCmdPath, queryArguments, null, cancellationToken);
            var buildId = result.ExitCode == 0 ? ParseBranchBuildId(result.Output, branch) : null;
            if ((result.ExitCode != 0 || buildId is null) && IsSteamCmdBootstrapOutput(result.Output))
            {
                await Task.Delay(SteamCmdBootstrapRetryDelay, cancellationToken);
                result = await RunSteamCmdAsync(steamCmdPath, queryArguments, null, cancellationToken);
                buildId = result.ExitCode == 0 ? ParseBranchBuildId(result.Output, branch) : null;
            }
            if (result.ExitCode != 0) throw new InvalidOperationException($"SteamCMD não conseguiu consultar o build da branch '{branch}' (código {result.ExitCode}). {Tail(result.Output)}");
            if (buildId is null)
                throw new InvalidOperationException(IsSteamCmdBootstrapOutput(result.Output)
                    ? $"O SteamCMD ainda estava atualizando os próprios arquivos e não retornou o Build ID da branch '{branch}'. Tente novamente para repetir a consulta."
                    : $"SteamCMD não retornou o Build ID da branch '{branch}'. " + Tail(result.Output));
            _buildCache[cacheKey] = (buildId, DateTimeOffset.UtcNow);
            return buildId;
        }
        finally { _buildCheckGate.Release(); }
    }

    private async Task<string> EnsureSteamCmdAsync(OperationState? operation, CancellationToken cancellationToken)
    {
        var settings = await GetSettingsAsync();
        var steamCmdExe = Path.Combine(settings.Directory, "steamcmd.exe");
        if (File.Exists(steamCmdExe)) return steamCmdExe;

        var destination = settings.Directory;
        Directory.CreateDirectory(destination);
        var zipPath = Path.Combine(destination, "steamcmd.zip");
        var staging = Path.Combine(destination, "extract-" + Guid.NewGuid().ToString("N"));
        try
        {
            operation?.Set("running", "Baixando SteamCMD", $"Baixando e preparando o SteamCMD oficial da Valve em {destination}.");
            using var response = await httpClients.CreateClient("steamcmd").GetAsync(SteamCmdDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var destinationStream = File.Create(zipPath))
                await response.Content.CopyToAsync(destinationStream, cancellationToken);
            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(zipPath, staging);
            var extracted = Path.Combine(staging, "steamcmd.exe");
            if (!File.Exists(extracted)) throw new InvalidDataException("O pacote oficial baixado não continha steamcmd.exe.");
            File.Move(extracted, steamCmdExe, overwrite: true);
            return steamCmdExe;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            if (File.Exists(zipPath)) File.Delete(zipPath);
        }
    }

    private SteamCmdSettingsView ResolveSettings(string configuredDirectory)
    {
        var defaultDirectory = Path.GetFullPath(Path.Combine(state.ContentRootPath, "steamcmd"));
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            var selected = Path.GetFullPath(configuredDirectory);
            return new(selected, defaultDirectory, File.Exists(Path.Combine(selected, "steamcmd.exe")), "Pasta escolhida nas Preferências");
        }

        var environmentPath = Environment.GetEnvironmentVariable("DSS_STEAMCMD_PATH");
        if (!string.IsNullOrWhiteSpace(environmentPath))
        {
            var fromEnvironment = ResolveExistingSteamCmdPath(environmentPath);
            if (fromEnvironment is not null)
            {
                var folder = Path.GetDirectoryName(fromEnvironment)!;
                return new(folder, defaultDirectory, true, "Variável DSS_STEAMCMD_PATH");
            }
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var priorInstallDirectories = new[]
            {
                Path.Combine(state.DataDirectory, "tools", "steamcmd"),
                Path.Combine(localAppData, "ValheimServerStudio", "tools", "steamcmd")
            }
            .Select(Path.GetFullPath)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var priorDirectory in priorInstallDirectories)
        {
            if (File.Exists(Path.Combine(priorDirectory, "steamcmd.exe")))
                return new(priorDirectory, defaultDirectory, true, "Instalação anterior do DSS");
        }

        var pathExe = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator)
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Select(directory => Path.Combine(directory.Trim(), "steamcmd.exe"))
            .FirstOrDefault(File.Exists);
        if (!string.IsNullOrWhiteSpace(pathExe))
            return new(Path.GetDirectoryName(Path.GetFullPath(pathExe))!, defaultDirectory, true, "PATH do sistema");

        return new(defaultDirectory, defaultDirectory, File.Exists(Path.Combine(defaultDirectory, "steamcmd.exe")), "Pasta padrão ao lado do DSS");
    }

    private static string? ResolveExistingSteamCmdPath(string value)
    {
        try
        {
            var fullPath = Path.GetFullPath(value.Trim());
            if (File.Exists(fullPath) && string.Equals(Path.GetFileName(fullPath), "steamcmd.exe", StringComparison.OrdinalIgnoreCase))
                return fullPath;
            if (Directory.Exists(fullPath))
            {
                var executable = Path.Combine(fullPath, "steamcmd.exe");
                if (File.Exists(executable)) return executable;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        return null;
    }

    private async Task<(int ExitCode, string Output)> RunSteamCmdAsync(string steamCmdPath, IReadOnlyList<string> args,
        Action<string, bool>? onOutput, CancellationToken cancellationToken)
    {
        await _steamCmdGate.WaitAsync(cancellationToken);
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = steamCmdPath,
                WorkingDirectory = Path.GetDirectoryName(steamCmdPath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var argument in args) start.ArgumentList.Add(argument);
            using var process = new Process { StartInfo = start };
            if (!process.Start()) throw new InvalidOperationException("O SteamCMD não pôde ser iniciado.");
            process.StandardInput.Close();
            var output = new Queue<string>();
            var outputCharacters = 0;
            var consoleLogPath = Path.Combine(start.WorkingDirectory, "logs", "console_log.txt");
            var consoleLogTask = onOutput is null
                ? Task.FromResult(false)
                : TailSteamCmdConsoleLogAsync(consoleLogPath, process, onOutput, cancellationToken);
            var bootstrapStopTriggered = 0;
            void HandleLine(string line, bool partial)
            {
                if (!partial)
                {
                    lock (output)
                    {
                        output.Enqueue(line);
                        outputCharacters += line.Length + Environment.NewLine.Length;
                        while (outputCharacters > MaxCapturedOutputChars && output.Count > 1)
                            outputCharacters -= output.Dequeue().Length + Environment.NewLine.Length;
                    }
                }
                if (partial) return;
                if (IsSteamCmdBootstrapOutput(line) && Interlocked.Exchange(ref bootstrapStopTriggered, 1) == 0)
                {
                    // SteamCMD's self-updater may relaunch into an interactive prompt without the original +commands.
                    // Stop that bootstrap process tree so the caller can rerun the exact command on the updated client.
                    try { process.Kill(entireProcessTree: true); }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
            }
            var outTask = PumpAsync(process.StandardOutput, HandleLine, cancellationToken);
            var errorTask = PumpAsync(process.StandardError, HandleLine, cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
                await Task.WhenAll(outTask, errorTask);
                var loggedToConsoleFile = await consoleLogTask;
                string capturedOutput;
                lock (output) capturedOutput = string.Join(Environment.NewLine, output);
                if (onOutput is not null && !loggedToConsoleFile)
                    foreach (var line in output)
                        onOutput(line, false);
                return (process.ExitCode, capturedOutput);
            }
            catch
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                }
                try { await Task.WhenAll(outTask, errorTask, consoleLogTask); } catch { }
                throw;
            }
        }
        finally { _steamCmdGate.Release(); }
    }

    private static async Task PumpAsync(StreamReader reader, Action<string, bool> onLine, CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        var buffer = new char[1024];
        var lineTruncated = false;
        void FlushLine()
        {
            onLine(lineTruncated ? line + " … [linha truncada]" : line.ToString(), false);
            line.Clear();
            lineTruncated = false;
        }
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0) break;
            for (var index = 0; index < count; index++)
            {
                var character = buffer[index];
                if (character is '\r' or '\n')
                    FlushLine();
                else if (line.Length < MaxSteamCmdLineChars) line.Append(character);
                else lineTruncated = true;
            }
            if (line.Length > 0 || lineTruncated) onLine(lineTruncated ? line + " … [linha truncada]" : line.ToString(), true);
        }
        if (line.Length > 0 || lineTruncated) FlushLine();
    }

    private static async Task<bool> TailSteamCmdConsoleLogAsync(string path, Process process, Action<string, bool> onOutput,
        CancellationToken cancellationToken)
    {
        long position = File.Exists(path) ? new FileInfo(path).Length : 0;
        var pending = new StringBuilder();
        var observedNewLogData = false;
        var previousWasCarriageReturn = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var readData = false;
            try
            {
                if (File.Exists(path))
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (stream.Length < position)
                    {
                        position = 0;
                        pending.Clear();
                        previousWasCarriageReturn = false;
                    }
                    if (stream.Length > position)
                    {
                        stream.Position = position;
                        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 4096, leaveOpen: true);
                        var buffer = new char[4096];
                        int count;
                        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
                        {
                            readData = true;
                            observedNewLogData = true;
                            for (var index = 0; index < count; index++)
                            {
                                var character = buffer[index];
                                if (character == '\r')
                                {
                                    onOutput(CleanSteamCmdLogLine(pending.ToString()), false);
                                    pending.Clear();
                                    previousWasCarriageReturn = true;
                                }
                                else if (character == '\n')
                                {
                                    if (!previousWasCarriageReturn) onOutput(CleanSteamCmdLogLine(pending.ToString()), false);
                                    pending.Clear();
                                    previousWasCarriageReturn = false;
                                }
                                else
                                {
                                    if (pending.Length < MaxSteamCmdLineChars) pending.Append(character);
                                    previousWasCarriageReturn = false;
                                }
                            }
                        }
                        position = stream.Position;
                        if (pending.Length > 0) onOutput(CleanSteamCmdLogLine(pending.ToString()), true);
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            if (process.HasExited && !readData) break;
            await Task.Delay(TimeSpan.FromMilliseconds(125), cancellationToken);
        }

        if (pending.Length > 0) onOutput(CleanSteamCmdLogLine(pending.ToString()), false);
        return observedNewLogData;
    }

    private static string CleanSteamCmdLogLine(string line) =>
        Regex.Replace(line, @"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\]\s*", "");

    private static string ReadInstalledBuild(ServerProfile profile, IGameAdapter adapter)
    {
        if (string.IsNullOrWhiteSpace(profile.InstallPath) || !Directory.Exists(profile.InstallPath)) return "";
        var manifestBuild = ReadSteamManifestBuildId(Path.GetFullPath(profile.InstallPath), adapter);
        if (!string.IsNullOrWhiteSpace(manifestBuild)) return manifestBuild;

        var markerPath = Path.Combine(profile.InstallPath, ".dss-server.json");
        try
        {
            if (File.Exists(markerPath))
            {
                using var marker = JsonDocument.Parse(File.ReadAllText(markerPath));
                if (marker.RootElement.TryGetProperty("buildId", out var build) && build.ValueKind == JsonValueKind.String)
                    return build.GetString() ?? "";
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }

        return profile.InstalledBuildId;
    }

    private static string? ReadSteamManifestBuildId(string installPath, IGameAdapter adapter)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath)) return null;
        var appManifest = $"appmanifest_{adapter.SteamAppId}.acf";
        var fullInstallPath = Path.GetFullPath(installPath);
        foreach (var manifestPath in new[]
        {
            Path.Combine(fullInstallPath, appManifest),
            Path.Combine(fullInstallPath, "steamapps", appManifest)
        })
        {
            try
            {
                if (!File.Exists(manifestPath)) continue;
                var match = Regex.Match(File.ReadAllText(manifestPath), "(?im)^\\s*\\\"buildid\\\"\\s*\\\"(?<build>\\d+)\\\"");
                if (match.Success) return match.Groups["build"].Value;
            }
            catch (IOException) { }
        }
        return null;
    }

    private static bool IsInstalledBuildDifferent(string installedBuild, string latestBuild) =>
        long.TryParse(installedBuild, out var installed) && long.TryParse(latestBuild, out var latest) && latest != installed;

    private static string? ParseBranchBuildId(string output, string branch) =>
        ParseBranches(output)?.FirstOrDefault(item => string.Equals(item.Id, branch, StringComparison.OrdinalIgnoreCase))?.BuildId;

    private static IReadOnlyList<GameBranchView>? ParseBranches(string output)
    {
        var block = Regex.Match(output, "(?is)\\\"branches\\\"\\s*\\{(?<body>(?:[^{}]|\\{[^{}]*\\})*)\\}");
        if (!block.Success) return null;

        var branches = new List<GameBranchView>();
        foreach (Match match in Regex.Matches(block.Groups["body"].Value, "(?is)\\\"(?<id>[A-Za-z0-9_.-]+)\\\"\\s*\\{(?<body>[^{}]*)\\}"))
        {
            var id = match.Groups["id"].Value;
            var body = match.Groups["body"].Value;
            var buildId = Regex.Match(body, "(?is)\\\"buildid\\\"\\s*\\\"(?<value>\\d+)\\\"").Groups["value"].Value;
            var description = Regex.Match(body, "(?is)\\\"description\\\"\\s*\\\"(?<value>[^\\\"]*)\\\"").Groups["value"].Value;
            if (string.IsNullOrWhiteSpace(buildId)) continue;
            var name = id switch
            {
                "public" => "Pública atual",
                "default_old" => "Estável anterior",
                "public-test" => "Teste público (instável)",
                _ => string.IsNullOrWhiteSpace(description) ? id : description
            };
            branches.Add(new(id, name, description, buildId));
        }

        return branches.Count == 0 ? null : branches
            .OrderBy(branch => branch.Id == "public" ? 0 : branch.Id == "default_old" ? 1 : 2)
            .ThenBy(branch => branch.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string GetSelectedServerBranch(ServerProfile profile)
    {
        if (profile.GameSettings.TryGetValue("serverBranch", out var value) && value.ValueKind == JsonValueKind.String)
        {
            var branch = value.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(branch) && Regex.IsMatch(branch, "^[A-Za-z0-9_.-]{1,80}$")) return branch;
        }
        return "public";
    }

    private static bool IsSteamCmdBootstrapOutput(string output) =>
        output.Contains("Update complete, launching", StringComparison.OrdinalIgnoreCase);

    private static bool IsAppManifestStateFailure(string output, string appId) =>
        Regex.IsMatch(output,
            $@"Error!\s*App\s*'{Regex.Escape(appId)}'\s*state\s+is\s+0x6\s+after\s+update\s+job",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private async Task EnsureNoRunningServersForInstallAsync(ServerProfile profile)
    {
        var installPath = Path.GetFullPath(profile.InstallPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var serverIds = await state.ReadAsync(data => data.Instances
            .Where(item => Path.IsPathFullyQualified(item.InstallPath) &&
                string.Equals(Path.GetFullPath(item.InstallPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), installPath, comparison))
            .SelectMany(item => item.Servers.Select(server => server.Id))
            .Distinct()
            .ToArray());
        var active = serverIds.Where(id => supervisor.GetStatus(id) == "running").ToArray();
        if (active.Length > 0)
            throw new InvalidOperationException("A recuperação do manifesto foi cancelada porque um servidor que compartilha esta instalação está em execução. Pare todos os servidores e tente novamente.");
    }

    private SteamAppManifestBackup? QuarantineSteamAppManifest(string installPath, string appId, Guid profileId)
    {
        var manifestPath = Path.Combine(installPath, "steamapps", $"appmanifest_{appId}.acf");
        if (!File.Exists(manifestPath)) return null;

        var backupDirectory = Path.Combine(state.LogsDirectory, "steamcmd", "manifest-recovery");
        Directory.CreateDirectory(backupDirectory);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
        var backupPath = Path.Combine(backupDirectory, $"{profileId:N}-{appId}-{timestamp}.acf.bak");
        File.Move(manifestPath, backupPath);
        return new(manifestPath, backupPath);
    }

    private static void RestoreSteamAppManifestIfMissing(SteamAppManifestBackup? backup)
    {
        if (backup is null || !File.Exists(backup.BackupPath) || File.Exists(backup.OriginalPath)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(backup.OriginalPath)!);
        File.Move(backup.BackupPath, backup.OriginalPath);
    }

    private sealed record SteamAppManifestBackup(string OriginalPath, string BackupPath);

    private static string Tail(string value)
    {
        var lines = value.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 &&
                !line.Contains("Installing update...", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("Cleaning up...", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("Update complete, launching", StringComparison.OrdinalIgnoreCase))
            .TakeLast(4);
        var tail = string.Join(" | ", lines);
        return tail.Length > 0 ? tail : "Sem detalhes adicionais do SteamCMD.";
    }
    private static string ShortMessage(string value) => value.Length <= 500 ? value : value[..500];

    private sealed class OperationState
    {
        private const int MaxOutputLines = 1000;
        private const int MaxOutputCharacters = 512 * 1024;
        private const int MaxVisibleLineCharacters = 16 * 1024;
        private readonly bool startsServerAfterInstall;
        private readonly object _gate = new();
        private readonly Queue<string> _output = new();
        private readonly CancellationTokenSource _cancellation;
        private readonly StreamWriter _logWriter;
        private long _outputBaseSequence;
        private long _outputSequence;
        private int _outputCharacters;
        private long _revision;
        private TaskCompletionSource<bool> _changed = NewChangeSignal();
        private string _state = "queued";
        private string _phase = "Na fila";
        private string _message = "A operação está na fila.";
        private string? _currentOutputLine;
        public Guid Id { get; } = Guid.NewGuid();
        public string LogPath { get; }

        public OperationState(bool startsServerAfterInstall, CancellationToken stoppingToken, string logPath)
        {
            this.startsServerAfterInstall = startsServerAfterInstall;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            LogPath = logPath;
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            _logWriter = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false))
            { AutoFlush = true };
        }

        public bool IsRunning { get { lock (_gate) return _state is "queued" or "running"; } }
        public CancellationToken CancellationToken => _cancellation.Token;
        public bool TryAbort()
        {
            lock (_gate)
            {
                if (_state is not ("queued" or "running")) return false;
                _cancellation.Cancel();
                return true;
            }
        }
        public void DisposeCancellation()
        {
            lock (_gate)
            {
                _logWriter.Flush();
                _logWriter.Dispose();
                _cancellation.Dispose();
            }
        }
        public void Set(string state, string phase, string message)
        {
            lock (_gate)
            {
                if (_state == state && _phase == phase && _message == message) return;
                _state = state;
                _phase = phase;
                _message = message;
                SignalChanged();
            }
        }
        public void AddOutput(string line, bool partial = false)
        {
            lock (_gate)
            {
                if (partial)
                {
                    var current = line.Length > 2000 ? line[^2000..] : line;
                    if (string.Equals(_currentOutputLine, current, StringComparison.Ordinal)) return;
                    _currentOutputLine = current;
                    SignalChanged();
                    return;
                }
                var changed = _currentOutputLine is not null || line.Length > 0;
                _currentOutputLine = null;
                if (line.Length > 0)
                {
                    _logWriter.WriteLine(line);
                    var visible = line.Length > MaxVisibleLineCharacters ? line[^MaxVisibleLineCharacters..] : line;
                    _output.Enqueue(visible);
                    _outputCharacters += visible.Length;
                    _outputSequence++;
                    while (_output.Count > MaxOutputLines || _outputCharacters > MaxOutputCharacters)
                    {
                        _outputCharacters -= _output.Dequeue().Length;
                        _outputBaseSequence++;
                    }
                }
                if (changed) SignalChanged();
            }
        }
        public async Task WaitForChangeAsync(long afterRevision, CancellationToken cancellationToken)
        {
            Task wait;
            lock (_gate)
            {
                if (_revision > afterRevision || _state is not ("queued" or "running")) return;
                wait = _changed.Task;
            }
            await wait.WaitAsync(cancellationToken);
        }
        public SteamOperationView Snapshot(long afterSequence = 0)
        {
            lock (_gate)
            {
                var skipped = (int)Math.Clamp(afterSequence - _outputBaseSequence, 0, _output.Count);
                var lines = skipped == _output.Count ? Array.Empty<string>() : _output.Skip(skipped).ToArray();
                return new(_state, _phase, _message, lines, _state is "queued" or "running",
                    startsServerAfterInstall, _outputBaseSequence, _outputSequence, _currentOutputLine, Id, _revision);
            }
        }
        private void SignalChanged()
        {
            _revision++;
            var changed = _changed;
            _changed = NewChangeSignal();
            changed.TrySetResult(true);
        }
        private static TaskCompletionSource<bool> NewChangeSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
