using System.Text.Json;
using DedicatedServerStudio.Models;

namespace DedicatedServerStudio.Services;

public sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly AsyncLocal<Guid?> RevisionActor = new();
    private const int MaxHistoryEntriesPerProfile = 500;
    private const int MaxGlobalHistoryEntries = 500;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _filePath;
    private AppData _data;

    public string DataDirectory { get; }
    public string LogsDirectory { get; }
    public string ContentRootPath { get; }

    public StateStore(IWebHostEnvironment environment)
    {
        ContentRootPath = Path.GetFullPath(environment.ContentRootPath);
        DataDirectory = ResolveDataDirectory();
        LogsDirectory = Path.Combine(ContentRootPath, "logs");
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        _filePath = Path.Combine(DataDirectory, "studio-state.json");
        if (File.Exists(_filePath))
        {
            try
            {
                using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
                _data = JsonSerializer.Deserialize<AppData>(stream, JsonOptions) ?? new AppData();
            }
            catch (JsonException ex) { throw new InvalidDataException($"State file is invalid: {_filePath}", ex); }

            _data.Users ??= [];
            _data.FailedLoginAttempts ??= [];
            _data.Instances ??= [];
            var migrated = false;
            var now = DateTimeOffset.UtcNow;
            foreach (var user in _data.Users)
            {
                if (string.Equals(user.Username, "admin", StringComparison.OrdinalIgnoreCase) && user.Role != "admin")
                {
                    user.Role = "admin";
                    _data.AdminPasswordHash = user.PasswordHash;
                    migrated = true;
                }
                if (user.LoginLocked)
                {
                    if (user.LoginLockedUntil is not null) { user.LoginLockedUntil = null; migrated = true; }
                    continue;
                }

                if (user.LoginLockedUntil is not { } legacyLock) continue;
                if (legacyLock > now) user.LoginLocked = true;
                else
                {
                    // An already-expired temporary lock from an older DSS build stays expired.
                    user.FailedLoginCount = 0;
                    user.FailedLoginWindowStartedAt = null;
                }
                user.LoginLockedUntil = null;
                migrated = true;
            }
            if (!_data.FailedLoginAttemptsAggregated)
            {
                _data.FailedLoginAttempts = _data.FailedLoginAttempts
                    .GroupBy(attempt => (
                        IpAddress: string.IsNullOrWhiteSpace(attempt.IpAddress) ? "unknown" : attempt.IpAddress.Trim(),
                        Username: string.IsNullOrWhiteSpace(attempt.AttemptedUsername) ? "(vazio)" : attempt.AttemptedUsername.Trim()),
                        EqualityComparer<(string IpAddress, string Username)>.Create(
                            (left, right) => string.Equals(left.IpAddress, right.IpAddress, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(left.Username, right.Username, StringComparison.OrdinalIgnoreCase),
                            value => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.IpAddress), StringComparer.OrdinalIgnoreCase.GetHashCode(value.Username))))
                    .Select(group =>
                    {
                        var ordered = group.OrderBy(attempt => attempt.OccurredAt).ToArray();
                        var latest = ordered[^1];
                        return new FailedLoginAttempt
                        {
                            IpAddress = group.Key.IpAddress,
                            FirstOccurredAt = ordered.Min(attempt => attempt.OccurredAt),
                            OccurredAt = latest.OccurredAt,
                            AttemptedUsername = group.Key.Username,
                            DeviceName = ordered.Select(attempt => attempt.DeviceName).LastOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "",
                            AttemptCount = ordered.Sum(attempt => Math.Max(1, attempt.AttemptCount)),
                            DuringLockoutCount = ordered.Sum(attempt => attempt.DuringLockoutCount > 0
                                ? attempt.DuringLockoutCount
                                : attempt.DuringLockout ? Math.Max(1, attempt.AttemptCount) : 0),
                            DuringLockout = latest.DuringLockout
                        };
                    })
                    .OrderByDescending(attempt => attempt.OccurredAt)
                    .Take(1_000)
                    .ToList();
                _data.FailedLoginAttemptsAggregated = true;
                migrated = true;
            }
            foreach (var profile in _data.Instances)
            {
                profile.AllowedUserIds ??= [];
                profile.UserAccess ??= [];
                foreach (var userId in profile.AllowedUserIds)
                    if (profile.UserAccess.TryAdd(userId, "edit")) migrated = true;
                foreach (var grant in profile.UserAccess.ToArray())
                {
                    var normalized = grant.Value is "edit" or "operate" ? grant.Value : "edit";
                    if (grant.Value != normalized) { profile.UserAccess[grant.Key] = normalized; migrated = true; }
                }
                var accessIds = profile.UserAccess.Keys.ToList();
                if (!profile.AllowedUserIds.SequenceEqual(accessIds)) migrated = true;
                profile.AllowedUserIds = accessIds;
                profile.Servers ??= [];
                if (profile.GroupId == Guid.Empty) { profile.GroupId = profile.Id; migrated = true; }
            }
            // Older DSS versions stored each server as a full profile. Collapse those records into
            // one shared profile with endpoint records, keeping the existing server IDs for runtime logs.
            foreach (var group in _data.Instances.GroupBy(profile => profile.GroupId).ToArray())
            {
                var members = group.OrderBy(profile => profile.CreatedAt).ThenBy(profile => profile.Id).ToArray();
                if (members.Length == 0) continue;
                var root = members.FirstOrDefault(profile => profile.Id == group.Key) ?? members[0];
                var hadServerRecords = root.Servers.Count > 0;
                var knownServerIds = root.Servers.Select(server => server.Id).ToHashSet();
                foreach (var member in members)
                {
                    if (!knownServerIds.Add(member.Id)) continue;
                    root.Servers.Add(new ServerEndpoint
                    {
                        Id = member.Id,
                        Name = member.Id == root.Id ? root.Name : member.Name,
                        ServerName = member.ServerName,
                        WorldName = member.WorldName,
                        Port = member.Port,
                        SaveDirectory = member.SaveDirectory,
                        UseInstallPathSaves = member.UseInstallPathSaves,
                        IsPublic = member.IsPublic,
                        Crossplay = member.Crossplay,
                        ExtraArguments = member.ExtraArguments?.ToList() ?? [],
                        GameSettings = member.GameSettings?.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase) ?? new(StringComparer.OrdinalIgnoreCase),
                        ProtectedPassword = member.ProtectedPassword,
                        CreatedAt = member.CreatedAt,
                        UpdatedAt = member.UpdatedAt
                    });
                }
                root.UserAccess = members.SelectMany(member => member.UserAccess)
                    .GroupBy(grant => grant.Key)
                    .ToDictionary(group => group.Key, group => group.Any(grant => grant.Value == "edit") ? "edit" : "operate");
                root.AllowedUserIds = root.UserAccess.Keys.ToList();
                // Repair a prior migration that selected a generated "Servidor N" row instead
                // of the original profile when legacy creation timestamps were identical.
                var suffixMarker = " · Servidor ";
                var suffixAt = root.Name.LastIndexOf(suffixMarker, StringComparison.OrdinalIgnoreCase);
                if (members.Length == 1 && suffixAt > 0 && int.TryParse(root.Name[(suffixAt + suffixMarker.Length)..], out var serverNumber) && serverNumber > 1)
                {
                    var baseName = root.Name[..suffixAt].TrimEnd();
                    var originalServer = root.Servers.FirstOrDefault(server => server.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase));
                    if (originalServer is not null && originalServer.Id != root.Id)
                    {
                        var previousRootId = root.Id;
                        root.Id = originalServer.Id;
                        root.Name = originalServer.Name;
                        root.CreatedAt = originalServer.CreatedAt;
                        root.ServerName = originalServer.ServerName;
                        root.WorldName = originalServer.WorldName;
                        root.Port = originalServer.Port;
                        root.SaveDirectory = originalServer.SaveDirectory;
                        root.UseInstallPathSaves = originalServer.UseInstallPathSaves;
                        root.IsPublic = originalServer.IsPublic;
                        root.Crossplay = originalServer.Crossplay;
                        root.ExtraArguments = originalServer.ExtraArguments.ToList();
                        root.ProtectedPassword = originalServer.ProtectedPassword;
                        foreach (var mod in _data.InstalledMods.Where(mod => mod.InstanceId == previousRootId)) mod.InstanceId = root.Id;
                        foreach (var revision in _data.Revisions.Where(revision => revision.InstanceId == previousRootId)) revision.InstanceId = root.Id;
                        var oldImage = Path.Combine(DataDirectory, "profile-images", $"{previousRootId:N}.jpg");
                        var primaryImage = Path.Combine(DataDirectory, "profile-images", $"{root.Id:N}.jpg");
                        if (!File.Exists(primaryImage) && File.Exists(oldImage)) File.Move(oldImage, primaryImage);
                        migrated = true;
                    }
                }
                root.GroupId = root.Id;
                if (members.Length > 1)
                {
                    var memberIds = members.Select(member => member.Id).ToHashSet();
                    foreach (var mod in _data.InstalledMods.Where(mod => memberIds.Contains(mod.InstanceId)))
                        mod.InstanceId = root.Id;
                    _data.InstalledMods = _data.InstalledMods
                        .GroupBy(mod => (mod.InstanceId, mod.Provider, mod.PackageId), new InstalledModKeyComparer())
                        .Select(mods => mods.First()).ToList();
                    foreach (var revision in _data.Revisions.Where(revision => memberIds.Contains(revision.InstanceId)))
                        revision.InstanceId = root.Id;
                    foreach (var child in members.Skip(1)) _data.Instances.Remove(child);
                    migrated = true;
                }
                if (!hadServerRecords) migrated = true;
            }
            if (!_data.Users.Any(user => user.Role == "admin") && !string.IsNullOrWhiteSpace(_data.AdminPasswordHash))
            {
                _data.Users.Add(new DssUser { Username = "admin", PasswordHash = _data.AdminPasswordHash, Role = "admin" });
                migrated = true;
            }
            if (PruneHistory()) migrated = true;
            if (migrated) SaveUnsafe();
        }
        else
        {
            _data = new AppData();
            _data.FailedLoginAttemptsAggregated = true;
            SaveUnsafe();
        }
    }

    public string GetServerLogPath(Guid id)
    {
        var path = Path.Combine(LogsDirectory, "servers", id.ToString("N"), "server.log");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var legacyPath = Path.Combine(DataDirectory, "instances", id.ToString("N"), "server.log");
        if (!File.Exists(path) && File.Exists(legacyPath))
        {
            try { File.Copy(legacyPath, path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return path;
    }

    public string GetPlayerHistoryPath(Guid id)
    {
        // Keep recognized player history with DSS data rather than the install folder.
        // This survives replacing or moving the application directory while leaving
        // the user-requested application logs under the project root.
        var directory = Path.Combine(DataDirectory, "servers", id.ToString("N"));
        var path = Path.Combine(directory, "players.json");
        if (File.Exists(path)) return path;

        var legacyPaths = new[]
        {
            Path.Combine(LogsDirectory, "servers", id.ToString("N"), "players.json"),
            Path.Combine(DataDirectory, "instances", id.ToString("N"), "players.json")
        };
        foreach (var legacyPath in legacyPaths)
        {
            if (!File.Exists(legacyPath)) continue;
            try
            {
                Directory.CreateDirectory(directory);
                File.Copy(legacyPath, path, overwrite: false);
                break;
            }
            catch (IOException)
            {
                if (File.Exists(path)) break;
            }
            catch (UnauthorizedAccessException)
            {
                break;
            }
        }
        return path;
    }

    private sealed class InstalledModKeyComparer : IEqualityComparer<(Guid InstanceId, string Provider, string PackageId)>
    {
        public bool Equals((Guid InstanceId, string Provider, string PackageId) x, (Guid InstanceId, string Provider, string PackageId) y) =>
            x.InstanceId == y.InstanceId && StringComparer.OrdinalIgnoreCase.Equals(x.Provider, y.Provider) && StringComparer.OrdinalIgnoreCase.Equals(x.PackageId, y.PackageId);

        public int GetHashCode((Guid InstanceId, string Provider, string PackageId) value) =>
            HashCode.Combine(value.InstanceId, StringComparer.OrdinalIgnoreCase.GetHashCode(value.Provider), StringComparer.OrdinalIgnoreCase.GetHashCode(value.PackageId));
    }

    public static string ResolveDataDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var newDefault = Path.Combine(localAppData, "DedicatedServerStudio");
        var legacyDefault = Path.Combine(localAppData, "ValheimServerStudio");
        var useLegacyData = File.Exists(Path.Combine(legacyDefault, "studio-state.json")) &&
            !File.Exists(Path.Combine(newDefault, "studio-state.json"));
        return Environment.GetEnvironmentVariable("DSS_DATA_DIR")
            ?? Environment.GetEnvironmentVariable("VALHEIM_STUDIO_DATA_DIR")
            ?? (useLegacyData ? legacyDefault : newDefault);
    }

    public Task<T> ReadAsync<T>(Func<AppData, T> read) => WithLockAsync(data => read(data), save: false);
    public Task<T> UpdateAsync<T>(Func<AppData, T> update) => WithLockAsync(update, save: true);

    public IDisposable UseRevisionActor(Guid? userId)
    {
        var previous = RevisionActor.Value;
        RevisionActor.Value = userId;
        return new RestoreRevisionActor(() => RevisionActor.Value = previous);
    }

    private async Task<T> WithLockAsync<T>(Func<AppData, T> action, bool save)
    {
        await _gate.WaitAsync();
        try
        {
            var originalRevisionCount = save ? _data.Revisions.Count : 0;
            var result = action(_data);
            if (save)
            {
                StampNewRevisions(originalRevisionCount);
                PruneHistory();
                await SaveUnsafeAsync();
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    private void StampNewRevisions(int startIndex)
    {
        if (RevisionActor.Value is not { } actorId || startIndex >= _data.Revisions.Count) return;
        var user = _data.Users.FirstOrDefault(item => item.Id == actorId);
        if (user is null) return;
        var imagePath = Path.Combine(DataDirectory, "user-profile-images", $"{actorId:N}.jpg");
        var imageVersion = File.Exists(imagePath) ? File.GetLastWriteTimeUtc(imagePath).Ticks : (long?)null;
        for (var index = startIndex; index < _data.Revisions.Count; index++)
        {
            var revision = _data.Revisions[index];
            revision.ActorUserId = actorId;
            revision.ActorName = user.Username;
            revision.ActorImageVersion = imageVersion;
        }
    }

    private bool PruneHistory()
    {
        if (_data.Revisions.Count <= MaxHistoryEntriesPerProfile) return false;
        static bool IsGlobal(ConfigRevision revision) => revision.IsGlobal || revision.InstanceId == Guid.Empty ||
            revision.Kind.Equals("server-profile", StringComparison.OrdinalIgnoreCase) &&
            revision.Summary is "Instância criada" or "Perfil criado" or "Perfil importado de JSON";

        var retained = _data.Revisions.Where(IsGlobal)
            .OrderByDescending(revision => revision.CreatedAt).Take(MaxGlobalHistoryEntries)
            .Concat(_data.Revisions.Where(revision => !IsGlobal(revision))
                .GroupBy(revision => revision.InstanceId)
                .SelectMany(group => group.OrderByDescending(revision => revision.CreatedAt).Take(MaxHistoryEntriesPerProfile)))
            .OrderBy(revision => revision.CreatedAt)
            .ToList();
        if (retained.Count == _data.Revisions.Count) return false;
        _data.Revisions = retained;
        return true;
    }

    private sealed class RestoreRevisionActor(Action restore) : IDisposable
    {
        private Action? _restore = restore;
        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }

    private void SaveUnsafe()
    {
        using var stream = new FileStream(_filePath, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        JsonSerializer.Serialize(stream, _data, JsonOptions);
    }

    private async Task SaveUnsafeAsync()
    {
        var tempPath = _filePath + ".tmp";
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await JsonSerializer.SerializeAsync(stream, _data, JsonOptions);
            await stream.FlushAsync();
        }
        File.Move(tempPath, _filePath, overwrite: true);
    }
}
