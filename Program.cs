using System.Diagnostics;
using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using DedicatedServerStudio.Models;
using DedicatedServerStudio.Services;

if (args.Length == 2 && args[0].Equals("--console-bridge", StringComparison.OrdinalIgnoreCase))
{
    await ConsoleBridgeHost.RunAsync(args[1]);
    return;
}

var argsList = args.ToHashSet(StringComparer.OrdinalIgnoreCase);
var isLan = argsList.Contains("--lan");
var isPublic = argsList.Contains("--public");
var listenMode = isPublic ? "public" : isLan ? "lan" : "local";
var configuredListenUrl = Environment.GetEnvironmentVariable("DSS_URL") ?? Environment.GetEnvironmentVariable("VALHEIM_STUDIO_URL");
var listenUrl = ResolveListenUrl(listenMode, configuredListenUrl);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(listenUrl);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
// Nexus download URLs may contain short-lived authorization parameters; keep request URLs out of logs.
builder.Logging.AddFilter("System.Net.Http.HttpClient.catalog", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient.mod-download", LogLevel.Warning);
builder.Logging.AddProvider(new DssFileLoggerProvider(Path.Combine(builder.Environment.ContentRootPath, "logs")));
builder.Services.AddAntiforgery(options => options.HeaderName = "X-DSS-CSRF");
builder.Services.AddSingleton<StateStore>();
builder.Services.AddSingleton<ProviderCredentialVault>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<DssUpdateProgressStore>();
builder.Services.AddSingleton<IGameAdapter, ValheimGameAdapter>();
builder.Services.AddSingleton<GameAdapterRegistry>();
builder.Services.AddSingleton<WindowsFirewallService>();
builder.Services.AddSingleton<HostMetricsService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<HostMetricsService>());
builder.Services.AddSingleton<InstanceSupervisor>();
builder.Services.AddSingleton<PlayerManagementService>();
builder.Services.AddHttpClient("steamcmd", client =>
{
    client.Timeout = TimeSpan.FromMinutes(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("DedicatedServerStudio/0.4.2");
});
builder.Services.AddSingleton<SteamCmdService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<SteamCmdService>());
builder.Services.AddSingleton<ProviderCatalogService>();
builder.Services.AddSingleton<NexusDownloadAuthorizationService>();
builder.Services.AddSingleton<ModPackageManager>();
builder.Services.AddHttpClient("catalog", client =>
{
    client.Timeout = TimeSpan.FromSeconds(18);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("DedicatedServerStudio/0.4.2");
});
builder.Services.AddHttpClient("mod-download", client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
    client.DefaultRequestHeaders.UserAgent.ParseAdd("DedicatedServerStudio/0.4.2");
});
builder.Services.AddHttpClient("r2modman", client =>
{
    client.Timeout = TimeSpan.FromSeconds(45);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("DedicatedServerStudio/0.4.2");
});
builder.Services.AddHttpClient("public-ip", client =>
{
    client.Timeout = TimeSpan.FromSeconds(4);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("DedicatedServerStudio/0.4.2");
});
builder.Services.AddHttpClient<GitHubReleaseService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(45);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("DedicatedServerStudio/0.4.2");
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
});
builder.Services.AddSingleton<R2ModmanProfileService>();

var app = builder.Build();
var listenPort = Uri.TryCreate(listenUrl, UriKind.Absolute, out var listenUri) ? listenUri.Port : 5089;
using var instanceMutex = new Mutex(initiallyOwned: true, $"DedicatedServerStudio-{listenPort}", out var isFirstInstance);
if (!isFirstInstance)
{
    app.Logger.LogWarning("Another DSS instance is already active on port {Port}; this duplicate launch will exit.", listenPort);
    return;
}

var state = app.Services.GetRequiredService<StateStore>();
var auth = app.Services.GetRequiredService<AuthService>();
var updateProgress = app.Services.GetRequiredService<DssUpdateProgressStore>();
if (listenMode != "local" && await state.ReadAsync(data => string.IsNullOrWhiteSpace(data.AdminPasswordHash)))
    throw new InvalidOperationException("Set up the administrator password on localhost before enabling LAN or public access.");

app.Use(async (context, next) =>
{
    using var revisionActor = state.UseRevisionActor(auth.GetSessionUserId(context));
    if ((context.Request.Method is "POST" or "PUT" or "PATCH" or "DELETE") && context.Request.Headers.Origin.Count > 0)
    {
        var origin = context.Request.Headers.Origin.ToString();
        var expected = $"{context.Request.Scheme}://{context.Request.Host}";
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed) ||
            !string.Equals(parsed.GetLeftPart(UriPartial.Authority), expected, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Cross-origin state changes are blocked." });
            return;
        }
    }
    var path = context.Request.Path.Value ?? "";
    var sessionUserId = auth.GetSessionUserId(context);
    if (sessionUserId is { } signedInUserId && (context.Request.Method is "POST" or "PUT" or "PATCH" or "DELETE"))
    {
        var isStandardUser = await state.ReadAsync(data => data.Users.Any(user => user.Id == signedInUserId && user.Role == "user"));
        var instanceSegment = path.StartsWith("/api/instances/", StringComparison.OrdinalIgnoreCase)
            ? path["/api/instances/".Length..].Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            : null;
        var isInstanceAction = Guid.TryParse(instanceSegment, out _);
        var isPersonalPreference = context.Request.Method == "PUT" && path.Equals("/api/settings/language", StringComparison.OrdinalIgnoreCase);
        var isLogout = context.Request.Method == "POST" && path.Equals("/api/auth/logout", StringComparison.OrdinalIgnoreCase);
        if (isStandardUser && !isInstanceAction && !isPersonalPreference && !isLogout)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Contas User têm acesso somente de leitura e podem iniciar, parar ou reiniciar servidores dos perfis associados." });
            return;
        }
    }
    if (path.StartsWith("/api/instances/", StringComparison.OrdinalIgnoreCase))
    {
        var segment = path["/api/instances/".Length..].Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (Guid.TryParse(segment, out var profileId) && sessionUserId is { } userId)
        {
            var access = await state.ReadAsync(data =>
            {
                var role = data.Users.FirstOrDefault(user => user.Id == userId)?.Role ?? "";
                var profile = data.Instances.FirstOrDefault(item => item.Id == profileId);
                if (profile is null) return (Role: role, Permission: (string?)null, Associated: false);
                var associated = profile.UserAccess.ContainsKey(userId) || profile.AllowedUserIds.Contains(userId);
                if (role == "admin") return (Role: role, Permission: (string?)"admin", Associated: true);
                if (profile.UserAccess.TryGetValue(userId, out var configuredPermission)) return (Role: role, Permission: (string?)configuredPermission, Associated: associated);
                return (Role: role, Permission: associated ? "edit" : null, Associated: associated);
            });
            var pathParts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var isReadRequest = context.Request.Method is "GET" or "HEAD" or "OPTIONS";
            var isServerOperation = context.Request.Method == "POST" && pathParts.Length == 4 &&
                (pathParts[3] is "start" or "stop" or "restart");
            var isForceStop = context.Request.Method == "POST" && pathParts.Length == 4 && pathParts[3] == "force-stop";
            var isConsoleCommand = context.Request.Method == "POST" && pathParts.Length == 4 && pathParts[3] == "commands";
            var isPlayerManagementOperation = context.Request.Method == "POST" && pathParts.Length == 5 &&
                pathParts[3] == "players" && (pathParts[4] is "admin" or "unadmin" or "ban" or "unban" or "allow" or "unallow" or "kick");
            var isModUpdateCheck = context.Request.Method == "POST" && pathParts.Length == 5 &&
                pathParts[3] == "mods" && pathParts[4] == "check-updates";
            var isGameUpdateOperation = context.Request.Method == "POST" && pathParts.Length == 5 &&
                pathParts[3] == "game" && (pathParts[4] is "check-updates" or "update" or "abort");
            var isAllowedOperation = access.Role == "user"
                ? access.Associated && access.Permission == "operate" &&
                    (isServerOperation || isForceStop || isConsoleCommand || isPlayerManagementOperation || isModUpdateCheck || isGameUpdateOperation)
                : access.Permission == "operate" && context.Request.Method == "POST" && pathParts.Length == 4 && (pathParts[3] is "start" or "restart");
            var canEditProfile = access.Role == "user" && access.Associated && access.Permission == "edit";
            if (access.Role == "user"
                    ? !access.Associated || (!isReadRequest && !isAllowedOperation && !canEditProfile)
                    : access.Permission is null || (!isReadRequest && access.Permission != "admin" && access.Permission != "edit" && !isAllowedOperation))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }
    }
    await next();
});

app.UseAntiforgery();

app.UseDefaultFiles();
app.UseStaticFiles();

var supervisor = app.Services.GetRequiredService<InstanceSupervisor>();
await supervisor.RecoverRunningProcessesAsync();
var firewall = app.Services.GetRequiredService<WindowsFirewallService>();
var gameAdapters = app.Services.GetRequiredService<GameAdapterRegistry>();
var steamCmd = app.Services.GetRequiredService<SteamCmdService>();
var providers = app.Services.GetRequiredService<ProviderCatalogService>();
var nexusDownloadAuthorizations = app.Services.GetRequiredService<NexusDownloadAuthorizationService>();
var providerCredentials = app.Services.GetRequiredService<ProviderCredentialVault>();
var modPackages = app.Services.GetRequiredService<ModPackageManager>();
var playerManagement = app.Services.GetRequiredService<PlayerManagementService>();
var hostMetrics = app.Services.GetRequiredService<HostMetricsService>();
var r2modmanProfiles = app.Services.GetRequiredService<R2ModmanProfileService>();
var githubReleases = app.Services.GetRequiredService<GitHubReleaseService>();

var elevationStartupId = Guid.NewGuid().ToString("N");
var appVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
app.MapGet("/api/health", () => Results.Ok(new { status = "ready", app = "Dedicated Server Studio", shortName = "DSS", version = appVersion, startupId = elevationStartupId }));

app.MapGet("/api/app/elevation/status", (HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    return Results.Ok(new { isAdministrator = firewall.IsRunningAsAdministrator, startupId = elevationStartupId });
});

async Task<IResult> ChangeElevationAsync(bool enabled, HttpContext context)
{
    if (!await RequireAdmin(context, auth, state)) return Results.Forbid();
    var projectRoot = app.Environment.ContentRootPath;
    var configPath = Path.Combine(projectRoot, "dss-config.json");
    if (firewall.IsRunningAsAdministrator == enabled)
    {
        try
        {
            // Persist the requested mode even when the current process already matches it.
            await SetRunAsAdministratorAsync(configPath, enabled);
            return Results.Ok(new { message = enabled ? "O painel já está em modo administrador." : "O painel já está no modo padrão do Windows." });
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Could not save the DSS elevation preference");
            return Results.BadRequest(new { error = "Não foi possível salvar a preferência de execução. " + ex.Message });
        }
    }
    if (!OperatingSystem.IsWindows()) return Results.BadRequest(new { error = "A elevação do DSS está disponível somente no Windows." });

    var scriptPath = Path.Combine(projectRoot, "run.ps1");
    if (!File.Exists(scriptPath)) return Results.Problem("O script run.ps1 não foi encontrado na raiz do DSS.", statusCode: 500);
    var previousConfig = File.Exists(configPath) ? await File.ReadAllTextAsync(configPath) : null;
    var preferenceSaved = false;
    string? startupSignalPath = null;
    try
    {
        await SetRunAsAdministratorAsync(configPath, enabled);
        preferenceSaved = true;
        var powerShellPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powerShellPath)) throw new FileNotFoundException("Windows PowerShell não foi encontrado.", powerShellPath);
        var quotedScriptPath = "\"" + scriptPath.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        var arguments = $"-NoLogo -NoProfile -ExecutionPolicy Bypass -File {quotedScriptPath} -WaitForExistingInstance -PauseOnFailure -ListenMode {listenMode} -PreviousProcessId {Environment.ProcessId}";
        if (enabled)
        {
            var startInfo = new ProcessStartInfo(powerShellPath, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = projectRoot,
                WindowStyle = ProcessWindowStyle.Normal
            };
            using var elevatedProcess = Process.Start(startInfo) ?? throw new InvalidOperationException("O Windows não iniciou o processo elevado.");
        }
        else
        {
            startupSignalPath = Path.Combine(Path.GetTempPath(), $"dss-standard-start-{Guid.NewGuid():N}.json");
            var quotedSignalPath = "\"" + startupSignalPath.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
            arguments += $" -RequireNonAdministrator -StartupSignalPath {quotedSignalPath}";
            StartUnelevatedProcess(powerShellPath, arguments, projectRoot);

            var signalDeadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(startupSignalPath) && DateTime.UtcNow < signalDeadline)
                await Task.Delay(100, context.RequestAborted);
            if (!File.Exists(startupSignalPath))
                throw new InvalidOperationException("O novo processo sem elevação não confirmou a inicialização. O painel atual continuará aberto.");

            using var startupSignal = JsonDocument.Parse(await File.ReadAllTextAsync(startupSignalPath, context.RequestAborted));
            var signal = startupSignal.RootElement;
            var ready = signal.TryGetProperty("ready", out var readyValue) && readyValue.ValueKind == JsonValueKind.True;
            var isAdministrator = signal.TryGetProperty("isAdministrator", out var administratorValue) && administratorValue.ValueKind == JsonValueKind.True;
            var signalMessage = signal.TryGetProperty("message", out var messageValue) ? messageValue.GetString() : null;
            if (!ready || isAdministrator)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(signalMessage)
                    ? "O novo processo não confirmou que será executado sem privilégios de administrador. O painel atual continuará aberto."
                    : signalMessage);
        }

        // Start the replacement as soon as the 202 response has reached the browser. A fixed
        // delay here made every elevation switch wait even when the handoff was already ready.
        context.Response.OnCompleted(() =>
        {
            app.Lifetime.StopApplication();
            return Task.CompletedTask;
        });
        return Results.Accepted("/api/health", new { message = enabled
            ? "O painel será reiniciado como administrador."
            : "O painel será reiniciado com as permissões padrão do Windows." });
    }
    catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
    {
        if (enabled || !preferenceSaved)
        {
            try { await RestoreDssConfigurationAsync(configPath, previousConfig); }
            catch (Exception restoreError) { app.Logger.LogError(restoreError, "Could not restore DSS configuration after UAC cancellation"); }
        }
        return Results.BadRequest(new { error = enabled
            ? "A solicitação do Windows foi cancelada. O painel continuará aberto no modo atual."
            : "O painel continua aberto. A preferência pelo modo padrão foi salva para a próxima inicialização." });
    }
    catch (Exception ex)
    {
        if (enabled || !preferenceSaved)
        {
            try { await RestoreDssConfigurationAsync(configPath, previousConfig); }
            catch (Exception restoreError) { app.Logger.LogError(restoreError, "Could not restore DSS configuration after elevation failure"); }
        }
        app.Logger.LogError(ex, "Could not restart DSS with the requested permission mode");
        return Results.BadRequest(new { error = !enabled && preferenceSaved
            ? "O painel continua aberto. A preferência pelo modo padrão foi salva para a próxima inicialização. " + ex.Message
            : "Não foi possível reiniciar o painel. " + ex.Message });
    }
    finally
    {
        if (startupSignalPath is not null)
        {
            try { if (File.Exists(startupSignalPath)) File.Delete(startupSignalPath); }
            catch (Exception cleanupError) { app.Logger.LogWarning(cleanupError, "Could not remove the DSS startup handoff signal"); }
        }
    }
}

app.MapPost("/api/app/elevation/enable", (Delegate)(Func<HttpContext, Task<IResult>>)(context => ChangeElevationAsync(true, context)));
app.MapPost("/api/app/elevation/disable", (Delegate)(Func<HttpContext, Task<IResult>>)(context => ChangeElevationAsync(false, context)));

app.MapGet("/api/app/update", async (HttpContext context, CancellationToken cancellationToken) =>
{
    if (!await RequireAdmin(context, auth, state)) return Results.Forbid();
    try
    {
        var release = await githubReleases.GetLatestReleaseAsync(cancellationToken);
        if (release is null)
            return Results.Ok(new { currentVersion = githubReleases.CurrentVersion, updateAvailable = false, latestVersion = (string?)null, releaseName = (string?)null, releasePageUrl = (string?)null, releaseNotes = (string?)null, publishedAt = (DateTimeOffset?)null, packageAvailable = false, packageName = (string?)null, packageSize = (long?)null });
        return Results.Ok(new
        {
            currentVersion = release.CurrentVersion,
            updateAvailable = release.UpdateAvailable,
            latestVersion = release.LatestVersion,
            releaseName = release.ReleaseName,
            releasePageUrl = release.ReleasePageUrl,
            releaseNotes = release.ReleaseNotes,
            publishedAt = release.PublishedAt,
            packageAvailable = release.PackageAvailable,
            packageName = release.PackageName,
            packageSize = release.PackageSize
        });
    }
    catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException)
    {
        app.Logger.LogWarning(ex, "Could not check GitHub for a DSS release");
        return Results.Json(new { error = "Não foi possível consultar as releases do DSS no GitHub." }, statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/app/update/install", async (HttpContext context, CancellationToken cancellationToken) =>
{
    if (!await RequireAdmin(context, auth, state)) return Results.Forbid();
    if (!OperatingSystem.IsWindows()) return Results.BadRequest(new { error = "A atualização automática está disponível somente no Windows." });

    var profiles = await state.ReadAsync(data => data.Instances.ToArray());
    var activeServerWithoutBridge = profiles
        .SelectMany(profile => profile.Servers.Count == 0 ? [profile] : profile.Servers.Select(profile.WithServer))
        .FirstOrDefault(server =>
        {
            var status = supervisor.GetStatus(server.Id);
            return status is "starting" or "stopping" || status == "running" && !supervisor.IsConsoleConnected(server.Id);
        });
    if (activeServerWithoutBridge is not null)
    {
        var serverStatus = supervisor.GetStatus(activeServerWithoutBridge.Id);
        var error = serverStatus is "starting" or "stopping"
            ? $"Aguarde o servidor '{activeServerWithoutBridge.ServerName}' terminar de iniciar ou parar antes de atualizar o painel."
            : $"O servidor '{activeServerWithoutBridge.ServerName}' não tem uma ponte de console persistente disponível. Reinicie-o uma vez nesta versão do DSS antes de atualizar o painel; depois, console e Gestão poderão se reconectar sem reiniciar o jogo.";
        return Results.Conflict(new { error });
    }
    if (profiles.Any(profile => steamCmd.GetOperation(profile.Id)?.Running == true))
        return Results.Conflict(new { error = "Aguarde a operação do SteamCMD terminar antes de atualizar o DSS." });

    string? archivePath = null;
    try
    {
        var release = await githubReleases.GetLatestReleaseAsync(cancellationToken);
        if (release is null || !release.UpdateAvailable)
            return Results.Conflict(new { error = "Não há uma versão mais recente do DSS para instalar." });
        if (!release.PackageAvailable)
            return Results.Conflict(new { error = "A release mais recente ainda não contém o ZIP de produção correspondente." });

        var projectRoot = Path.GetFullPath(app.Environment.ContentRootPath);
        var helperPath = Path.Combine(projectRoot, "apply-update.ps1");
        if (!File.Exists(helperPath))
            return Results.Problem("O atualizador desta instalação está ausente. Baixe e instale manualmente a versão mais recente.", statusCode: 500);

        archivePath = Path.Combine(Path.GetTempPath(), $"DedicatedServerStudio-{Guid.NewGuid():N}.zip");
        var progress = updateProgress.Begin(release.LatestVersion);
        var operationId = progress.OperationId;
        var progressPath = updateProgress.ProgressFilePath;
        var capturedArchivePath = archivePath;
        _ = Task.Run(async () =>
        {
            try
            {
                updateProgress.Report(operationId, "downloading", 5, "Baixando pacote", 0, release.PackageSize);
                long lastProgressTick = 0;
                var lastProgressPercent = -1;
                var sha256 = await githubReleases.DownloadPackageAsync(release, capturedArchivePath, CancellationToken.None,
                    (downloaded, total) =>
                    {
                        var percent = total is > 0 ? 5 + (int)Math.Min(40, downloaded * 40 / total.Value) : 12;
                        var now = Environment.TickCount64;
                        if (percent != lastProgressPercent || now - lastProgressTick >= 350 || downloaded == total)
                        {
                            updateProgress.Report(operationId, "downloading", percent, "Baixando pacote", downloaded, total);
                            lastProgressPercent = percent;
                            lastProgressTick = now;
                        }
                    });
                updateProgress.Report(operationId, "verifying", 46, "Pacote baixado e verificado", release.PackageSize, release.PackageSize);
                var currentLauncherProcessId = int.TryParse(Environment.GetEnvironmentVariable("DSS_LAUNCHER_PID"), out var launcherPid) && launcherPid > 0
                    ? launcherPid
                    : 0;
                var powerShellPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
                if (!File.Exists(powerShellPath)) throw new FileNotFoundException("Windows PowerShell não foi encontrado.", powerShellPath);
                var startInfo = new ProcessStartInfo
                {
                    FileName = powerShellPath,
                    WorkingDirectory = projectRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                foreach (var argument in new[]
                {
                    "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", helperPath,
                    "-ProjectRoot", projectRoot, "-ArchivePath", capturedArchivePath, "-ExpectedVersion", release.LatestVersion,
                    "-ExpectedSha256", sha256, "-CurrentProcessId", Environment.ProcessId.ToString(), "-Port", listenPort.ToString(),
                    "-CurrentLauncherProcessId", currentLauncherProcessId.ToString(),
                    "-ProgressPath", progressPath, "-OperationId", operationId.ToString("D")
                }) startInfo.ArgumentList.Add(argument);
                using var updater = Process.Start(startInfo) ?? throw new InvalidOperationException("Não foi possível iniciar o processo de atualização.");
                var preparationDeadline = DateTimeOffset.UtcNow.AddMinutes(15);
                while (DateTimeOffset.UtcNow < preparationDeadline)
                {
                    var currentProgress = updateProgress.Get(operationId);
                    if (currentProgress?.Phase == "waiting-for-shutdown")
                    {
                        app.Logger.LogInformation("DSS update {OperationId} is staged and validated; shutting down for installation.", operationId);
                        app.Lifetime.StopApplication();
                        return;
                    }
                    if (currentProgress?.Phase == "failed") return;
                    if (updater.HasExited)
                    {
                        var exitCode = updater.ExitCode;
                        var message = $"O atualizador encerrou antes de preparar a instalação (código {exitCode}). Consulte logs/dss-update-*.log.";
                        app.Logger.LogError("DSS update helper exited before shutdown with code {ExitCode} for operation {OperationId}.", exitCode, operationId);
                        updateProgress.Report(operationId, "failed", 100, "Falha ao preparar a atualização", error: message);
                        return;
                    }
                    await Task.Delay(250);
                }

                try { updater.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                var timeoutMessage = "A preparação da atualização excedeu 15 minutos. O painel permaneceu aberto e nenhum arquivo foi substituído.";
                app.Logger.LogError("DSS update preparation timed out for operation {OperationId}.", operationId);
                updateProgress.Report(operationId, "failed", 100, "Tempo esgotado ao preparar a atualização", error: timeoutMessage);
                try { if (File.Exists(capturedArchivePath)) File.Delete(capturedArchivePath); }
                catch (Exception cleanupError) { app.Logger.LogWarning(cleanupError, "Could not remove a timed-out DSS update download"); }
            }
            catch (Exception ex)
            {
                app.Logger.LogError(ex, "Could not prepare DSS update operation {OperationId}", operationId);
                updateProgress.Report(operationId, "failed", 100, "A atualização falhou", error: ex.Message);
                try { if (File.Exists(capturedArchivePath)) File.Delete(capturedArchivePath); }
                catch (Exception cleanupError) { app.Logger.LogWarning(cleanupError, "Could not remove a failed DSS update download"); }
            }
        });
        return Results.Accepted($"/api/app/update/progress?operationId={operationId:D}", progress);
    }
    catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or InvalidOperationException or Win32Exception)
    {
        if (archivePath is not null)
        {
            try { if (File.Exists(archivePath)) File.Delete(archivePath); }
            catch (Exception cleanupError) { app.Logger.LogWarning(cleanupError, "Could not remove a failed DSS update download"); }
        }
        app.Logger.LogError(ex, "Could not prepare DSS update");
        return Results.Problem("Não foi possível preparar a atualização do DSS. " + ex.Message, statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/api/app/update/progress", async (Guid? operationId, HttpContext context) =>
{
    if (!await RequireAdmin(context, auth, state)) return Results.Forbid();
    context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
    var progress = updateProgress.Get(operationId);
    return progress is null ? Results.NotFound() : Results.Ok(progress);
});

app.MapPut("/api/settings/access", async (ListenModeInput input, HttpContext context) =>
{
    if (!await RequireAdmin(context, auth, state)) return Results.Forbid();
    var requestedMode = input.ListenMode?.Trim().ToLowerInvariant();
    if (requestedMode is not ("local" or "lan" or "public"))
        return Results.BadRequest(new { error = "Selecione acesso local, pela rede local ou público." });
    if (requestedMode == "public" && !input.AcknowledgePublicRisk)
        return Results.BadRequest(new { error = "Confirme os riscos antes de habilitar o acesso público." });
    if (requestedMode != "local" && await state.ReadAsync(data => string.IsNullOrWhiteSpace(data.AdminPasswordHash)))
        return Results.Conflict(new { error = "Configure a senha de administrador antes de permitir acesso remoto." });

    var projectRoot = app.Environment.ContentRootPath;
    var configPath = Path.Combine(projectRoot, "dss-config.json");
    var previousConfig = File.Exists(configPath) ? await File.ReadAllTextAsync(configPath) : null;
    Process? restartedProcess = null;
    var restartHandoffReady = false;
    try
    {
        await SetDssConfigValueAsync(configPath, "listenMode", JsonValue.Create(requestedMode));
        if (requestedMode == listenMode)
            return Results.Ok(new { listenMode = requestedMode, message = "A configuração de acesso já está ativa." });
        if (!OperatingSystem.IsWindows())
            return Results.BadRequest(new { error = "A troca do modo de acesso pelo painel está disponível somente no Windows." });

        var scriptPath = Path.Combine(projectRoot, "run.ps1");
        if (!File.Exists(scriptPath)) throw new FileNotFoundException("O script run.ps1 não foi encontrado na raiz do programa.", scriptPath);
        var powerShellPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powerShellPath)) throw new FileNotFoundException("Windows PowerShell não foi encontrado.", powerShellPath);
        var startupSignalPath = Path.Combine(Path.GetTempPath(), $"dss-access-start-{Guid.NewGuid():N}.json");
        var startInfo = new ProcessStartInfo(powerShellPath)
        {
            UseShellExecute = true,
            WorkingDirectory = projectRoot,
            WindowStyle = ProcessWindowStyle.Normal
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-WaitForExistingInstance");
        startInfo.ArgumentList.Add("-PreviousProcessId");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        startInfo.ArgumentList.Add("-ListenMode");
        startInfo.ArgumentList.Add(requestedMode);
        startInfo.ArgumentList.Add("-StartupSignalPath");
        startInfo.ArgumentList.Add(startupSignalPath);
        startInfo.ArgumentList.Add("-PauseOnFailure");
        try
        {
            restartedProcess = Process.Start(startInfo) ?? throw new InvalidOperationException("O Windows não iniciou o processo de reinicialização.");
            var signalDeadline = DateTime.UtcNow.AddSeconds(12);
            while (!File.Exists(startupSignalPath) && DateTime.UtcNow < signalDeadline)
                await Task.Delay(100, context.RequestAborted);
            if (!File.Exists(startupSignalPath))
                throw new InvalidOperationException("O novo PowerShell não confirmou que está pronto para assumir o painel. A instância atual continuará ativa.");
            using var startupSignal = JsonDocument.Parse(await File.ReadAllTextAsync(startupSignalPath, context.RequestAborted));
            var signal = startupSignal.RootElement;
            var ready = signal.TryGetProperty("ready", out var readyValue) && readyValue.ValueKind == JsonValueKind.True;
            var message = signal.TryGetProperty("message", out var messageValue) ? messageValue.GetString() : null;
            if (!ready) throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
                ? "O novo processo não confirmou a reinicialização. A instância atual continuará ativa."
                : message);
            restartHandoffReady = true;
        }
        finally
        {
            if (!restartHandoffReady && restartedProcess is { HasExited: false })
            {
                try { restartedProcess.Kill(entireProcessTree: true); }
                catch (Exception cleanupError) { app.Logger.LogWarning(cleanupError, "Could not stop the failed access-mode restart process"); }
            }
            restartedProcess?.Dispose();
            try { if (File.Exists(startupSignalPath)) File.Delete(startupSignalPath); }
            catch (Exception cleanupError) { app.Logger.LogWarning(cleanupError, "Could not remove the access-mode startup signal"); }
        }
        _ = Task.Run(async () => { await Task.Delay(300); app.Lifetime.StopApplication(); });
        return Results.Accepted("/api/health", new { listenMode = requestedMode, message = "O painel será reiniciado para aplicar o novo modo de acesso." });
    }
    catch (Exception ex)
    {
        try { await RestoreDssConfigurationAsync(configPath, previousConfig); }
        catch (Exception restoreError) { app.Logger.LogError(restoreError, "Could not restore access configuration after restart failure"); }
        app.Logger.LogError(ex, "Could not restart DSS with the requested network access mode");
        return Results.BadRequest(new { error = "Não foi possível aplicar o modo de acesso. " + ex.Message });
    }
});

app.MapGet("/api/settings/firewall", async (HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var enabled = await firewall.IsPanelPortAuthorizedAsync(listenPort);
    return Results.Ok(new { enabled, isAdministrator = firewall.IsRunningAsAdministrator,
        supported = OperatingSystem.IsWindows(), port = listenPort, scope = "LocalSubnet" });
});

app.MapPut("/api/settings/firewall", async (PanelFirewallInput input, HttpContext context) =>
{
    if (!await RequireAdmin(context, auth, state)) return Results.Forbid();
    if (!OperatingSystem.IsWindows()) return Results.BadRequest(new { error = "A regra de Firewall do painel está disponível somente no Windows." });
    if (!firewall.IsRunningAsAdministrator)
        return Results.Conflict(new { error = "Reinicie o painel como administrador para alterar a regra de Firewall." });
    var result = await firewall.ConfigurePanelPortAsync(listenPort, input.Enabled);
    if (!result.Success) return Results.BadRequest(new { error = result.Message });
    return Results.Ok(new { enabled = input.Enabled, isAdministrator = true, supported = true, port = listenPort,
        scope = "LocalSubnet", message = result.Message });
});

app.MapGet("/api/games", (HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    return Results.Ok(gameAdapters.List());
});

app.MapGet("/api/games/{gameId}/server-path", (string gameId, string? installPath, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    return Results.Ok(steamCmd.CheckServerPath(gameId, installPath));
});

app.MapGet("/api/games/{gameId}/branches", async (string gameId, HttpContext context, CancellationToken cancellationToken) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    try { return Results.Ok(new { branches = await steamCmd.GetAvailableBranchesAsync(gameId, cancellationToken) }); }
    catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway); }
});

app.MapGet("/api/auth/status", async (HttpContext context) =>
{
    var setupComplete = await auth.IsSetupAsync();
    return Results.Json(new { setupRequired = !setupComplete, authenticated = auth.IsAuthenticated(context) });
});

app.MapGet("/api/security/antiforgery", (IAntiforgery antiforgery, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var tokens = antiforgery.GetAndStoreTokens(context);
    return Results.Ok(new { requestToken = tokens.RequestToken });
});

app.MapPost("/api/auth/setup", async (AuthRequest request, HttpContext context) =>
{
    if (request.Password.Length < 11)
        return Results.BadRequest(new { error = "Use uma senha de administrador com pelo menos 11 caracteres." });
    if (!await auth.SetupAsync(request.Password))
        return Results.Conflict(new { error = "A conta de administrador já foi configurada." });
    var admin = await state.ReadAsync(data => data.Users.First(user => user.Role == "admin"));
    auth.CreateSession(context, admin.Id);
    return Results.Ok(new { authenticated = true });
});

app.MapPost("/api/auth/login", async (AuthRequest request, HttpContext context) =>
{
    if (!await auth.IsSetupAsync()) return Results.Conflict(new { error = "Configure a conta de administrador primeiro." });
    var remoteAddress = context.Connection.RemoteIpAddress;
    var sourceIp = remoteAddress is null ? "unknown" : remoteAddress.IsIPv4MappedToIPv6 ? remoteAddress.MapToIPv4().ToString() : remoteAddress.ToString();
    var userId = await auth.AuthenticateAsync(request.Username, request.Password, sourceIp);
    if (userId is null)
        return Results.Json(new { error = "Usuário ou senha inválidos, ou acesso temporariamente bloqueado." }, statusCode: StatusCodes.Status401Unauthorized);
    auth.CreateSession(context, userId.Value);
    return Results.Ok(new { authenticated = true });
});

app.MapGet("/api/auth/me", async (HttpContext context) =>
{
    if (auth.GetSessionUserId(context) is not { } id) return Results.Unauthorized();
    var user = await state.ReadAsync(data => data.Users.FirstOrDefault(item => item.Id == id));
    if (user is null) return Results.Unauthorized();
    var imagePath = UserProfileImagePath(state.DataDirectory, id);
    var imageUrl = File.Exists(imagePath) ? $"/api/auth/me/image?v={File.GetLastWriteTimeUtc(imagePath).Ticks}" : null;
    var uiLanguage = string.IsNullOrWhiteSpace(user.UiLanguage) ? "en-US" : user.UiLanguage;
    return Results.Ok(new { id = user.Id, username = user.Username, role = user.Role, imageUrl, uiLanguage });
});

app.MapGet("/api/auth/me/image", (HttpContext context) =>
{
    if (auth.GetSessionUserId(context) is not { } id) return Results.Unauthorized();
    var imagePath = UserProfileImagePath(state.DataDirectory, id);
    context.Response.Headers.CacheControl = "private, no-store";
    return File.Exists(imagePath) ? Results.File(imagePath, "image/jpeg", enableRangeProcessing: true) : Results.NotFound();
});

app.MapPut("/api/auth/me/image", async (ProfileImageInput input, HttpContext context, CancellationToken cancellationToken) =>
{
    if (auth.GetSessionUserId(context) is not { } id) return Results.Unauthorized();
    const string jpegPrefix = "data:image/jpeg;base64,";
    if (string.IsNullOrWhiteSpace(input.ImageData) || input.ImageData.Length > 3_000_000 || !input.ImageData.StartsWith(jpegPrefix, StringComparison.Ordinal))
        return Results.BadRequest(new { error = "A imagem precisa ser um JPEG recortado e ter até 2 MB." });
    byte[] imageBytes;
    try { imageBytes = Convert.FromBase64String(input.ImageData[jpegPrefix.Length..]); }
    catch (FormatException) { return Results.BadRequest(new { error = "O conteúdo da imagem está inválido." }); }
    if (imageBytes.Length < 4 || imageBytes.Length > 2_000_000 || imageBytes[0] != 0xFF || imageBytes[1] != 0xD8 || imageBytes[2] != 0xFF)
        return Results.BadRequest(new { error = "O recorte deve ser um JPEG válido com até 2 MB." });

    var imagePath = UserProfileImagePath(state.DataDirectory, id);
    Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
    var temporaryPath = imagePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try
    {
        await File.WriteAllBytesAsync(temporaryPath, imageBytes, cancellationToken);
        File.Move(temporaryPath, imagePath, overwrite: true);
    }
    finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    return Results.Ok(new { message = "Foto do perfil atualizada.", imageUrl = $"/api/auth/me/image?v={File.GetLastWriteTimeUtc(imagePath).Ticks}" });
});

app.MapDelete("/api/auth/me/image", (HttpContext context) =>
{
    if (auth.GetSessionUserId(context) is not { } id) return Results.Unauthorized();
    var imagePath = UserProfileImagePath(state.DataDirectory, id);
    if (File.Exists(imagePath)) File.Delete(imagePath);
    return Results.NoContent();
});

app.MapPut("/api/auth/me/password", async (UserPasswordChangeInput input, HttpContext context) =>
{
    if (auth.GetSessionUserId(context) is not { } id) return Results.Unauthorized();
    if (string.IsNullOrEmpty(input.NewPassword) || input.NewPassword.Length < 11 || input.NewPassword.Length > 256)
        return Results.BadRequest(new { error = "A nova senha precisa ter entre 11 e 256 caracteres." });
    var result = await state.UpdateAsync(data =>
    {
        var user = data.Users.FirstOrDefault(item => item.Id == id);
        if (user is null) return 0;
        if (!AuthService.VerifyPassword(input.CurrentPassword, user.PasswordHash)) return -1;
        var hash = AuthService.HashPassword(input.NewPassword);
        user.PasswordHash = hash;
        user.FailedLoginCount = 0;
        user.FailedLoginWindowStartedAt = null;
        user.LoginLockedUntil = null;
        if (user.Role == "admin") data.AdminPasswordHash = hash;
        return 1;
    });
    return result switch
    {
        1 => Results.Ok(new { message = "Senha atualizada. Sua sessão continua ativa." }),
        0 => Results.Unauthorized(),
        _ => Results.BadRequest(new { error = "A senha atual não confere." })
    };
});

app.MapGet("/api/users", async (HttpContext context) =>
{
    if (!await RequireAdmin(context, auth, state)) return Results.Forbid();
    var now = DateTimeOffset.UtcNow;
    var users = await state.ReadAsync(data => data.Users.Select(user => new UserView(user.Id, user.Username, user.Role,
        user.CreatedAt, user.Role == "admin" ? data.Instances.Count : data.Instances.Count(profile => profile.AllowedUserIds.Contains(user.Id)),
        GetUserImageUrl(state.DataDirectory, user.Id), user.LoginLockedUntil is { } lockedUntil && lockedUntil > now ? lockedUntil : null,
        user.LoginLocked || user.LoginLockedUntil is { } activeLock && activeLock > now, user.FailedLoginCount)).OrderBy(item => item.Username).ToArray());
    return Results.Ok(new { users });
});

app.MapGet("/api/security/login-attempts", async (HttpContext context) =>
{
    if (!await RequireAdmin(context, auth, state)) return Results.Forbid();
    var attempts = await state.ReadAsync(data => data.FailedLoginAttempts
        .OrderByDescending(item => item.OccurredAt).Take(100)
        .Select(item =>
        {
            var matchedUser = data.Users.FirstOrDefault(user => string.Equals(user.Username, item.AttemptedUsername, StringComparison.OrdinalIgnoreCase));
            return new
            {
                item.AttemptedUsername,
                AttemptedUserId = matchedUser?.Id,
                AccountLocked = matchedUser is not null && (matchedUser.LoginLocked || matchedUser.LoginLockedUntil is { } until && until > DateTimeOffset.UtcNow),
                item.IpAddress,
                item.DeviceName,
                item.AttemptCount,
                item.FirstOccurredAt,
                LastAttemptAt = item.OccurredAt,
                item.DuringLockoutCount
            };
        }).ToArray());
    return Results.Ok(new { attempts });
});

app.MapGet("/api/users/{id:guid}/image", (Guid id, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var imagePath = UserProfileImagePath(state.DataDirectory, id);
    context.Response.Headers.CacheControl = "private, no-store";
    return File.Exists(imagePath) ? Results.File(imagePath, "image/jpeg", enableRangeProcessing: true) : Results.NotFound();
});

app.MapPost("/api/users", async (UserCreateInput input, HttpContext context) =>
{
    if (!await RequireAdmin(context, auth, state)) return Results.Forbid();
    var username = input.Username?.Trim() ?? "";
    if (username.Length is < 3 or > 32 || !username.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))
        return Results.BadRequest(new { error = "O usuário deve ter de 3 a 32 caracteres: letras, números, ponto, hífen ou sublinhado." });
    if (input.Password is null || input.Password.Length < 11 || input.Password.Length > 256)
        return Results.BadRequest(new { error = "A senha precisa ter entre 11 e 256 caracteres." });
    if (input.Role is not ("admin" or "user")) return Results.BadRequest(new { error = "Escolha a função administrador ou usuário." });
    if (string.Equals(username, "admin", StringComparison.OrdinalIgnoreCase) && input.Role != "admin")
        return Results.BadRequest(new { error = "A conta admin padrão deve permanecer como administrador." });
    var user = new DssUser { Username = username, PasswordHash = AuthService.HashPassword(input.Password), Role = input.Role };
    var added = await state.UpdateAsync(data =>
    {
        if (data.Users.Any(item => string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase))) return false;
        data.Users.Add(user);
        return true;
    });
    return added ? Results.Created($"/api/users/{user.Id}", new { id = user.Id, username = user.Username, role = user.Role })
        : Results.Conflict(new { error = "Esse nome de usuário já está em uso." });
});

app.MapPut("/api/users/{id:guid}", async (Guid id, UserUpdateInput input, HttpContext context) =>
{
    if (!await RequireAdmin(context, auth, state)) return Results.Forbid();
    if (input.Role is not ("admin" or "user")) return Results.BadRequest(new { error = "Escolha a função administrador ou usuário." });
    if (input.Password is { Length: > 0 } password && password.Length is < 11 or > 256)
        return Results.BadRequest(new { error = "A senha precisa ter entre 11 e 256 caracteres." });
    var isDefaultAdmin = await state.ReadAsync(data => data.Users.Any(user => user.Id == id && string.Equals(user.Username, "admin", StringComparison.OrdinalIgnoreCase)));
    if (isDefaultAdmin && input.Role != "admin")
        return Results.Conflict(new { error = "A conta admin padrão deve permanecer como administrador." });
    var updated = await state.UpdateAsync(data =>
    {
        var user = data.Users.FirstOrDefault(item => item.Id == id);
        if (user is null) return false;
        if (string.Equals(user.Username, "admin", StringComparison.OrdinalIgnoreCase) && input.Role != "admin") return false;
        if (user.Role == "admin" && input.Role != "admin" && data.Users.Count(item => item.Role == "admin") <= 1) return false;
        user.Role = input.Role;
        if (!string.IsNullOrEmpty(input.Password))
        {
            user.PasswordHash = AuthService.HashPassword(input.Password);
            if (user.Role == "admin") data.AdminPasswordHash = user.PasswordHash;
        }
        return true;
    });
    if (!updated) return Results.Conflict(new { error = "Usuário inexistente ou não é possível remover o último administrador." });
    return Results.Ok(new { message = "Usuário atualizado." });
});

app.MapPost("/api/users/{id:guid}/unlock", async (Guid id, HttpContext context) =>
{
    if (!await RequireAdmin(context, auth, state)) return Results.Forbid();
    var unlocked = await state.UpdateAsync(data =>
    {
        var user = data.Users.FirstOrDefault(item => item.Id == id);
        if (user is null) return false;
        user.LoginLocked = false;
        user.LoginLockedUntil = null;
        user.FailedLoginCount = 0;
        user.FailedLoginWindowStartedAt = null;
        data.FailedLoginAttempts.RemoveAll(item => string.Equals(item.AttemptedUsername, user.Username, StringComparison.OrdinalIgnoreCase));
        return true;
    });
    return unlocked ? Results.Ok(new { message = "Bloqueio de login removido." }) : Results.NotFound();
});

app.MapDelete("/api/users/{id:guid}", async (Guid id, HttpContext context) =>
{
    if (!await RequireAdmin(context, auth, state)) return Results.Forbid();
    if (auth.GetSessionUserId(context) == id) return Results.Conflict(new { error = "Entre com outro administrador antes de remover esta conta." });
    var removed = await state.UpdateAsync(data =>
    {
        var user = data.Users.FirstOrDefault(item => item.Id == id);
        if (user is null || (user.Role == "admin" && data.Users.Count(item => item.Role == "admin") <= 1)) return false;
        data.Users.Remove(user);
        foreach (var profile in data.Instances)
        {
            profile.AllowedUserIds.Remove(id);
            profile.UserAccess.Remove(id);
        }
        return true;
    });
    if (removed)
    {
        return Results.NoContent();
    }
    return Results.Conflict(new { error = "Usuário inexistente ou não é possível remover o último administrador." });
});

app.MapPost("/api/auth/logout", (HttpContext context) =>
{
    auth.SignOut(context);
    return Results.Ok(new { authenticated = false });
});

app.MapGet("/api/settings/steamcmd", async (HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    return Results.Ok(await steamCmd.GetSettingsAsync());
});

app.MapPut("/api/settings/steamcmd", async (SteamCmdDirectoryInput input, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    try
    {
        var previous = await steamCmd.GetSettingsAsync();
        var updated = await steamCmd.SetDirectoryAsync(input.Directory);
        if (!string.Equals(previous.Directory, updated.Directory, StringComparison.OrdinalIgnoreCase))
            await state.UpdateAsync(data => { data.Revisions.Add(new ConfigRevision { Kind = "dss-settings", Target = "SteamCMD", Summary = "Pasta do SteamCMD alterada", CanRollback = false, IsGlobal = true }); return true; });
        return Results.Ok(updated);
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPut("/api/settings/language", async (UiLanguageInput input, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    if (auth.GetSessionUserId(context) is not { } userId) return Results.Unauthorized();
    var language = input.Language?.Trim();
    if (language is not ("pt-BR" or "en-US" or "es-ES" or "fr-FR" or "de-DE" or "ru-RU" or "zh-CN"))
        return Results.BadRequest(new { error = "Idioma não suportado." });
    var savedLanguage = await state.UpdateAsync(data =>
    {
        var user = data.Users.FirstOrDefault(item => item.Id == userId);
        if (user is null) return null;
        user.UiLanguage = language;
        return user.UiLanguage;
    });
    if (savedLanguage is null) return Results.Unauthorized();
    return Results.Ok(new { language = savedLanguage });
});

app.MapGet("/api/dashboard", async (HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var userId = auth.GetSessionUserId(context);
    var isAdmin = await state.ReadAsync(data => data.Users.Any(user => user.Id == userId && user.Role == "admin"));
    var uiLanguage = await state.ReadAsync(data =>
    {
        var preference = data.Users.FirstOrDefault(user => user.Id == userId)?.UiLanguage;
        return string.IsNullOrWhiteSpace(preference) ? "en-US" : preference;
    });
    var profiles = await state.ReadAsync(data => data.Instances.Where(profile => isAdmin || profile.AllowedUserIds.Contains(userId ?? Guid.Empty) || profile.UserAccess.ContainsKey(userId ?? Guid.Empty)).Select(profile => ToView(profile, supervisor.GetStatus(profile.Id), gameAdapters, state.DataDirectory, steamCmd, supervisor)).ToArray());
    var visibleProfileIds = profiles.Select(profile => profile.Id).ToHashSet();
    var revisions = await state.ReadAsync(data => data.Revisions
        .Where(item => IsGlobalRevision(item) && (item.InstanceId == Guid.Empty || visibleProfileIds.Contains(item.InstanceId)))
        .OrderByDescending(item => item.CreatedAt).Take(8).Select(ToRevisionView).ToArray());
    var clientAccess = GetClientAccessInfo(context);
    return Results.Ok(new
    {
        instances = profiles,
        totalInstances = profiles.Length,
        runningInstances = profiles.Sum(item => item.Servers?.Count(server => server.RuntimeStatus == "running") ?? 0),
        stoppedInstances = profiles.Sum(item => item.Servers?.Count(server => server.RuntimeStatus != "running") ?? 0),
        totalServers = profiles.Sum(item => item.Servers?.Count ?? 0),
        recentChanges = revisions,
        dataDirectory = state.DataDirectory,
        logsDirectory = state.LogsDirectory,
        uiLanguage,
        listenMode,
        panelAccessUrls = GetPanelAccessUrls(listenMode, listenPort),
        clientAccess
    });
});

app.MapGet("/api/host/metrics", (HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    return Results.Ok(hostMetrics.Snapshot);
});

app.MapGet("/api/network/local-addresses", (HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    return Results.Ok(new { addresses = GetLocalIpv4Addresses() });
});

app.MapGet("/api/network/public-address", async (HttpContext context, IHttpClientFactory clients, CancellationToken cancellationToken) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    return Results.Ok(new { address = await GetPublicIpv4Async(clients, cancellationToken) });
});

app.MapGet("/api/processes/metrics", async (HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var userId = auth.GetSessionUserId(context);
    var isAdmin = await state.ReadAsync(data => data.Users.Any(user => user.Id == userId && user.Role == "admin"));
    var profiles = await state.ReadAsync(data => data.Instances
        .Where(profile => isAdmin || profile.AllowedUserIds.Contains(userId ?? Guid.Empty))
        .SelectMany(root => root.Servers.Select(server => new { Root = root, Server = server }))
        .Where(item => supervisor.GetStatus(item.Server.Id) == "running")
        .Select(item => new { Id = item.Server.Id, ProfileId = item.Root.Id, Name = item.Server.Name, item.Root.GameId, item.Server.WorldName, item.Server.ServerName, item.Server.Port })
        .ToArray());
    var processes = profiles.Select(profile => new
        {
            profile.Id, profile.Name, profile.GameId, profile.WorldName, profile.ServerName, profile.Port,
            Metrics = supervisor.GetProcessMetrics(profile.Id)
        })
        .Where(profile => profile.Metrics is not null)
        .ToArray();
    return Results.Ok(new { processes, sampledAt = DateTimeOffset.UtcNow });
});

app.MapGet("/api/instances/{id:guid}/performance", async (Guid id, Guid? serverId, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var profile = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == id));
    if (profile is null) return Results.NotFound();
    var endpoint = profile.Servers.FirstOrDefault(item => item.Id == serverId) ?? profile.Servers.FirstOrDefault(item => item.Id == id) ?? profile.Servers.FirstOrDefault();
    if (endpoint is null) return Results.NotFound();
    var activeProfile = profile.WithServer(endpoint);
    gameAdapters.TryGet(activeProfile.GameId, out var adapter);
    return Results.Ok(new
    {
        running = supervisor.GetStatus(endpoint.Id) == "running",
        process = supervisor.GetProcessMetrics(endpoint.Id),
        savesBytes = supervisor.GetSaveDirectoryBytes(activeProfile),
        networkProtocol = adapter?.FirewallProtocol ?? "—",
        networkPortStart = activeProfile.Port,
        networkPortEnd = activeProfile.Port + Math.Max(0, (adapter?.PortSpan ?? 1) - 1)
    });
});

app.MapGet("/api/instances/{id:guid}/network/connectivity", async (Guid id, Guid? serverId, HttpContext context,
    IHttpClientFactory clients, CancellationToken cancellationToken) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var root = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == id));
    var endpoint = root?.Servers.FirstOrDefault(server => server.Id == serverId) ??
        root?.Servers.FirstOrDefault(server => server.Id == id) ?? root?.Servers.FirstOrDefault();
    if (root is null || endpoint is null) return Results.NotFound();
    var profile = root.WithServer(endpoint);
    var localAddresses = GetLocalIpv4Addresses();
    var publicAddress = await GetPublicIpv4Async(clients, cancellationToken);

    var running = supervisor.GetStatus(endpoint.Id) == "running";
    var queryPort = profile.Port + 1;
    if (!running)
    {
        return Results.Ok(new
        {
            running,
            crossplay = profile.Crossplay,
            port = profile.Port,
            queryPort,
            localAddresses = localAddresses.Select(address => new { address, status = "offline", message = "Servidor parado." }).ToArray(),
            publicAddress = publicAddress is null ? null : new { address = publicAddress, status = "offline", message = "Servidor parado." }
        });
    }

    if (profile.Crossplay || !profile.GameId.Equals("valheim", StringComparison.OrdinalIgnoreCase) || queryPort is < 1 or > 65535)
    {
        var message = profile.Crossplay
            ? "Crossplay usa um relay; conexões não são verificadas por consulta UDP direta."
            : "O servidor está ativo, mas o teste UDP de consulta não está disponível para este jogo.";
        var status = profile.Crossplay ? "relay" : "unsupported";
        return Results.Ok(new
        {
            running,
            crossplay = profile.Crossplay,
            port = profile.Port,
            queryPort,
            localAddresses = localAddresses.Select(address => new { address, status, message }).ToArray(),
            publicAddress = publicAddress is null ? null : new { address = publicAddress, status, message }
        });
    }

    async Task<(bool QueryPort, bool GamePort)> ProbeAddressAsync(IPAddress address)
    {
        var queryTask = playerManagement.CanQueryServerAsync(address, queryPort, cancellationToken);
        var gameTask = playerManagement.CanQueryServerAsync(address, profile.Port, cancellationToken);
        await Task.WhenAll(queryTask, gameTask);
        return (await queryTask, await gameTask);
    }

    var loopbackTask = ProbeAddressAsync(IPAddress.Loopback);
    var localChecksTask = Task.WhenAll(localAddresses.Select(address => ProbeAddressAsync(IPAddress.Parse(address))));
    var publicCheckTask = publicAddress is null
        ? Task.FromResult((QueryPort: false, GamePort: false))
        : ProbeAddressAsync(IPAddress.Parse(publicAddress));
    var logPath = supervisor.GetConsoleLogPath(endpoint.Id) ?? state.GetServerLogPath(endpoint.Id);
    var observationTask = playerManagement.GetConnectionObservationAsync(logPath, cancellationToken);
    await Task.WhenAll(loopbackTask, localChecksTask, publicCheckTask, observationTask);
    var loopbackResponded = await loopbackTask;
    var localChecks = await localChecksTask;
    var publicResponded = await publicCheckTask;
    var observation = await observationTask;
    var hasRecentPlayerConnection = observation.ActiveConnections > 0 || observation.LastSeenAt is { } lastSeen &&
        DateTimeOffset.UtcNow - lastSeen <= TimeSpan.FromMinutes(5);

    object BuildStatus(string address, (bool QueryPort, bool GamePort) probe, bool isPublic)
    {
        if (probe.QueryPort)
            return new { address, status = "online", message = "A consulta Steam respondeu neste endereço pela porta UDP de consulta." };
        if (probe.GamePort)
            return new { address, status = "online", message = "O servidor respondeu à consulta UDP na porta principal do jogo." };
        if (hasRecentPlayerConnection)
            return new { address, status = "observed", message = "Os logs confirmam que jogadores se conectaram recentemente; a consulta Steam não confirmou este endereço diretamente." };
        var message = !profile.IsPublic
            ? "A visibilidade pública está desativada; a consulta Steam pode não responder mesmo que conexões diretas estejam funcionando."
            : isPublic
                ? "A consulta UDP não respondeu deste computador. NAT loopback pode impedir este teste; isso não confirma que conexões externas estejam bloqueadas."
                : loopbackResponded.QueryPort || loopbackResponded.GamePort
                    ? "A consulta respondeu no próprio DSS, mas não por este endereço. Isso não prova que a porta de jogo esteja bloqueada; confirme por um cliente nessa rede."
                    : "A consulta Steam UDP não respondeu. Isso não prova que a porta de jogo esteja bloqueada; confirme usando um cliente Valheim nessa rede.";
        return new { address, status = "unverified", message };
    }

    return Results.Ok(new
    {
        running,
        crossplay = false,
        port = profile.Port,
        queryPort,
        localAddresses = localAddresses.Select((address, index) => BuildStatus(address, localChecks[index], false)).ToArray(),
        publicAddress = publicAddress is null ? null : BuildStatus(publicAddress, publicResponded, true)
    });
});

app.MapGet("/api/instances", async (HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var userId = auth.GetSessionUserId(context);
    var isAdmin = await state.ReadAsync(data => data.Users.Any(user => user.Id == userId && user.Role == "admin"));
    return Results.Ok(await state.ReadAsync(data => data.Instances.Where(profile => isAdmin || profile.AllowedUserIds.Contains(userId ?? Guid.Empty) || profile.UserAccess.ContainsKey(userId ?? Guid.Empty)).Select(profile => ToView(profile, supervisor.GetStatus(profile.Id), gameAdapters, state.DataDirectory, steamCmd, supervisor)).ToArray()));
});

app.MapGet("/api/instances/{id:guid}/servers", async (Guid id, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var profile = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == id));
    if (profile is null) return Results.NotFound();
    return Results.Ok(new { profileId = profile.Id, servers = profile.Servers.OrderBy(server => server.CreatedAt).Select(server => ToServerView(server, supervisor.GetStatus(server.Id))).ToArray() });
});

app.MapPost("/api/instances/{id:guid}/servers", async (Guid id, Guid? sourceServerId, MultiServerInput input, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var parent = await state.ReadAsync(data => data.Instances.FirstOrDefault(profile => profile.Id == id));
    if (parent is null) return Results.NotFound();
    var templateServer = parent.Servers.FirstOrDefault(server => server.Id == (sourceServerId ?? input.SourceServerId)) ?? parent.Servers.FirstOrDefault();
    if (templateServer is null) return Results.Conflict(new { error = "Este perfil ainda não tem servidor para usar como base." });
    if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 60 || string.IsNullOrWhiteSpace(input.ServerName) || input.ServerName.Trim().Length > 60 || string.IsNullOrWhiteSpace(input.WorldName) || input.WorldName.Trim().Length > 40)
        return Results.BadRequest(new { error = "Informe nomes válidos para a instância, o servidor e o mundo." });
    var password = string.IsNullOrWhiteSpace(input.GamePassword) ? SafeUnprotect(templateServer.ProtectedPassword) : input.GamePassword.Trim();
    if (password.Length < 5 || password.Length > 100) return Results.BadRequest(new { error = "A senha de jogo precisa ter entre 5 e 100 caracteres." });
    if (input.Port is < 1024 or > 65534 || input.Port + gameAdapters.PortSpan(parent.GameId) - 1 > 65535 ||
        await state.ReadAsync(data => data.Instances.SelectMany(item => item.Servers.Select(item.WithServer)).Any(server =>
            PortRangesOverlap(server.Port, gameAdapters.PortSpan(server.GameId), input.Port, gameAdapters.PortSpan(parent.GameId)))))
        return Results.Conflict(new { error = "A porta escolhida ou sua faixa reservada já está em uso." });
    var child = new ServerEndpoint
    {
        Id = Guid.NewGuid(), Name = input.Name.Trim(), ServerName = input.ServerName.Trim(), WorldName = input.WorldName.Trim(),
        Port = input.Port, IsPublic = input.IsPublic ?? templateServer.IsPublic, Crossplay = input.Crossplay ?? templateServer.Crossplay,
        SaveDirectory = (input.SaveDirectory ?? templateServer.SaveDirectory).Trim(),
        UseInstallPathSaves = input.UseInstallPathSaves ?? templateServer.UseInstallPathSaves,
        ExtraArguments = templateServer.ExtraArguments.ToList(),
        GameSettings = templateServer.GameSettings.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase),
        ProtectedPassword = SecretProtection.Protect(password), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
    };
    var childProfile = parent.WithServer(child);
    var childSavePath = NormalizeSavePath(SavePathResolver.Resolve(childProfile, state.DataDirectory));
    if (parent.Servers.Any(server => server.WorldName.Equals(child.WorldName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(NormalizeSavePath(SavePathResolver.Resolve(parent.WithServer(server), state.DataDirectory)), childSavePath,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
        return Results.Conflict(new { error = "Este mundo já existe na pasta de saves compartilhada. Escolha outro nome de mundo." });
    await state.UpdateAsync(data =>
    {
        var profile = data.Instances.First(item => item.Id == id);
        profile.Servers.Add(child);
        data.Revisions.Add(NewProfileRevision(profile.Id, "Servidor adicionado ao perfil", "", JsonSerializer.Serialize(child), isGlobal: true));
        return true;
    });
    return Results.Created($"/api/instances/{id}/servers/{child.Id}", ToServerView(child, "stopped"));
});

app.MapDelete("/api/instances/{id:guid}/servers/{serverId:guid}", async (Guid id, Guid serverId, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var removed = await state.UpdateAsync(data =>
    {
        var profile = data.Instances.FirstOrDefault(item => item.Id == id);
        if (profile is null || profile.Servers.Count <= 1) return false;
        var server = profile.Servers.FirstOrDefault(item => item.Id == serverId);
        if (server is null || supervisor.GetStatus(server.Id) == "running") return false;
        profile.Servers.Remove(server);
        data.Revisions.Add(new ConfigRevision { InstanceId = id, Kind = "profile-action", Target = "servidor", Summary = $"Servidor {server.Name} removido do perfil", IsGlobal = true, CanRollback = false });
        return true;
    });
    return removed ? Results.NoContent() : Results.Conflict(new { error = "Mantenha ao menos um servidor no perfil e pare o servidor antes de removê-lo." });
});

app.MapGet("/api/instances/{id:guid}/access", async (Guid id, HttpContext context, bool includeAvailable = false) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var userId = auth.GetSessionUserId(context);
    var viewer = await state.ReadAsync(data =>
    {
        var user = data.Users.FirstOrDefault(item => item.Id == userId);
        var profile = data.Instances.FirstOrDefault(item => item.Id == id);
        var isAdmin = user?.Role == "admin";
        var associated = user is not null && profile is not null &&
            (profile.UserAccess.ContainsKey(user.Id) || profile.AllowedUserIds.Contains(user.Id));
        return (ProfileExists: profile is not null, CanView: isAdmin || associated);
    });
    if (!viewer.ProfileExists) return Results.NotFound();
    if (!viewer.CanView || includeAvailable && !(await RequireProfileEditor(id, context, auth, state))) return Results.Forbid();
    var access = await state.ReadAsync(data =>
    {
        var profile = data.Instances.FirstOrDefault(item => item.Id == id);
        if (profile is null) return (Found: false, UserIds: Array.Empty<Guid>(), UserAccess: new Dictionary<Guid, string>(), Users: Array.Empty<UserView>());
        var userAccess = profile.UserAccess.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var legacyUserId in profile.AllowedUserIds)
            userAccess.TryAdd(legacyUserId, "edit");
        var selected = userAccess.Keys.ToHashSet();
        var users = data.Users.Where(user => user.Role != "admin" && (includeAvailable || selected.Contains(user.Id)))
            .OrderBy(user => user.Username).Select(user => new UserView(user.Id, user.Username, user.Role, user.CreatedAt, 0,
                GetUserImageUrl(state.DataDirectory, user.Id))).ToArray();
        return (Found: true, UserIds: profile.AllowedUserIds.ToArray(), UserAccess: userAccess, Users: users);
    });
    return !access.Found ? Results.NotFound() : Results.Ok(new { userIds = access.UserIds, userAccess = access.UserAccess, users = access.Users });
});

app.MapPut("/api/instances/{id:guid}/access", async (Guid id, ProfileUsersInput input, HttpContext context) =>
{
    if (!await RequireProfileEditor(id, context, auth, state)) return Results.Forbid();
    var updated = await state.UpdateAsync(data =>
    {
        var profile = data.Instances.FirstOrDefault(item => item.Id == id);
        if (profile is null) return false;
        var validIds = data.Users.Where(user => user.Role != "admin").Select(user => user.Id).ToHashSet();
        if (input.UserAccess is null || input.UserAccess.Any(grant => !validIds.Contains(grant.Key) || grant.Value is not ("edit" or "operate"))) return false;
        profile.UserAccess = input.UserAccess.ToDictionary(grant => grant.Key, grant => grant.Value);
        profile.AllowedUserIds = profile.UserAccess.Keys.ToList();
        return true;
    });
    return updated ? Results.Ok(new { message = "Acesso ao perfil atualizado." }) : Results.BadRequest(new { error = "Selecione usuários existentes e defina a permissão de edição ou operação." });
});

app.MapGet("/api/instances/{id:guid}/my-access", async (Guid id, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var userId = auth.GetSessionUserId(context);
    var access = await state.ReadAsync(data =>
    {
        var user = data.Users.FirstOrDefault(item => item.Id == userId);
        var profile = data.Instances.FirstOrDefault(item => item.Id == id);
        if (user is null || profile is null) return (Found: false, Permission: (string?)null);
        if (user.Role == "admin") return (Found: true, Permission: (string?)"admin");
        if (profile.UserAccess.TryGetValue(user.Id, out var permission)) return (Found: true, Permission: (string?)permission);
        return profile.AllowedUserIds.Contains(user.Id)
            ? (Found: true, Permission: (string?)"edit")
            : (Found: false, Permission: (string?)null);
    });
    return access.Found ? Results.Ok(new { permission = access.Permission }) : Results.Forbid();
});

app.MapGet("/api/instances/{id:guid}/image", async (Guid id, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    if (!await state.ReadAsync(data => data.Instances.Any(profile => profile.Id == id))) return Results.NotFound();
    var imagePath = ProfileImagePath(state.DataDirectory, id);
    context.Response.Headers.CacheControl = "no-store";
    return File.Exists(imagePath) ? Results.File(imagePath, "image/jpeg", enableRangeProcessing: true) : Results.NotFound();
});

app.MapPut("/api/instances/{id:guid}/image", async (Guid id, ProfileImageInput input, HttpContext context, CancellationToken cancellationToken) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    if (!await state.ReadAsync(data => data.Instances.Any(profile => profile.Id == id))) return Results.NotFound();
    const string jpegPrefix = "data:image/jpeg;base64,";
    if (string.IsNullOrWhiteSpace(input.ImageData) || input.ImageData.Length > 3_000_000 || !input.ImageData.StartsWith(jpegPrefix, StringComparison.Ordinal))
        return Results.BadRequest(new { error = "A imagem precisa ser um JPEG recortado e ter até 2 MB." });
    byte[] imageBytes;
    try { imageBytes = Convert.FromBase64String(input.ImageData[jpegPrefix.Length..]); }
    catch (FormatException) { return Results.BadRequest(new { error = "O conteúdo da imagem está inválido." }); }
    if (imageBytes.Length < 4 || imageBytes.Length > 2_000_000 || imageBytes[0] != 0xFF || imageBytes[1] != 0xD8 || imageBytes[2] != 0xFF)
        return Results.BadRequest(new { error = "O recorte deve ser um JPEG válido com até 2 MB." });

    var imagePath = ProfileImagePath(state.DataDirectory, id);
    Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
    var temporaryPath = imagePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try
    {
        await File.WriteAllBytesAsync(temporaryPath, imageBytes, cancellationToken);
        File.Move(temporaryPath, imagePath, overwrite: true);
    }
    finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }

    await state.UpdateAsync(data =>
    {
        if (!data.Instances.Any(profile => profile.Id == id)) return false;
        data.Revisions.Add(new ConfigRevision { InstanceId = id, Kind = "profile-image", Target = "profile image", Summary = "Imagem do perfil atualizada", CanRollback = false });
        return true;
    });
    return Results.Ok(new { message = "Imagem do perfil atualizada.", imageUrl = $"/api/instances/{id}/image?v={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}" });
});

app.MapDelete("/api/instances/{id:guid}/image", async (Guid id, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    if (!await state.ReadAsync(data => data.Instances.Any(profile => profile.Id == id))) return Results.NotFound();
    var imagePath = ProfileImagePath(state.DataDirectory, id);
    if (File.Exists(imagePath)) File.Delete(imagePath);
    await state.UpdateAsync(data =>
    {
        data.Revisions.Add(new ConfigRevision { InstanceId = id, Kind = "profile-image", Target = "profile image", Summary = "Imagem do perfil removida", CanRollback = false });
        return true;
    });
    return Results.NoContent();
});

app.MapPost("/api/instances", async (ProfileInput input, HttpContext context) =>
{
    if (!await RequireAdmin(context, auth, state)) return Results.Forbid();
    var validation = ValidateProfile(input);
    if (validation is not null) return Results.BadRequest(new { error = validation });
    if (!gameAdapters.TryGet(input.GameId, out _))
        return Results.BadRequest(new { error = $"O jogo '{input.GameId}' ainda não tem um adaptador de servidor." });
    if (input.Port + gameAdapters.PortSpan(input.GameId) - 1 > 65535)
        return Results.BadRequest(new { error = "A faixa de portas deste perfil ultrapassa o limite UDP." });
    var conflict = await state.ReadAsync(data => PortConflict(data.Instances, input.Port, input.GameId, null, gameAdapters));
    if (conflict) return Results.Conflict(new { error = "As portas necessárias para este servidor já estão reservadas por outro perfil." });
    var profile = FromInput(input);
    var saveConflict = await state.ReadAsync(data => SavePathConflict(data.Instances, profile, state.DataDirectory, null));
    if (saveConflict) return Results.Conflict(new { error = "Outro perfil já usa esta pasta de saves; escolha uma pasta exclusiva para proteger os mundos." });
    await state.UpdateAsync(data =>
    {
        data.Instances.Add(profile);
        data.Revisions.Add(NewProfileRevision(profile.Id, "Perfil criado", "", SerializeProfile(profile), isGlobal: true));
        return true;
    });
    steamCmd.CheckProfileInBackground(profile.Id);
    return Results.Created($"/api/instances/{profile.Id}", ToView(profile, "stopped", gameAdapters, state.DataDirectory, steamCmd, supervisor));
});

app.MapPut("/api/instances/{id:guid}", async (Guid id, ProfileInput input, Guid? serverId, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var validation = ValidateProfile(input, isUpdate: true);
    if (validation is not null) return Results.BadRequest(new { error = validation });
    if (!gameAdapters.TryGet(input.GameId, out _))
        return Results.BadRequest(new { error = $"O jogo '{input.GameId}' ainda não tem um adaptador de servidor." });
    if (input.Port + gameAdapters.PortSpan(input.GameId) - 1 > 65535)
        return Results.BadRequest(new { error = "A faixa de portas deste perfil ultrapassa o limite UDP." });
    var oldProfile = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == id));
    if (oldProfile is null) return Results.NotFound();
    var selectedServer = oldProfile.Servers.FirstOrDefault(server => server.Id == serverId) ?? oldProfile.Servers.FirstOrDefault(server => server.Id == id) ?? oldProfile.Servers.FirstOrDefault();
    if (selectedServer is null) return Results.Conflict(new { error = "Este perfil ainda não tem um servidor configurado." });
    if (supervisor.GetStatus(selectedServer.Id) == "running") return Results.Conflict(new { error = "Pare este servidor antes de alterar as configurações de inicialização." });
    var installPathChanged = !string.Equals(oldProfile.InstallPath, input.InstallPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    if (installPathChanged && await IsGroupRunningAsync(id, state, supervisor)) return Results.Conflict(new { error = "Pare todos os servidores deste perfil antes de alterar a instalação compartilhada." });
    if (await IsGroupSteamCmdRunningAsync(id, state, steamCmd)) return Results.Conflict(new { error = "Aguarde a instalação ou atualização do jogo terminar antes de editar este perfil." });
    var conflict = await state.ReadAsync(data => data.Instances.SelectMany(profile => profile.Servers.Select(profile.WithServer))
        .Any(server => server.Id != selectedServer.Id && PortRangesOverlap(server.Port, gameAdapters.PortSpan(server.GameId), input.Port, gameAdapters.PortSpan(input.GameId!))));
    if (conflict) return Results.Conflict(new { error = "As portas necessárias para este servidor já estão reservadas por outro servidor." });
    var candidate = FromInput(input, oldProfile);
    var nextEndpoint = new ServerEndpoint
    {
        Id = selectedServer.Id, Name = selectedServer.Name, ServerName = input.ServerName.Trim(), WorldName = input.WorldName.Trim(),
        Port = input.Port, SaveDirectory = (input.SaveDirectory ?? "").Trim(), UseInstallPathSaves = input.UseInstallPathSaves ?? string.IsNullOrWhiteSpace(input.SaveDirectory),
        IsPublic = input.IsPublic, Crossplay = input.Crossplay,
        ExtraArguments = candidate.ExtraArguments.ToList(),
        GameSettings = (input.GameSettings ?? []).Where(pair => !pair.Key.Equals("serverBranch", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase),
        ProtectedPassword = string.IsNullOrWhiteSpace(input.GamePassword) ? selectedServer.ProtectedPassword : SecretProtection.Protect(input.GamePassword.Trim()),
        CreatedAt = selectedServer.CreatedAt, UpdatedAt = DateTimeOffset.UtcNow
    };
    candidate.Servers = oldProfile.Servers.Select(server => server.Id == selectedServer.Id ? nextEndpoint : server).ToList();
    var selectedBranch = input.GameSettings?.Where(pair => pair.Key.Equals("serverBranch", StringComparison.OrdinalIgnoreCase)).Select(pair => (KeyValuePair<string, JsonElement>?)pair).FirstOrDefault();
    candidate.GameSettings = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
    if (selectedBranch is { } branchValue) candidate.GameSettings[branchValue.Key] = branchValue.Value.Clone();
    var firstEndpoint = candidate.Servers.First();
    ApplyEndpointFields(candidate, firstEndpoint);
    var saveConflict = await state.ReadAsync(data => data.Instances
        .SelectMany(profile => profile.Servers.Where(server => server.Id != selectedServer.Id).Select(profile.WithServer))
        .Any(server => server.WorldName.Equals(nextEndpoint.WorldName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(NormalizeSavePath(SavePathResolver.Resolve(server, state.DataDirectory)),
                NormalizeSavePath(SavePathResolver.Resolve(candidate.WithServer(nextEndpoint), state.DataDirectory)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)));
    if (saveConflict) return Results.Conflict(new { error = "Outro servidor deste perfil já usa este mundo na mesma pasta de saves." });
    var oldBranch = oldProfile.GameSettings.TryGetValue("serverBranch", out var oldBranchValue) ? oldBranchValue : (JsonElement?)null;
    var newBranch = candidate.GameSettings.TryGetValue("serverBranch", out var newBranchValue) ? newBranchValue : (JsonElement?)null;
    var branchChanged = !JsonElementEquals(oldBranch, newBranch);
    if (branchChanged && await IsGroupRunningAsync(id, state, supervisor)) return Results.Conflict(new { error = "Pare todos os servidores deste perfil antes de trocar a branch da instalação compartilhada." });

    var updated = await state.UpdateAsync(data =>
    {
        var profile = data.Instances.FirstOrDefault(item => item.Id == id);
        if (profile is null) return null;
        var before = SerializeProfile(profile);
        var next = candidate;
        var after = SerializeProfile(next);
        data.Instances[data.Instances.IndexOf(profile)] = next;
        if (!string.Equals(before, after, StringComparison.Ordinal))
            data.Revisions.Add(NewProfileRevision(id, "Parâmetros do servidor atualizados", before, after));
        return next;
    });
    if (updated is null) return Results.NotFound();
    if (branchChanged) steamCmd.CheckProfileInBackground(updated.Id);
    return Results.Ok(ToView(updated, "stopped", gameAdapters, state.DataDirectory, steamCmd, supervisor));
});

app.MapDelete("/api/instances/{id:guid}", async (Guid id, bool? deleteServerFiles, bool? deleteSaves, HttpContext context) =>
{
    if (!await RequireProfileEditor(id, context, auth, state)) return Results.Forbid();
    if (await IsGroupSteamCmdRunningAsync(id, state, steamCmd)) return Results.Conflict(new { error = "Aguarde a instalação ou atualização do jogo terminar antes de remover o perfil." });
    if (await IsGroupRunningAsync(id, state, supervisor)) return Results.Conflict(new { error = "Pare todas as instâncias do perfil antes de removê-lo." });
    var removal = await state.ReadAsync(data => new
    {
        Profile = data.Instances.FirstOrDefault(item => item.Id == id),
        Profiles = data.Instances.ToArray()
    });
    if (removal.Profile is null) return Results.NotFound();
    if (deleteServerFiles == true && gameAdapters.TryGet(removal.Profile.GameId, out var removalAdapter) &&
        IsExecutableRunningFromDirectory(removal.Profile.InstallPath, removalAdapter.ExecutableName))
        return Results.Conflict(new { error = "Os arquivos não podem ser excluídos enquanto o servidor estiver aberto. Feche o processo do jogo e tente novamente." });
    try { DeleteProfileFiles(removal.Profile, removal.Profiles, state.DataDirectory, app.Environment.ContentRootPath, deleteServerFiles == true, deleteSaves == true); }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
    {
        return Results.Conflict(new { error = error.Message });
    }
    var removed = await state.UpdateAsync(data =>
    {
        var profile = data.Instances.FirstOrDefault(item => item.Id == id);
        if (profile is null) return false;
        var groupId = profile.GroupId == Guid.Empty ? profile.Id : profile.GroupId;
        data.Instances.Remove(profile);
        var siblings = data.Instances.Where(item => (item.GroupId == Guid.Empty ? item.Id : item.GroupId) == groupId)
            .OrderBy(item => item.CreatedAt).ThenBy(item => item.Id).ToArray();
        var profileMods = data.InstalledMods.Where(item => item.InstanceId == id).ToArray();
        data.InstalledMods.RemoveAll(item => item.InstanceId == id);
        if (siblings.Length > 0)
        {
            var newOwnerId = siblings[0].Id;
            foreach (var mod in profileMods)
            {
                data.InstalledMods.RemoveAll(item => item.InstanceId == newOwnerId &&
                    item.Provider.Equals(mod.Provider, StringComparison.OrdinalIgnoreCase) &&
                    item.PackageId.Equals(mod.PackageId, StringComparison.OrdinalIgnoreCase));
                mod.InstanceId = newOwnerId;
                data.InstalledMods.Add(mod);
            }
        }
        return true;
    });
    if (removed)
    {
        await state.UpdateAsync(data => { data.Revisions.Add(new ConfigRevision { InstanceId = id, Kind = "dss-settings", Target = "perfil", Summary = "Perfil removido", CanRollback = false, IsGlobal = true }); return true; });
        var imagePath = ProfileImagePath(state.DataDirectory, id);
        if (File.Exists(imagePath)) File.Delete(imagePath);
    }
    return removed ? Results.NoContent() : Results.NotFound();
});

app.MapPost("/api/instances/{id:guid}/start", async (Guid id, Guid? serverId, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var root = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == id));
    if (root is null) return Results.NotFound();
    var endpoint = root.Servers.FirstOrDefault(item => item.Id == serverId) ?? root.Servers.FirstOrDefault(item => item.Id == id) ?? root.Servers.FirstOrDefault();
    if (endpoint is null) return Results.NotFound();
    var profile = root.WithServer(endpoint);
    if (steamCmd.GetOperation(id)?.Running == true) return Results.Conflict(new { error = "Aguarde o SteamCMD concluir antes de iniciar um servidor deste perfil." });
    if (!gameAdapters.TryGet(profile.GameId, out var adapter)) return Results.BadRequest(new { error = "O adaptador deste jogo não está disponível." });
    if (profile.GameId.Equals("valheim", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(profile.ProtectedPassword))
        return Results.BadRequest(new { error = "O perfil pode ser criado sem senha. Para instalar e iniciar Valheim, defina uma senha com pelo menos 5 caracteres em Configurações." });
    if (string.IsNullOrWhiteSpace(profile.InstallPath) || !Path.IsPathFullyQualified(profile.InstallPath))
        return Results.BadRequest(new { error = "Defina a pasta do servidor dedicado antes de iniciar este perfil." });
    var executablePath = Path.Combine(profile.InstallPath, adapter.ExecutableName);
    if (!File.Exists(executablePath))
    {
        var isStandardUser = await state.ReadAsync(data => data.Users.Any(user => user.Id == auth.GetSessionUserId(context) && user.Role == "user"));
        if (isStandardUser) return Results.Forbid();
        var installation = await steamCmd.BeginInstallAsync(id, startAfterInstall: true, endpoint.Id);
        if (installation.Started) await state.UpdateAsync(data => { data.Revisions.Add(new ConfigRevision { InstanceId = id, Kind = "profile-action", Target = "jogo", Summary = "Instalação do servidor solicitada ao iniciar", CanRollback = false }); return true; });
        return installation.Started
            ? Results.Accepted($"/api/instances/{id}/game/update-status", new { message = installation.Message, installing = true })
            : Results.Conflict(new { error = installation.Message });
    }
    try { profile.GamePassword = SecretProtection.Unprotect(profile.ProtectedPassword); }
    catch (Exception) { return Results.Problem("Não foi possível descriptografar a senha salva desta instância.", statusCode: 500); }
    var result = await supervisor.StartAsync(profile);
    if (result.Success) await state.UpdateAsync(data => { data.Revisions.Add(new ConfigRevision { InstanceId = id, Kind = "profile-action", Target = "servidor", Summary = "Servidor iniciado", CanRollback = false }); return true; });
    return result.Success ? Results.Ok(new { message = result.Message, status = "running" }) : Results.BadRequest(new { error = result.Message });
});

app.MapPost("/api/instances/{id:guid}/firewall/authorize", async (Guid id, Guid? serverId, HttpContext context) =>
{
    if (!await RequireProfileEditor(id, context, auth, state)) return Results.Forbid();
    var root = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == id));
    var endpoint = root?.Servers.FirstOrDefault(server => server.Id == serverId) ?? root?.Servers.FirstOrDefault(server => server.Id == id) ?? root?.Servers.FirstOrDefault();
    if (root is null || endpoint is null) return Results.NotFound();
    var profile = root.WithServer(endpoint);
    if (!gameAdapters.TryGet(profile.GameId, out var adapter)) return Results.BadRequest(new { error = "O adaptador deste jogo não está disponível." });
    if (string.IsNullOrWhiteSpace(profile.InstallPath) || !Path.IsPathFullyQualified(profile.InstallPath))
        return Results.BadRequest(new { error = "Defina a pasta do servidor dedicado antes de autorizar o acesso de rede." });

    var executablePath = Path.Combine(profile.InstallPath, adapter.ExecutableName);
    var result = await firewall.EnsurePrivateNetworkRuleAsync(profile, adapter, executablePath);
    if (result.Success) await state.UpdateAsync(data => { data.Revisions.Add(new ConfigRevision { InstanceId = id, Kind = "profile-action", Target = "rede", Summary = "Autorização de rede configurada", CanRollback = false }); return true; });
    return result.Success ? Results.Ok(new { message = "Regra permanente registrada. Inícios remotos deste perfil não pedirão autorização de administrador." })
        : Results.BadRequest(new { error = result.Message });
});

app.MapGet("/api/instances/{id:guid}/firewall/status", async (Guid id, Guid? serverId, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var root = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == id));
    var endpoint = root?.Servers.FirstOrDefault(server => server.Id == serverId) ?? root?.Servers.FirstOrDefault(server => server.Id == id) ?? root?.Servers.FirstOrDefault();
    if (root is null || endpoint is null) return Results.NotFound();
    var profile = root.WithServer(endpoint);
    if (!gameAdapters.TryGet(profile.GameId, out var adapter)) return Results.BadRequest(new { error = "O adaptador deste jogo não está disponível." });
    if (string.IsNullOrWhiteSpace(profile.InstallPath) || !Path.IsPathFullyQualified(profile.InstallPath))
        return Results.Ok(new { authorized = false, verified = true });
    var executablePath = Path.Combine(profile.InstallPath, adapter.ExecutableName);
    var authorized = await firewall.IsAuthorizedAsync(profile, adapter, executablePath);
    return Results.Ok(new { authorized, verified = true });
});

app.MapPost("/api/instances/{id:guid}/game/check-updates", async (Guid id, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    if (!await state.ReadAsync(data => data.Instances.Any(item => item.Id == id))) return Results.NotFound();
    await steamCmd.CheckProfileNowAsync(id);
    var profile = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == id));
    if (profile is null) return Results.NotFound();
    return Results.Ok(ToView(profile, supervisor.GetStatus(id), gameAdapters, state.DataDirectory, steamCmd, supervisor));
});

app.MapPost("/api/instances/{id:guid}/game/update", async (Guid id, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var canInstallServer = await RequireProfileEditor(id, context, auth, state);
    if (!canInstallServer)
    {
        var hasInstalledServer = await state.ReadAsync(data =>
        {
            var profile = data.Instances.FirstOrDefault(item => item.Id == id);
            return profile is not null && gameAdapters.TryGet(profile.GameId, out var adapter) &&
                !string.IsNullOrWhiteSpace(profile.InstallPath) &&
                File.Exists(Path.Combine(profile.InstallPath, adapter.ExecutableName));
        });
        if (!hasInstalledServer)
            return Results.Conflict(new { error = "Este acesso pode atualizar um servidor já instalado, mas não instalar um servidor ausente." });
    }
    if (await IsGroupRunningAsync(id, state, supervisor)) return Results.Conflict(new { error = "Pare todos os servidores deste perfil antes de atualizar os arquivos compartilhados do jogo." });
    if (await IsGroupSteamCmdRunningAsync(id, state, steamCmd)) return Results.Conflict(new { error = "Já existe uma operação do SteamCMD para este perfil." });
    var installation = await steamCmd.BeginInstallAsync(id, startAfterInstall: false);
    if (installation.Started) await state.UpdateAsync(data => { data.Revisions.Add(new ConfigRevision { InstanceId = id, Kind = "profile-action", Target = "jogo", Summary = "Atualização do servidor solicitada", CanRollback = false }); return true; });
    return installation.Started
        ? Results.Accepted($"/api/instances/{id}/game/update-status", new { message = installation.Message, operation = steamCmd.GetOperation(id) })
        : Results.Conflict(new { error = installation.Message });
});

app.MapGet("/api/instances/{id:guid}/game/update-status", (Guid id, HttpContext context, long after = 0) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
    context.Response.Headers.Pragma = "no-cache";
    return Results.Ok(new { operation = steamCmd.GetOperation(id, after) });
});

app.MapGet("/api/instances/{id:guid}/game/update-log", (Guid id, Guid? operationId, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var path = steamCmd.GetOperationLogPath(id, operationId);
    return path is null ? Results.NotFound(new { error = "Ainda não há um log completo do SteamCMD para esta operação." })
        : Results.File(path, "text/plain; charset=utf-8", $"steamcmd-{id:N}.log", enableRangeProcessing: true);
});

app.MapGet("/api/instances/{id:guid}/game/update-stream", async (Guid id, Guid? operationId, long after, HttpContext context, CancellationToken cancellationToken) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    context.Response.ContentType = "application/x-ndjson; charset=utf-8";
    context.Response.Headers.CacheControl = "no-cache, no-transform";
    context.Response.Headers["X-Accel-Buffering"] = "no";
    try
    {
        await foreach (var snapshot in steamCmd.StreamOperationAsync(id, operationId, after, cancellationToken))
        {
            var type = snapshot.Running ? "progress" : "completed";
            // WriteAsJsonAsync updates Content-Type for each call. Once the first
            // progress record starts the NDJSON response, those headers are read-only.
            // Serialize directly to the body so subsequent SteamCMD updates keep streaming.
            var record = JsonSerializer.SerializeToUtf8Bytes(new { type, operation = snapshot }, JsonSerializerOptions.Web);
            await context.Response.Body.WriteAsync(record, cancellationToken);
            await context.Response.WriteAsync("\n", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    return Results.Empty;
});

app.MapPost("/api/instances/{id:guid}/game/abort", async (Guid id, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var aborted = steamCmd.AbortInstall(id);
    if (aborted) await state.UpdateAsync(data => { data.Revisions.Add(new ConfigRevision { InstanceId = id, Kind = "profile-action", Target = "jogo", Summary = "Cancelamento da instalação solicitado", CanRollback = false }); return true; });
    return aborted
        ? Results.Accepted($"/api/instances/{id}/game/update-status", new { message = "Cancelamento solicitado. O SteamCMD está sendo encerrado." })
        : Results.Conflict(new { error = "Não há uma instalação ou atualização em andamento para este perfil." });
});

app.MapPost("/api/instances/{id:guid}/stop", async (Guid id, Guid? serverId, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var root = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == id));
    var endpoint = root?.Servers.FirstOrDefault(item => item.Id == serverId) ?? root?.Servers.FirstOrDefault(item => item.Id == id) ?? root?.Servers.FirstOrDefault();
    if (endpoint is null) return Results.NotFound();
    if (supervisor.GetStatus(endpoint.Id) == "stopped") return Results.Ok(new { status = "stopped", message = "A instância já está parada." });
    var result = await supervisor.StopAsync(endpoint.Id);
    if (result.Success) await state.UpdateAsync(data => { data.Revisions.Add(new ConfigRevision { InstanceId = id, Kind = "profile-action", Target = "servidor", Summary = "Servidor parado", CanRollback = false }); return true; });
    return result.Success ? Results.Ok(new { message = result.Message, status = "stopped" }) : Results.Conflict(new { error = result.Message });
});

app.MapPost("/api/instances/{id:guid}/force-stop", async (Guid id, Guid? serverId, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var root = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == id));
    var endpoint = root?.Servers.FirstOrDefault(item => item.Id == serverId) ?? root?.Servers.FirstOrDefault(item => item.Id == id) ?? root?.Servers.FirstOrDefault();
    if (endpoint is null) return Results.NotFound();
    var result = await supervisor.ForceStopAsync(endpoint.Id);
    if (result.Success) await state.UpdateAsync(data => { data.Revisions.Add(new ConfigRevision { InstanceId = id, Kind = "profile-action", Target = "servidor", Summary = "Servidor encerrado à força", CanRollback = false }); return true; });
    return result.Success ? Results.Ok(new { message = result.Message, status = "stopped" }) : Results.Conflict(new { error = result.Message });
});

app.MapPost("/api/instances/{id:guid}/restart", async (Guid id, Guid? serverId, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    if (steamCmd.GetOperation(id)?.Running == true) return Results.Conflict(new { error = "Aguarde o SteamCMD concluir antes de reiniciar o servidor." });
    var root = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == id));
    var endpoint = root?.Servers.FirstOrDefault(item => item.Id == serverId) ?? root?.Servers.FirstOrDefault(item => item.Id == id) ?? root?.Servers.FirstOrDefault();
    if (root is null || endpoint is null) return Results.NotFound();
    var profile = root.WithServer(endpoint);
    if (!gameAdapters.TryGet(profile.GameId, out var adapter)) return Results.BadRequest(new { error = "O adaptador deste jogo não está disponível." });
    if (profile.GameId.Equals("valheim", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(profile.ProtectedPassword))
        return Results.BadRequest(new { error = "Defina uma senha com pelo menos 5 caracteres em Configurações antes de reiniciar Valheim." });
    if (string.IsNullOrWhiteSpace(profile.InstallPath) || !Path.IsPathFullyQualified(profile.InstallPath))
        return Results.BadRequest(new { error = "Defina a pasta do servidor dedicado antes de reiniciar este perfil." });
    if (supervisor.GetStatus(endpoint.Id) == "running")
    {
        var stopped = await supervisor.StopAsync(endpoint.Id);
        if (!stopped.Success) return Results.Conflict(new { error = stopped.Message });
    }
    try { profile.GamePassword = SecretProtection.Unprotect(profile.ProtectedPassword); }
    catch (Exception) { return Results.Problem("Não foi possível descriptografar a senha salva desta instância.", statusCode: 500); }
    var result = await supervisor.StartAsync(profile);
    if (result.Success) await state.UpdateAsync(data => { data.Revisions.Add(new ConfigRevision { InstanceId = id, Kind = "profile-action", Target = "servidor", Summary = "Servidor reiniciado", CanRollback = false }); return true; });
    return result.Success ? Results.Ok(new { message = result.Message, status = "running" }) : Results.BadRequest(new { error = result.Message });
});

app.MapGet("/api/instances/{id:guid}/logs", (Guid id, Guid? serverId, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    return Results.Ok(new { lines = supervisor.GetLogLines(serverId ?? id) });
});

app.MapGet("/api/instances/{id:guid}/console", (Guid id, Guid? serverId, long? after, int? limit, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var processId = serverId ?? id;
    var result = supervisor.GetConsoleEntries(processId, Math.Max(0, after ?? 0), limit ?? 1000);
    var running = supervisor.GetStatus(processId) == "running";
    return Results.Ok(new { entries = result.Entries, sequence = result.Sequence, running, consoleConnected = running && supervisor.IsConsoleConnected(processId) });
});

app.MapGet("/api/instances/{id:guid}/console/log-file", (Guid id, Guid? serverId, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var processId = serverId ?? id;
    var path = supervisor.GetConsoleLogPath(processId);
    return path is null ? Results.NotFound(new { error = "Ainda não há um log de console para este servidor." })
        : Results.File(path, "text/plain; charset=utf-8", $"console-{processId:N}.log", enableRangeProcessing: true);
});

app.MapPost("/api/instances/{id:guid}/commands", async (Guid id, Guid? serverId, ConsoleCommandInput input, HttpContext context) =>
{
    if (!await RequireProfileEditor(id, context, auth, state) && !await RequireProfileOperator(id, context, auth, state)) return Results.Forbid();
    var modOwnerId = await ResolveModOwnerIdAsync(id, state);
    if (modOwnerId is null) return Results.NotFound();
    var consoleModReady = await state.ReadAsync(data => data.InstalledMods.Any(mod => mod.InstanceId == modOwnerId &&
        mod.PackageId.Equals("DreamWraith-ConsoleStdinHeeler", StringComparison.OrdinalIgnoreCase) && mod.Enabled));
    if (!consoleModReady)
        return Results.Conflict(new { error = "Para enviar comandos ao servidor Valheim, instale e ative DreamWraith-ConsoleStdinHeeler em Buscar mods e reinicie o servidor." });
    var result = await supervisor.SendCommandAsync(serverId ?? id, input.Command);
    if (result.Success) await state.UpdateAsync(data => { data.Revisions.Add(new ConfigRevision { InstanceId = id, Kind = "profile-action", Target = "console", Summary = "Comando enviado ao console", CanRollback = false }); return true; });
    return result.Success ? Results.Ok(new { message = result.Message }) : Results.Conflict(new { error = result.Message });
});

app.MapGet("/api/instances/{id:guid}/players", async (Guid id, Guid? serverId, HttpContext context, CancellationToken cancellationToken) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var root = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == id));
    var endpoint = root?.Servers.FirstOrDefault(item => item.Id == serverId) ?? root?.Servers.FirstOrDefault(item => item.Id == id) ?? root?.Servers.FirstOrDefault();
    if (root is null || endpoint is null) return Results.NotFound();
    var profile = root.WithServer(endpoint);
    if (!profile.GameId.Equals("valheim", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "A gestão de jogadores está disponível para perfis Valheim." });
    try
    {
        var saveDirectory = SavePathResolver.Resolve(profile, state.DataDirectory);
        var logPath = supervisor.GetConsoleLogPath(endpoint.Id) ?? state.GetServerLogPath(endpoint.Id);
        var historyPath = state.GetPlayerHistoryPath(root.Id, endpoint.Id);
        var snapshot = await playerManagement.GetAsync(profile, saveDirectory, logPath, historyPath,
            supervisor.GetStatus(endpoint.Id) == "running", cancellationToken);
        return Results.Ok(snapshot);
    }
    catch (UnauthorizedAccessException) { return Results.Problem("O Windows negou acesso às listas de jogadores do servidor.", statusCode: 403); }
    catch (IOException) { return Results.Problem("Não foi possível ler os dados de jogadores do servidor.", statusCode: 500); }
});

app.MapPost("/api/instances/{id:guid}/players/{action}", async (Guid id, string action, Guid? serverId, PlayerAccessInput input,
    HttpContext context, CancellationToken cancellationToken) =>
{
    if (!await RequireProfileEditor(id, context, auth, state) && !await RequireProfileOperator(id, context, auth, state)) return Results.Forbid();
    var root = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == id));
    var endpoint = root?.Servers.FirstOrDefault(item => item.Id == serverId) ?? root?.Servers.FirstOrDefault(item => item.Id == id) ?? root?.Servers.FirstOrDefault();
    if (root is null || endpoint is null) return Results.NotFound();
    var profile = root.WithServer(endpoint);
    if (!profile.GameId.Equals("valheim", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "A gestão de jogadores está disponível para perfis Valheim." });
    try
    {
        var playerId = input.UserId?.Trim() ?? "";
        var playerName = input.PlayerName?.Trim() ?? "";
        var saveDirectory = SavePathResolver.Resolve(profile, state.DataDirectory);
        var isRunning = supervisor.GetStatus(endpoint.Id) == "running";
        if (action == "kick" && !isRunning)
            return Results.Conflict(new { error = "Inicie o servidor para kickar um jogador conectado." });
        if (action == "kick")
        {
            if (playerId.Length is 0 or > 160 || playerId.Any(character =>
                    !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or ':' or '-')))
                return Results.BadRequest(new { error = "Informe um ID de plataforma válido." });
            var consoleModReady = await state.ReadAsync(data => data.InstalledMods.Any(mod => mod.InstanceId == id &&
                mod.PackageId.Equals("DreamWraith-ConsoleStdinHeeler", StringComparison.OrdinalIgnoreCase) && mod.Enabled));
            if (!consoleModReady)
                return Results.Conflict(new { error = "Para aplicar esta ação durante a execução, instale e ative DreamWraith-ConsoleStdinHeeler e reinicie o servidor." });

            var logPath = supervisor.GetConsoleLogPath(endpoint.Id) ?? state.GetServerLogPath(endpoint.Id);
            var historyPath = state.GetPlayerHistoryPath(root.Id, endpoint.Id);
            var snapshot = await playerManagement.GetAsync(profile, saveDirectory, logPath, historyPath, true, cancellationToken);
            var matchingPlayers = snapshot.Players.Where(player =>
                string.Equals(player.UserId, playerId, StringComparison.Ordinal)).ToArray();
            var target = playerName.Length > 0
                ? matchingPlayers.FirstOrDefault(player => string.Equals(player.Name, playerName, StringComparison.OrdinalIgnoreCase))
                : matchingPlayers.OrderByDescending(player => player.IsActive).FirstOrDefault();
            if (action == "kick" && (target is null || !target.IsActive))
                return Results.Conflict(new { error = "O jogador não está mais conectado; atualize a lista antes de tentar kická-lo." });

            // Kicks are session actions, so address the exact live character by name.
            var command = $"kick {QuoteConsoleArgument(target!.Name)}";
            var sent = await supervisor.SendCommandAsync(endpoint.Id, command);
            if (!sent.Success) return Results.Conflict(new { error = sent.Message });

            var disconnected = false;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                var afterKick = await playerManagement.GetAsync(profile, saveDirectory, logPath, historyPath, true, cancellationToken);
                if (afterKick.LivePlayerNames is { } liveNames)
                {
                    if (!liveNames.Contains(target!.Name.Trim(), StringComparer.OrdinalIgnoreCase))
                    {
                        disconnected = true;
                        break;
                    }
                }
                else if (!afterKick.Players.Any(player => player.IsActive &&
                             string.Equals(player.Name.Trim(), target!.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    disconnected = true;
                    break;
                }
            }
            if (!disconnected)
                return Results.Conflict(new { error = $"O comando de kick foi enviado, mas o DSS não confirmou a saída de {target!.Name}. Verifique a saída do ConsoleStdinHeeler e tente novamente." });

            await state.UpdateAsync(data =>
            {
                data.Revisions.Add(new ConfigRevision { InstanceId = id, Kind = "profile-action", Target = "jogadores", Summary = "Jogador removido do servidor", CanRollback = false });
                return true;
            });
            return Results.Ok(new PlayerListMutation(true, $"{target!.Name} foi desconectado do servidor.", false));
        }

        string? banCommandNotice = null;
        var banCommandSent = false;
        if (action == "ban" && isRunning)
        {
            if (playerId.Length is 0 or > 160 || playerId.Any(character =>
                    !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or ':' or '-')))
                return Results.BadRequest(new { error = "Informe um ID de plataforma válido." });

            var logPath = supervisor.GetConsoleLogPath(endpoint.Id) ?? state.GetServerLogPath(endpoint.Id);
            var historyPath = state.GetPlayerHistoryPath(root.Id, endpoint.Id);
            var snapshot = await playerManagement.GetAsync(profile, saveDirectory, logPath, historyPath, true, cancellationToken);
            var matchingPlayers = snapshot.Players.Where(player =>
                string.Equals(player.UserId, playerId, StringComparison.Ordinal)).ToArray();
            var target = playerName.Length > 0
                ? matchingPlayers.FirstOrDefault(player => string.Equals(player.Name, playerName, StringComparison.OrdinalIgnoreCase))
                : matchingPlayers.OrderByDescending(player => player.IsActive).FirstOrDefault();

            // A persistent Valheim ban is stored by platform ID. If that character
            // is online, also send the game's name-based command when available;
            // the saved ban still works when the player is offline or the console mod
            // is not installed.
            if (target?.IsActive == true)
            {
                var consoleModReady = await state.ReadAsync(data => data.InstalledMods.Any(mod => mod.InstanceId == id &&
                    mod.PackageId.Equals("DreamWraith-ConsoleStdinHeeler", StringComparison.OrdinalIgnoreCase) && mod.Enabled));
                if (!consoleModReady)
                    banCommandNotice = "O ID ficará banido nos próximos acessos; instale o ConsoleStdinHeeler para aplicar o banimento imediatamente ao jogador conectado.";
                else
                {
                    var sent = await supervisor.SendCommandAsync(endpoint.Id, $"ban {QuoteConsoleArgument(target.Name)}");
                    if (sent.Success) banCommandSent = true;
                    else banCommandNotice = "O ID ficará banido nos próximos acessos, mas o DSS não conseguiu enviar o banimento imediato.";
                }
            }
        }

        var mutation = await playerManagement.UpdateAccessAsync(saveDirectory, action, playerId,
            isRunning, cancellationToken);
        if (action == "ban" && banCommandNotice is not null)
            mutation = mutation with { Message = banCommandNotice };
        else if (action == "ban" && banCommandSent)
            mutation = mutation with { Changed = true, Message = "O comando de banimento imediato também foi enviado ao servidor." };
        if (mutation.Changed)
            await state.UpdateAsync(data =>
            {
                data.Revisions.Add(new ConfigRevision { InstanceId = id, Kind = "profile-action", Target = "jogadores",
                    Summary = action switch
                    {
                        "admin" => "Admin promovido",
                        "unadmin" => "Admin removido",
                        "ban" => "Jogador banido",
                        "unban" => "Banimento removido",
                        "allow" => "Jogador permitido no servidor",
                        _ => "Permissão de entrada removida"
                    }, CanRollback = false });
                return true;
            });
        return Results.Ok(mutation);
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (UnauthorizedAccessException) { return Results.Problem("O Windows negou acesso às listas de jogadores do servidor.", statusCode: 403); }
    catch (IOException) { return Results.Problem("Não foi possível salvar a lista de jogadores do servidor.", statusCode: 500); }
});

app.MapGet("/api/instances/{id:guid}/export", async (Guid id, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var profile = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == id));
    if (profile is null) return Results.NotFound();
    var modOwnerId = await ResolveModOwnerIdAsync(id, state);
    var managedMods = modOwnerId is null
        ? Array.Empty<InstalledMod>()
        : await state.ReadAsync(data => data.InstalledMods.Where(item => item.InstanceId == modOwnerId.Value).ToArray());
    var mods = managedMods
        .Select(mod =>
        {
            var provider = mod.IsFramework || mod.Provider.Equals("bepinex", StringComparison.OrdinalIgnoreCase)
                ? "bepinex"
                : string.IsNullOrWhiteSpace(mod.SourceProvider) ? mod.Provider : mod.SourceProvider;
            if (provider.Equals("hex", StringComparison.OrdinalIgnoreCase)) provider = "hexium";
            return new ProfileExportMod(provider, mod.PackageId, mod.Version, mod.Enabled);
        })
        .Where(mod => (mod.Provider.Equals("thunderstore", StringComparison.OrdinalIgnoreCase) ||
                       mod.Provider.Equals("hexium", StringComparison.OrdinalIgnoreCase) ||
                       mod.Provider.Equals("bepinex", StringComparison.OrdinalIgnoreCase)) &&
                      !string.IsNullOrWhiteSpace(mod.PackageId) && !string.IsNullOrWhiteSpace(mod.Version))
        .OrderBy(mod => mod.PackageId, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    return Results.Ok(new ProfileExportDocument(1, profile.GameId, profile.Name, profile.InstallPath, profile.SaveDirectory,
        profile.ServerName, profile.WorldName, profile.Port, profile.IsPublic, profile.Crossplay,
        profile.ExtraArguments, profile.GameSettings, !string.IsNullOrEmpty(profile.ProtectedPassword), profile.UseInstallPathSaves,
        mods, Math.Max(0, managedMods.Length - mods.Length)));
});

app.MapPost("/api/instances/import", async (ProfileImportDocument document, HttpContext context) =>
{
    if (!await RequireAdmin(context, auth, state)) return Results.Forbid();
    if (document.SchemaVersion != 1) return Results.BadRequest(new { error = "A versão deste JSON de perfil não é compatível." });
    var input = new ProfileInput(document.Name, document.InstallPath, document.ServerName, document.WorldName,
        document.GamePassword ?? "", document.Port, document.IsPublic, document.Crossplay, document.ExtraArguments,
        document.GameId, document.SaveDirectory, document.GameSettings, document.UseInstallPathSaves);
    var validation = ValidateProfile(input, isUpdate: true);
    if (validation is not null) return Results.BadRequest(new { error = validation });
    if (!gameAdapters.TryGet(document.GameId, out _))
        return Results.BadRequest(new { error = $"O jogo '{document.GameId}' ainda não tem um adaptador de servidor." });
    if (input.Port + gameAdapters.PortSpan(input.GameId) - 1 > 65535)
        return Results.BadRequest(new { error = "A faixa de portas deste perfil ultrapassa o limite UDP." });
    var conflict = await state.ReadAsync(data => PortConflict(data.Instances, input.Port, input.GameId, null, gameAdapters));
    if (conflict) return Results.Conflict(new { error = "As portas deste perfil já estão reservadas por outro perfil." });
    var profile = FromInput(input);
    var saveConflict = await state.ReadAsync(data => SavePathConflict(data.Instances, profile, state.DataDirectory, null));
    if (saveConflict) return Results.Conflict(new { error = "Outro perfil já usa esta pasta de saves; escolha uma pasta exclusiva para proteger os mundos." });
    await state.UpdateAsync(data =>
    {
        data.Instances.Add(profile);
        data.Revisions.Add(NewProfileRevision(profile.Id, "Perfil importado de JSON", "", SerializeProfile(profile), isGlobal: true));
        return true;
    });
    steamCmd.CheckProfileInBackground(profile.Id);
    return Results.Created($"/api/instances/{profile.Id}", ToView(profile, "stopped", gameAdapters, state.DataDirectory, steamCmd, supervisor));
});

app.MapGet("/api/instances/{id:guid}/mods", async (Guid id, HttpContext context, CancellationToken cancellationToken) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var profile = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == id));
    if (profile is null) return Results.NotFound();
    var modOwnerId = await ResolveModOwnerIdAsync(id, state);
    if (modOwnerId is null) return Results.NotFound();
    var managed = await state.ReadAsync(data => data.InstalledMods.Where(item => item.InstanceId == modOwnerId).OrderBy(item => item.Name).ToArray());
    var packages = new List<InstalledMod>(managed.Length);
    var unavailableCatalogs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    using var metadataTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    metadataTimeout.CancelAfter(TimeSpan.FromSeconds(5));
    foreach (var mod in managed)
    {
        var owner = mod.Owner;
        var description = mod.Description;
        var iconUrl = mod.IconUrl;
        var projectUrl = mod.ProjectUrl;
        var fileSize = mod.FileSize;
        var createdAt = mod.CreatedAt;
        var updatedAt = mod.UpdatedAt;
        var ratingScore = mod.RatingScore;
        var categories = mod.Categories;
        var catalogProvider = !string.IsNullOrWhiteSpace(mod.SourceProvider) ? mod.SourceProvider :
            mod.Provider.Equals("bepinex", StringComparison.OrdinalIgnoreCase) ? "thunderstore" : mod.Provider;
        if ((string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(description) || string.IsNullOrWhiteSpace(iconUrl) || !createdAt.HasValue || !updatedAt.HasValue || !ratingScore.HasValue || !fileSize.HasValue || categories.Count == 0) && !unavailableCatalogs.Contains(catalogProvider) &&
            (catalogProvider.Equals("thunderstore", StringComparison.OrdinalIgnoreCase) || catalogProvider.Equals("hexium", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var metadata = await providers.FindPackageAsync(catalogProvider, mod.PackageId, metadataTimeout.Token, version: mod.Version);
                owner = string.IsNullOrWhiteSpace(owner) ? metadata?.Owner ?? "" : owner;
                description = string.IsNullOrWhiteSpace(description) ? metadata?.Description ?? "" : description;
                iconUrl = string.IsNullOrWhiteSpace(iconUrl) ? metadata?.IconUrl : iconUrl;
                projectUrl = string.IsNullOrWhiteSpace(projectUrl) ? metadata?.ProjectUrl : projectUrl;
                fileSize ??= metadata?.FileSize;
                createdAt ??= metadata?.CreatedAt;
                updatedAt ??= metadata?.UpdatedAt;
                ratingScore ??= metadata?.RatingScore;
                if (categories.Count == 0 && metadata?.Categories is { Count: > 0 }) categories = metadata.Categories.ToList();
            }
            catch (ProviderException) { unavailableCatalogs.Add(catalogProvider); }
            catch (HttpRequestException) { unavailableCatalogs.Add(catalogProvider); }
            catch (JsonException) { unavailableCatalogs.Add(catalogProvider); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { unavailableCatalogs.Add(catalogProvider); }
        }
        packages.Add(new InstalledMod
        {
            Id = mod.Id, InstanceId = mod.InstanceId, Provider = mod.Provider, SourceProvider = catalogProvider, PackageId = mod.PackageId,
            Name = mod.Name, Version = mod.Version, Description = description, Owner = owner, IconUrl = iconUrl, ProjectUrl = projectUrl,
            CreatedAt = createdAt, UpdatedAt = updatedAt, RatingScore = ratingScore, Categories = categories, FileSize = fileSize,
            Sha256 = mod.Sha256, ManagedFiles = mod.ManagedFiles, FileHashes = mod.FileHashes,
            Enabled = mod.Enabled, Locked = mod.Locked, IsFramework = mod.IsFramework, Dependencies = mod.Dependencies,
            DependencyMetadataKnown = mod.DependencyMetadataKnown, InstalledAt = mod.InstalledAt
        });
    }
    if (!Path.IsPathFullyQualified(profile.InstallPath) || !Directory.Exists(profile.InstallPath))
        return Results.Ok(new { loaderInstalled = false, loaderManaged = false, mods = Array.Empty<object>(), packages });
    var pluginPath = Path.Combine(profile.InstallPath, "BepInEx", "plugins");
    var files = Directory.Exists(pluginPath)
        ? Directory.EnumerateFiles(pluginPath, "*.dll", SearchOption.AllDirectories)
            .Take(2000)
            .Select(path => new { name = Path.GetFileNameWithoutExtension(path), relativePath = Path.GetRelativePath(pluginPath, path), bytes = new FileInfo(path).Length })
            .OrderBy(item => item.name, StringComparer.OrdinalIgnoreCase).ToArray()
        : [];
    var loader = packages.FirstOrDefault(item => item.IsFramework);
    var bepinexRoot = Path.Combine(profile.InstallPath, "BepInEx");
    var detectedFromFiles = File.Exists(Path.Combine(bepinexRoot, "core", "BepInEx.dll")) &&
        (File.Exists(Path.Combine(profile.InstallPath, "winhttp.dll")) || File.Exists(Path.Combine(profile.InstallPath, "doorstop_config.ini")) ||
         File.Exists(Path.Combine(profile.InstallPath, "winhttp.dll.dss-disabled")));
    var loaderInstalled = loader is not null || detectedFromFiles;
    return Results.Ok(new { loaderInstalled, loaderManaged = loader is not null,
        loaderEnabled = loader?.Enabled ?? true, mods = files, packages });
});

app.MapPost("/api/instances/{id:guid}/mods/r2modman/preview", async (Guid id,
    [FromForm(Name = "code")] string? code, [FromForm(Name = "file")] IFormFile? file,
    HttpContext context, CancellationToken cancellationToken) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var profile = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == id));
    if (profile is null) return Results.NotFound();
    if (!profile.GameId.Equals("valheim", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "A importação de perfis r2modman está disponível para servidores Valheim." });
    if (await IsGroupRunningAsync(id, state, supervisor))
        return Results.Conflict(new { error = "Pare todos os servidores deste perfil antes de importar mods e configurações." });
    try
    {
        var archiveBytes = await ReadR2ModmanUploadAsync(file, code, r2modmanProfiles, cancellationToken);
        var archive = r2modmanProfiles.ParseArchive(archiveBytes);
        var existing = r2modmanProfiles.CountExistingConfigs(profile.InstallPath, archive.Configs);
        var frameworkAlreadyPresent = ModPackageManager.HasActiveBepInEx(profile.InstallPath) &&
            archive.Mods.Any(mod => mod.PackageId.Equals(ModPackageManager.BepInExFrameworkPackageId, StringComparison.OrdinalIgnoreCase));
        return Results.Ok(new
        {
            profileName = archive.ProfileName,
            mods = archive.Mods,
            packageCount = archive.Mods.Count,
            enabledCount = archive.Mods.Count(mod => mod.Enabled),
            disabledCount = archive.Mods.Count(mod => !mod.Enabled),
            frameworkAlreadyPresent,
            configs = archive.Configs.Select(config => new { config.RelativePath, config.Size }),
            configCount = archive.Configs.Count,
            configBytes = archive.ConfigBytes,
            overwriteCount = existing,
            overwrites = archive.Configs.Where(config => File.Exists(Path.Combine(profile.InstallPath, "BepInEx", "config", config.RelativePath.Replace('/', Path.DirectorySeparatorChar))))
                .Select(config => config.RelativePath).ToArray()
        });
    }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (HttpRequestException) { return Results.Json(new { error = "Não foi possível baixar o perfil pelo código. Confira a conexão ou tente importar o arquivo .r2z." }, statusCode: 502); }
    catch (TaskCanceledException) { return Results.Json(new { error = "A consulta do código excedeu o tempo limite." }, statusCode: 504); }
    catch (IOException) { return Results.BadRequest(new { error = "Não foi possível ler o arquivo do perfil." }); }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Unexpected failure while previewing an r2modman archive for profile {ProfileId}", id);
        return Results.Json(new { error = "Não foi possível preparar a prévia do perfil r2modman. Consulte o log do DSS para obter os detalhes técnicos." }, statusCode: 500);
    }
}).WithMetadata(new RequestSizeLimitAttribute(30 * 1024 * 1024), new RequestFormLimitsAttribute { MultipartBodyLengthLimit = 30 * 1024 * 1024 });

app.MapPost("/api/instances/{id:guid}/mods/r2modman/import", async (Guid id,
    [FromForm(Name = "code")] string? code, [FromForm(Name = "file")] IFormFile? file,
    [FromForm(Name = "confirmOverwrite")] bool confirmOverwrite,
    [FromForm(Name = "selectionProvided")] bool selectionProvided,
    HttpContext context, CancellationToken cancellationToken) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var profile = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == id));
    if (profile is null) return Results.NotFound();
    if (!profile.GameId.Equals("valheim", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "A importação de perfis r2modman está disponível para servidores Valheim." });
    if (await IsGroupRunningAsync(id, state, supervisor))
        return Results.Conflict(new { error = "Pare todos os servidores deste perfil antes de importar mods e configurações." });
    byte[] archiveBytes;
    try { archiveBytes = await ReadR2ModmanUploadAsync(file, code, r2modmanProfiles, cancellationToken); }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (HttpRequestException) { return Results.Json(new { error = "Não foi possível baixar o perfil pelo código. Confira a conexão ou tente importar o arquivo .r2z." }, statusCode: 502); }
    catch (TaskCanceledException) { return Results.Json(new { error = "A consulta do código excedeu o tempo limite." }, statusCode: 504); }
    catch (IOException) { return Results.BadRequest(new { error = "Não foi possível ler o arquivo do perfil." }); }
    R2ModmanArchive archive;
    try { archive = r2modmanProfiles.ParseArchive(archiveBytes); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
    // ASP.NET Core's minimal API form binder can throw while building route metadata
    // for string[] parameters. Read repeated form fields directly instead.
    var importForm = await context.Request.ReadFormAsync(cancellationToken);
    var selectedMods = importForm["selectedMods"].ToArray();
    var selectedConfigs = importForm["selectedConfigs"].ToArray();
    var frameworkAlreadyPresent = ModPackageManager.HasActiveBepInEx(profile.InstallPath) &&
        archive.Mods.Any(mod => mod.PackageId.Equals(ModPackageManager.BepInExFrameworkPackageId, StringComparison.OrdinalIgnoreCase));
    IReadOnlyList<R2ModmanPackage> requestedMods = !selectionProvided ? archive.Mods : archive.Mods
        .Where(mod => selectedMods.Contains(mod.PackageId, StringComparer.OrdinalIgnoreCase)).ToArray();
    IReadOnlyList<R2ModmanConfig> requestedConfigs = !selectionProvided ? archive.Configs : archive.Configs
        .Where(config => selectedConfigs.Contains(config.RelativePath, StringComparer.OrdinalIgnoreCase)).ToArray();
    if ((selectedMods.Length > 0 && requestedMods.Count != selectedMods.Distinct(StringComparer.OrdinalIgnoreCase).Count()) ||
        (selectedConfigs.Length > 0 && requestedConfigs.Count != selectedConfigs.Distinct(StringComparer.OrdinalIgnoreCase).Count()))
        return Results.BadRequest(new { error = "A seleção contém itens que não pertencem ao perfil importado." });
    if (requestedMods.Count == 0 && requestedConfigs.Count == 0)
        return Results.BadRequest(new { error = "Selecione ao menos um mod ou uma configuração para importar." });
    int confirmedOverwriteCount;
    try { confirmedOverwriteCount = r2modmanProfiles.CountExistingConfigs(profile.InstallPath, requestedConfigs); }
    catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException)
    { return Results.BadRequest(new { error = ex.Message }); }
    if (confirmedOverwriteCount > 0 && !confirmOverwrite)
        return Results.Conflict(new { error = $"Existem {confirmedOverwriteCount} configurações que seriam substituídas. Revise a prévia e confirme a substituição." });
    var modOwnerId = await ResolveModOwnerIdAsync(id, state);
    if (modOwnerId is null) return Results.NotFound();
    if (requestedConfigs.Count > 0 && !Path.IsPathFullyQualified(profile.InstallPath))
        return Results.BadRequest(new { error = "Configure uma pasta absoluta para o servidor antes de importar configurações." });

    return Results.Stream(async stream =>
    {
        var channel = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var streamJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var importTask = Task.Run(async () =>
        {
            try
            {
                IReadOnlyList<InstalledMod> installed = [];
                if (requestedMods.Count > 0)
                {
                    var versions = requestedMods.ToDictionary(mod => mod.PackageId, mod => mod.Version, StringComparer.OrdinalIgnoreCase);
                    installed = await modPackages.InstallBatchAsync(modOwnerId.Value, "thunderstore",
                        requestedMods.Select(mod => mod.PackageId).ToArray(), cancellationToken, versions,
                        progress => channel.Writer.TryWrite(new
                        {
                            type = "progress", progress.Phase, progress.PackageId, progress.PackageName,
                            progress.Index, progress.Total, progress.DownloadedBytes, progress.TotalBytes
                        }));
                }

                foreach (var desired in requestedMods.Where(mod => mod.Enabled))
                {
                    var current = await state.ReadAsync(data => data.InstalledMods.FirstOrDefault(mod =>
                        mod.InstanceId == modOwnerId.Value && mod.PackageId.Equals(desired.PackageId, StringComparison.OrdinalIgnoreCase)));
                    if (current is null) throw new InvalidDataException($"O pacote {desired.PackageId} não foi encontrado após a instalação.");
                    if (!current.Enabled) await modPackages.SetEnabledWithDependentsAsync(modOwnerId.Value, current.Id, true, cascadeDependents: true, cancellationToken);
                }
                foreach (var desired in requestedMods.Where(mod => !mod.Enabled))
                {
                    var current = await state.ReadAsync(data => data.InstalledMods.FirstOrDefault(mod =>
                        mod.InstanceId == modOwnerId.Value && mod.PackageId.Equals(desired.PackageId, StringComparison.OrdinalIgnoreCase)));
                    if (current is null) throw new InvalidDataException($"O pacote {desired.PackageId} não foi encontrado após a instalação.");
                    if (current.Enabled) await modPackages.SetEnabledWithDependentsAsync(modOwnerId.Value, current.Id, false, cascadeDependents: true, cancellationToken);
                }

                await r2modmanProfiles.ImportConfigsAsync(profile.InstallPath, requestedConfigs, cancellationToken);
                await state.UpdateAsync(data =>
                {
                    data.Revisions.Add(new ConfigRevision
                    {
                        InstanceId = id, Kind = "r2modman-import", Target = archive.ProfileName,
                        Summary = $"Perfil r2modman importado: {requestedMods.Count} mod(s), {requestedConfigs.Count} configuração(ões); {confirmedOverwriteCount} substituída(s).",
                        CanRollback = false
                    });
                    foreach (var mod in installed)
                        data.Revisions.Add(new ConfigRevision { InstanceId = id, Kind = "mod-package", Target = mod.PackageId,
                            Summary = $"Mod importado do r2modman: {mod.Name} {mod.Version}", CanRollback = false });
                    return true;
                });
                var message = $"Importação concluída: {requestedMods.Count} mod(s), {requestedConfigs.Count} configuração(ões)" +
                    (confirmedOverwriteCount > 0 ? $" e {confirmedOverwriteCount} configuração(ões) substituída(s)." : ".") +
                    (frameworkAlreadyPresent ? " O BepInEx existente foi atualizado/adotado pelo DSS na versão exportada." : "");
                channel.Writer.TryWrite(new { type = "completed", installed, configCount = requestedConfigs.Count, overwrittenCount = confirmedOverwriteCount, message });
            }
            catch (ModInstallException ex) { channel.Writer.TryWrite(new { type = "error", status = (int)ex.StatusCode, message = ex.Message }); }
            catch (ProviderException ex) { channel.Writer.TryWrite(new { type = "error", status = 503, message = ex.Message }); }
            catch (HttpRequestException) { channel.Writer.TryWrite(new { type = "error", status = 502, message = "Não foi possível acessar o catálogo Thunderstore." }); }
            catch (TaskCanceledException) { channel.Writer.TryWrite(new { type = "error", status = 504, message = "A importação excedeu o tempo limite ou foi cancelada." }); }
            catch (InvalidDataException ex) { channel.Writer.TryWrite(new { type = "error", status = 400, message = ex.Message }); }
            catch (UnauthorizedAccessException) { channel.Writer.TryWrite(new { type = "error", status = 403, message = "O Windows negou acesso aos arquivos do perfil." }); }
            catch (IOException) { channel.Writer.TryWrite(new { type = "error", status = 500, message = "Não foi possível gravar os mods ou as configurações importadas." }); }
            catch (Exception ex)
            {
                app.Logger.LogError(ex, "Unexpected failure while importing an r2modman archive for profile {ProfileId}", id);
                channel.Writer.TryWrite(new { type = "error", status = 500, message = "A importação foi interrompida por uma falha inesperada. O log do DSS contém os detalhes técnicos." });
            }
            finally { channel.Writer.TryComplete(); }
        }, CancellationToken.None);

        await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
        {
            var line = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item, streamJsonOptions) + "\n");
            await stream.WriteAsync(line, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        await importTask;
    }, "application/x-ndjson");
}).WithMetadata(new RequestSizeLimitAttribute(30 * 1024 * 1024), new RequestFormLimitsAttribute { MultipartBodyLengthLimit = 30 * 1024 * 1024 });

app.MapPost("/api/instances/{id:guid}/mods/install", async (Guid id, ModInstallInput input, HttpContext context, CancellationToken cancellationToken) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    if (await IsGroupRunningAsync(id, state, supervisor)) return Results.Conflict(new { error = "Pare todos os servidores deste perfil antes de instalar ou atualizar mods." });
    if (input.PackageId.Length is 0 or > 160) return Results.BadRequest(new { error = "Identificador de pacote inválido." });
    var modOwnerId = await ResolveModOwnerIdAsync(id, state);
    if (modOwnerId is null) return Results.NotFound();
    try
    {
        var installProfile = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == modOwnerId.Value));
        var previousMods = await state.ReadAsync(data => data.InstalledMods.Where(item => item.InstanceId == modOwnerId.Value).ToArray());
        var previousFiles = previousMods.ToDictionary(item => item.Id, item => ExistingManagedFiles(installProfile, item));
        var installed = await modPackages.InstallAsync(modOwnerId.Value, input.Provider, input.PackageId, cancellationToken, input.DependencyVersions);
        await state.UpdateAsync(data =>
        {
            foreach (var mod in installed)
                data.Revisions.Add(CreateModHistoryRevision(id, mod, FindPreviousMod(previousMods, mod), previousFiles, installProfile));
            return true;
        });
        var names = installed.Select(mod => $"{mod.Name} {mod.Version}").ToArray();
        return Results.Ok(new { installed, message = installed.Count > 1
            ? $"{installed.Count} pacotes instalados/atualizados, incluindo dependências: {string.Join(", ", names)}."
            : $"{names.FirstOrDefault() ?? input.PackageId} instalado." });
    }
    catch (ModInstallException ex) { return Results.Json(new { error = ex.Message }, statusCode: (int)ex.StatusCode); }
    catch (ProviderException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable); }
    catch (HttpRequestException) { return Results.Json(new { error = "O download falhou. Nenhum arquivo temporário foi mantido." }, statusCode: StatusCodes.Status502BadGateway); }
    catch (TaskCanceledException) { return Results.Json(new { error = "O download excedeu o tempo limite." }, statusCode: StatusCodes.Status504GatewayTimeout); }
    catch (InvalidDataException) { return Results.BadRequest(new { error = "O arquivo baixado não é um pacote ZIP válido." }); }
    catch (IOException) { return Results.Problem("Não foi possível gravar o pacote no servidor. Verifique permissões e espaço em disco.", statusCode: StatusCodes.Status500InternalServerError); }
    catch (UnauthorizedAccessException) { return Results.Problem("O Windows negou acesso aos arquivos do servidor.", statusCode: StatusCodes.Status403Forbidden); }
});

app.MapPost("/api/instances/{id:guid}/mods/install-batch", async (Guid id, ModInstallBatchInput input, HttpContext context, CancellationToken cancellationToken) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    if (await IsGroupRunningAsync(id, state, supervisor)) return Results.Conflict(new { error = "Pare todos os servidores deste perfil antes de instalar ou atualizar mods." });
    if (string.IsNullOrWhiteSpace(input.Provider) || input.PackageIds is null || input.PackageIds.Count is 0 or > 200 ||
        input.PackageIds.Any(packageId => string.IsNullOrWhiteSpace(packageId) || packageId.Length > 160))
        return Results.BadRequest(new { error = "A seleção de mods é inválida. Escolha entre 1 e 200 pacotes." });
    var modOwnerId = await ResolveModOwnerIdAsync(id, state);
    if (modOwnerId is null) return Results.NotFound();
    var installProfile = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == modOwnerId.Value));
    var previousMods = await state.ReadAsync(data => data.InstalledMods.Where(item => item.InstanceId == modOwnerId.Value).ToArray());
    var previousFiles = previousMods.ToDictionary(item => item.Id, item => ExistingManagedFiles(installProfile, item));
    var packageIds = input.PackageIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    return Results.Stream(async stream =>
    {
        var channel = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var streamJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var installTask = Task.Run(async () =>
        {
            try
            {
                var installed = await modPackages.InstallBatchAsync(modOwnerId.Value, input.Provider, packageIds, cancellationToken,
                    input.DependencyVersions, progress => channel.Writer.TryWrite(new
                    {
                        type = "progress", progress.Phase, progress.PackageId, progress.PackageName, progress.Index,
                        progress.Total, progress.DownloadedBytes, progress.TotalBytes
                    }));
                if (installed.Count > 0)
                    await state.UpdateAsync(data =>
                    {
                        foreach (var mod in installed)
                            data.Revisions.Add(CreateModHistoryRevision(id, mod, FindPreviousMod(previousMods, mod), previousFiles, installProfile));
                        return true;
                    });
                var names = installed.Select(mod => $"{mod.Name} {mod.Version}").ToArray();
                var message = installed.Count == 0
                    ? "Todos os mods selecionados e suas dependências já estavam instalados nas versões compatíveis."
                    : $"Lote concluído: {installed.Count} pacote(s) instalado(s)/atualizado(s), incluindo dependências: {string.Join(", ", names)}.";
                channel.Writer.TryWrite(new { type = "completed", installed, message });
            }
            catch (ModInstallException ex) { channel.Writer.TryWrite(new { type = "error", status = (int)ex.StatusCode, message = ex.Message }); }
            catch (ProviderException ex) { channel.Writer.TryWrite(new { type = "error", status = 503, message = ex.Message }); }
            catch (HttpRequestException) { channel.Writer.TryWrite(new { type = "error", status = 502, message = "O download falhou. Nenhum arquivo temporário foi mantido." }); }
            catch (TaskCanceledException) { channel.Writer.TryWrite(new { type = "error", status = 504, message = "O download excedeu o tempo limite." }); }
            catch (InvalidDataException) { channel.Writer.TryWrite(new { type = "error", status = 400, message = "Um dos arquivos baixados não é um pacote ZIP válido." }); }
            catch (IOException) { channel.Writer.TryWrite(new { type = "error", status = 500, message = "Não foi possível gravar o lote no servidor. Verifique permissões e espaço em disco." }); }
            catch (UnauthorizedAccessException) { channel.Writer.TryWrite(new { type = "error", status = 403, message = "O Windows negou acesso aos arquivos do servidor." }); }
            catch (Exception) { channel.Writer.TryWrite(new { type = "error", status = 500, message = "O lote foi interrompido por uma falha inesperada. Confira o inventário antes de tentar novamente." }); }
            finally { channel.Writer.TryComplete(); }
        });

        await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
        {
            var line = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item, streamJsonOptions) + "\n");
            await stream.WriteAsync(line, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        await installTask;
    }, "application/x-ndjson");
});

app.MapPost("/api/instances/{id:guid}/mods/uninstall-batch", async (Guid id, ModUninstallBatchInput input, HttpContext context, CancellationToken cancellationToken) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    if (await IsGroupRunningAsync(id, state, supervisor)) return Results.Conflict(new { error = "Pare todos os servidores deste perfil antes de remover mods." });
    if (input.ModIds is null || input.ModIds.Count is 0 or > 200 || input.ModIds.Any(modId => modId == Guid.Empty))
        return Results.BadRequest(new { error = "Selecione entre 1 e 200 mods válidos para remover." });
    var modOwnerId = await ResolveModOwnerIdAsync(id, state);
    if (modOwnerId is null) return Results.NotFound();
    try
    {
        var selectedIds = input.ModIds.Distinct().ToHashSet();
        var installProfile = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == modOwnerId.Value));
        var selectedMods = await state.ReadAsync(data => data.InstalledMods.Where(item => item.InstanceId == modOwnerId.Value && selectedIds.Contains(item.Id)).ToArray());
        var results = await modPackages.UninstallBatchAsync(modOwnerId.Value, input.ModIds.Distinct().ToArray(), cancellationToken);
        var removed = results.Where(result => result.Error is null).ToArray();
        if (removed.Length > 0)
            await state.UpdateAsync(data =>
            {
                foreach (var result in removed)
                {
                    var mod = selectedMods.FirstOrDefault(item => item.Id == result.ModId);
                    if (mod is not null)
                        data.Revisions.Add(CreateModRemovalHistoryRevision(id, mod, result.RemovedFiles, result.PreservedFiles));
                }
                return true;
            });
        var removedFiles = removed.Sum(result => result.Removed);
        var preservedFiles = removed.Sum(result => result.Preserved);
        var message = removed.Length == results.Count
            ? $"{removed.Length} mods removidos em lote · {removedFiles} arquivo(s) excluído(s), {preservedFiles} alteração(ões) preservada(s)."
            : $"{removed.Length} de {results.Count} mods removidos · {removedFiles} arquivo(s) excluído(s). {string.Join("; ", results.Where(result => result.Error is not null).Select(result => $"{result.Name}: {result.Error}"))}";
        return Results.Ok(new { removedModIds = removed.Select(result => result.ModId), failed = results.Where(result => result.Error is not null),
            removals = removed.Select(result => new { result.ModId, result.RemovedFiles, result.PreservedFiles }), message });
    }
    catch (ModInstallException ex) { return Results.Json(new { error = ex.Message }, statusCode: (int)ex.StatusCode); }
    catch (IOException) { return Results.Problem("Não foi possível remover os arquivos dos mods.", statusCode: StatusCodes.Status500InternalServerError); }
    catch (UnauthorizedAccessException) { return Results.Problem("O Windows negou acesso aos arquivos dos mods.", statusCode: StatusCodes.Status403Forbidden); }
});

app.MapPost("/api/instances/{id:guid}/mods/{modId:guid}/enabled", async (Guid id, Guid modId, JsonElement input, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    if (await IsGroupRunningAsync(id, state, supervisor)) return Results.Conflict(new { error = "Pare todos os servidores deste perfil antes de ativar ou desativar mods." });
    if (!input.TryGetProperty("enabled", out var enabledValue) || enabledValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        return Results.BadRequest(new { error = "Informe enabled como true ou false." });
    var enabled = enabledValue.GetBoolean();
    var cascadeDependents = input.TryGetProperty("cascadeDependents", out var cascadeValue) && cascadeValue.ValueKind == JsonValueKind.True;
    var modOwnerId = await ResolveModOwnerIdAsync(id, state);
    if (modOwnerId is null) return Results.NotFound();
    try
    {
        var modName = await state.ReadAsync(data => data.InstalledMods.FirstOrDefault(item => item.InstanceId == modOwnerId && item.Id == modId)?.Name);
        var related = await modPackages.SetEnabledWithDependentsAsync(modOwnerId.Value, modId, enabled, cascadeDependents, context.RequestAborted);
        var displayName = string.IsNullOrWhiteSpace(modName) ? "Mod" : modName;
        var logToggleActions = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == id)?.LogModToggleActions == true);
        if (logToggleActions)
            await state.UpdateAsync(data => { data.Revisions.Add(new ConfigRevision { InstanceId = id, Kind = "mod-package", Target = displayName, Summary = enabled ? $"{displayName} ativado" : $"{displayName} inativado{(related.Count == 0 ? "" : $" com {related.Count} dependente(s)")}", CanRollback = false }); return true; });
        var message = enabled
            ? related.Count == 0 ? "Mod ativado." : related.Count == 1
                ? "Mod ativado; 1 dependência foi verificada e habilitada."
                : $"Mod ativado; {related.Count} dependências foram verificadas e habilitadas."
            : related.Count == 0 ? "Mod inativado." : $"Mod inativado junto com {related.Count} dependente(s).";
        return Results.Ok(new { enabled, dependencies = enabled ? related : Array.Empty<ModDependentView>(), dependents = enabled ? Array.Empty<ModDependentView>() : related, message });
    }
    catch (ModInstallException ex) { return Results.Json(new { error = ex.Message }, statusCode: (int)ex.StatusCode); }
    catch (IOException) { return Results.Problem("Não foi possível alterar os arquivos do mod.", statusCode: StatusCodes.Status500InternalServerError); }
    catch (UnauthorizedAccessException) { return Results.Problem("O Windows negou acesso aos arquivos do mod.", statusCode: StatusCodes.Status403Forbidden); }
});

app.MapPost("/api/instances/{id:guid}/mods/enabled-batch", async (Guid id, ModEnabledBatchInput input, HttpContext context, CancellationToken cancellationToken) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    if (await IsGroupRunningAsync(id, state, supervisor)) return Results.Conflict(new { error = "Pare todos os servidores deste perfil antes de ativar ou desativar mods." });
    if (input.ModIds is null || input.ModIds.Count is 0 or > 200 || input.ModIds.Any(modId => modId == Guid.Empty) || input.Enabled is null)
        return Results.BadRequest(new { error = "Selecione entre 1 e 200 mods válidos e informe o estado desejado." });
    var modOwnerId = await ResolveModOwnerIdAsync(id, state);
    if (modOwnerId is null) return Results.NotFound();
    var ids = input.ModIds.Distinct().ToArray();
    var before = await state.ReadAsync(data => data.InstalledMods.Where(item => item.InstanceId == modOwnerId).ToDictionary(item => item.Id));
    var selected = ids.Where(before.ContainsKey).Select(modId => before[modId]).ToArray();
    if (selected.Length != ids.Length) return Results.NotFound(new { error = "Um ou mais mods selecionados não estão neste perfil." });
    var changedLocked = selected.Where(item => item.Locked && item.Enabled != input.Enabled.Value).Select(item => item.Name).ToArray();
    if (changedLocked.Length > 0)
        return Results.Conflict(new { error = $"Desbloqueie estes mods antes de alterar o estado: {string.Join(", ", changedLocked)}." });

    var failures = new List<object>();
    var affectedIds = ids.ToHashSet();
    foreach (var mod in selected.Where(item => item.Enabled != input.Enabled.Value))
    {
        try
        {
            // A cascata é confirmada uma única vez pela interface antes de chamar este endpoint.
            var related = await modPackages.SetEnabledWithDependentsAsync(modOwnerId.Value, mod.Id, input.Enabled.Value,
                cascadeDependents: true, cancellationToken);
            foreach (var item in related) affectedIds.Add(item.Id);
        }
        catch (ModInstallException ex) { failures.Add(new { modId = mod.Id, name = mod.Name, error = ex.Message }); }
        catch (IOException) { failures.Add(new { modId = mod.Id, name = mod.Name, error = "Não foi possível alterar os arquivos deste mod." }); }
        catch (UnauthorizedAccessException) { failures.Add(new { modId = mod.Id, name = mod.Name, error = "O Windows negou acesso aos arquivos deste mod." }); }
    }

    var after = await state.ReadAsync(data => data.InstalledMods.Where(item => item.InstanceId == modOwnerId).ToDictionary(item => item.Id));
    var changedMods = before.Values.Where(item => affectedIds.Contains(item.Id) && after.TryGetValue(item.Id, out var current) && current.Enabled != item.Enabled)
        .Select(item => new { item.Id, item.Name, Enabled = after[item.Id].Enabled }).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    var logToggleActions = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == id)?.LogModToggleActions == true);
    if (logToggleActions && changedMods.Length > 0)
    {
        var enabled = input.Enabled.Value;
        await state.UpdateAsync(data =>
        {
            data.Revisions.Add(new ConfigRevision
            {
                InstanceId = id,
                Kind = "mod-package",
                Target = "Mods do perfil",
                Summary = $"{changedMods.Length} {(changedMods.Length == 1 ? "mod" : "mods")} {(enabled ? "ativado" : "desativado")} em lote",
                CanRollback = false,
                ModToggleNames = changedMods.Select(item => item.Name).ToList(),
                ModToggleEnabled = enabled
            });
            return true;
        });
    }

    var changedCount = changedMods.Length;
    var action = input.Enabled.Value ? "ativado" : "desativado";
    var message = $"{changedCount} {(changedCount == 1 ? "mod" : "mods")} {action}{(changedCount == 1 ? "" : "s")} em lote.";
    if (failures.Count == 1) message += " 1 mod não pôde ser alterado.";
    else if (failures.Count > 1) message += $" {failures.Count} mods não puderam ser alterados.";
    return Results.Ok(new { changed = changedMods, failed = failures, message });
});

app.MapPost("/api/instances/{id:guid}/mods/{modId:guid}/locked", async (Guid id, Guid modId, JsonElement input, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    if (!input.TryGetProperty("locked", out var lockedValue) || lockedValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        return Results.BadRequest(new { error = "Informe locked como true ou false." });
    var locked = lockedValue.GetBoolean();
    var modOwnerId = await ResolveModOwnerIdAsync(id, state);
    if (modOwnerId is null) return Results.NotFound();
    var found = await modPackages.SetLockedAsync(modOwnerId.Value, modId, locked);
    if (!found) return Results.NotFound(new { error = "O pacote não está no inventário gerenciado." });
    return Results.Ok(new { locked, message = locked ? "Mod bloqueado contra ativação, desativação e remoção." : "Bloqueio do mod removido." });
});

app.MapPost("/api/instances/{id:guid}/mods/locked-batch", async (Guid id, ModLockBatchInput input, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    if (input.ModIds is null || input.ModIds.Count is 0 or > 200 || input.ModIds.Any(modId => modId == Guid.Empty))
        return Results.BadRequest(new { error = "Selecione entre 1 e 200 mods válidos." });
    var modOwnerId = await ResolveModOwnerIdAsync(id, state);
    if (modOwnerId is null) return Results.NotFound();
    var changed = await modPackages.SetLockedBatchAsync(modOwnerId.Value, input.ModIds.Distinct().ToArray(), input.Locked);
    return Results.Ok(new { locked = input.Locked, changed, message = $"{changed} mod(s) {(input.Locked ? "bloqueados" : "desbloqueados")} em lote." });
});

app.MapGet("/api/instances/{id:guid}/mods/{modId:guid}/dependents", async (Guid id, Guid modId, HttpContext context, CancellationToken cancellationToken) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var modOwnerId = await ResolveModOwnerIdAsync(id, state);
    if (modOwnerId is null) return Results.NotFound();
    try { return Results.Ok(new { dependents = await modPackages.GetDependentsAsync(modOwnerId.Value, modId, cancellationToken) }); }
    catch (ModInstallException ex) { return Results.Json(new { error = ex.Message }, statusCode: (int)ex.StatusCode); }
});

app.MapPost("/api/instances/{id:guid}/mods/{modId:guid}/version", async (Guid id, Guid modId, JsonElement input, HttpContext context, CancellationToken cancellationToken) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    if (await IsGroupRunningAsync(id, state, supervisor)) return Results.Conflict(new { error = "Pare todos os servidores deste perfil antes de alterar a versão de um mod." });
    var requestedVersion = input.TryGetProperty("version", out var versionValue) && versionValue.ValueKind == JsonValueKind.String
        ? versionValue.GetString()?.Trim() : null;
    if (string.IsNullOrWhiteSpace(requestedVersion) || requestedVersion.Length > 80)
        return Results.BadRequest(new { error = "Selecione uma versão válida." });
    var modOwnerId = await ResolveModOwnerIdAsync(id, state);
    if (modOwnerId is null) return Results.NotFound();
    var installed = await state.ReadAsync(data => data.InstalledMods.Where(item => item.InstanceId == modOwnerId).ToArray());
    var mod = installed.FirstOrDefault(item => item.Id == modId);
    if (mod is null) return Results.NotFound();
    if (mod.Locked) return Results.Conflict(new { error = $"{mod.Name} está bloqueado na versão {mod.Version}. Desbloqueie-o antes de selecionar ou instalar outra versão." });

    var currentCatalogProvider = string.IsNullOrWhiteSpace(mod.SourceProvider) ? mod.Provider : mod.SourceProvider;
    if (currentCatalogProvider.Equals("bepinex", StringComparison.OrdinalIgnoreCase) || currentCatalogProvider.Equals("ts", StringComparison.OrdinalIgnoreCase))
        currentCatalogProvider = "thunderstore";
    else if (currentCatalogProvider.Equals("hex", StringComparison.OrdinalIgnoreCase)) currentCatalogProvider = "hexium";
    var catalogProvider = input.TryGetProperty("provider", out var providerValue) && providerValue.ValueKind == JsonValueKind.String
        ? providerValue.GetString()?.Trim() : currentCatalogProvider;
    if (catalogProvider?.Equals("ts", StringComparison.OrdinalIgnoreCase) == true) catalogProvider = "thunderstore";
    else if (catalogProvider?.Equals("hex", StringComparison.OrdinalIgnoreCase) == true) catalogProvider = "hexium";
    if (string.IsNullOrWhiteSpace(catalogProvider) || !providers.GetProviders().Any(item =>
            item.Id.Equals(catalogProvider, StringComparison.OrdinalIgnoreCase) && item.CanInstall))
        return Results.BadRequest(new { error = "Escolha um provedor com instalação direta disponível." });
    var selectedCatalogProvider = catalogProvider!;
    var selectedProviderName = providers.GetProviders().First(item => item.Id.Equals(selectedCatalogProvider, StringComparison.OrdinalIgnoreCase)).Name;
    if (mod.IsFramework && !selectedCatalogProvider.Equals(currentCatalogProvider, StringComparison.OrdinalIgnoreCase))
        return Results.Conflict(new { error = "O BepInEx deve permanecer no provedor de origem registrado." });
    try
    {
        var package = await providers.FindPackageAsync(selectedCatalogProvider, mod.PackageId, cancellationToken);
        if (package is null) return Results.NotFound(new { error = "O pacote não está disponível no catálogo de versões deste provedor." });
        var versionExists = (package.Versions ?? []).Any(item => item.Version.Equals(requestedVersion, StringComparison.OrdinalIgnoreCase)) ||
            package.Version.Equals(requestedVersion, StringComparison.OrdinalIgnoreCase);
        if (!versionExists) return Results.Conflict(new { error = "A versão selecionada não está mais disponível no provedor." });

        var requirements = installed.Where(parent => parent.Id != mod.Id)
            .SelectMany(parent => parent.Dependencies.Select(dependency => (parent.Name, Version: RequiredDependencyVersion(dependency, mod.PackageId))))
            .Where(item => item.Version is not null).ToArray();
        var tooOldFor = requirements.Where(item => IsNewerModVersion(item.Version!, requestedVersion)).ToArray();
        if (tooOldFor.Length > 0)
        {
            var minimum = tooOldFor.Select(item => item.Version!).OrderByDescending(value => value, Comparer<string>.Create((left, right) =>
                IsNewerModVersion(left, right) ? 1 : IsNewerModVersion(right, left) ? -1 : 0)).First();
            return Results.Conflict(new { error = $"A versão {requestedVersion} é mais antiga que o mínimo exigido pelas dependências deste perfil ({minimum})." });
        }

        var selectedVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [mod.PackageId] = requestedVersion };
        var changed = await modPackages.InstallAsync(modOwnerId.Value, selectedCatalogProvider, mod.PackageId, cancellationToken,
            selectedVersions, replacingModId: mod.Id);
        await state.UpdateAsync(data =>
        {
            foreach (var item in changed) data.Revisions.Add(new ConfigRevision { InstanceId = id, Kind = "mod-package", Target = item.PackageId,
                Summary = $"Versão do mod alterada: {item.Name} {item.Version}", CanRollback = false });
            return true;
        });
        var providerChanged = !selectedCatalogProvider.Equals(currentCatalogProvider, StringComparison.OrdinalIgnoreCase);
        return Results.Ok(new { installed = changed, message = providerChanged
            ? $"{mod.Name} atualizado para a versão {requestedVersion} pelo {selectedProviderName}."
            : $"{mod.Name} atualizado para a versão {requestedVersion}." });
    }
    catch (ModInstallException ex) { return Results.Json(new { error = ex.Message }, statusCode: (int)ex.StatusCode); }
    catch (ProviderException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable); }
    catch (HttpRequestException) { return Results.Json(new { error = "O download falhou. Nenhum arquivo temporário foi mantido." }, statusCode: StatusCodes.Status502BadGateway); }
    catch (TaskCanceledException) { return Results.Json(new { error = "A consulta ou o download excedeu o tempo limite." }, statusCode: StatusCodes.Status504GatewayTimeout); }
    catch (InvalidDataException) { return Results.BadRequest(new { error = "O arquivo baixado não é um pacote ZIP válido." }); }
    catch (IOException) { return Results.Problem("Não foi possível gravar a versão escolhida no servidor.", statusCode: StatusCodes.Status500InternalServerError); }
    catch (UnauthorizedAccessException) { return Results.Problem("O Windows negou acesso aos arquivos do servidor.", statusCode: StatusCodes.Status403Forbidden); }
});

app.MapPost("/api/instances/{id:guid}/mods/{modId:guid}/update", async (Guid id, Guid modId, HttpContext context, CancellationToken cancellationToken) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    if (await IsGroupRunningAsync(id, state, supervisor)) return Results.Conflict(new { error = "Pare todos os servidores deste perfil antes de atualizar mods." });
    var modOwnerId = await ResolveModOwnerIdAsync(id, state);
    if (modOwnerId is null) return Results.NotFound();
    var mod = await state.ReadAsync(data => data.InstalledMods.FirstOrDefault(item => item.InstanceId == modOwnerId && item.Id == modId));
    if (mod is null) return Results.NotFound();
    if (mod.Locked) return Results.Conflict(new { error = $"{mod.Name} está bloqueado na versão {mod.Version}. Desbloqueie-o antes de atualizar." });
    var wasDisabled = !mod.Enabled;
    var updateSucceeded = false;
    try
    {
        if (!mod.Enabled) await modPackages.SetEnabledAsync(modOwnerId.Value, modId, true);
        var installed = await modPackages.InstallAsync(modOwnerId.Value, mod.Provider, mod.PackageId, cancellationToken);
        await state.UpdateAsync(data =>
        {
            foreach (var item in installed) data.Revisions.Add(new ConfigRevision { InstanceId = id, Kind = "mod-package", Target = item.PackageId,
                Summary = $"Mod atualizado: {item.Name} {item.Version}", CanRollback = false });
            return true;
        });
        updateSucceeded = true;
        return Results.Ok(new { installed, message = $"{mod.Name} atualizado e ativado." });
    }
    catch (ModInstallException ex) { return Results.Json(new { error = ex.Message }, statusCode: (int)ex.StatusCode); }
    catch (ProviderException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable); }
    catch (HttpRequestException) { return Results.Json(new { error = "O download falhou. Nenhum arquivo temporário foi mantido." }, statusCode: StatusCodes.Status502BadGateway); }
    catch (TaskCanceledException) { return Results.Json(new { error = "O download excedeu o tempo limite." }, statusCode: StatusCodes.Status504GatewayTimeout); }
    catch (InvalidDataException) { return Results.BadRequest(new { error = "O arquivo baixado não é um pacote ZIP válido." }); }
    catch (IOException) { return Results.Problem("Não foi possível gravar o pacote no servidor.", statusCode: StatusCodes.Status500InternalServerError); }
    catch (UnauthorizedAccessException) { return Results.Problem("O Windows negou acesso aos arquivos do servidor.", statusCode: StatusCodes.Status403Forbidden); }
    finally
    {
        if (!updateSucceeded && wasDisabled)
        {
            try { await modPackages.SetEnabledAsync(modOwnerId.Value, modId, false); }
            catch (Exception ex) when (ex is ModInstallException or IOException or UnauthorizedAccessException) { }
        }
    }
});

app.MapPost("/api/instances/{id:guid}/mods/check-updates", async (Guid id, HttpContext context, CancellationToken cancellationToken) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var modOwnerId = await ResolveModOwnerIdAsync(id, state);
    if (modOwnerId is null) return Results.NotFound();
    var allInstalled = await state.ReadAsync(data => data.InstalledMods.Where(item => item.InstanceId == modOwnerId).ToArray());
    var installed = allInstalled.Where(item => !string.IsNullOrWhiteSpace(item.PackageId) && item.PackageId.Length <= 160 &&
        item.PackageId.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.') &&
        item.Provider is not null && (item.Provider.Equals("thunderstore", StringComparison.OrdinalIgnoreCase) ||
            item.Provider.Equals("bepinex", StringComparison.OrdinalIgnoreCase) || item.Provider.Equals("hexium", StringComparison.OrdinalIgnoreCase) ||
            item.Provider.Equals("nexus", StringComparison.OrdinalIgnoreCase)))
        .ToArray();
    var unidentifiedCount = allInstalled.Length - installed.Length;
    if (installed.Length == 0) return Results.Ok(new { checkedAt = DateTimeOffset.UtcNow, updates = Array.Empty<object>(),
        checkedCount = 0, unidentifiedCount, message = "Nenhum pacote com identificador compatível foi encontrado para verificar." });
    var results = new List<object>();
    foreach (var group in installed.GroupBy(item => item.Provider.Equals("bepinex", StringComparison.OrdinalIgnoreCase) ? "thunderstore" : item.Provider,
                 StringComparer.OrdinalIgnoreCase))
    {
        if (group.Key.Equals("nexus", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var mod in group)
            {
                try
                {
                    var latest = await providers.FindPackageAsync("nexus", mod.PackageId, cancellationToken);
                    var available = latest is not null && IsNewerModVersion(latest.Version, mod.Version);
                    results.Add(new { modId = mod.Id, hasUpdate = available, latestVersion = latest?.Version, latestName = latest?.Name });
                }
                catch (ProviderException ex) { results.Add(new { modId = mod.Id, hasUpdate = false, error = ex.Message }); }
                catch (HttpRequestException) { results.Add(new { modId = mod.Id, hasUpdate = false, error = "Não foi possível consultar o catálogo Nexus." }); }
                catch (TaskCanceledException) { results.Add(new { modId = mod.Id, hasUpdate = false, error = "A consulta ao Nexus expirou." }); }
            }
            continue;
        }
        IReadOnlyList<CatalogPackage> current;
        try { current = await providers.FindPackagesOnlineAsync(group.Key, group.Select(mod => mod.PackageId).ToArray(), cancellationToken); }
        catch (ProviderException ex) { results.AddRange(group.Select(mod => (object)new { modId = mod.Id, hasUpdate = false, error = ex.Message })); continue; }
        catch (HttpRequestException) { results.AddRange(group.Select(mod => (object)new { modId = mod.Id, hasUpdate = false, error = "Não foi possível consultar o catálogo." })); continue; }
        catch (TaskCanceledException) { results.AddRange(group.Select(mod => (object)new { modId = mod.Id, hasUpdate = false, error = "A consulta ao provedor expirou." })); continue; }
        foreach (var mod in group)
        {
            var latest = current.FirstOrDefault(item => item.PackageId.Equals(mod.PackageId, StringComparison.OrdinalIgnoreCase));
            var available = latest is not null && IsNewerModVersion(latest.Version, mod.Version);
            results.Add(new { modId = mod.Id, hasUpdate = available, latestVersion = latest?.Version, latestName = latest?.Name });
        }
    }
    return Results.Ok(new { checkedAt = DateTimeOffset.UtcNow, updates = results, checkedCount = installed.Length, unidentifiedCount,
        message = $"{installed.Length} pacote(s) com identificador conferido(s)." });
});

app.MapDelete("/api/instances/{id:guid}/mods/{modId:guid}", async (Guid id, Guid modId, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    if (await IsGroupRunningAsync(id, state, supervisor)) return Results.Conflict(new { error = "Pare todos os servidores deste perfil antes de remover mods." });
    var modOwnerId = await ResolveModOwnerIdAsync(id, state);
    if (modOwnerId is null) return Results.NotFound();
    try
    {
        var mod = await state.ReadAsync(data => data.InstalledMods.FirstOrDefault(item => item.InstanceId == modOwnerId && item.Id == modId));
        if (mod is null) return Results.NotFound(new { error = "O pacote não está no inventário gerenciado." });
        var result = await modPackages.UninstallAsync(modOwnerId.Value, modId);
        await state.UpdateAsync(data =>
        {
            data.Revisions.Add(CreateModRemovalHistoryRevision(id, mod, result.RemovedFiles, result.PreservedFiles));
            return true;
        });
        return Results.Ok(new { message = $"Pacote removido: {result.Removed} arquivo(s) excluído(s), {result.Preserved} arquivo(s) alterado(s) preservado(s).",
            removedFiles = result.RemovedFiles, preservedFiles = result.PreservedFiles });
    }
    catch (ModInstallException ex) { return Results.Json(new { error = ex.Message }, statusCode: (int)ex.StatusCode); }
});

app.MapGet("/api/instances/{id:guid}/configs", async (Guid id, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var profile = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == id));
    if (profile is null) return Results.NotFound();
    var configRoot = Path.GetFullPath(Path.Combine(profile.InstallPath, "BepInEx", "config"));
    if (!Directory.Exists(configRoot)) return Results.Ok(new { files = Array.Empty<string>() });
    var files = Directory.EnumerateFiles(configRoot, "*", SearchOption.AllDirectories)
        .Where(IsEditableConfig)
        .Take(5000)
        .Select(path => Path.GetRelativePath(configRoot, path).Replace('\\', '/'))
        .Order(StringComparer.OrdinalIgnoreCase).ToArray();
    return Results.Ok(new { files });
});

app.MapPost("/api/instances/{id:guid}/configs/delete-batch", async (Guid id, ConfigBatchDeleteInput? input, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    if (await IsGroupRunningAsync(id, state, supervisor))
        return Results.Conflict(new { error = "Pare todos os servidores do perfil antes de excluir arquivos BepInEx." });
    if (input?.Paths is null || input.Paths.Length is < 1 or > 5000)
        return Results.BadRequest(new { error = "Selecione entre 1 e 5.000 arquivos de configuração." });

    var profile = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == id));
    if (profile is null) return Results.NotFound();
    var selected = new List<(string RelativePath, string? FullPath)>();
    foreach (var value in input.Paths.Distinct(StringComparer.OrdinalIgnoreCase))
    {
        var relativePath = value ?? "";
        string? fullPath = null;
        try { if (!string.IsNullOrWhiteSpace(relativePath)) fullPath = SafeConfigPath(profile, relativePath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        selected.Add((relativePath, fullPath));
    }
    if (selected.Any(item => string.IsNullOrWhiteSpace(item.RelativePath) || item.FullPath is null || !IsEditableConfig(item.FullPath)))
        return Results.BadRequest(new { error = "A lista contém um caminho de configuração inválido." });

    var deleted = new List<string>();
    var failed = new List<object>();
    var revisions = new List<ConfigRevision>();
    foreach (var item in selected)
    {
        var fullPath = item.FullPath!;
        if (!File.Exists(fullPath))
        {
            failed.Add(new { path = item.RelativePath, message = "O arquivo não existe mais." });
            continue;
        }
        try
        {
            var canRollback = new FileInfo(fullPath).Length <= 1_000_000;
            var before = canRollback ? await File.ReadAllTextAsync(fullPath, context.RequestAborted) : "";
            File.Delete(fullPath);
            deleted.Add(item.RelativePath.Replace('\\', '/'));
            revisions.Add(new ConfigRevision
            {
                InstanceId = id, Kind = "bepinex-file", Target = item.RelativePath.Replace('\\', '/'),
                Summary = $"BepInEx config deleted: {Path.GetFileName(fullPath)}",
                BeforeExists = true, BeforeJson = canRollback ? ProtectHistory(before) : "",
                AfterJson = ProtectHistory(""), CanRollback = canRollback
            });
        }
        catch (IOException)
        {
            failed.Add(new { path = item.RelativePath, message = "O arquivo está em uso ou não pôde ser excluído." });
        }
        catch (UnauthorizedAccessException)
        {
            failed.Add(new { path = item.RelativePath, message = "O Windows negou acesso ao arquivo." });
        }
    }
    if (revisions.Count > 0)
        await state.UpdateAsync(data => { data.Revisions.AddRange(revisions); return true; });
    return Results.Ok(new
    {
        deleted,
        failed,
        message = failed.Count == 0
            ? $"{deleted.Count} arquivo(s) de configuração excluído(s)."
            : $"{deleted.Count} arquivo(s) excluído(s); {failed.Count} não puderam ser removidos."
    });
});

app.MapGet("/api/instances/{id:guid}/configs/{**relativePath}", async (Guid id, string relativePath, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var profile = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == id));
    if (profile is null) return Results.NotFound();
    var path = SafeConfigPath(profile, relativePath);
    if (path is null || !IsEditableConfig(path)) return Results.BadRequest(new { error = "Caminho de configuração inválido." });
    if (!File.Exists(path)) return Results.NotFound();
    string content;
    try
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        content = await reader.ReadToEndAsync(context.RequestAborted);
    }
    catch (IOException) when (supervisor.GetStatus(id) == "running")
    {
        return Results.Conflict(new { error = "O arquivo está temporariamente em uso pelo servidor. Tente abri-lo novamente em instantes." });
    }
    return Results.Ok(new { path = relativePath.Replace('\\', '/'), content, lastModified = File.GetLastWriteTimeUtc(path) });
});

app.MapDelete("/api/instances/{id:guid}/configs/{**relativePath}", async (Guid id, string relativePath, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    if (await IsGroupRunningAsync(id, state, supervisor)) return Results.Conflict(new { error = "Pare todos os servidores do perfil antes de excluir um arquivo BepInEx." });
    var profile = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == id));
    if (profile is null) return Results.NotFound();
    var path = SafeConfigPath(profile, relativePath);
    if (path is null || !IsEditableConfig(path)) return Results.BadRequest(new { error = "Caminho de configuração inválido." });
    if (!File.Exists(path)) return Results.NotFound(new { error = "O arquivo de configuração não existe mais." });
    try
    {
        var canRollback = new FileInfo(path).Length <= 1_000_000;
        var before = canRollback ? await File.ReadAllTextAsync(path, context.RequestAborted) : "";
        File.Delete(path);
        var revision = new ConfigRevision
        {
            InstanceId = id, Kind = "bepinex-file", Target = relativePath.Replace('\\', '/'),
            Summary = $"BepInEx config deleted: {Path.GetFileName(path)}",
            BeforeExists = true, BeforeJson = canRollback ? ProtectHistory(before) : "",
            AfterJson = ProtectHistory(""), CanRollback = canRollback
        };
        await state.UpdateAsync(data => { data.Revisions.Add(revision); return true; });
        return Results.Ok(new { message = $"Arquivo {Path.GetFileName(path)} excluído. O BepInEx poderá gerá-lo novamente na próxima inicialização.", revision = ToRevisionView(revision) });
    }
    catch (IOException) { return Results.Problem("Não foi possível excluir o arquivo de configuração.", statusCode: StatusCodes.Status500InternalServerError); }
    catch (UnauthorizedAccessException) { return Results.Problem("O Windows negou acesso ao arquivo de configuração.", statusCode: StatusCodes.Status403Forbidden); }
});

app.MapPut("/api/instances/{id:guid}/configs/{**relativePath}", async (Guid id, string relativePath, ConfigInput input, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    if (await IsGroupRunningAsync(id, state, supervisor)) return Results.Conflict(new { error = "Pare todos os servidores do perfil antes de alterar um arquivo BepInEx." });
    var profile = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == id));
    if (profile is null) return Results.NotFound();
    var path = SafeConfigPath(profile, relativePath);
    if (path is null || !IsEditableConfig(path)) return Results.BadRequest(new { error = "Caminho de configuração inválido." });
    if (System.Text.Encoding.UTF8.GetByteCount(input.Content) > 10 * 1024 * 1024)
        return Results.BadRequest(new { error = "O arquivo de configuração excede 10 MB." });
    var existed = File.Exists(path);
    var before = existed ? await File.ReadAllTextAsync(path) : "";
    var directory = Path.GetDirectoryName(path)!;
    Directory.CreateDirectory(directory);
    var tempPath = path + ".vhs.tmp";
    await File.WriteAllTextAsync(tempPath, input.Content);
    File.Move(tempPath, path, overwrite: true);
    var revision = new ConfigRevision
    {
        InstanceId = id, Kind = "bepinex-file", Target = relativePath.Replace('\\', '/'),
        Summary = existed ? $"BepInEx config edited: {Path.GetFileName(path)}" : $"BepInEx config created: {Path.GetFileName(path)}",
        BeforeExists = existed,
        BeforeJson = ProtectHistory(before),
        AfterJson = ProtectHistory(input.Content)
    };
    await state.UpdateAsync(data => { data.Revisions.Add(revision); return true; });
    return Results.Ok(ToRevisionView(revision));
});

app.MapGet("/api/history", async (HttpContext context, Guid? instanceId) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    if (instanceId is Guid requestedId)
    {
        var userId = auth.GetSessionUserId(context);
        var canAccess = await state.ReadAsync(data => data.Users.Any(user => user.Id == userId && user.Role == "admin") ||
            data.Instances.Any(profile => profile.Id == requestedId && profile.AllowedUserIds.Contains(userId ?? Guid.Empty)));
        if (!canAccess) return Results.NotFound();
    }
    var revisions = await state.ReadAsync(data => data.Revisions
        .Where(item => instanceId is null ? IsGlobalRevision(item) : item.InstanceId == instanceId && !item.IsGlobal)
        .OrderByDescending(item => item.CreatedAt).Take(500).Select(ToRevisionView).ToArray());
    return Results.Ok(new { revisions });
});

app.MapDelete("/api/history", async (HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    if (!await RequireAdmin(context, auth, state)) return Results.Forbid();
    var removedCount = await state.UpdateAsync(data =>
    {
        var count = data.Revisions.Count;
        data.Revisions.Clear();
        return count;
    });
    return Results.Ok(new { removedCount, message = $"Histórico limpo: {removedCount} registro(s) removido(s)." });
});

app.MapGet("/api/instances/{id:guid}/history", async (Guid id, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var userId = auth.GetSessionUserId(context);
    var canAccess = await state.ReadAsync(data => data.Users.Any(user => user.Id == userId && user.Role == "admin") ||
        data.Instances.Any(profile => profile.Id == id && profile.AllowedUserIds.Contains(userId ?? Guid.Empty)));
    if (!canAccess) return Results.NotFound();
    var revisions = await state.ReadAsync(data => data.Revisions.Where(item => item.InstanceId == id && !item.IsGlobal)
        .OrderByDescending(item => item.CreatedAt).Take(500).Select(ToRevisionView).ToArray());
    var logModToggleActions = await state.ReadAsync(data => data.Instances.First(item => item.Id == id).LogModToggleActions);
    return Results.Ok(new { revisions, logModToggleActions });
});

app.MapPut("/api/instances/{id:guid}/history/settings", async (Guid id, ProfileHistorySettingsInput input, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var updated = await state.UpdateAsync(data =>
    {
        var profile = data.Instances.FirstOrDefault(item => item.Id == id);
        if (profile is null) return false;
        profile.LogModToggleActions = input.LogModToggleActions;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    });
    return updated ? Results.Ok(new { logModToggleActions = input.LogModToggleActions }) : Results.NotFound();
});

app.MapGet("/api/instances/{id:guid}/history/{revisionId:guid}", async (Guid id, Guid revisionId, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var userId = auth.GetSessionUserId(context);
    var canAccess = await state.ReadAsync(data => data.Users.Any(user => user.Id == userId && user.Role == "admin") ||
        data.Instances.Any(profile => profile.Id == id && profile.AllowedUserIds.Contains(userId ?? Guid.Empty)));
    if (!canAccess) return Results.NotFound();
    var revision = await state.ReadAsync(data => data.Revisions.FirstOrDefault(item =>
        item.Id == revisionId && item.InstanceId == id && !item.IsGlobal));
    return revision is null ? Results.NotFound() : Results.Ok(ToRevisionDetail(revision));
});

app.MapPost("/api/history/{revisionId:guid}/rollback", async (Guid revisionId, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var revision = await state.ReadAsync(data => data.Revisions.FirstOrDefault(item => item.Id == revisionId));
    if (revision is null) return Results.NotFound();
    if (await IsGroupRunningAsync(revision.InstanceId, state, supervisor)) return Results.Conflict(new { error = "Pare todos os servidores do perfil antes de restaurar uma configuração." });

    if (revision.Kind == "server-profile")
    {
        if (string.IsNullOrWhiteSpace(revision.BeforeJson)) return Results.BadRequest(new { error = "Esta revisão não possui um estado anterior para restaurar." });
        var oldProfile = JsonSerializer.Deserialize<ServerProfile>(revision.BeforeJson);
        if (oldProfile is null) return Results.Problem("O snapshot do servidor está inválido.", statusCode: 500);
        var result = await state.UpdateAsync(data =>
        {
            var current = data.Instances.FirstOrDefault(item => item.Id == revision.InstanceId);
            if (current is null) return (Found: false, NewRevision: (ConfigRevision?)null, Restored: (ServerProfile?)null);
            var oldCurrent = SerializeProfile(current);
            oldProfile.UpdatedAt = DateTimeOffset.UtcNow;
            data.Instances[data.Instances.IndexOf(current)] = oldProfile;
            var audit = NewProfileRevision(oldProfile.Id, $"Rollback: {revision.Summary}", oldCurrent, SerializeProfile(oldProfile));
            data.Revisions.Add(audit);
            return (Found: true, NewRevision: audit, Restored: oldProfile);
        });
        return !result.Found ? Results.NotFound() : Results.Ok(new { message = "Configurações do servidor restauradas.", revision = ToRevisionView(result.NewRevision!) });
    }

    if (revision.Kind == "bepinex-file")
        return Results.BadRequest(new { error = "As revisões de arquivos BepInEx só podem ser restauradas linha a linha pelo modal de antes e depois." });
    return Results.BadRequest(new { error = "Tipo de revisão não suportado." });
});

app.MapPost("/api/instances/{id:guid}/history/{revisionId:guid}/restore-lines", async (Guid id, Guid revisionId, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var revision = await state.ReadAsync(data => data.Revisions.FirstOrDefault(item =>
        item.Id == revisionId && item.InstanceId == id && !item.IsGlobal));
    if (revision is null) return Results.NotFound();
    if (revision.Kind != "bepinex-file" || !revision.CanRollback)
        return Results.BadRequest(new { error = "Esta revisão não permite restaurar linhas." });
    if (await IsGroupRunningAsync(id, state, supervisor))
        return Results.Conflict(new { error = "Pare todos os servidores do perfil antes de restaurar linhas de configuração." });

    var profile = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == id));
    if (profile is null) return Results.NotFound();
    var path = SafeConfigPath(profile, revision.Target);
    if (path is null || !IsEditableConfig(path))
        return Results.BadRequest(new { error = "O caminho salvo na revisão não é mais seguro." });
    if (!revision.BeforeExists)
        return Results.Conflict(new { error = "Esta revisão criou o arquivo e não possui linhas anteriores para restaurar parcialmente." });
    var fileMissing = !File.Exists(path);
    if (!fileMissing && new FileInfo(path).Length > 1_000_000)
        return Results.Conflict(new { error = "O arquivo atual excede o limite seguro de 1 MB para restauração linha a linha." });

    var beforeSnapshot = TryUnprotectHistory(revision.BeforeJson);
    var afterSnapshot = TryUnprotectHistory(revision.AfterJson);
    if (beforeSnapshot.StartsWith("(O conteúdo protegido", StringComparison.Ordinal) ||
        afterSnapshot.StartsWith("(O conteúdo protegido", StringComparison.Ordinal))
        return Results.Conflict(new { error = "O DSS não conseguiu abrir o snapshot desta revisão. Nenhuma alteração foi feita." });

    if (fileMissing && afterSnapshot.Length == 0)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var recreateTempPath = path + ".dss-restore-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(recreateTempPath, beforeSnapshot, new System.Text.UTF8Encoding(false));
            if (File.Exists(path)) return Results.Conflict(new { error = "O arquivo reapareceu durante a restauração. Abra novamente a comparação antes de continuar." });
            File.Move(recreateTempPath, path);
        }
        finally
        {
            if (File.Exists(recreateTempPath)) File.Delete(recreateTempPath);
        }
        var recreateAudit = new ConfigRevision
        {
            InstanceId = id, Kind = "bepinex-file", Target = revision.Target,
            Summary = $"Arquivo recriado: {Path.GetFileName(path)}", BeforeExists = false,
            BeforeJson = ProtectHistory(""), AfterJson = ProtectHistory(beforeSnapshot), CanRollback = true
        };
        await state.UpdateAsync(data => { data.Revisions.Add(recreateAudit); return true; });
        return Results.Ok(new { message = $"Arquivo {Path.GetFileName(path)} recriado com o conteúdo anterior à exclusão.", revision = ToRevisionView(recreateAudit) });
    }

    var currentRaw = fileMissing ? "" : await File.ReadAllTextAsync(path);
    var currentNormalized = NormalizeConfigLines(currentRaw);
    var beforeLines = SplitConfigLines(NormalizeConfigLines(beforeSnapshot));
    var afterLines = SplitConfigLines(NormalizeConfigLines(afterSnapshot));
    var currentLines = SplitConfigLines(currentNormalized);
    if (fileMissing && afterSnapshot.Length != 0)
        return Results.Conflict(new { error = "O arquivo atual foi removido depois desta revisão e tinha conteúdo posterior. A restauração linha a linha foi bloqueada para evitar sobrescrever uma exclusão diferente." });
    if (!fileMissing && currentLines.Length != afterLines.Length)
        return Results.Conflict(new { error = $"A restauração foi bloqueada: o arquivo atual tem {currentLines.Length} linhas e a revisão esperava {afterLines.Length}. Compare as alterações antes de tentar novamente." });
    if (((long)beforeLines.Length + 1) * ((long)afterLines.Length + 1) > 4_000_000)
        return Results.Conflict(new { error = "A revisão é grande demais para identificar trechos de linha com segurança. O arquivo não foi alterado." });

    var hunks = ComputeLineChangeHunks(beforeLines, afterLines);
    if (hunks.Count == 0)
        return Results.BadRequest(new { error = "Esta revisão não contém linhas diferentes para restaurar." });
    foreach (var hunk in hunks)
    {
        if (!fileMissing && (hunk.AfterStart < 0 || hunk.AfterStart + hunk.AfterLines.Length > currentLines.Length ||
            !currentLines.Skip(hunk.AfterStart).Take(hunk.AfterLines.Length).SequenceEqual(hunk.AfterLines, StringComparer.Ordinal))
           )
            return Results.Conflict(new { error = "A restauração foi bloqueada: uma ou mais linhas alteradas já não têm o mesmo nome, atributo ou texto da revisão. Nenhuma linha foi modificada." });

        if (hunk.AfterLines.Length == 0 && afterLines.Length > 0)
        {
            var hasPrevious = hunk.AfterStart > 0;
            var hasNext = hunk.AfterStart < afterLines.Length;
            var previousMatches = !hasPrevious || currentLines[hunk.AfterStart - 1] == afterLines[hunk.AfterStart - 1];
            var nextMatches = !hasNext || currentLines[hunk.AfterStart] == afterLines[hunk.AfterStart];
            if ((!hasPrevious && !hasNext) || !previousMatches || !nextMatches)
                return Results.Conflict(new { error = "A restauração foi bloqueada: não foi possível confirmar as linhas vizinhas ao trecho removido. Nenhuma linha foi modificada." });
        }
    }

    var restoredLines = currentLines.ToList();
    foreach (var hunk in hunks.OrderByDescending(item => item.AfterStart))
    {
        restoredLines.RemoveRange(hunk.AfterStart, hunk.AfterLines.Length);
        restoredLines.InsertRange(hunk.AfterStart, hunk.BeforeLines);
    }
    var newline = currentRaw.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    var restored = string.Join(newline, restoredLines);
    var tempPath = path + ".dss-restore-" + Guid.NewGuid().ToString("N") + ".tmp";
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(tempPath, restored, new System.Text.UTF8Encoding(false));
        if ((fileMissing && File.Exists(path)) || (!fileMissing && (!File.Exists(path) || !string.Equals(await File.ReadAllTextAsync(path), currentRaw, StringComparison.Ordinal))))
            return Results.Conflict(new { error = "O arquivo mudou durante a restauração. Nenhuma linha foi gravada; abra a revisão novamente e compare o estado atual." });
        File.Move(tempPath, path, overwrite: true);
    }
    finally
    {
        if (File.Exists(tempPath)) File.Delete(tempPath);
    }

    var audit = new ConfigRevision
    {
        InstanceId = id, Kind = "bepinex-file", Target = revision.Target,
        Summary = $"Linhas restauradas: {revision.Summary}", BeforeExists = true,
        BeforeJson = ProtectHistory(currentRaw), AfterJson = ProtectHistory(restored), CanRollback = true
    };
    await state.UpdateAsync(data => { data.Revisions.Add(audit); return true; });
    return Results.Ok(new { message = fileMissing ? $"Arquivo recriado e {hunks.Count} trecho(s) restaurado(s)." : $"{hunks.Count} trecho(s) restaurado(s); as demais linhas foram preservadas.", revision = ToRevisionView(audit) });
});

app.MapGet("/api/providers", (HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    return Results.Ok(new { providers = providers.GetProviders() });
});

app.MapGet("/api/providers/status", async (HttpContext context, CancellationToken cancellationToken) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var statuses = await providers.CheckAvailabilityAsync(cancellationToken);
    return Results.Ok(new
    {
        providers = statuses.Select(status => new
        {
            id = status.Provider.Id,
            name = status.Provider.Name,
            state = status.Provider.State,
            detail = status.Provider.Detail,
            accountRequired = status.Provider.AccountRequired,
            canInstall = status.Provider.CanInstall,
            connectivity = status.Connectivity,
            checkedAt = status.CheckedAt,
            message = status.Message
        })
    });
});

app.MapGet("/api/providers/settings", async (HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var configured = await Task.WhenAll(new[] { "nexus", "curseforge" }.Select(async id => new
    {
        id,
        configured = await providerCredentials.IsConfiguredAsync(id)
    }));
    return Results.Ok(new { providers = configured });
});

app.MapPut("/api/providers/{provider}/settings", async (string provider, ProviderCredentialInput input, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    if (provider is not ("nexus" or "curseforge")) return Results.BadRequest(new { error = "Este provedor não aceita chave de API." });
    if (!input.Clear && string.IsNullOrWhiteSpace(input.ApiKey))
        return Results.BadRequest(new { error = "Informe uma chave ou escolha removê-la." });
    if (input.ApiKey?.Length > 1024) return Results.BadRequest(new { error = "A chave informada excede o tamanho permitido." });
    await providerCredentials.SetAsync(provider, input.Clear ? null : input.ApiKey);
    return Results.Ok(new { id = provider, configured = !input.Clear, message = input.Clear ? "Chave removida." : "Chave protegida e salva neste DSS." });
});

app.MapPost("/api/integrations/nexus/download-link", async (NexusDownloadAuthorizationInput input, HttpContext context) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(input.Link) || input.Link.Length > 2048 ||
        !Uri.TryCreate(input.Link.Trim(), UriKind.Absolute, out var uri) ||
        !string.Equals(uri.Scheme, "nxm", StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(uri.Host, "valheim", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Cole um link NXM válido do jogo Valheim, copiado em ‘Download with Manager’." });

    var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (segments.Length != 4 || !segments[0].Equals("mods", StringComparison.OrdinalIgnoreCase) ||
        !segments[2].Equals("files", StringComparison.OrdinalIgnoreCase) ||
        !long.TryParse(segments[1], out var modId) || modId <= 0 ||
        !long.TryParse(segments[3], out var fileId) || fileId <= 0)
        return Results.BadRequest(new { error = "O link precisa apontar para um arquivo específico: nxm://valheim/mods/{id}/files/{id}." });

    var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2))
        .Where(parts => parts.Length == 2)
        .Select(parts => new KeyValuePair<string, string>(Uri.UnescapeDataString(parts[0].Replace('+', ' ')),
            Uri.UnescapeDataString(parts[1].Replace('+', ' '))))
        .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);
    if (!query.TryGetValue("key", out var key) || string.IsNullOrWhiteSpace(key) || key.Length > 512 ||
        !query.TryGetValue("expires", out var expiresText) || !long.TryParse(expiresText, out var expires))
        return Results.BadRequest(new { error = "Esse link NXM não contém a autorização temporária. Copie novamente ‘Download with Manager’ para o arquivo." });

    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    if (expires <= now || expires > now + (90L * 24 * 60 * 60))
        return Results.BadRequest(new { error = "A autorização desse link expirou ou tem uma validade inválida. Gere um novo link no Nexus." });

    nexusDownloadAuthorizations.Set(modId.ToString(), fileId.ToString(), new NexusDownloadAuthorization(key, expires));
    return Results.Ok(new { modId, fileId, expiresAt = DateTimeOffset.FromUnixTimeSeconds(expires), message = "Link Nexus autorizado temporariamente para esse arquivo." });
});

app.MapPost("/api/providers/{provider}/test", async (string provider, HttpContext context, CancellationToken cancellationToken) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    try
    {
        var result = await providers.TestCredentialAsync(provider, cancellationToken);
        return Results.Ok(new { id = provider, connectivity = result.Connectivity, checkedAt = result.CheckedAt, message = result.Message });
    }
    catch (ProviderException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapGet("/api/catalog", async (string provider, string? q, string? fields, int? page, int? pageSize, string? sort, string? direction, bool? includeTotal, HttpContext context, CancellationToken cancellationToken) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    try
    {
        var selectedFields = (fields ?? "title,creator,description,packageId").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = await providers.SearchAsync(provider, q ?? "", selectedFields, page ?? 1, pageSize ?? 24, sort ?? "downloads", direction ?? "desc", cancellationToken, includeTotal ?? true);
        return Results.Ok(new { provider, query = q ?? "", items = result.Items, total = result.Total,
            page = result.Page, totalPages = result.TotalPages, pageSize = result.PageSize });
    }
    catch (ProviderException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable); }
    catch (HttpRequestException) { return Results.Json(new { error = "Não foi possível acessar este catálogo agora. Tente novamente mais tarde." }, statusCode: StatusCodes.Status502BadGateway); }
    catch (TaskCanceledException) { return Results.Json(new { error = "A consulta ao catálogo expirou." }, statusCode: StatusCodes.Status504GatewayTimeout); }
    catch (JsonException) { return Results.Json(new { error = "O provedor retornou um catálogo em formato inesperado; a resposta não foi usada." }, statusCode: StatusCodes.Status502BadGateway); }
});

app.MapGet("/api/catalog/count", async (string provider, string? q, string? fields, int? pageSize, HttpContext context, CancellationToken cancellationToken) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    try
    {
        var selectedFields = (fields ?? "title,creator,description,packageId").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = await providers.CountAsync(provider, q ?? "", selectedFields, pageSize ?? 24, cancellationToken);
        return Results.Ok(new { provider, query = q ?? "", total = result.Total, totalPages = result.TotalPages, pageSize = result.PageSize });
    }
    catch (ProviderException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable); }
    catch (HttpRequestException) { return Results.Json(new { error = "Não foi possível acessar este catálogo agora. Tente novamente mais tarde." }, statusCode: StatusCodes.Status502BadGateway); }
    catch (TaskCanceledException) { return Results.Json(new { error = "A consulta ao catálogo expirou." }, statusCode: StatusCodes.Status504GatewayTimeout); }
    catch (JsonException) { return Results.Json(new { error = "O provedor retornou um catálogo em formato inesperado; a resposta não foi usada." }, statusCode: StatusCodes.Status502BadGateway); }
});

app.MapGet("/api/catalog/details/{provider}", async (string provider, string? ids, HttpContext context, CancellationToken cancellationToken) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    var packageIds = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase).Take(24).ToArray();
    if (packageIds.Length == 0) return Results.BadRequest(new { error = "Informe ao menos um identificador de mod." });
    try
    {
        var items = await providers.FindPackagesOnlineAsync(provider, packageIds, cancellationToken);
        return Results.Ok(new { items });
    }
    catch (ProviderException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable); }
    catch (HttpRequestException) { return Results.Json(new { error = "Não foi possível consultar o provedor dos mods agora." }, statusCode: StatusCodes.Status502BadGateway); }
    catch (TaskCanceledException) { return Results.Json(new { error = "A consulta ao provedor expirou." }, statusCode: StatusCodes.Status504GatewayTimeout); }
    catch (JsonException) { return Results.Json(new { error = "O provedor retornou dados em formato inesperado." }, statusCode: StatusCodes.Status502BadGateway); }
});

app.MapGet("/api/catalog/{provider}/{packageId}", async (string provider, string packageId, HttpContext context, CancellationToken cancellationToken) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    try
    {
        var package = await providers.FindPackageOnlineAsync(provider, packageId, cancellationToken);
        return package is null ? Results.NotFound(new { error = "Este mod não foi encontrado no provedor." }) : Results.Ok(package);
    }
    catch (ProviderException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable); }
    catch (HttpRequestException) { return Results.Json(new { error = "Não foi possível consultar o provedor do mod agora." }, statusCode: StatusCodes.Status502BadGateway); }
    catch (TaskCanceledException) { return Results.Json(new { error = "A consulta ao provedor expirou." }, statusCode: StatusCodes.Status504GatewayTimeout); }
    catch (JsonException) { return Results.Json(new { error = "O provedor retornou dados em formato inesperado." }, statusCode: StatusCodes.Status502BadGateway); }
});

app.MapGet("/api/catalog/{provider}/{packageId}/versions", async (string provider, string packageId, HttpContext context, CancellationToken cancellationToken) =>
{
    if (!RequireAuth(context, auth)) return Results.Unauthorized();
    try
    {
        var package = await providers.FindPackageOnlineAsync(provider, packageId, cancellationToken);
        if (package is null) return Results.NotFound(new { error = "Pacote não encontrado no catálogo atual." });
        var versions = (package.Versions ?? []).OrderByDescending(item => item.Version, StringComparer.OrdinalIgnoreCase).ToArray();
        var availableVersions = versions.Select(item => (object)new { version = item.Version, downloads = item.Downloads, createdAt = item.CreatedAt }).ToArray();
        if (availableVersions.Length == 0 && !string.IsNullOrWhiteSpace(package.Version))
            availableVersions = [new { version = package.Version, downloads = package.TotalDownloads, createdAt = package.UpdatedAt }];
        return Results.Ok(new { packageId = package.PackageId, name = package.Name, currentVersion = package.Version,
            versions = availableVersions });
    }
    catch (ProviderException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable); }
    catch (HttpRequestException) { return Results.Json(new { error = "Não foi possível acessar o catálogo para consultar as versões." }, statusCode: StatusCodes.Status502BadGateway); }
    catch (TaskCanceledException) { return Results.Json(new { error = "A consulta de versões expirou." }, statusCode: StatusCodes.Status504GatewayTimeout); }
    catch (JsonException) { return Results.Json(new { error = "O provedor retornou versões em formato inesperado." }, statusCode: StatusCodes.Status502BadGateway); }
});

app.MapFallbackToFile("index.html");

app.Lifetime.ApplicationStarted.Register(() =>
{
    if (argsList.Contains("--no-browser") || Environment.GetEnvironmentVariable("DSS_NO_BROWSER") == "1" || Environment.GetEnvironmentVariable("VALHEIM_STUDIO_NO_BROWSER") == "1") return;
    try
    {
    var browserUrl = listenMode == "local" ? listenUrl : $"http://127.0.0.1:{listenPort}";
        Process.Start(new ProcessStartInfo(browserUrl) { UseShellExecute = true });
    }
    catch { /* The web UI remains available in a browser opened by the user. */ }
});

await app.RunAsync();

static async Task<byte[]> ReadR2ModmanUploadAsync(IFormFile? file, string? code,
    R2ModmanProfileService importer, CancellationToken cancellationToken)
{
    var hasFile = file is { Length: > 0 };
    var hasCode = !string.IsNullOrWhiteSpace(code);
    if (hasFile && hasCode) throw new ArgumentException("Escolha um arquivo .r2z ou um código, não os dois.");
    if (hasFile)
    {
        if (!Path.GetExtension(file!.FileName).Equals(".r2z", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Selecione um arquivo de perfil com extensão .r2z.");
        if (file.Length > 25L * 1024 * 1024) throw new InvalidDataException("O arquivo .r2z excede o limite de 25 MB.");
        await using var source = file.OpenReadStream();
        using var destination = new MemoryStream((int)file.Length);
        await source.CopyToAsync(destination, cancellationToken);
        return destination.ToArray();
    }
    if (hasCode) return await importer.DownloadCodeAsync(code!, cancellationToken);
    throw new ArgumentException("Selecione um arquivo .r2z ou informe o código do perfil.");
}

static Task<Guid?> ResolveModOwnerIdAsync(Guid instanceId, StateStore state) => state.ReadAsync(data =>
{
    var profile = data.Instances.FirstOrDefault(item => item.Id == instanceId);
    return profile?.Id;
});

static Task<bool> IsGroupRunningAsync(Guid instanceId, StateStore state, InstanceSupervisor supervisor) => state.ReadAsync(data =>
{
    var profile = data.Instances.FirstOrDefault(item => item.Id == instanceId);
    if (profile is null) return false;
    return profile.Servers.Any(server => supervisor.GetStatus(server.Id) == "running");
});

static Task<bool> IsGroupSteamCmdRunningAsync(Guid instanceId, StateStore state, SteamCmdService steamCmd) => state.ReadAsync(data =>
{
    var profile = data.Instances.FirstOrDefault(item => item.Id == instanceId);
    if (profile is null) return false;
    return steamCmd.GetOperation(profile.Id)?.Running == true;
});

static async Task SetRunAsAdministratorAsync(string configPath, bool enabled)
{
    JsonObject config;
    if (File.Exists(configPath))
    {
        var parsed = JsonNode.Parse(await File.ReadAllTextAsync(configPath));
        config = parsed as JsonObject ?? throw new InvalidDataException("dss-config.json precisa conter um objeto JSON na raiz.");
    }
    else config = new JsonObject();

    config["runAsAdministrator"] = enabled;
    var temporaryPath = configPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try
    {
        await File.WriteAllTextAsync(temporaryPath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, configPath, overwrite: true);
    }
    finally
    {
        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
    }
}

static async Task SetDssConfigValueAsync(string configPath, string propertyName, JsonNode? value)
{
    JsonObject config;
    if (File.Exists(configPath))
    {
        var parsed = JsonNode.Parse(await File.ReadAllTextAsync(configPath));
        config = parsed as JsonObject ?? throw new InvalidDataException("dss-config.json precisa conter um objeto JSON na raiz.");
    }
    else config = new JsonObject();

    config[propertyName] = value?.DeepClone();
    var temporaryPath = configPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try
    {
        await File.WriteAllTextAsync(temporaryPath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, configPath, overwrite: true);
    }
    finally
    {
        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
    }
}

[SupportedOSPlatform("windows")]
static void StartUnelevatedProcess(string executablePath, string arguments, string workingDirectory)
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("A troca de permissões está disponível somente no Windows.");
    var shellType = Type.GetTypeFromProgID("Shell.Application");
    if (shellType is null) throw new InvalidOperationException("O shell do Windows não está disponível para iniciar o painel sem elevação.");
    var shell = Activator.CreateInstance(shellType)
        ?? throw new InvalidOperationException("Não foi possível acessar o shell do Windows para iniciar o painel sem elevação.");
    try
    {
        shellType.InvokeMember("ShellExecute", BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
            binder: null, target: shell, args: [executablePath, arguments, workingDirectory, "open", 1]);
    }
    finally
    {
        if (Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
    }
}

static async Task RestoreDssConfigurationAsync(string configPath, string? originalConfig)
{
    if (originalConfig is null)
    {
        if (File.Exists(configPath)) File.Delete(configPath);
        return;
    }
    await File.WriteAllTextAsync(configPath, originalConfig);
}

static bool RequireAuth(HttpContext context, AuthService auth)
{
    if (auth.IsAuthenticated(context)) return true;
    context.Response.Headers.WWWAuthenticate = "Session";
    return false;
}

static async Task<bool> RequireAdmin(HttpContext context, AuthService auth, StateStore state)
{
    var userId = auth.GetSessionUserId(context);
    if (userId is null) return false;
    return await state.ReadAsync(data => data.Users.Any(user => user.Id == userId && user.Role == "admin"));
}

static async Task<bool> RequireProfileEditor(Guid profileId, HttpContext context, AuthService auth, StateStore state)
{
    var userId = auth.GetSessionUserId(context);
    if (userId is null) return false;
    return await state.ReadAsync(data =>
    {
        var user = data.Users.FirstOrDefault(item => item.Id == userId);
        if (user?.Role == "admin") return true;
        if (user?.Role != "user") return false;
        var profile = data.Instances.FirstOrDefault(item => item.Id == profileId);
        if (profile is null) return false;
        if (profile.UserAccess.TryGetValue(user.Id, out var permission)) return permission == "edit";
        return profile.AllowedUserIds.Contains(user.Id);
    });
}

static async Task<bool> RequireProfileOperator(Guid profileId, HttpContext context, AuthService auth, StateStore state)
{
    var userId = auth.GetSessionUserId(context);
    if (userId is null) return false;
    return await state.ReadAsync(data =>
    {
        var user = data.Users.FirstOrDefault(item => item.Id == userId);
        if (user?.Role == "admin") return true;
        if (user?.Role != "user") return false;
        var profile = data.Instances.FirstOrDefault(item => item.Id == profileId);
        if (profile is null) return false;
        if (profile.UserAccess.TryGetValue(user.Id, out var permission)) return permission is "edit" or "operate";
        return profile.AllowedUserIds.Contains(user.Id);
    });
}

static string QuoteConsoleArgument(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

static string? ValidateProfile(ProfileInput input, bool isUpdate = false)
{
    if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 60) return "Informe um nome de instância com até 60 caracteres.";
    if (string.IsNullOrWhiteSpace(input.ServerName) || input.ServerName.Length > 60) return "O nome público do servidor precisa ter entre 1 e 60 caracteres.";
    if (string.IsNullOrWhiteSpace(input.WorldName) || input.WorldName.Length > 40) return "O nome do mundo precisa ter entre 1 e 40 caracteres.";
    if (!string.IsNullOrWhiteSpace(input.GamePassword) && input.GamePassword.Trim().Length < 5) return "A senha de jogo precisa ter pelo menos 5 caracteres.";
    if (input.GamePassword?.Trim().Length > 100) return "A senha de jogo é longa demais.";
    if (input.Port is < 1024 or > 65534) return "Use uma porta entre 1024 e 65534.";
    if (input.InstallPath is null || input.InstallPath.Length > 400) return "O caminho de instalação é inválido ou longo demais.";
    if (input.SaveDirectory is { Length: > 400 }) return "O caminho da pasta de saves não pode passar de 400 caracteres.";
    if (!string.IsNullOrWhiteSpace(input.SaveDirectory) && !Path.IsPathFullyQualified(input.SaveDirectory)) return "Informe um caminho completo para a pasta de saves.";
    if (input.GameId is { Length: > 50 }) return "O identificador do jogo é inválido.";
    if (input.ExtraArguments is { Count: > 40 } || input.ExtraArguments?.Any(value => value is null || value.Length > 256 || value.Contains('\n') || value.Contains('\r')) == true)
        return "Use até 40 parâmetros adicionais, um por linha, com no máximo 256 caracteres cada.";
    if (input.GameSettings is { Count: > 100 } || input.GameSettings?.Any(item => item.Key.Length > 80 || item.Value.ValueKind == JsonValueKind.Undefined) == true)
        return "As configurações específicas do jogo não estão em um formato válido.";
    if (string.Equals(input.GameId, "valheim", StringComparison.OrdinalIgnoreCase) && input.GameSettings is { } valheimSettings)
    {
        if (valheimSettings.TryGetValue("serverBranch", out var serverBranch))
        {
            var branchId = serverBranch.ValueKind == JsonValueKind.String ? serverBranch.GetString() : null;
            if (string.IsNullOrWhiteSpace(branchId) || branchId.Length > 80 || !branchId.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-'))
                return "A branch do servidor precisa usar apenas letras, números, ponto, hífen ou sublinhado.";
        }
        foreach (var (key, min, max) in new[] { ("saveInterval", 60, 86400), ("backups", 0, 100), ("backupShort", 60, 1209600), ("backupLong", 60, 1209600) })
            if (valheimSettings.TryGetValue(key, out var number) && (number.ValueKind != JsonValueKind.Number || !number.TryGetInt32(out var parsed) || parsed < min || parsed > max))
                return $"O campo {key} precisa estar entre {min} e {max}.";
        foreach (var key in new[] { "logFile", "instanceId" })
            if (valheimSettings.TryGetValue(key, out var text) && (text.ValueKind != JsonValueKind.String || (text.GetString()?.Length ?? 0) > 400 || text.GetString()!.Any(char.IsControl)))
                return $"O campo {key} tem valor inválido.";
        var presets = new HashSet<string>(["normal", "casual", "easy", "hard", "hardcore", "immersive", "hammer"], StringComparer.OrdinalIgnoreCase);
        if (valheimSettings.TryGetValue("preset", out var preset) && (preset.ValueKind != JsonValueKind.String || (!string.IsNullOrWhiteSpace(preset.GetString()) && !presets.Contains(preset.GetString()!))))
            return "A predefinição de mundo do Valheim não é válida.";
        var modifiers = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["combat"] = new(["veryeasy", "easy", "hard", "veryhard"], StringComparer.OrdinalIgnoreCase),
            ["deathPenalty"] = new(["casual", "veryeasy", "easy", "hard", "hardcore"], StringComparer.OrdinalIgnoreCase),
            ["resources"] = new(["muchless", "less", "more", "muchmore", "most"], StringComparer.OrdinalIgnoreCase),
            ["raids"] = new(["none", "muchless", "less", "more", "muchmore"], StringComparer.OrdinalIgnoreCase),
            ["portals"] = new(["casual", "hard", "veryhard"], StringComparer.OrdinalIgnoreCase)
        };
        if (valheimSettings.TryGetValue("modifiers", out var configuredModifiers))
        {
            if (configuredModifiers.ValueKind != JsonValueKind.Object) return "Os modificadores do mundo precisam ser um objeto válido.";
            foreach (var modifier in configuredModifiers.EnumerateObject())
                if (!modifiers.TryGetValue(modifier.Name, out var allowed) || modifier.Value.ValueKind != JsonValueKind.String ||
                    (!string.IsNullOrWhiteSpace(modifier.Value.GetString()) && !allowed.Contains(modifier.Value.GetString()!)))
                    return $"O valor do modificador {modifier.Name} não é válido.";
        }
        var worldKeys = new HashSet<string>(["nobuildcost", "playerevents", "passivemobs", "nomap"], StringComparer.OrdinalIgnoreCase);
        if (valheimSettings.TryGetValue("setKeys", out var setKeys) && (setKeys.ValueKind != JsonValueKind.Array ||
            setKeys.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || !worldKeys.Contains(item.GetString() ?? ""))))
            return "As opções de mundo selecionadas não são válidas.";
    }
    return null;
}

static bool PortConflict(IEnumerable<ServerProfile> profiles, int port, string? gameId, Guid? exceptId, GameAdapterRegistry adapters)
{
    var requestedSpan = adapters.PortSpan(gameId);
    return profiles.SelectMany(profile => profile.Servers.Select(profile.WithServer))
        .Any(server => server.Id != exceptId && PortRangesOverlap(port, requestedSpan, server.Port, adapters.PortSpan(server.GameId)));
}

static bool SavePathConflict(IEnumerable<ServerProfile> profiles, ServerProfile candidateProfile, string dataDirectory, Guid? exceptId)
{
    var candidatePath = NormalizeSavePath(SavePathResolver.Resolve(candidateProfile, dataDirectory));
    var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    return profiles.SelectMany(profile => profile.Servers.Select(profile.WithServer))
        .Any(server => server.Id != exceptId && string.Equals(server.WorldName, candidateProfile.WorldName, pathComparison) &&
            string.Equals(NormalizeSavePath(SavePathResolver.Resolve(server, dataDirectory)), candidatePath, pathComparison));
}

static bool PortRangesOverlap(int firstPort, int firstSpan, int secondPort, int secondSpan) =>
    firstPort < secondPort + secondSpan && secondPort < firstPort + firstSpan;

static string NormalizeSavePath(string value) => Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

static ServerProfile FromInput(ProfileInput input, ServerProfile? old = null)
{
    var profile = new ServerProfile
    {
        Id = old?.Id ?? Guid.NewGuid(), GroupId = old?.GroupId ?? Guid.Empty,
        GameId = string.IsNullOrWhiteSpace(input.GameId) ? old?.GameId ?? "valheim" : input.GameId.Trim().ToLowerInvariant(),
        Name = input.Name.Trim(), InstallPath = input.InstallPath.Trim(), SaveDirectory = (input.SaveDirectory ?? old?.SaveDirectory ?? "").Trim(),
        UseInstallPathSaves = input.UseInstallPathSaves ?? old?.UseInstallPathSaves ?? string.IsNullOrWhiteSpace(input.SaveDirectory),
        LogModToggleActions = old?.LogModToggleActions ?? false,
        ServerName = input.ServerName.Trim(), WorldName = input.WorldName.Trim(), Port = input.Port,
        IsPublic = input.IsPublic, Crossplay = input.Crossplay,
        ExtraArguments = input.ExtraArguments?.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).ToList() ?? [],
        GameSettings = input.GameSettings?.ToDictionary(item => item.Key, item => item.Value.Clone(), StringComparer.OrdinalIgnoreCase)
            ?? old?.GameSettings ?? new(StringComparer.OrdinalIgnoreCase),
        InstalledBuildId = old?.InstalledBuildId ?? "", LatestBuildId = old?.LatestBuildId ?? "",
        LastUpdateCheck = old?.LastUpdateCheck, UpdateCheckError = old?.UpdateCheckError ?? "",
        GamePassword = string.IsNullOrWhiteSpace(input.GamePassword) && old is not null
            ? SafeUnprotect(old.ProtectedPassword) : input.GamePassword ?? "",
        CreatedAt = old?.CreatedAt ?? DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };
    if (profile.GroupId == Guid.Empty) profile.GroupId = profile.Id;
    profile.AllowedUserIds = old?.AllowedUserIds.ToList() ?? [];
    profile.UserAccess = old is null ? [] : new Dictionary<Guid, string>(old.UserAccess);
    profile.ProtectedPassword = string.IsNullOrWhiteSpace(profile.GamePassword) ? "" : SecretProtection.Protect(profile.GamePassword);
    profile.Servers = old?.Servers.ToList() ?? [];
    if (old is null)
        profile.Servers.Add(new ServerEndpoint
        {
            Id = profile.Id, Name = profile.Name, ServerName = profile.ServerName, WorldName = profile.WorldName, Port = profile.Port,
            SaveDirectory = profile.SaveDirectory, UseInstallPathSaves = profile.UseInstallPathSaves, IsPublic = profile.IsPublic,
            Crossplay = profile.Crossplay, ExtraArguments = profile.ExtraArguments.ToList(),
            GameSettings = profile.GameSettings.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase),
            ProtectedPassword = profile.ProtectedPassword, CreatedAt = profile.CreatedAt, UpdatedAt = profile.UpdatedAt
        });
    return profile;
}

static void ApplyEndpointFields(ServerProfile profile, ServerEndpoint endpoint)
{
    profile.ServerName = endpoint.ServerName;
    profile.WorldName = endpoint.WorldName;
    profile.Port = endpoint.Port;
    profile.SaveDirectory = endpoint.SaveDirectory;
    profile.UseInstallPathSaves = endpoint.UseInstallPathSaves;
    profile.IsPublic = endpoint.IsPublic;
    profile.Crossplay = endpoint.Crossplay;
    profile.ExtraArguments = endpoint.ExtraArguments.ToList();
    profile.ProtectedPassword = endpoint.ProtectedPassword;
}

static bool JsonElementEquals(JsonElement? left, JsonElement? right) =>
    left.HasValue == right.HasValue && (!left.HasValue || JsonElement.DeepEquals(left.Value, right!.Value));

static string SafeUnprotect(string value)
{
    try { return SecretProtection.Unprotect(value); }
    catch { return ""; }
}

static string ProfileImagePath(string dataDirectory, Guid id) => Path.Combine(dataDirectory, "profile-images", $"{id:N}.jpg");
static string UserProfileImagePath(string dataDirectory, Guid id) => Path.Combine(dataDirectory, "user-profile-images", $"{id:N}.jpg");

static string? GetUserImageUrl(string dataDirectory, Guid id)
{
    var imagePath = UserProfileImagePath(dataDirectory, id);
    return File.Exists(imagePath) ? $"/api/users/{id}/image?v={File.GetLastWriteTimeUtc(imagePath).Ticks}" : null;
}

static void DeleteProfileFiles(ServerProfile profile, IReadOnlyList<ServerProfile> profiles, string dataDirectory, string applicationRoot, bool deleteServerFiles, bool deleteSaves)
{
    if (!deleteServerFiles && !deleteSaves) return;
    var endpoints = profile.Servers.Count > 0 ? profile.Servers.Select(profile.WithServer).ToArray() : [profile];
    var saves = endpoints.Select(endpoint => (Endpoint: endpoint, Path: NormalizeDeletionPath(SavePathResolver.Resolve(endpoint, dataDirectory))))
        .DistinctBy(item => item.Path, PathComparer()).ToArray();
    var otherEndpoints = profiles.Where(item => item.Id != profile.Id)
        .SelectMany(item => item.Servers.Count > 0 ? item.Servers.Select(item.WithServer) : [item]).ToArray();

    string? installationPath = null;
    if (deleteServerFiles)
    {
        if (string.IsNullOrWhiteSpace(profile.InstallPath) || !Path.IsPathFullyQualified(profile.InstallPath))
            throw new InvalidOperationException("A pasta do servidor não é válida; os arquivos do jogo não foram excluídos.");
        installationPath = NormalizeDeletionPath(profile.InstallPath);
        EnsureSafeDeletionPath(installationPath, dataDirectory, applicationRoot);
        if (Directory.Exists(installationPath)) EnsureNoReparsePoints(installationPath);
        if (otherEndpoints.Any(endpoint => !string.IsNullOrWhiteSpace(endpoint.InstallPath) && Path.IsPathFullyQualified(endpoint.InstallPath) &&
            PathsOverlap(installationPath, NormalizeDeletionPath(endpoint.InstallPath))))
            throw new InvalidOperationException("Esta pasta é compartilhada com outro perfil. Remova o perfil sem excluir os arquivos do servidor.");
        if (!deleteSaves && saves.Any(item => SamePath(item.Path, installationPath)))
            throw new InvalidOperationException("A pasta de saves aponta para toda a pasta do servidor; escolha também excluir os saves ou mantenha os arquivos do servidor.");
    }

    if (deleteSaves)
    {
        foreach (var save in saves)
        {
            var defaultManagedPath = NormalizeDeletionPath(Path.Combine(dataDirectory, "instances", save.Endpoint.Id.ToString("N"), "worlds"));
            var isDefaultManagedSave = !save.Endpoint.UseInstallPathSaves && string.IsNullOrWhiteSpace(save.Endpoint.SaveDirectory) && SamePath(save.Path, defaultManagedPath);
            EnsureSafeDeletionPath(save.Path, dataDirectory, applicationRoot, allowDssManagedSave: isDefaultManagedSave);
            if (Directory.Exists(save.Path)) EnsureNoReparsePoints(save.Path);
            if (otherEndpoints.Any(endpoint => PathsOverlap(save.Path, NormalizeDeletionPath(SavePathResolver.Resolve(endpoint, dataDirectory)))))
                throw new InvalidOperationException($"A pasta de saves '{save.Path}' também é usada por outro perfil e não será excluída.");
        }
        foreach (var save in saves) DeleteDirectorySafely(save.Path, dataDirectory, applicationRoot, allowDssManagedSave: !save.Endpoint.UseInstallPathSaves && string.IsNullOrWhiteSpace(save.Endpoint.SaveDirectory));
    }

    if (installationPath is not null)
    {
        var preservedSaves = deleteSaves ? [] : saves.Select(item => item.Path).Where(path => IsSameOrWithinPath(path, installationPath)).ToArray();
        if (preservedSaves.Any(path => SamePath(path, installationPath)))
            throw new InvalidOperationException("Os saves ocupam a pasta do servidor inteira; não é possível preservar os saves e excluir o restante dessa pasta.");
        DeleteDirectoryPreserving(installationPath, preservedSaves, dataDirectory, applicationRoot);
    }
}

static string NormalizeDeletionPath(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

static StringComparer PathComparer() => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

static bool SamePath(string left, string right) => PathComparer().Equals(NormalizeDeletionPath(left), NormalizeDeletionPath(right));

static bool IsSameOrWithinPath(string path, string parent)
{
    var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    var normalizedPath = NormalizeDeletionPath(path);
    var normalizedParent = NormalizeDeletionPath(parent);
    return string.Equals(normalizedPath, normalizedParent, comparison) || normalizedPath.StartsWith(normalizedParent + Path.DirectorySeparatorChar, comparison);
}

static bool PathsOverlap(string left, string right) => IsSameOrWithinPath(left, right) || IsSameOrWithinPath(right, left);

static void EnsureSafeDeletionPath(string path, string dataDirectory, string applicationRoot, bool allowDssManagedSave = false)
{
    var normalized = NormalizeDeletionPath(path);
    var root = Path.GetPathRoot(normalized)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    if (string.IsNullOrWhiteSpace(root) || string.Equals(normalized, root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        throw new InvalidOperationException("O DSS bloqueou a exclusão porque o caminho aponta para a raiz de um disco.");
    var applicationData = NormalizeDeletionPath(dataDirectory);
    if (PathsOverlap(normalized, applicationData) && !allowDssManagedSave)
        throw new InvalidOperationException("O DSS bloqueou a exclusão para proteger os dados e as configurações do próprio painel.");
    var protectedRoots = new[] { applicationRoot, AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) }
        .Where(value => !string.IsNullOrWhiteSpace(value)).Select(NormalizeDeletionPath);
    if (protectedRoots.Any(protectedPath => PathsOverlap(normalized, protectedPath)))
        throw new InvalidOperationException("O DSS bloqueou a exclusão porque o caminho inclui arquivos do sistema ou do próprio aplicativo.");
    if (File.Exists(normalized)) throw new InvalidOperationException("O caminho de exclusão aponta para um arquivo, não para uma pasta.");
}

static void DeleteDirectorySafely(string path, string dataDirectory, string applicationRoot, bool allowDssManagedSave = false)
{
    EnsureSafeDeletionPath(path, dataDirectory, applicationRoot, allowDssManagedSave);
    if (!Directory.Exists(path)) return;
    EnsureNoReparsePoints(path);
    Directory.Delete(path, recursive: true);
}

static void DeleteDirectoryPreserving(string path, IReadOnlyList<string> preservedPaths, string dataDirectory, string applicationRoot)
{
    EnsureSafeDeletionPath(path, dataDirectory, applicationRoot);
    if (!Directory.Exists(path)) return;
    EnsureNoReparsePoints(path);
    DeleteDirectoryContents(path, preservedPaths);
}

static bool IsExecutableRunningFromDirectory(string? directory, string executableName)
{
    if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return false;
    string expectedPath;
    try { expectedPath = Path.GetFullPath(Path.Combine(directory, executableName)); }
    catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }

    foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executableName)))
    {
        using (process)
        {
            try
            {
                if (process.HasExited) continue;
                var runningPath = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(runningPath)) return true;
                if (string.Equals(Path.GetFullPath(runningPath), expectedPath,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return true;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // If Windows will not expose the matching process path, do not risk deleting files in use.
                return true;
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or NotSupportedException) { }
        }
    }
    return false;
}

static void DeleteDirectoryContents(string path, IReadOnlyList<string> preservedPaths)
{
    foreach (var file in Directory.EnumerateFiles(path)) File.Delete(file);
    foreach (var directory in Directory.EnumerateDirectories(path))
    {
        var hasPreservedChild = preservedPaths.Any(preserved => IsSameOrWithinPath(preserved, directory));
        if (preservedPaths.Any(preserved => SamePath(preserved, directory))) continue;
        if (hasPreservedChild)
        {
            DeleteDirectoryContents(directory, preservedPaths);
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory, recursive: false);
        }
        else Directory.Delete(directory, recursive: true);
    }
}

static void EnsureNoReparsePoints(string path)
{
    var rootAttributes = File.GetAttributes(path);
    if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
        throw new InvalidOperationException($"O DSS bloqueou a exclusão porque o caminho '{path}' é um link ou ponto de montagem.");
    if ((rootAttributes & FileAttributes.Directory) == 0)
        throw new InvalidOperationException("O DSS bloqueou a exclusão porque o caminho selecionado não é uma pasta.");
    var pending = new Stack<string>();
    pending.Push(path);
    while (pending.Count > 0)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"O DSS bloqueou a exclusão porque encontrou um link ou ponto de montagem em '{entry}'.");
            if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
        }
    }
}

static ProfileView ToView(ServerProfile profile, string status, GameAdapterRegistry adapters, string dataDirectory, SteamCmdService steamCmd, InstanceSupervisor supervisor)
{
    var hasAdapter = adapters.TryGet(profile.GameId, out var adapter);
    var installed = hasAdapter && !string.IsNullOrWhiteSpace(profile.InstallPath) && File.Exists(Path.Combine(profile.InstallPath, adapter.ExecutableName));
    bool? updateAvailable = null;
    if (installed && long.TryParse(profile.InstalledBuildId, out var installedBuild) && long.TryParse(profile.LatestBuildId, out var latestBuild))
        updateAvailable = latestBuild != installedBuild;
    return new ProfileView(
        profile.Id, profile.GameId, profile.Name, profile.InstallPath, profile.SaveDirectory, profile.ServerName, profile.WorldName, profile.Port,
        adapters.PortSpan(profile.GameId), profile.IsPublic, profile.Crossplay, profile.ExtraArguments, profile.GameSettings,
        !string.IsNullOrEmpty(profile.ProtectedPassword), status, profile.CreatedAt, profile.UpdatedAt,
        profile.UseInstallPathSaves, SavePathResolver.Resolve(profile, dataDirectory), installed, profile.InstalledBuildId,
        profile.LatestBuildId, updateAvailable, profile.LastUpdateCheck, profile.UpdateCheckError, steamCmd.GetOperation(profile.Id),
        File.Exists(ProfileImagePath(dataDirectory, profile.Id)), profile.Id,
        profile.Servers.Select(server => ToServerView(server, supervisor.GetStatus(server.Id))).ToArray());
}

static ServerEndpointView ToServerView(ServerEndpoint server, string status) => new(
    server.Id, server.Name, server.ServerName, server.WorldName, server.Port, server.SaveDirectory, server.UseInstallPathSaves,
    server.IsPublic, server.Crossplay, server.ExtraArguments.ToList(), server.GameSettings.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase),
    !string.IsNullOrEmpty(server.ProtectedPassword), status, server.CreatedAt);

static bool IsNewerModVersion(string latest, string installed)
{
    static (Version? Number, string? PreRelease, string Raw) Parse(string value)
    {
        var normalized = value.Trim().TrimStart('v', 'V');
        var pieces = normalized.Split('-', 2, StringSplitOptions.TrimEntries);
        Version.TryParse(pieces[0], out var number);
        return (number, pieces.Length > 1 ? pieces[1] : null, normalized);
    }
    var left = Parse(latest);
    var right = Parse(installed);
    if (left.Number is not null && right.Number is not null)
    {
        var comparison = left.Number.CompareTo(right.Number);
        if (comparison != 0) return comparison > 0;
        if (left.PreRelease is null) return right.PreRelease is not null;
        if (right.PreRelease is null) return false;
        return StringComparer.OrdinalIgnoreCase.Compare(left.PreRelease, right.PreRelease) > 0;
    }
    return StringComparer.OrdinalIgnoreCase.Compare(left.Raw, right.Raw) > 0;
}

static string? RequiredDependencyVersion(string dependency, string packageId)
{
    var prefix = packageId + "-";
    if (!dependency.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
    var version = dependency[prefix.Length..].Trim();
    var core = version.Split(['-', '+'], 2, StringSplitOptions.TrimEntries)[0];
    return Version.TryParse(core, out _) ? version : null;
}

static string SerializeProfile(ServerProfile profile) => JsonSerializer.Serialize(profile);

static RevisionDetailView ToRevisionDetail(ConfigRevision revision)
{
    string before;
    string after;
    var hasSnapshot = false;
    if (revision.Kind == "bepinex-file")
    {
        hasSnapshot = true;
        before = revision.BeforeExists ? TryUnprotectHistory(revision.BeforeJson) : "(O arquivo ainda não existia.)";
        after = TryUnprotectHistory(revision.AfterJson);
    }
    else if (revision.Kind == "server-profile")
    {
        hasSnapshot = !string.IsNullOrWhiteSpace(revision.AfterJson);
        before = string.IsNullOrWhiteSpace(revision.BeforeJson) ? "(O perfil ainda não existia.)" : FormatProfileSnapshot(revision.BeforeJson);
        after = string.IsNullOrWhiteSpace(revision.AfterJson) ? "(Sem estado posterior salvo.)" : FormatProfileSnapshot(revision.AfterJson);
    }
    else if (!string.IsNullOrWhiteSpace(revision.BeforeJson) || !string.IsNullOrWhiteSpace(revision.AfterJson))
    {
        hasSnapshot = true;
        before = FormatRevisionSnapshot(revision.BeforeJson);
        after = FormatRevisionSnapshot(revision.AfterJson);
    }
    else
    {
        (before, after) = DescribeActionChange(revision);
    }

    return new RevisionDetailView(revision.Id, revision.InstanceId, revision.CreatedAt, revision.Kind,
        revision.Target, revision.Summary, before, after, hasSnapshot, revision.CanRollback,
        RevisionActorName(revision), RevisionActorImageUrl(revision))
    {
        ModProvider = revision.ModProvider,
        ModPackageId = revision.ModPackageId,
        ModName = revision.ModName,
        ModVersion = revision.ModVersion,
        ModIconUrl = revision.ModIconUrl,
        ModProjectUrl = revision.ModProjectUrl,
        ModToggleNames = revision.ModToggleNames ?? [],
        ModToggleEnabled = revision.ModToggleEnabled,
        FilesInstalled = revision.FilesInstalled ?? [],
        FilesRemoved = revision.FilesRemoved ?? [],
        FilesPreserved = revision.FilesPreserved ?? []
    };
}

static string FormatProfileSnapshot(string json)
{
    try
    {
        var profile = JsonSerializer.Deserialize<ServerProfile>(json);
        if (profile is null) return "(Snapshot indisponível.)";
        var safeSnapshot = new
        {
            profile.Name,
            profile.ServerName,
            profile.WorldName,
            profile.InstallPath,
            profile.SaveDirectory,
            profile.UseInstallPathSaves,
            profile.Port,
            profile.IsPublic,
            profile.Crossplay,
            HasPassword = !string.IsNullOrEmpty(profile.ProtectedPassword),
            profile.ExtraArguments,
            profile.GameSettings
        };
        return JsonSerializer.Serialize(safeSnapshot, new JsonSerializerOptions { WriteIndented = true });
    }
    catch (JsonException) { return "(Não foi possível ler o snapshot do perfil.)"; }
}

static string FormatRevisionSnapshot(string value)
{
    if (string.IsNullOrWhiteSpace(value)) return "(Nenhum snapshot salvo.)";
    try
    {
        using var document = JsonDocument.Parse(value);
        return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }
    catch (JsonException) { return value; }
}

static string TryUnprotectHistory(string value)
{
    try { return SecretProtection.Unprotect(value); }
    catch (Exception) { return "(O conteúdo protegido desta revisão não pôde ser aberto neste host.)"; }
}

static (string Before, string After) DescribeActionChange(ConfigRevision revision)
{
    var summary = revision.Summary;
    if (summary.Contains("Servidor iniciado", StringComparison.OrdinalIgnoreCase)) return ("Servidor parado", "Servidor em execução");
    if (summary.Contains("Servidor parado", StringComparison.OrdinalIgnoreCase)) return ("Servidor em execução", "Servidor parado");
    if (summary.Contains("Servidor reiniciado", StringComparison.OrdinalIgnoreCase)) return ("Servidor em execução", "Servidor reiniciado e em execução");
    if (summary.Contains("inativado", StringComparison.OrdinalIgnoreCase)) return ("Mod ativo", "Mod inativo");
    if (summary.Contains("ativado", StringComparison.OrdinalIgnoreCase)) return ("Mod inativo", "Mod ativo");
    if (summary.Contains("Mod instalado/atualizado:", StringComparison.OrdinalIgnoreCase)) return ("Mod ausente ou versão anterior", summary);
    if (summary.Contains("Mod removido", StringComparison.OrdinalIgnoreCase)) return ("Mod instalado", "Mod removido do perfil");
    if (summary.Contains("Imagem do perfil atualizada", StringComparison.OrdinalIgnoreCase)) return ("Imagem anterior não capturada", "Imagem do perfil atualizada");
    if (summary.Contains("Imagem do perfil removida", StringComparison.OrdinalIgnoreCase)) return ("Imagem do perfil presente ou ausente", "Imagem removida");
    if (summary.Contains("Comando enviado ao console", StringComparison.OrdinalIgnoreCase)) return ("Console aguardando comando", "Comando enviado; o texto não foi armazenado no histórico");
    return ("Esta ação não salvou um snapshot anterior.", summary);
}

static ConfigRevision NewProfileRevision(Guid id, string summary, string before, string after, bool isGlobal = false) => new()
{
    InstanceId = id, Kind = "server-profile", Target = "launch settings", Summary = summary,
    BeforeJson = before, AfterJson = after, CanRollback = !string.IsNullOrEmpty(before), IsGlobal = isGlobal
};

static bool IsGlobalRevision(ConfigRevision revision) => revision.IsGlobal || revision.InstanceId == Guid.Empty ||
    (revision.Kind == "server-profile" && (revision.Summary == "Instância criada" || revision.Summary == "Perfil criado" || revision.Summary == "Perfil importado de JSON"));

static string ProtectHistory(string value) => SecretProtection.Protect(value);

static string NormalizeConfigLines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

static string[] SplitConfigLines(string value) => value.Length == 0 ? [] : value.Split('\n');

static List<ConfigLineHunk> ComputeLineChangeHunks(string[] before, string[] after)
{
    var width = after.Length + 1;
    var lcs = new uint[(before.Length + 1) * width];
    for (var i = before.Length - 1; i >= 0; i--)
    for (var j = after.Length - 1; j >= 0; j--)
    {
        var at = i * width + j;
        lcs[at] = before[i] == after[j]
            ? 1 + lcs[(i + 1) * width + j + 1]
            : Math.Max(lcs[(i + 1) * width + j], lcs[at + 1]);
    }

    var hunks = new List<ConfigLineHunk>();
    var beforeIndex = 0;
    var afterIndex = 0;
    while (beforeIndex < before.Length || afterIndex < after.Length)
    {
        if (beforeIndex < before.Length && afterIndex < after.Length && before[beforeIndex] == after[afterIndex])
        {
            beforeIndex++;
            afterIndex++;
            continue;
        }

        var beforeStart = beforeIndex;
        var afterStart = afterIndex;
        var removed = new List<string>();
        var added = new List<string>();
        while (beforeIndex < before.Length || afterIndex < after.Length)
        {
            if (beforeIndex < before.Length && afterIndex < after.Length && before[beforeIndex] == after[afterIndex]) break;
            if (beforeIndex < before.Length && (afterIndex >= after.Length ||
                lcs[(beforeIndex + 1) * width + afterIndex] >= lcs[beforeIndex * width + afterIndex + 1]))
                removed.Add(before[beforeIndex++]);
            else if (afterIndex < after.Length)
                added.Add(after[afterIndex++]);
        }
        hunks.Add(new ConfigLineHunk(beforeStart, afterStart, removed.ToArray(), added.ToArray()));
    }
    return hunks;
}

static RevisionView ToRevisionView(ConfigRevision revision) => new(
    revision.Id, revision.InstanceId, revision.CreatedAt, revision.Kind, revision.Target,
    revision.Summary, revision.CanRollback, RevisionActorName(revision), RevisionActorImageUrl(revision))
{
    ModProvider = revision.ModProvider,
    ModPackageId = revision.ModPackageId,
    ModName = revision.ModName,
    ModVersion = revision.ModVersion,
    ModIconUrl = revision.ModIconUrl,
    ModProjectUrl = revision.ModProjectUrl,
    ModToggleNames = revision.ModToggleNames ?? [],
    ModToggleEnabled = revision.ModToggleEnabled,
    FilesInstalled = revision.FilesInstalled ?? [],
    FilesRemoved = revision.FilesRemoved ?? [],
    FilesPreserved = revision.FilesPreserved ?? []
};

static ConfigRevision CreateModHistoryRevision(Guid instanceId, InstalledMod installed,
    InstalledMod? previous, IReadOnlyDictionary<Guid, HashSet<string>> previousFiles, ServerProfile? profile)
{
    var removedFiles = new List<string>();
    var preservedFiles = new List<string>();
    if (previous is not null && previousFiles.TryGetValue(previous.Id, out var existingBefore))
    {
        var newFiles = (installed.ManagedFiles ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var oldFile in (previous.ManagedFiles ?? []).Where(path => !newFiles.Contains(path) && existingBefore.Contains(path)))
        {
            if (ManagedFileExists(profile, oldFile)) preservedFiles.Add(oldFile.Replace('\\', '/'));
            else removedFiles.Add(oldFile.Replace('\\', '/'));
        }
    }
    var updated = previous is not null;
    return new ConfigRevision
    {
        InstanceId = instanceId,
        Kind = "mod-package",
        Target = installed.Name,
        Summary = $"Mod {(updated ? "atualizado" : "instalado")}: {installed.Name} {installed.Version}",
        CanRollback = false,
        ModProvider = NormalizeModHistoryProvider(installed.SourceProvider, installed.Provider),
        ModPackageId = installed.PackageId,
        ModName = installed.Name,
        ModVersion = installed.Version,
        ModIconUrl = installed.IconUrl,
        ModProjectUrl = installed.ProjectUrl,
        FilesInstalled = (installed.ManagedFiles ?? []).Select(path => path.Replace('\\', '/')).ToList(),
        FilesRemoved = removedFiles,
        FilesPreserved = preservedFiles
    };
}

static ConfigRevision CreateModRemovalHistoryRevision(Guid instanceId, InstalledMod mod,
    IReadOnlyList<string> removedFiles, IReadOnlyList<string> preservedFiles) => new()
{
    InstanceId = instanceId,
    Kind = "mod-package",
    Target = mod.Name,
    Summary = $"Mod removido: {mod.Name}",
    CanRollback = false,
    ModProvider = NormalizeModHistoryProvider(mod.SourceProvider, mod.Provider),
    ModPackageId = mod.PackageId,
    ModName = mod.Name,
    ModVersion = mod.Version,
    ModIconUrl = mod.IconUrl,
    ModProjectUrl = mod.ProjectUrl,
    FilesRemoved = removedFiles.ToList(),
    FilesPreserved = preservedFiles.ToList()
};

static InstalledMod? FindPreviousMod(IReadOnlyList<InstalledMod> previousMods, InstalledMod current)
{
    var source = NormalizeModHistoryProvider(current.SourceProvider, current.Provider);
    return previousMods.FirstOrDefault(item => item.PackageId.Equals(current.PackageId, StringComparison.OrdinalIgnoreCase) &&
        NormalizeModHistoryProvider(item.SourceProvider, item.Provider).Equals(source, StringComparison.OrdinalIgnoreCase));
}

static string NormalizeModHistoryProvider(string? sourceProvider, string? fallbackProvider)
{
    var provider = string.IsNullOrWhiteSpace(sourceProvider) ? fallbackProvider ?? "" : sourceProvider;
    return provider.Equals("bepinex", StringComparison.OrdinalIgnoreCase) ? "thunderstore" : provider.Trim().ToLowerInvariant();
}

static HashSet<string> ExistingManagedFiles(ServerProfile? profile, InstalledMod mod)
{
    var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var relative in mod.ManagedFiles ?? [])
        if (ManagedFileExists(profile, relative)) existing.Add(relative);
    return existing;
}

static bool ManagedFileExists(ServerProfile? profile, string relative)
{
    if (profile is null || string.IsNullOrWhiteSpace(profile.InstallPath) || !Path.IsPathFullyQualified(profile.InstallPath) ||
        string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return false;
    try
    {
        var root = Path.GetFullPath(profile.InstallPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        return File.Exists(candidate) || File.Exists(candidate + ".dss-disabled");
    }
    catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
    {
        return false;
    }
}

static string RevisionActorName(ConfigRevision revision) => revision.ActorName ?? "Autor não registrado";

static string? RevisionActorImageUrl(ConfigRevision revision) => revision.ActorUserId is { } userId && revision.ActorImageVersion is { } version
    ? $"/api/users/{userId}/image?v={version}"
    : null;

static string? SafeConfigPath(ServerProfile profile, string relativePath)
{
    if (string.IsNullOrWhiteSpace(profile.InstallPath) || !Path.IsPathFullyQualified(profile.InstallPath)) return null;
    var root = Path.GetFullPath(Path.Combine(profile.InstallPath, "BepInEx", "config"));
    var candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    return candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? candidate : null;
}

static bool IsEditableConfig(string path) =>
    new[] { ".cfg", ".json", ".yaml", ".yml", ".toml", ".xml", ".txt" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

static string ResolveListenUrl(string mode, string? configuredUrl)
{
    if (!string.IsNullOrWhiteSpace(configuredUrl) && !Uri.TryCreate(configuredUrl, UriKind.Absolute, out _))
        throw new InvalidOperationException("DSS_URL/VALHEIM_STUDIO_URL precisa ser uma URL absoluta HTTP ou HTTPS.");
    if (Uri.TryCreate(configuredUrl, UriKind.Absolute, out var configured))
    {
        if (configured.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("DSS_URL/VALHEIM_STUDIO_URL precisa usar HTTP ou HTTPS.");
        var builder = new UriBuilder(configured)
        {
            Host = mode == "local" ? IPAddress.Loopback.ToString() : "0.0.0.0",
            Path = "/",
            Query = "",
            Fragment = ""
        };
        return builder.Uri.GetLeftPart(UriPartial.Authority);
    }
    return mode == "local" ? "http://127.0.0.1:5089" : "http://0.0.0.0:5089";
}

static string[] GetPanelAccessUrls(string mode, int port)
{
    if (mode == "local") return [$"http://127.0.0.1:{port}"];
    try
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Where(address => address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address))
            .Select(address => $"http://{address.Address}:{port}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
    catch (NetworkInformationException) { return []; }
}

static string[] GetLocalIpv4Addresses()
{
    try
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(address => address.Address)
            .Where(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(address) &&
                !address.GetAddressBytes().Take(2).SequenceEqual(new byte[] { 169, 254 }) &&
                IsPrivateIpv4(address))
            .Distinct()
            .OrderBy(address => IsPrivateIpv4(address) ? 0 : 1)
            .ThenBy(address => address.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(address => address.ToString())
            .ToArray();
    }
    catch (NetworkInformationException) { return []; }
}

static async Task<string?> GetPublicIpv4Async(IHttpClientFactory clients, CancellationToken cancellationToken)
{
    using var attempts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    var client = clients.CreateClient("public-ip");
    async Task<string?> FetchAsync(string endpoint)
    {
        try
        {
            using var response = await client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, attempts.Token);
            if (!response.IsSuccessStatusCode) return null;
            var value = (await response.Content.ReadAsStringAsync(attempts.Token)).Trim();
            return IPAddress.TryParse(value, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? address.ToString()
                : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (HttpRequestException) { return null; }
    }

    var requests = new[]
    {
        FetchAsync("https://api.ipify.org"),
        FetchAsync("https://checkip.amazonaws.com")
    };
    try
    {
        var pending = requests.ToList();
        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending);
            pending.Remove(completed);
            if (await completed is { } address) return address;
        }
        return null;
    }
    finally
    {
        attempts.Cancel();
        try { await Task.WhenAll(requests); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
    }
}

static bool IsPrivateIpv4(IPAddress address)
{
    var bytes = address.GetAddressBytes();
    return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168);
}

static object GetClientAccessInfo(HttpContext context)
{
    var remoteAddress = context.Connection.RemoteIpAddress;
    if (remoteAddress is null) return new { isLocal = false, network = "unknown" };
    if (remoteAddress.IsIPv4MappedToIPv6) remoteAddress = remoteAddress.MapToIPv4();
    if (IPAddress.IsLoopback(remoteAddress)) return new { isLocal = true, network = "local" };

    try
    {
        var localAddresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(address => address.Address.IsIPv4MappedToIPv6 ? address.Address.MapToIPv4() : address.Address);
        if (localAddresses.Any(address => address.Equals(remoteAddress)))
            return new { isLocal = true, network = "local" };
    }
    catch (NetworkInformationException) { }

    var isPrivateNetwork = false;
    if (remoteAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
    {
        var bytes = remoteAddress.GetAddressBytes();
        isPrivateNetwork = bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254);
    }
    else if (remoteAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
    {
        var bytes = remoteAddress.GetAddressBytes();
        isPrivateNetwork = remoteAddress.IsIPv6LinkLocal || (bytes[0] & 0xFE) == 0xFC;
    }
    return new { isLocal = false, network = isPrivateNetwork ? "lan" : "public" };
}

public sealed record ConfigLineHunk(int BeforeStart, int AfterStart, string[] BeforeLines, string[] AfterLines);

public sealed record AuthRequest(string Password, string Username = "admin");
public sealed record ListenModeInput(string ListenMode, bool AcknowledgePublicRisk = false);
public sealed record PanelFirewallInput(bool Enabled);
public sealed record ConfigInput(string Content);
public sealed record ConfigBatchDeleteInput(string[] Paths);
public sealed record NexusDownloadAuthorizationInput(string Link);
public sealed record ProfileImportDocument(
    int SchemaVersion,
    string GameId,
    string Name,
    string InstallPath,
    string SaveDirectory,
    string ServerName,
    string WorldName,
    int Port,
    bool IsPublic,
    bool Crossplay,
    List<string>? ExtraArguments,
    Dictionary<string, JsonElement>? GameSettings,
    string? GamePassword = null,
    bool? UseInstallPathSaves = null,
    IReadOnlyList<ProfileExportMod>? Mods = null,
    int OmittedModCount = 0);
