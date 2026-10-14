using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using DedicatedServerStudio.Models;

namespace DedicatedServerStudio.Services;

public sealed class ModPackageManager(StateStore state, ProviderCatalogService catalog, IHttpClientFactory clients)
{
    public const string BepInExFrameworkPackageId = "denikson-BepInExPack_Valheim";

    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _fileCommitGate = new(1, 1);
    private const long MinimumFreeSpaceReserve = 64L * 1024 * 1024;
    private const int MaxEntries = 10_000;

    public async Task<IReadOnlyList<InstalledMod>> InstallAsync(Guid instanceId, string provider, string packageId,
        CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? dependencyVersions = null,
        Guid? replacingModId = null)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var visited = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var installed = new List<InstalledMod>();
            var selectedVersions = dependencyVersions?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            await InstallTreeCoreAsync(instanceId, NormalizeInstallProvider(provider, packageId), packageId, visited, installed,
                cancellationToken, selectedVersions: selectedVersions, replacingModId: replacingModId);
            return installed;
        }
        finally { _operationGate.Release(); }
    }

    public async Task<IReadOnlyList<InstalledMod>> InstallBatchAsync(Guid instanceId, string provider,
        IReadOnlyList<string> packageIds, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? dependencyVersions = null, Action<ModBatchProgress>? progress = null)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var selectedVersions = dependencyVersions?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var nodes = new Dictionary<string, BatchInstallNode>(StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<string>();
            var queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Require(string nodeProvider, string packageId, string? minimumVersion, bool isRoot = false)
            {
                var key = BatchNodeKey(nodeProvider, packageId);
                if (!nodes.TryGetValue(key, out var node))
                {
                    node = new BatchInstallNode(nodeProvider, packageId);
                    nodes.Add(key, node);
                    pending.Enqueue(key);
                    queued.Add(key);
                }
                node.IsRoot |= isRoot;
                if (minimumVersion is null) return;
                if (node.MinimumVersion is null || IsVersionAtLeast(minimumVersion, node.MinimumVersion))
                {
                    if (node.MinimumVersion is not null && IsVersionAtLeast(node.MinimumVersion, minimumVersion)) return;
                    node.MinimumVersion = minimumVersion;
                    node.Package = null;
                    node.Satisfied = false;
                    if (queued.Add(key)) pending.Enqueue(key);
                }
            }

            foreach (var packageId in packageIds.Distinct(StringComparer.OrdinalIgnoreCase))
                Require(NormalizeInstallProvider(provider, packageId), packageId, null, isRoot: true);

            var profile = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == instanceId));
            if (profile is null) throw new ModInstallException("A instância não existe mais.", HttpStatusCode.NotFound);

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = pending.Dequeue();
                queued.Remove(key);
                var node = nodes[key];
                var selectedVersion = selectedVersions.TryGetValue(node.PackageId, out var chosen) ? chosen : null;
                if (selectedVersion is not null && node.MinimumVersion is not null &&
                    !IsVersionAtLeast(selectedVersion, node.MinimumVersion))
                    throw new ModInstallException($"A versão {selectedVersion} de {node.PackageId} é inferior ao mínimo exigido ({node.MinimumVersion}).");

                var isFramework = node.Provider.Equals("bepinex", StringComparison.OrdinalIgnoreCase);
                var registeredProvider = isFramework ? "bepinex" : node.Provider;
                var existing = await state.ReadAsync(data => data.InstalledMods.FirstOrDefault(item => item.InstanceId == instanceId &&
                    item.Provider.Equals(registeredProvider, StringComparison.OrdinalIgnoreCase) &&
                    item.PackageId.Equals(node.PackageId, StringComparison.OrdinalIgnoreCase)));
                if (existing is { Locked: true })
                {
                    var lockedVersionSatisfies = existing.Enabled && (selectedVersion is not null
                        ? existing.Version.Equals(selectedVersion, StringComparison.OrdinalIgnoreCase)
                        : node.MinimumVersion is null || IsVersionAtLeast(existing.Version, node.MinimumVersion));
                    if (!lockedVersionSatisfies)
                        throw new ModInstallException($"{existing.Name} está bloqueado na versão {existing.Version}. A dependência exige {selectedVersion ?? $"pelo menos {node.MinimumVersion ?? "uma versão ativa"}"}. Desbloqueie o mod para continuar.", HttpStatusCode.Conflict);
                }
                var activeFramework = isFramework && HasActiveBepInEx(profile.InstallPath);
                var existingSatisfies = existing is { Enabled: true } &&
                    (selectedVersion is not null
                        ? existing.Version.Equals(selectedVersion, StringComparison.OrdinalIgnoreCase)
                        : node.MinimumVersion is null || IsVersionAtLeast(existing.Version, node.MinimumVersion));
                // A detected loader satisfies a dependency, but an explicitly selected BepInEx
                // package must still be downloaded at its requested version so it can be adopted
                // into the managed inventory (or updated when its version differs).
                var activeFrameworkSatisfiesDependency = isFramework && activeFramework && !node.IsRoot && selectedVersion is null;
                if ((existingSatisfies || activeFrameworkSatisfiesDependency) &&
                    (!isFramework || activeFramework))
                {
                    node.Package = null;
                    node.Satisfied = true;
                    node.Dependencies.Clear();
                    node.ResolvedMinimumVersion = node.MinimumVersion;
                    continue;
                }

                if (node.ResolvedMinimumVersion is not null && node.Package is not null &&
                    string.Equals(node.ResolvedMinimumVersion, node.MinimumVersion, StringComparison.OrdinalIgnoreCase))
                    continue;

                var lookupProvider = isFramework ? "thunderstore" : node.Provider;
                var package = await catalog.FindPackageAsync(lookupProvider, node.PackageId, cancellationToken,
                    version: selectedVersion, minimumVersion: node.MinimumVersion, includeDownloadLink: true);
                if (package is null)
                    throw new ModInstallException(node.MinimumVersion is null
                        ? $"O pacote {node.PackageId} não foi encontrado no catálogo atual."
                        : $"A dependência {node.PackageId} {node.MinimumVersion} não está disponível no catálogo atual.", HttpStatusCode.NotFound);

                node.Package = package;
                node.InstallProvider = isFramework ? "bepinex" : package.Provider;
                node.Satisfied = false;
                node.ResolvedMinimumVersion = node.MinimumVersion;
                node.Dependencies.Clear();
                foreach (var dependency in package.Dependencies)
                {
                    if (!TryParseDependency(dependency, out var dependencyId, out var dependencyVersion)) continue;
                    var dependencyProvider = DependencyInstallProvider(package.Provider, dependencyId);
                    var dependencyKey = BatchNodeKey(dependencyProvider, dependencyId);
                    node.Dependencies.Add(dependencyKey);
                    Require(dependencyProvider, dependencyId, dependencyVersion);
                }

                var explicitlyDependsOnFramework = package.Dependencies.Any(dependency =>
                    TryParseDependency(dependency, out var dependencyId, out _) &&
                    dependencyId.Equals("denikson-BepInExPack_Valheim", StringComparison.OrdinalIgnoreCase));
                if (!isFramework && !explicitlyDependsOnFramework && !HasActiveBepInEx(profile.InstallPath))
                {
                    const string frameworkId = BepInExFrameworkPackageId;
                    node.Dependencies.Add(BatchNodeKey("bepinex", frameworkId));
                    Require("bepinex", frameworkId, null);
                }
            }

            var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visit = new Stack<string>(packageIds.Select(packageId => BatchNodeKey(NormalizeInstallProvider(provider, packageId), packageId)));
            while (visit.Count > 0)
            {
                var key = visit.Pop();
                if (!reachable.Add(key) || !nodes.TryGetValue(key, out var node)) continue;
                foreach (var dependencyKey in node.Dependencies) visit.Push(dependencyKey);
            }

            var remaining = new HashSet<string>(reachable.Where(key => nodes[key].Package is not null), StringComparer.OrdinalIgnoreCase);
            var totalToInstall = remaining.Count;
            var installed = new List<InstalledMod>();
            var completedCount = 0;
            while (remaining.Count > 0)
            {
                var ready = remaining.Where(key => nodes[key].Dependencies.All(dependencyKey => !remaining.Contains(dependencyKey)))
                    .OrderBy(key => nodes[key].Package!.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                if (ready.Length == 0)
                    throw new ModInstallException("Foi encontrado um ciclo entre as dependências dos mods selecionados.", HttpStatusCode.Conflict);
                foreach (var key in ready)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var node = nodes[key];
                    var package = node.Package!;
                    var packageIndex = ++completedCount;
                    progress?.Invoke(new("download", package.PackageId, package.Name, packageIndex, totalToInstall, 0, package.FileSize));
                    Action<long, long?>? reportDownload = progress is null ? null :
                        (downloaded, total) => progress(new("download", package.PackageId, package.Name, packageIndex, totalToInstall, downloaded, total));
                    installed.Add(await InstallCoreAsync(instanceId, node.InstallProvider!, package, cancellationToken, reportDownload));
                    remaining.Remove(key);
                }
            }
            return installed;
        }
        finally { _operationGate.Release(); }
    }

    private static string BatchNodeKey(string provider, string packageId) =>
        $"{provider.Trim().ToLowerInvariant()}:{packageId.Trim().ToLowerInvariant()}";

    private static string NormalizeInstallProvider(string provider, string packageId) =>
        provider.Equals("bepinex", StringComparison.OrdinalIgnoreCase) ||
        packageId.Equals(BepInExFrameworkPackageId, StringComparison.OrdinalIgnoreCase)
            ? "bepinex" : provider;

    private static string DependencyInstallProvider(string parentProvider, string dependencyId) =>
        dependencyId.Equals(BepInExFrameworkPackageId, StringComparison.OrdinalIgnoreCase)
            ? "bepinex"
            : parentProvider.Equals("ts", StringComparison.OrdinalIgnoreCase) ? "thunderstore"
            : parentProvider.Equals("hex", StringComparison.OrdinalIgnoreCase) ? "hexium"
            : parentProvider;

    private sealed class BatchInstallNode(string provider, string packageId)
    {
        public string Provider { get; } = provider;
        public string PackageId { get; } = packageId;
        public bool IsRoot { get; set; }
        public string? MinimumVersion { get; set; }
        public string? ResolvedMinimumVersion { get; set; }
        public string? InstallProvider { get; set; }
        public CatalogPackage? Package { get; set; }
        public bool Satisfied { get; set; }
        public HashSet<string> Dependencies { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private async Task InstallTreeCoreAsync(Guid instanceId, string provider, string packageId, Dictionary<string, string?> visited,
        List<InstalledMod> installed, CancellationToken cancellationToken, string? requiredVersion = null,
        IReadOnlyDictionary<string, string>? selectedVersions = null, Guid? replacingModId = null)
    {
        var isFramework = provider.Equals("bepinex", StringComparison.OrdinalIgnoreCase);
        var key = $"{provider}:{packageId}";
        if (visited.TryGetValue(key, out var previousVersion))
        {
            if (!string.Equals(previousVersion, requiredVersion, StringComparison.OrdinalIgnoreCase))
                throw new ModInstallException($"Os mods selecionados exigem versões incompatíveis da dependência {packageId}.", HttpStatusCode.Conflict);
            return;
        }
        visited[key] = requiredVersion;
        var lookupProvider = isFramework ? "thunderstore" : provider;
        var requestedVersion = selectedVersions is not null && selectedVersions.TryGetValue(packageId, out var chosenVersion)
            ? chosenVersion : null;
        if (requestedVersion is not null && requiredVersion is not null && !IsVersionAtLeast(requestedVersion, requiredVersion))
            throw new ModInstallException($"A versão {requestedVersion} de {packageId} é inferior ao mínimo exigido ({requiredVersion}).");
        var package = await catalog.FindPackageAsync(lookupProvider, packageId, cancellationToken,
            version: requestedVersion, minimumVersion: requiredVersion, includeDownloadLink: true);
        if (package is null) throw new ModInstallException(requiredVersion is null
            ? $"O pacote {packageId} não foi encontrado no catálogo atual."
            : $"A dependência {packageId} {requiredVersion} não está disponível no catálogo atual.", HttpStatusCode.NotFound);

        var registeredPackageProvider = isFramework ? "bepinex" : package.Provider;
        var lockedPackage = await state.ReadAsync(data => data.InstalledMods.FirstOrDefault(item => item.InstanceId == instanceId &&
            item.Provider.Equals(registeredPackageProvider, StringComparison.OrdinalIgnoreCase) &&
            item.PackageId.Equals(package.PackageId, StringComparison.OrdinalIgnoreCase)));
        if (lockedPackage is { Locked: true })
        {
            var lockedVersionSatisfies = lockedPackage.Enabled && (requestedVersion is not null
                ? lockedPackage.Version.Equals(requestedVersion, StringComparison.OrdinalIgnoreCase)
                : requiredVersion is null || IsVersionAtLeast(lockedPackage.Version, requiredVersion));
            if (!lockedVersionSatisfies)
                throw new ModInstallException($"{lockedPackage.Name} está bloqueado na versão {lockedPackage.Version}; a instalação exige {requestedVersion ?? (requiredVersion is null ? package.Version : $"pelo menos {requiredVersion}")}. Desbloqueie o mod para alterar a versão exigida.", HttpStatusCode.Conflict);
            installed.Add(lockedPackage);
            return;
        }

        foreach (var dependency in package.Dependencies)
        {
            if (!TryParseDependency(dependency, out var dependencyId, out var dependencyVersion)) continue;
            var dependencyProvider = DependencyInstallProvider(package.Provider, dependencyId);
            var isBepInExDependency = dependencyProvider.Equals("bepinex", StringComparison.OrdinalIgnoreCase);
            var registeredProvider = dependencyProvider.Equals("bepinex", StringComparison.OrdinalIgnoreCase) ? "bepinex" : package.Provider;
            var current = await state.ReadAsync(data => data.InstalledMods.FirstOrDefault(item => item.InstanceId == instanceId &&
                item.Provider.Equals(registeredProvider, StringComparison.OrdinalIgnoreCase) && item.PackageId.Equals(dependencyId, StringComparison.OrdinalIgnoreCase)));
            if (isBepInExDependency && current is null)
            {
                var profile = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == instanceId));
                if (profile is not null && HasActiveBepInEx(profile.InstallPath)) continue;
            }
            var selectedDependencyVersion = requestedVersionForDependency(selectedVersions, dependencyId);
            if (current is not null && current.Enabled && (selectedDependencyVersion is not null
                ? current.Version.Equals(selectedDependencyVersion, StringComparison.OrdinalIgnoreCase)
                : IsVersionAtLeast(current.Version, dependencyVersion))) continue;
            await InstallTreeCoreAsync(instanceId, dependencyProvider, dependencyId, visited, installed, cancellationToken,
                dependencyVersion, selectedVersions);
        }

        if (!isFramework && !package.Dependencies.Any(dependency => TryParseDependency(dependency, out var dependencyId, out _) &&
                dependencyId.Equals(BepInExFrameworkPackageId, StringComparison.OrdinalIgnoreCase)))
        {
            var framework = await state.ReadAsync(data => data.InstalledMods.FirstOrDefault(item => item.InstanceId == instanceId &&
                item.Provider.Equals("bepinex", StringComparison.OrdinalIgnoreCase) &&
                item.PackageId.Equals(BepInExFrameworkPackageId, StringComparison.OrdinalIgnoreCase)));
            var profileForFramework = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == instanceId));
            if (profileForFramework is not null && !HasActiveBepInEx(profileForFramework.InstallPath))
                await InstallTreeCoreAsync(instanceId, "bepinex", BepInExFrameworkPackageId, visited, installed,
                    cancellationToken, selectedVersions: selectedVersions);
        }

        var registered = isFramework ? "bepinex" : package.Provider;
        var existing = await state.ReadAsync(data => data.InstalledMods.FirstOrDefault(item => item.InstanceId == instanceId &&
            item.Provider.Equals(registered, StringComparison.OrdinalIgnoreCase) && item.PackageId.Equals(package.PackageId, StringComparison.OrdinalIgnoreCase)));
        var frameworkActive = !isFramework;
        if (isFramework)
        {
            var frameworkProfile = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == instanceId));
            frameworkActive = frameworkProfile is not null && HasActiveBepInEx(frameworkProfile.InstallPath);
        }
        if (existing is not null && existing.Enabled && existing.Version.Equals(package.Version, StringComparison.OrdinalIgnoreCase) &&
            frameworkActive)
        {
            installed.Add(existing);
            return;
        }
        if (existing is { Enabled: false }) await SetEnabledCoreAsync(instanceId, existing.Id, true);
        installed.Add(await InstallCoreAsync(instanceId, isFramework ? "bepinex" : package.Provider, package,
            cancellationToken, replacingModId: replacingModId));
    }

    private static string? requestedVersionForDependency(IReadOnlyDictionary<string, string>? selectedVersions, string packageId) =>
        selectedVersions is not null && selectedVersions.TryGetValue(packageId, out var version) ? version : null;

    private static bool IsVersionAtLeast(string candidate, string minimum)
    {
        static (Version? Core, bool Prerelease, string Raw) Parse(string value)
        {
            var normalized = value.Trim().TrimStart('v', 'V');
            var core = normalized.Split(['-', '+'], 2, StringSplitOptions.TrimEntries)[0];
            Version.TryParse(core, out var parsed);
            return (parsed, normalized.Contains('-'), normalized);
        }
        var left = Parse(candidate);
        var right = Parse(minimum);
        if (left.Core is not null && right.Core is not null)
        {
            var comparison = left.Core.CompareTo(right.Core);
            if (comparison != 0) return comparison > 0;
            if (left.Prerelease != right.Prerelease) return !left.Prerelease;
            if (left.Prerelease) return StringComparer.OrdinalIgnoreCase.Compare(left.Raw, right.Raw) >= 0;
            return true;
        }
        return StringComparer.OrdinalIgnoreCase.Compare(left.Raw, right.Raw) >= 0;
    }

    private async Task<InstalledMod> InstallCoreAsync(Guid instanceId, string provider, CatalogPackage package, CancellationToken cancellationToken,
        Action<long, long?>? downloadProgress = null, Guid? replacingModId = null)
    {
        var isFramework = provider.Equals("bepinex", StringComparison.OrdinalIgnoreCase);
        var profile = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == instanceId));
        if (profile is null) throw new ModInstallException("A instância não existe mais.", HttpStatusCode.NotFound);
        if (!Path.IsPathFullyQualified(profile.InstallPath) || !Directory.Exists(profile.InstallPath))
            throw new ModInstallException("Configure uma pasta de servidor válida antes de instalar mods.");
        if (!isFramework && (Directory.Exists(Path.Combine(profile.InstallPath, "BepInEx")) is false ||
            Directory.Exists(Path.Combine(profile.InstallPath, "BepInEx", "plugins")) is false)
           )
            throw new ModInstallException("Instale o BepInEx neste servidor e execute-o uma vez antes de adicionar mods.");

        if (string.IsNullOrWhiteSpace(package.DownloadUrl) || !TryValidateDownloadUrl(package.DownloadUrl, package.Provider, out var downloadUri))
            throw new ModInstallException("O provedor não publicou um endereço HTTPS de download compatível com instalação direta. Abra a página do pacote e instale-o manualmente.", HttpStatusCode.NotImplemented);

        var registeredProvider = isFramework ? "bepinex" : package.Provider;
        var providerExisting = await state.ReadAsync(data => data.InstalledMods.FirstOrDefault(item => item.InstanceId == instanceId &&
            item.Provider.Equals(registeredProvider, StringComparison.OrdinalIgnoreCase) && item.PackageId.Equals(package.PackageId, StringComparison.OrdinalIgnoreCase)));
        var replacement = replacingModId is null ? null : await state.ReadAsync(data => data.InstalledMods.FirstOrDefault(item =>
            item.InstanceId == instanceId && item.Id == replacingModId.Value));
        if (replacingModId is not null && (replacement is null || !replacement.PackageId.Equals(package.PackageId, StringComparison.OrdinalIgnoreCase)))
            throw new ModInstallException("O mod selecionado mudou no inventário. Atualize a lista e tente novamente.", HttpStatusCode.Conflict);
        if (replacement is { Locked: true })
            throw new ModInstallException($"{replacement.Name} está bloqueado. Desbloqueie-o antes de trocar de provedor ou versão.", HttpStatusCode.Conflict);
        if (replacement is not null && providerExisting is not null && replacement.Id != providerExisting.Id)
            throw new ModInstallException("Este pacote já está instalado pelo provedor escolhido. Atualize o inventário antes de trocar a origem.", HttpStatusCode.Conflict);
        var existing = replacement ?? providerExisting;
        var managedSet = existing?.ManagedFiles.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stageRoot = Path.Combine(state.DataDirectory, "staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stageRoot);
        var archivePath = Path.Combine(stageRoot, "package.zip");
        try
        {
            await DownloadAsync(downloadUri!, archivePath, cancellationToken, downloadProgress, package.FileSize, package.Provider);
            var archiveHash = await HashFileAsync(archivePath, cancellationToken);
            var payload = ReadPayload(archivePath, stageRoot, isFramework);
            var installRoot = Path.GetFullPath(profile.InstallPath);
            if (payload.Count == 0) throw new ModInstallException(isFramework
                ? "O pacote BepInEx não contém os arquivos esperados dentro de BepInExPack_Valheim."
                : "O pacote não contém arquivos reconhecidos (plugins/, config/, BepInEx/plugins/, BepInEx/config/ ou uma DLL na raiz).");
            EnsureAvailableSpace(installRoot, payload.Sum(file => new FileInfo(file.StagedPath).Length),
                "Não há espaço livre suficiente na unidade do servidor para instalar este mod.");

            var transaction = Path.Combine(stageRoot, "transaction");
            var backups = new List<(string Target, string Backup)>();
            var created = new List<string>();
            var installedPayload = new List<StagedPackageFile>();
            await _fileCommitGate.WaitAsync(cancellationToken);
            try
            {
                if (payload.Select(item => item.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != payload.Count)
                    throw new ModInstallException("O ZIP contém caminhos duplicados que diferem apenas por maiúsculas/minúsculas.");

                foreach (var file in payload)
                {
                    var target = ResolveTarget(installRoot, file.RelativePath);
                    RejectReparsePoints(installRoot, target);
                    if (File.Exists(target))
                    {
                        // Existing BepInEx defaults can be user configuration left behind
                        // after the loader package was removed from DSS. Preserve them,
                        // regardless of whether they are still present in the inventory.
                        if (isFramework && IsFrameworkConfig(file.RelativePath) && !managedSet.Contains(file.RelativePath))
                            continue;
                        if (!managedSet.Contains(file.RelativePath) && !isFramework)
                            throw new ModInstallException($"O arquivo {file.RelativePath} já existe e não pertence a este pacote. Nenhum arquivo foi substituído.", HttpStatusCode.Conflict);
                        if (managedSet.Contains(file.RelativePath))
                        {
                            var expectedHash = FindTrackedHash(existing!, file.RelativePath);
                            if (expectedHash is null || !string.Equals(expectedHash, HashFile(target), StringComparison.OrdinalIgnoreCase))
                                throw new ModInstallException($"O arquivo gerenciado {file.RelativePath} foi editado após a instalação. Faça backup/revisão antes de atualizar.", HttpStatusCode.Conflict);
                        }
                    }
                }
                foreach (var file in payload)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var target = ResolveTarget(installRoot, file.RelativePath);
                    RejectReparsePoints(installRoot, target);
                    // Recheck at commit time too: a config can appear after preflight.
                    // Never overwrite a user-owned default BepInEx config in that race.
                    if (File.Exists(target) && isFramework && IsFrameworkConfig(file.RelativePath) && !managedSet.Contains(file.RelativePath))
                        continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (File.Exists(target))
                    {
                        var backup = Path.Combine(transaction, "backups", file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                        File.Move(target, backup, overwrite: true);
                        backups.Add((target, backup));
                    }
                    File.Move(file.StagedPath, target, overwrite: true);
                    created.Add(target);
                    installedPayload.Add(file);
                }

                if (isFramework)
                {
                    foreach (var relativeDirectory in new[] { "BepInEx/plugins", "BepInEx/config" })
                    {
                        var directory = ResolveTarget(installRoot, relativeDirectory);
                        RejectReparsePoints(installRoot, Path.Combine(directory, ".dss-directory-check"));
                        Directory.CreateDirectory(directory);
                    }
                }

                var installed = new InstalledMod
                {
                    Id = existing?.Id ?? Guid.NewGuid(), InstanceId = instanceId, Provider = registeredProvider,
                    SourceProvider = package.Provider,
                    PackageId = package.PackageId, Name = package.Name, Version = package.Version, Description = package.Description,
                    FileSize = package.FileSize,
                    Owner = package.Owner, IconUrl = package.IconUrl, ProjectUrl = package.ProjectUrl,
                    CreatedAt = package.CreatedAt, UpdatedAt = package.UpdatedAt, RatingScore = package.RatingScore,
                    Categories = (package.Categories ?? []).ToList(),
                    Sha256 = archiveHash, ManagedFiles = installedPayload.Select(item => item.RelativePath).ToList(),
                    FileHashes = installedPayload.ToDictionary(item => item.RelativePath, item => item.Sha256, StringComparer.OrdinalIgnoreCase),
                    Dependencies = package.Dependencies.ToList(), DependencyMetadataKnown = true,
                    Enabled = true, Locked = existing?.Locked ?? false, IsFramework = isFramework,
                    InstalledAt = existing?.InstalledAt ?? DateTimeOffset.UtcNow
                };
                await state.UpdateAsync(data =>
                {
                    data.InstalledMods.RemoveAll(item => item.InstanceId == instanceId &&
                        (item.Id == replacingModId || (item.Provider.Equals(registeredProvider, StringComparison.OrdinalIgnoreCase) &&
                            item.PackageId.Equals(package.PackageId, StringComparison.OrdinalIgnoreCase))));
                    data.InstalledMods.Add(installed);
                    return true;
                });
                if (existing is not null)
                {
                    var newFiles = installed.ManagedFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var oldFile in existing.ManagedFiles.Where(path => !newFiles.Contains(path)))
                    {
                        try
                        {
                            var oldPath = ResolveTarget(installRoot, oldFile);
                            RejectReparsePoints(installRoot, oldPath);
                            var oldHash = FindTrackedHash(existing, oldFile);
                            if (oldHash is not null && File.Exists(oldPath) && string.Equals(oldHash, HashFile(oldPath), StringComparison.OrdinalIgnoreCase))
                                File.Delete(oldPath);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        { /* A safe, committed update wins; stale files can be cleaned up manually. */ }
                    }
                }
                return installed;
            }
            catch
            {
                foreach (var target in created) if (File.Exists(target)) File.Delete(target);
                foreach (var (target, backup) in backups)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (File.Exists(backup)) File.Move(backup, target, overwrite: true);
                }
                throw;
            }
            finally { _fileCommitGate.Release(); }
        }
        finally
        {
            try { Directory.Delete(stageRoot, recursive: true); } catch (IOException) { }
        }
    }

    public async Task<(int Removed, int Preserved)> UninstallAsync(Guid instanceId, Guid modId)
    {
        await _operationGate.WaitAsync();
        try
        {
            var dependents = await GetDependentsCoreAsync(instanceId, modId, CancellationToken.None);
            if (dependents.Count != 0)
                throw new ModInstallException($"Não é permitido remover este pacote enquanto houver mods dependentes: {string.Join(", ", dependents.Select(item => item.Name))}.", HttpStatusCode.Conflict);
            return await UninstallCoreAsync(instanceId, modId);
        }
        finally { _operationGate.Release(); }
    }

    public async Task<IReadOnlyList<BatchUninstallResult>> UninstallBatchAsync(Guid instanceId, IReadOnlyList<Guid> modIds,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var ids = modIds.Distinct().ToHashSet();
            var installed = await state.ReadAsync(data => data.InstalledMods.Where(item => item.InstanceId == instanceId).ToArray());
            var selected = installed.Where(item => ids.Contains(item.Id)).ToArray();
            if (selected.Length != ids.Count)
                throw new ModInstallException("Um ou mais mods selecionados não estão mais no inventário.", HttpStatusCode.NotFound);
            if (selected.Length == 0)
                throw new ModInstallException("Selecione ao menos um mod para remover.");
            // Resolve dependency metadata for every selected package before considering locks.
            // A locked dependent must still block deleting its parent package.
            foreach (var mod in selected)
                _ = await GetDependentsCoreAsync(instanceId, mod.Id, cancellationToken);
            installed = await state.ReadAsync(data => data.InstalledMods.Where(item => item.InstanceId == instanceId).ToArray());
            selected = installed.Where(item => ids.Contains(item.Id)).ToArray();
            var profile = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == instanceId));
            InstalledMod[] installedOnDisk = profile is null ? [] : installed.Where(item => HasManagedPayload(profile.InstallPath, item)).ToArray();
            var selectedIds = selected.Select(item => item.Id).ToHashSet();
            var dependentsByDependency = new Dictionary<string, List<InstalledMod>>(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in installedOnDisk)
            {
                foreach (var raw in mod.Dependencies)
                {
                    if (!TryParseDependency(raw, out var packageId, out _)) continue;
                    var dependencyProvider = packageId.Equals(BepInExFrameworkPackageId, StringComparison.OrdinalIgnoreCase)
                        ? "bepinex" : mod.Provider;
                    var key = DependencyKey(dependencyProvider, packageId);
                    if (!dependentsByDependency.TryGetValue(key, out var dependents))
                        dependentsByDependency[key] = dependents = [];
                    dependents.Add(mod);
                }
            }

            var externalDependents = new Dictionary<Guid, InstalledMod>();
            foreach (var target in selected)
            {
                var queue = new Queue<InstalledMod>();
                var visited = new HashSet<Guid> { target.Id };
                queue.Enqueue(target);
                while (queue.Count > 0)
                {
                    var dependency = queue.Dequeue();
                    var key = DependencyKey(dependency.Provider, dependency.PackageId);
                    if (!dependentsByDependency.TryGetValue(key, out var dependents)) continue;
                    foreach (var dependent in dependents)
                    {
                        if (!visited.Add(dependent.Id)) continue;
                        if (!selectedIds.Contains(dependent.Id)) externalDependents[dependent.Id] = dependent;
                        else queue.Enqueue(dependent);
                    }
                }
            }
            if (externalDependents.Count > 0)
                throw new ModInstallException($"A remoção foi bloqueada porque estes mods não selecionados dependem dos pacotes: {string.Join(", ", externalDependents.Values.Select(item => item.Name).Order(StringComparer.OrdinalIgnoreCase))}.", HttpStatusCode.Conflict);

            var locked = selected.Where(item => item.Locked && profile is not null && HasManagedPayload(profile.InstallPath, item))
                .Select(item => item.Name).ToArray();
            if (locked.Length > 0)
                throw new ModInstallException($"Desbloqueie estes mods selecionados antes de removê-los: {string.Join(", ", locked)}.", HttpStatusCode.Conflict);

            var results = await Task.WhenAll(selected.Select(async mod =>
            {
                try
                {
                    var files = await UninstallFilesCoreAsync(instanceId, mod.Id, cancellationToken);
                    return new BatchUninstallResult(mod.Id, mod.Name, files.Removed, files.Preserved, null);
                }
                catch (Exception ex) when (ex is ModInstallException or IOException or UnauthorizedAccessException)
                {
                    return new BatchUninstallResult(mod.Id, mod.Name, 0, 0, ex.Message);
                }
            }));

            var removedIds = results.Where(result => result.Error is null).Select(result => result.ModId).ToHashSet();
            if (removedIds.Count > 0)
            {
                var installProfile = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == instanceId));
                if (installProfile is not null && Path.IsPathFullyQualified(installProfile.InstallPath))
                    foreach (var mod in selected.Where(item => removedIds.Contains(item.Id) && !item.IsFramework))
                        PruneEmptyManagedDirectories(Path.GetFullPath(installProfile.InstallPath), mod.ManagedFiles);
                await state.UpdateAsync(data => { data.InstalledMods.RemoveAll(item => item.InstanceId == instanceId && removedIds.Contains(item.Id)); return true; });
            }
            return results;
        }
        finally { _operationGate.Release(); }
    }

    public Task<IReadOnlyList<ModDependentView>> GetDependentsAsync(Guid instanceId, Guid modId, CancellationToken cancellationToken = default) =>
        GetDependentsCoreAsync(instanceId, modId, cancellationToken);

    public async Task<bool> SetEnabledAsync(Guid instanceId, Guid modId, bool enabled)
    {
        await _operationGate.WaitAsync();
        try
        {
            var dependents = await GetDependentsCoreAsync(instanceId, modId, CancellationToken.None);
            var activeIds = await state.ReadAsync(data => data.InstalledMods
                .Where(item => item.InstanceId == instanceId && item.Enabled)
                .Select(item => item.Id)
                .ToHashSet());
            if (!enabled && dependents.Any(item => activeIds.Contains(item.Id)))
                throw new ModInstallException("Este mod possui dependentes. Confirme a inativação em cascata para desativá-los também.", HttpStatusCode.Conflict);
            return await SetEnabledCoreAsync(instanceId, modId, enabled);
        }
        finally { _operationGate.Release(); }
    }

    public async Task<bool> SetLockedAsync(Guid instanceId, Guid modId, bool locked)
    {
        await _operationGate.WaitAsync();
        try
        {
            return await state.UpdateAsync(data =>
            {
                var mod = data.InstalledMods.FirstOrDefault(item => item.InstanceId == instanceId && item.Id == modId);
                if (mod is null) return false;
                mod.Locked = locked;
                return true;
            });
        }
        finally { _operationGate.Release(); }
    }

    public async Task<int> SetLockedBatchAsync(Guid instanceId, IReadOnlyCollection<Guid> modIds, bool locked)
    {
        await _operationGate.WaitAsync();
        try
        {
            var ids = modIds.Where(id => id != Guid.Empty).ToHashSet();
            return await state.UpdateAsync(data =>
            {
                var changed = 0;
                foreach (var mod in data.InstalledMods.Where(item => item.InstanceId == instanceId && ids.Contains(item.Id)))
                {
                    if (mod.Locked == locked) continue;
                    mod.Locked = locked;
                    changed++;
                }
                return changed;
            });
        }
        finally { _operationGate.Release(); }
    }

    public async Task<IReadOnlyList<ModDependentView>> SetEnabledWithDependentsAsync(Guid instanceId, Guid modId, bool enabled,
        bool cascadeDependents, CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            IReadOnlyList<ModDependentView> related = enabled
                ? await GetDependenciesCoreAsync(instanceId, modId, cancellationToken)
                : await GetDependentsCoreAsync(instanceId, modId, cancellationToken);
            var installedForLockCheck = await state.ReadAsync(data => data.InstalledMods
                .Where(item => item.InstanceId == instanceId).ToDictionary(item => item.Id));
            if (!enabled)
            {
                var activeIds = installedForLockCheck.Values.Where(item => item.Enabled).Select(item => item.Id).ToHashSet();
                related = related.Where(item => activeIds.Contains(item.Id)).ToArray();
            }
            if (!enabled && related.Count != 0 && !cascadeDependents)
                throw new ModInstallException("Este mod possui dependentes. Confirme a inativação em cascata para desativá-los também.", HttpStatusCode.Conflict);
            var targetForLockCheck = installedForLockCheck.GetValueOrDefault(modId)
                ?? throw new ModInstallException("O pacote não está no inventário gerenciado.", HttpStatusCode.NotFound);
            var changingRelatedIds = related.Select(item => item.Id)
                .Where(id => installedForLockCheck.TryGetValue(id, out var item) && item.Enabled != enabled)
                .ToHashSet();
            if (targetForLockCheck.Enabled != enabled) changingRelatedIds.Add(modId);
            var lockedNames = changingRelatedIds.Select(id => installedForLockCheck[id]).Where(item => item.Locked).Select(item => item.Name).ToArray();
            if (lockedNames.Length > 0)
                throw new ModInstallException($"Desbloqueie estes mods antes de ativar ou desativar: {string.Join(", ", lockedNames)}.", HttpStatusCode.Conflict);
            var changed = new List<Guid>();
            try
            {
                if (!enabled)
                {
                    foreach (var dependent in related.OrderByDescending(item => item.Depth))
                    {
                        var current = await state.ReadAsync(data => data.InstalledMods.FirstOrDefault(item => item.InstanceId == instanceId && item.Id == dependent.Id));
                        if (current is { Enabled: true }) { await SetEnabledCoreAsync(instanceId, dependent.Id, false); changed.Add(dependent.Id); }
                    }
                }
                else
                {
                    // Enable the full dependency tree before the selected package.
                    foreach (var dependency in related.OrderByDescending(item => item.Depth))
                    {
                        var current = await state.ReadAsync(data => data.InstalledMods.FirstOrDefault(item => item.InstanceId == instanceId && item.Id == dependency.Id));
                        if (current is { Enabled: false }) { await SetEnabledCoreAsync(instanceId, dependency.Id, true); changed.Add(dependency.Id); }
                    }
                }
                var target = await state.ReadAsync(data => data.InstalledMods.FirstOrDefault(item => item.InstanceId == instanceId && item.Id == modId));
                if (target is null) throw new ModInstallException("O pacote não está no inventário gerenciado.", HttpStatusCode.NotFound);
                if (target.Enabled != enabled) { await SetEnabledCoreAsync(instanceId, modId, enabled); changed.Add(modId); }
                return related;
            }
            catch
            {
                foreach (var changedId in changed.AsEnumerable().Reverse())
                {
                    try { await SetEnabledCoreAsync(instanceId, changedId, !enabled); }
                    catch (Exception ex) when (ex is ModInstallException or IOException or UnauthorizedAccessException) { }
                }
                throw;
            }
        }
        finally { _operationGate.Release(); }
    }

    private async Task<IReadOnlyList<ModDependentView>> GetDependenciesCoreAsync(Guid instanceId, Guid modId, CancellationToken cancellationToken)
    {
        var installed = await state.ReadAsync(data => data.InstalledMods.Where(item => item.InstanceId == instanceId).ToArray());
        var target = installed.FirstOrDefault(item => item.Id == modId);
        if (target is null) throw new ModInstallException("O pacote não está no inventário gerenciado.", HttpStatusCode.NotFound);

        var metadataUpdates = new Dictionary<Guid, List<string>>();
        async Task<IReadOnlyList<string>> GetDependenciesForAsync(InstalledMod mod)
        {
            if (metadataUpdates.TryGetValue(mod.Id, out var refreshed)) return refreshed;
            if (mod.DependencyMetadataKnown) return mod.Dependencies;
            var provider = mod.Provider.Equals("bepinex", StringComparison.OrdinalIgnoreCase) ? "thunderstore" : mod.Provider;
            CatalogPackage? metadata;
            try
            {
                metadata = await catalog.FindPackageAsync(provider, mod.PackageId, cancellationToken, version: mod.Version)
                    ?? await catalog.FindPackageAsync(provider, mod.PackageId, cancellationToken);
            }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException or TaskCanceledException or JsonException)
            {
                throw new ModInstallException($"Não foi possível verificar as dependências de {mod.Name}. Tente novamente quando o catálogo estiver disponível.", HttpStatusCode.ServiceUnavailable);
            }
            if (metadata is null)
                throw new ModInstallException($"O catálogo não forneceu as dependências de {mod.Name}; a ativação foi bloqueada para evitar um perfil incompleto.", HttpStatusCode.ServiceUnavailable);
            metadataUpdates[mod.Id] = metadata.Dependencies.ToList();
            return metadataUpdates[mod.Id];
        }
        var currentByKey = installed.GroupBy(item => DependencyKey(item.Provider, item.PackageId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<Guid, ModDependentView>();
        var visited = new HashSet<Guid> { target.Id };
        async Task Visit(InstalledMod parent, int depth)
        {
            var dependencies = await GetDependenciesForAsync(parent);
            foreach (var raw in dependencies)
            {
                if (!TryParseDependency(raw, out var packageId, out var minimumVersion)) continue;
                var isBepInEx = packageId.Equals(BepInExFrameworkPackageId, StringComparison.OrdinalIgnoreCase);
                var provider = isBepInEx ? "bepinex" : parent.Provider;
                var key = DependencyKey(provider, packageId);
                if (!currentByKey.TryGetValue(key, out var dependency))
                {
                    if (isBepInEx)
                    {
                        var profile = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == instanceId));
                        if (profile is not null && HasActiveBepInEx(profile.InstallPath)) continue;
                    }
                    throw new ModInstallException($"A dependência {packageId} {minimumVersion} não está instalada neste perfil. Instale-a pelo catálogo antes de ativar {target.Name}.", HttpStatusCode.Conflict);
                }
                if (!IsVersionAtLeast(dependency.Version, minimumVersion))
                    throw new ModInstallException($"A dependência {dependency.Name} está na versão {dependency.Version}, abaixo do mínimo {minimumVersion}. Atualize-a antes de ativar {target.Name}.", HttpStatusCode.Conflict);
                if (!visited.Add(dependency.Id)) continue;
                result[dependency.Id] = new(dependency.Id, dependency.PackageId, dependency.Name, dependency.Version, dependency.Provider, depth);
                await Visit(dependency, depth + 1);
            }
        }
        await Visit(target, 1);
        if (metadataUpdates.Count != 0)
            await state.UpdateAsync(data =>
            {
                foreach (var (id, dependencies) in metadataUpdates)
                {
                    var saved = data.InstalledMods.FirstOrDefault(item => item.InstanceId == instanceId && item.Id == id);
                    if (saved is not null) { saved.Dependencies = dependencies; saved.DependencyMetadataKnown = true; }
                }
                return true;
            });
        return result.Values.OrderByDescending(item => item.Depth).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task<IReadOnlyList<ModDependentView>> GetDependentsCoreAsync(Guid instanceId, Guid modId, CancellationToken cancellationToken)
    {
        var installed = await state.ReadAsync(data => data.InstalledMods.Where(item => item.InstanceId == instanceId).ToArray());
        var target = installed.FirstOrDefault(item => item.Id == modId);
        if (target is null) throw new ModInstallException("O pacote não está no inventário gerenciado.", HttpStatusCode.NotFound);
        var profile = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == instanceId));
        if (profile is null) throw new ModInstallException("A instância não existe mais.", HttpStatusCode.NotFound);
        if (!HasManagedPayload(profile.InstallPath, target)) return [];
        // Inventory records can outlive files removed outside the DSS. Only packages with
        // managed files still present in this profile can block dependency removal.
        installed = installed.Where(item => HasManagedPayload(profile.InstallPath, item)).ToArray();
        var metadataUpdates = new Dictionary<Guid, List<string>>();
        foreach (var mod in installed.Where(item => !item.DependencyMetadataKnown))
        {
            var provider = mod.Provider.Equals("bepinex", StringComparison.OrdinalIgnoreCase) ? "thunderstore" : mod.Provider;
            CatalogPackage? metadata;
            try
            {
                metadata = await catalog.FindPackageAsync(provider, mod.PackageId, cancellationToken, version: mod.Version)
                    ?? await catalog.FindPackageAsync(provider, mod.PackageId, cancellationToken);
            }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException or TaskCanceledException or JsonException)
            {
                throw new ModInstallException($"Não foi possível verificar as dependências de {mod.Name}. Tente novamente quando o catálogo estiver disponível.", HttpStatusCode.ServiceUnavailable);
            }
            if (metadata is null)
                throw new ModInstallException($"O catálogo não forneceu as dependências de {mod.Name}; a alteração foi bloqueada para proteger os mods que dependem dele.", HttpStatusCode.ServiceUnavailable);
            metadataUpdates[mod.Id] = metadata.Dependencies.ToList();
        }
        if (metadataUpdates.Count != 0)
            await state.UpdateAsync(data =>
            {
                foreach (var (id, dependencies) in metadataUpdates)
                {
                    var saved = data.InstalledMods.FirstOrDefault(item => item.InstanceId == instanceId && item.Id == id);
                    if (saved is not null) { saved.Dependencies = dependencies; saved.DependencyMetadataKnown = true; }
                }
                return true;
            });

        var dependenciesByKey = new Dictionary<string, List<InstalledMod>>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in installed)
        {
            var rawDependencies = metadataUpdates.TryGetValue(mod.Id, out var updated) ? updated : mod.Dependencies;
            foreach (var raw in rawDependencies)
            {
                if (!TryParseDependency(raw, out var packageId, out _)) continue;
                    var dependencyProvider = packageId.Equals(BepInExFrameworkPackageId, StringComparison.OrdinalIgnoreCase)
                    ? "bepinex" : mod.Provider;
                var key = DependencyKey(dependencyProvider, packageId);
                if (!dependenciesByKey.TryGetValue(key, out var dependents)) dependenciesByKey[key] = dependents = [];
                if (!dependents.Any(item => item.Id == mod.Id)) dependents.Add(mod);
            }
        }

        var result = new Dictionary<Guid, ModDependentView>();
        var frontier = new List<(InstalledMod Mod, int Depth)> { (target, 0) };
        var visited = new HashSet<Guid> { target.Id };
        while (frontier.Count != 0)
        {
            var next = new List<(InstalledMod Mod, int Depth)>();
            foreach (var (dependency, depth) in frontier)
            {
                var key = DependencyKey(dependency.Provider, dependency.PackageId);
                if (!dependenciesByKey.TryGetValue(key, out var children)) continue;
                foreach (var child in children)
                {
                    if (!visited.Add(child.Id)) continue;
                    var childDepth = depth + 1;
                    result[child.Id] = new(child.Id, child.PackageId, child.Name, child.Version, child.Provider, childDepth);
                    next.Add((child, childDepth));
                }
            }
            frontier = next;
        }
        return result.Values.OrderBy(item => item.Depth).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool HasManagedPayload(string installPath, InstalledMod mod)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Path.IsPathFullyQualified(installPath) || mod.ManagedFiles is null || mod.ManagedFiles.Count == 0)
            return false;
        try
        {
            var root = Path.GetFullPath(installPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(root)) return false;
            var prefix = root + Path.DirectorySeparatorChar;
            foreach (var relative in mod.ManagedFiles)
            {
                if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) continue;
                var candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(candidate) || File.Exists(candidate + ".dss-disabled")) return true;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
        return false;
    }

    private static string DependencyKey(string provider, string packageId) =>
        $"{(provider.Equals("bepinex", StringComparison.OrdinalIgnoreCase) ? "bepinex" : provider).ToLowerInvariant()}:{packageId.ToLowerInvariant()}";

    private async Task<bool> SetEnabledCoreAsync(Guid instanceId, Guid modId, bool enabled)
    {
            var mod = await state.ReadAsync(data => data.InstalledMods.FirstOrDefault(item => item.InstanceId == instanceId && item.Id == modId));
            if (mod is null) throw new ModInstallException("O pacote não está no inventário gerenciado.", HttpStatusCode.NotFound);
            if (mod.Enabled == enabled) return mod.Enabled;
            if (mod.Locked) throw new ModInstallException($"{mod.Name} está bloqueado. Desbloqueie-o antes de ativar ou desativar.", HttpStatusCode.Conflict);
            var profile = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == instanceId));
            if (profile is null || !Path.IsPathFullyQualified(profile.InstallPath) || !Directory.Exists(profile.InstallPath))
                throw new ModInstallException("A pasta do servidor não está disponível.");
            var root = Path.GetFullPath(profile.InstallPath);
            var candidates = mod.ManagedFiles.Where(relative =>
            {
                var leaf = Path.GetFileName(relative);
                return mod.IsFramework
                    ? leaf.Equals("winhttp.dll", StringComparison.OrdinalIgnoreCase)
                    : leaf.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            }).ToArray();
            if (candidates.Length == 0) throw new ModInstallException("Este pacote não tem DLLs gerenciadas que possam ser desativadas.");
            var changes = new List<(string Source, string Target)>();
            foreach (var relative in candidates)
            {
                var active = ResolveTarget(root, relative);
                var inactive = active + ".dss-disabled";
                var source = enabled ? inactive : active;
                var target = enabled ? active : inactive;
                if (!File.Exists(source))
                {
                    if (!File.Exists(target)) continue;
                    continue;
                }
                RejectReparsePoints(root, source);
                var hash = FindTrackedHash(mod, relative);
                if (hash is null || !string.Equals(hash, await HashFileAsync(source, CancellationToken.None), StringComparison.OrdinalIgnoreCase))
                    throw new ModInstallException($"O arquivo {relative} foi alterado depois da instalação. Ele não será renomeado automaticamente.", HttpStatusCode.Conflict);
                if (File.Exists(target)) throw new ModInstallException($"Já existe um arquivo no destino {Path.GetFileName(target)}.", HttpStatusCode.Conflict);
                changes.Add((source, target));
            }
            if (changes.Count == 0) throw new ModInstallException("Nenhuma DLL gerenciada foi encontrada para alterar.", HttpStatusCode.Conflict);
            var moved = new List<(string Source, string Target)>();
            try
            {
                foreach (var change in changes)
                {
                    File.Move(change.Source, change.Target);
                    moved.Add(change);
                }
            }
            catch
            {
                foreach (var change in moved.AsEnumerable().Reverse())
                    if (File.Exists(change.Target) && !File.Exists(change.Source)) File.Move(change.Target, change.Source);
                throw;
            }
            await state.UpdateAsync(data =>
            {
                var stored = data.InstalledMods.FirstOrDefault(item => item.Id == modId);
                if (stored is not null) stored.Enabled = enabled;
                return true;
            });
            return enabled;
    }

    private async Task<(int Removed, int Preserved)> UninstallCoreAsync(Guid instanceId, Guid modId)
    {
        var result = await UninstallFilesCoreAsync(instanceId, modId, CancellationToken.None);
        var mod = await state.ReadAsync(data => data.InstalledMods.FirstOrDefault(item => item.InstanceId == instanceId && item.Id == modId));
        var profile = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == instanceId));
        if (mod is not null && profile is not null && !mod.IsFramework && Path.IsPathFullyQualified(profile.InstallPath))
            PruneEmptyManagedDirectories(Path.GetFullPath(profile.InstallPath), mod.ManagedFiles);
        await state.UpdateAsync(data => { data.InstalledMods.RemoveAll(item => item.Id == modId); return true; });
        return result;
    }

    private async Task<(int Removed, int Preserved)> UninstallFilesCoreAsync(Guid instanceId, Guid modId, CancellationToken cancellationToken)
    {
        var mod = await state.ReadAsync(data => data.InstalledMods.FirstOrDefault(item => item.InstanceId == instanceId && item.Id == modId));
        if (mod is null) throw new ModInstallException("O pacote não está no inventário gerenciado.", HttpStatusCode.NotFound);
        var profile = await state.ReadAsync(data => data.Instances.FirstOrDefault(item => item.Id == instanceId));
        if (profile is null) throw new ModInstallException("A instância não existe mais.", HttpStatusCode.NotFound);
        if (!Path.IsPathFullyQualified(profile.InstallPath) || !Directory.Exists(profile.InstallPath))
            throw new ModInstallException("A pasta do servidor não está disponível.");
        if (mod.Locked && HasManagedPayload(profile.InstallPath, mod))
            throw new ModInstallException($"{mod.Name} está bloqueado. Desbloqueie-o antes de removê-lo.", HttpStatusCode.Conflict);
        var removed = 0;
        var preserved = 0;
        foreach (var relative in mod.ManagedFiles)
        {
            var path = ResolveTarget(Path.GetFullPath(profile.InstallPath), relative);
            RejectReparsePoints(Path.GetFullPath(profile.InstallPath), path);
            var disabledPath = path + ".dss-disabled";
            if (!File.Exists(path) && !File.Exists(disabledPath)) continue;
            if (!File.Exists(path) && File.Exists(disabledPath)) path = disabledPath;
            var expected = FindTrackedHash(mod, relative);
            if (string.IsNullOrWhiteSpace(expected) || !string.Equals(expected, await HashFileAsync(path, cancellationToken), StringComparison.OrdinalIgnoreCase))
            {
                preserved++;
                continue;
            }
            File.Delete(path);
            removed++;
        }
        return (removed, preserved);
    }

    private static void PruneEmptyManagedDirectories(string installRoot, IEnumerable<string> managedFiles)
    {
        var root = Path.GetFullPath(installRoot);
        var separator = Path.DirectorySeparatorChar.ToString();
        var packageRoots = new[]
        {
            Path.GetFullPath(Path.Combine(root, "BepInEx", "plugins")),
            Path.GetFullPath(Path.Combine(root, "BepInEx", "config"))
        };
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in managedFiles)
        {
            var target = ResolveTarget(root, relative);
            var parent = Path.GetDirectoryName(target);
            foreach (var packageRoot in packageRoots)
            {
                if (!target.StartsWith(packageRoot + separator, StringComparison.OrdinalIgnoreCase)) continue;
                while (parent is not null && parent.StartsWith(packageRoot + separator, StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(parent);
                    parent = Path.GetDirectoryName(parent);
                }
                break;
            }
        }

        foreach (var directory in candidates.OrderByDescending(path => path.Length))
        {
            try
            {
                if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private async Task DownloadAsync(Uri uri, string destination, CancellationToken cancellationToken,
        Action<long, long?>? progress = null, long? expectedSize = null, string provider = "thunderstore")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("DedicatedServerStudio/0.1.0 (+self-hosted Valheim server manager)");
        using var response = await clients.CreateClient("mod-download").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new ModInstallException("O provedor limitou a frequência de downloads. Aguarde o intervalo informado pelo provedor e tente novamente.", HttpStatusCode.TooManyRequests);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is not { } finalUri || !TryValidateDownloadUrl(finalUri.AbsoluteUri, provider, out _))
            throw new ModInstallException("O servidor redirecionou o download para um domínio não autorizado.", HttpStatusCode.BadGateway);
        var totalBytes = response.Content.Headers.ContentLength ?? expectedSize;
        if (totalBytes is > 0) EnsureAvailableSpace(Path.GetDirectoryName(destination)!, totalBytes.Value,
            "Não há espaço livre suficiente na unidade temporária para baixar este mod.");
        progress?.Invoke(0, totalBytes);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[64 * 1024];
        long total = 0;
        long lastReportedBytes = 0;
        var lastReportedAt = Environment.TickCount64;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            total += read;
            EnsureAvailableSpace(Path.GetDirectoryName(destination)!, read,
                "A unidade temporária ficou sem espaço livre durante o download do mod.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            var now = Environment.TickCount64;
            if (progress is not null && (total - lastReportedBytes >= 512 * 1024 || now - lastReportedAt >= 250))
            {
                progress(total, totalBytes);
                lastReportedBytes = total;
                lastReportedAt = now;
            }
        }
        progress?.Invoke(total, totalBytes);
    }

    private static List<StagedPackageFile> ReadPayload(string archivePath, string stageRoot, bool framework)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaxEntries) throw new ModInstallException("O pacote contém arquivos demais.");
        var payload = new List<StagedPackageFile>();
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new ModInstallException("O pacote contém um link simbólico e foi recusado.");
            var normalized = entry.FullName.Replace('\\', '/');
            string managedPath;
            if (framework)
            {
                const string packRoot = "BepInExPack_Valheim/";
                var packRootIndex = normalized.IndexOf(packRoot, StringComparison.OrdinalIgnoreCase);
                if (packRootIndex < 0) continue;
                var relative = normalized[(packRootIndex + packRoot.Length)..];
                if (relative.Length == 0) continue;
                managedPath = relative;
            }
            else
            {
                const string bepinexRoot = "BepInEx/";
                var bepinexRootIndex = normalized.IndexOf(bepinexRoot, StringComparison.OrdinalIgnoreCase);
                var relative = bepinexRootIndex >= 0
                    ? normalized[(bepinexRootIndex + bepinexRoot.Length)..]
                    : normalized;
                relative = relative.TrimStart('.', '/');
                var pluginsIndex = relative.IndexOf("plugins/", StringComparison.OrdinalIgnoreCase);
                var configIndex = relative.IndexOf("config/", StringComparison.OrdinalIgnoreCase);
                if (!relative.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase) && pluginsIndex >= 0)
                    relative = relative[pluginsIndex..];
                else if (!relative.StartsWith("config/", StringComparison.OrdinalIgnoreCase) && configIndex >= 0)
                    relative = relative[configIndex..];
                if (!relative.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase) &&
                    !relative.StartsWith("config/", StringComparison.OrdinalIgnoreCase) &&
                    normalized.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    relative = $"plugins/{Path.GetFileName(normalized)}";
                if (!(relative.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("config/", StringComparison.OrdinalIgnoreCase))) continue;
                managedPath = $"BepInEx/{relative}";
            }
            if (managedPath.Contains(':') || managedPath.StartsWith('/') || managedPath.Split('/').Any(part => part is ".." or ""))
                throw new ModInstallException("O pacote contém um caminho inseguro e foi recusado.");
            EnsureAvailableSpace(stageRoot, entry.Length, "Não há espaço livre suficiente para descompactar este mod.");
            var stagedPath = Path.Combine(stageRoot, "payload", managedPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
            using (var source = entry.Open())
            using (var target = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                source.CopyTo(target);
            payload.Add(new StagedPackageFile(managedPath, stagedPath, HashFile(stagedPath)));
        }
        return payload;
    }

    private static void EnsureAvailableSpace(string path, long requiredBytes, string message)
    {
        try
        {
            var driveRoot = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(driveRoot)) return;
            var drive = new DriveInfo(driveRoot);
            var requiredWithReserve = requiredBytes > long.MaxValue - MinimumFreeSpaceReserve
                ? long.MaxValue : requiredBytes + MinimumFreeSpaceReserve;
            if (drive.IsReady && drive.AvailableFreeSpace < requiredWithReserve)
                throw new ModInstallException(message, HttpStatusCode.InsufficientStorage);
        }
        catch (ModInstallException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Network and virtual drives may not report free space reliably; actual file I/O remains authoritative.
        }
    }

    private static string ResolveTarget(string installRoot, string relative)
    {
        var target = Path.GetFullPath(Path.Combine(installRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(installRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ModInstallException("O caminho do pacote saiu da pasta do servidor.");
        return target;
    }

    private static bool IsFrameworkConfig(string relativePath) =>
        relativePath.StartsWith("BepInEx/config/", StringComparison.OrdinalIgnoreCase);

    private static void RejectReparsePoints(string installRoot, string target)
    {
        var current = Path.GetDirectoryName(target);
        while (current is not null && current.StartsWith(installRoot, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ModInstallException("O pacote atravessaria uma pasta vinculada/simbólica. Instalação recusada por segurança.");
            if (string.Equals(current, installRoot, StringComparison.OrdinalIgnoreCase)) break;
            current = Path.GetDirectoryName(current);
        }
    }

    private static bool TryValidateDownloadUrl(string value, string provider, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps) return false;
        var host = parsed.Host.ToLowerInvariant();
        var trusted = provider.Equals("hexium", StringComparison.OrdinalIgnoreCase)
            ? host == "hexium.gg" || host.EndsWith(".hexium.gg", StringComparison.Ordinal)
            : provider.Equals("nexus", StringComparison.OrdinalIgnoreCase)
                ? host == "nexusmods.com" || host.EndsWith(".nexusmods.com", StringComparison.Ordinal) ||
                  host == "nexusmods.net" || host.EndsWith(".nexusmods.net", StringComparison.Ordinal) ||
                  host == "nexus-cdn.com" || host.EndsWith(".nexus-cdn.com", StringComparison.Ordinal) ||
                  host.Contains("nexus", StringComparison.Ordinal) && host.EndsWith(".amazonaws.com", StringComparison.Ordinal)
                : host == "thunderstore.io" || host.EndsWith(".thunderstore.io", StringComparison.Ordinal) ||
                  host == "thunderstore.azureedge.net" || host.EndsWith(".amazonaws.com", StringComparison.Ordinal);
        if (!trusted) return false;
        uri = parsed;
        return true;
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string? FindTrackedHash(InstalledMod mod, string relativePath) =>
        mod.FileHashes.FirstOrDefault(item => string.Equals(item.Key, relativePath, StringComparison.OrdinalIgnoreCase)).Value;

    private static bool TryParseDependency(string dependency, out string packageId, out string version)
    {
        packageId = "";
        version = "";
        for (var split = dependency.LastIndexOf('-'); split > 0; split = dependency.LastIndexOf('-', split - 1))
        {
            var candidateId = dependency[..split].Trim();
            var candidateVersion = dependency[(split + 1)..].Trim();
            var numericVersion = candidateVersion.Split('-', '+')[0];
            if (candidateId.Length == 0 || !Version.TryParse(numericVersion, out _)) continue;
            packageId = candidateId;
            version = candidateVersion;
            return true;
        }
        return false;
    }

    public static bool HasActiveBepInEx(string installPath) => Path.IsPathFullyQualified(installPath) &&
        File.Exists(Path.Combine(installPath, "BepInEx", "core", "BepInEx.dll")) &&
        (File.Exists(Path.Combine(installPath, "winhttp.dll")) || File.Exists(Path.Combine(installPath, "doorstop_config.ini")));

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private sealed record StagedPackageFile(string RelativePath, string StagedPath, string Sha256);
}

public sealed class ModInstallException(string message, HttpStatusCode statusCode = HttpStatusCode.BadRequest) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

public sealed record ModBatchProgress(string Phase, string PackageId, string PackageName, int Index, int Total,
    long DownloadedBytes, long? TotalBytes);

public sealed record BatchUninstallResult(Guid ModId, string Name, int Removed, int Preserved, string? Error);
