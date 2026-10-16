using System.Text.Json.Serialization;
using System.Text.Json;

namespace DedicatedServerStudio.Models;

public sealed class AppData
{
    public string? AdminPasswordHash { get; set; }
    public List<DssUser> Users { get; set; } = [];
    public List<FailedLoginAttempt> FailedLoginAttempts { get; set; } = [];
    public bool FailedLoginAttemptsAggregated { get; set; }
    public string SteamCmdDirectory { get; set; } = "";
    // Legacy host-wide value retained so existing state files continue to load.
    // UI language is now stored on each DssUser.
    public string UiLanguage { get; set; } = "en-US";
    public List<ServerProfile> Instances { get; set; } = [];
    public List<ConfigRevision> Revisions { get; set; } = [];
    public List<InstalledMod> InstalledMods { get; set; } = [];
    // Provider credentials are encrypted by ProviderCredentialVault before they reach disk.
    public Dictionary<string, string> ProtectedProviderApiKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ServerProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    // Profiles sharing a group are independent server instances under one multi-server profile.
    public Guid GroupId { get; set; }
    // One profile owns shared game/mod/config files and can expose several independent server processes.
    public List<ServerEndpoint> Servers { get; set; } = [];
    public List<Guid> AllowedUserIds { get; set; } = [];
    // User-level profile permissions. Legacy AllowedUserIds entries are migrated to edit access.
    public Dictionary<Guid, string> UserAccess { get; set; } = [];
    // Profiles are grouped by game; adapters translate each game's settings to process arguments.
    public string GameId { get; set; } = "valheim";
    public string Name { get; set; } = "New Valheim server";
    public string InstallPath { get; set; } = "";
    public string SaveDirectory { get; set; } = "";
    // Defaults false when loading older profiles so their existing world path is preserved.
    public bool UseInstallPathSaves { get; set; }
    public bool LogModToggleActions { get; set; }
    public string InstalledBuildId { get; set; } = "";
    public string LatestBuildId { get; set; } = "";
    public DateTimeOffset? LastUpdateCheck { get; set; }
    public string UpdateCheckError { get; set; } = "";
    public string ServerName { get; set; } = "Valheim";
    public string WorldName { get; set; } = "Dedicated";
    public int Port { get; set; } = 2456;
    public bool IsPublic { get; set; }
    public bool Crossplay { get; set; }
    public List<string> ExtraArguments { get; set; } = [];
    public Dictionary<string, JsonElement> GameSettings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string ProtectedPassword { get; set; } = "";
    [JsonIgnore] public string GamePassword { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ServerProfile WithServer(ServerEndpoint server)
    {
        var settings = GameSettings.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        foreach (var pair in server.GameSettings) settings[pair.Key] = pair.Value.Clone();
        return new ServerProfile
        {
            Id = server.Id, GroupId = Id, AllowedUserIds = AllowedUserIds.ToList(), UserAccess = new Dictionary<Guid, string>(UserAccess), GameId = GameId, Name = server.Name,
            InstallPath = InstallPath, SaveDirectory = server.SaveDirectory, UseInstallPathSaves = server.UseInstallPathSaves,
            LogModToggleActions = LogModToggleActions, InstalledBuildId = InstalledBuildId, LatestBuildId = LatestBuildId,
            LastUpdateCheck = LastUpdateCheck, UpdateCheckError = UpdateCheckError, ServerName = server.ServerName,
            WorldName = server.WorldName, Port = server.Port, IsPublic = server.IsPublic, Crossplay = server.Crossplay,
            ExtraArguments = server.ExtraArguments.ToList(), GameSettings = settings, ProtectedPassword = server.ProtectedPassword,
            CreatedAt = server.CreatedAt, UpdatedAt = server.UpdatedAt, Servers = Servers
        };
    }
}

public sealed class ServerEndpoint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Servidor";
    public string ServerName { get; set; } = "Valheim";
    public string WorldName { get; set; } = "Dedicated";
    public int Port { get; set; } = 2456;
    public string SaveDirectory { get; set; } = "";
    public bool UseInstallPathSaves { get; set; }
    public bool IsPublic { get; set; }
    public bool Crossplay { get; set; }
    public List<string> ExtraArguments { get; set; } = [];
    public Dictionary<string, JsonElement> GameSettings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string ProtectedPassword { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class DssUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "user";
    public string? UiLanguage { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? FailedLoginWindowStartedAt { get; set; }
    public DateTimeOffset? LoginLockedUntil { get; set; }
    public bool LoginLocked { get; set; }
}

public sealed class FailedLoginAttempt
{
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset FirstOccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public string AttemptedUsername { get; set; } = "";
    public string IpAddress { get; set; } = "unknown";
    public string DeviceName { get; set; } = "";
    public int AttemptCount { get; set; } = 1;
    public int DuringLockoutCount { get; set; }
    public bool DuringLockout { get; set; }
}

public sealed class ConfigRevision
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InstanceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Kind { get; set; } = "server-profile";
    public string Target { get; set; } = "";
    public string Summary { get; set; } = "Configuration changed";
    public string BeforeJson { get; set; } = "";
    public string AfterJson { get; set; } = "";
    public bool BeforeExists { get; set; }
    public bool CanRollback { get; set; } = true;
    public bool IsGlobal { get; set; }
    public Guid? ActorUserId { get; set; }
    public string? ActorName { get; set; }
    public long? ActorImageVersion { get; set; }
    public string ModProvider { get; set; } = "";
    public string ModPackageId { get; set; } = "";
    public string ModName { get; set; } = "";
    public string ModVersion { get; set; } = "";
    public string? ModIconUrl { get; set; }
    public string? ModProjectUrl { get; set; }
    public List<string> FilesInstalled { get; set; } = [];
    public List<string> FilesRemoved { get; set; } = [];
    public List<string> FilesPreserved { get; set; } = [];
}

public sealed class InstalledMod
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InstanceId { get; set; }
    public string Provider { get; set; } = "manual";
    public string SourceProvider { get; set; } = "";
    public string PackageId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public long? FileSize { get; set; }
    public string Owner { get; set; } = "";
    public string? IconUrl { get; set; }
    public string? ProjectUrl { get; set; }
    public string Sha256 { get; set; } = "";
    public List<string> ManagedFiles { get; set; } = [];
    public Dictionary<string, string> FileHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Enabled { get; set; } = true;
    public bool Locked { get; set; }
    public bool IsFramework { get; set; }
    public List<string> Dependencies { get; set; } = [];
    public bool DependencyMetadataKnown { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public long? RatingScore { get; set; }
    public List<string> Categories { get; set; } = [];
    public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record ProfileInput(
    string Name,
    string InstallPath,
    string ServerName,
    string WorldName,
    string GamePassword,
    int Port,
    bool IsPublic,
    bool Crossplay,
    List<string>? ExtraArguments,
    string? GameId = null,
    string? SaveDirectory = null,
    Dictionary<string, JsonElement>? GameSettings = null,
    bool? UseInstallPathSaves = null);

public sealed record MultiServerInput(string Name, string ServerName, string WorldName, int Port, string? GamePassword = null, bool? IsPublic = null, bool? Crossplay = null, string? SaveDirectory = null, bool? UseInstallPathSaves = null, Guid? SourceServerId = null);
public sealed record UserCreateInput(string Username, string Password, string Role = "user");
public sealed record UserUpdateInput(string Role, string? Password = null);
public sealed record UserPasswordChangeInput(string CurrentPassword, string NewPassword);
public sealed record ProfileUsersInput(Dictionary<Guid, string> UserAccess);
public sealed record UserView(Guid Id, string Username, string Role, DateTimeOffset CreatedAt, int AccessibleProfiles, string? ImageUrl = null, DateTimeOffset? LockedUntil = null, bool IsLocked = false, int FailedLoginCount = 0);

public sealed record ProfileView(
    Guid Id,
    string GameId,
    string Name,
    string InstallPath,
    string SaveDirectory,
    string ServerName,
    string WorldName,
    int Port,
    int PortSpan,
    bool IsPublic,
    bool Crossplay,
    List<string> ExtraArguments,
    Dictionary<string, JsonElement> GameSettings,
    bool HasPassword,
    string RuntimeStatus,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool UseInstallPathSaves,
    string ResolvedSaveDirectory,
    bool ServerInstalled,
    string InstalledBuildId,
    string LatestBuildId,
    bool? UpdateAvailable,
    DateTimeOffset? LastUpdateCheck,
    string UpdateCheckError,
    SteamOperationView? GameOperation,
    bool HasImage = false,
    Guid GroupId = default,
    IReadOnlyList<ServerEndpointView>? Servers = null);

public sealed record ServerEndpointView(Guid Id, string Name, string ServerName, string WorldName, int Port,
    string SaveDirectory, bool UseInstallPathSaves, bool IsPublic, bool Crossplay, List<string> ExtraArguments,
    Dictionary<string, JsonElement> GameSettings, bool HasPassword, string RuntimeStatus, DateTimeOffset CreatedAt);

public sealed record GameView(string Id, string Name, string ExecutableName, int PortSpan, bool Available, string SteamAppId);

public sealed record ProfileExportDocument(
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
    List<string> ExtraArguments,
    Dictionary<string, JsonElement> GameSettings,
    bool PasswordConfigured,
    bool UseInstallPathSaves = true,
    IReadOnlyList<ProfileExportMod>? Mods = null,
    int OmittedModCount = 0);

public sealed record ProfileExportMod(string Provider, string PackageId, string Version, bool Enabled);

public sealed record ServerPathCheck(bool PathValid, bool ExecutableFound, string ExecutableName, string Message);
public sealed record GameBranchView(string Id, string Name, string Description, string BuildId);

public sealed record SteamOperationView(string State, string Phase, string Message, IReadOnlyList<string> Output, bool Running, bool StartsServerAfterInstall,
    long OutputBaseSequence = 0, long OutputSequence = 0, string? CurrentOutputLine = null, Guid OperationId = default, long Revision = 0);

public sealed record SteamCmdSettingsView(string Directory, string DefaultDirectory, bool ExecutableFound, string Source);

public sealed record SteamCmdDirectoryInput(string? Directory);
public sealed record UiLanguageInput(string? Language);
public sealed record ProfileHistorySettingsInput(bool LogModToggleActions);

public sealed record ConsoleCommandInput(string Command);
public sealed record PlayerAccessInput(string UserId);

public sealed record ProfileImageInput(string ImageData);
public sealed record ManagedProcessMetrics(int ProcessId, long WorkingSetBytes, double CpuPercent, double UptimeSeconds,
    long IoReadBytes, long IoWriteBytes, DateTimeOffset SampledAt);

public sealed record ConsoleLogEntry(long Sequence, DateTimeOffset Timestamp, string Stream, string Text);

public sealed record RevisionView(
    Guid Id,
    Guid InstanceId,
    DateTimeOffset CreatedAt,
    string Kind,
    string Target,
    string Summary,
    bool CanRollback,
    string ActorName,
    string? ActorImageUrl)
{
    public string? ModProvider { get; init; }
    public string? ModPackageId { get; init; }
    public string? ModName { get; init; }
    public string? ModVersion { get; init; }
    public string? ModIconUrl { get; init; }
    public string? ModProjectUrl { get; init; }
    public IReadOnlyList<string> FilesInstalled { get; init; } = [];
    public IReadOnlyList<string> FilesRemoved { get; init; } = [];
    public IReadOnlyList<string> FilesPreserved { get; init; } = [];
}

public sealed record RevisionDetailView(
    Guid Id,
    Guid InstanceId,
    DateTimeOffset CreatedAt,
    string Kind,
    string Target,
    string Summary,
    string Before,
    string After,
    bool HasSnapshot,
    bool CanRollback,
    string ActorName,
    string? ActorImageUrl)
{
    public string? ModProvider { get; init; }
    public string? ModPackageId { get; init; }
    public string? ModName { get; init; }
    public string? ModVersion { get; init; }
    public string? ModIconUrl { get; init; }
    public string? ModProjectUrl { get; init; }
    public IReadOnlyList<string> FilesInstalled { get; init; } = [];
    public IReadOnlyList<string> FilesRemoved { get; init; } = [];
    public IReadOnlyList<string> FilesPreserved { get; init; } = [];
}

public sealed record ModDependentView(Guid Id, string PackageId, string Name, string Version, string Provider, int Depth);

public sealed record CatalogPackage(
    string Provider,
    string PackageId,
    string Name,
    string Owner,
    string Version,
    string Description,
    string? IconUrl,
    string? ProjectUrl,
    string? DownloadUrl,
    long? FileSize,
    IReadOnlyList<string> Dependencies,
    long TotalDownloads = 0,
    DateTimeOffset? UpdatedAt = null,
    [property: JsonIgnore] IReadOnlyList<CatalogVersion>? Versions = null,
    DateTimeOffset? CreatedAt = null,
    long? RatingScore = null,
    IReadOnlyList<string>? Categories = null);

public sealed record CatalogVersion(string Version, string Description, string? IconUrl, string? DownloadUrl,
    long? FileSize, IReadOnlyList<string> Dependencies, DateTimeOffset? CreatedAt, long Downloads,
    string? ProviderVersionId = null);

public sealed record ProviderView(
    string Id,
    string Name,
    string State,
    string Detail,
    bool AccountRequired,
    bool CanInstall);

public sealed record ProviderCredentialInput(string? ApiKey, bool Clear = false);

public sealed record ModInstallInput(string Provider, string PackageId, IReadOnlyDictionary<string, string>? DependencyVersions = null);
public sealed record ModInstallBatchInput(string Provider, IReadOnlyList<string> PackageIds, IReadOnlyDictionary<string, string>? DependencyVersions = null);
public sealed record ModUninstallBatchInput(IReadOnlyList<Guid> ModIds);
public sealed record ModLockBatchInput(IReadOnlyList<Guid> ModIds, bool Locked);
