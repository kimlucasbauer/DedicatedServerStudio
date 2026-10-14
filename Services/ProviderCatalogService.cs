using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DedicatedServerStudio.Models;

namespace DedicatedServerStudio.Services;

public sealed class ProviderCatalogService(IHttpClientFactory clients, ProviderCredentialVault credentials,
    NexusDownloadAuthorizationService nexusDownloadAuthorizations)
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, (DateTimeOffset ExpiresAt, IReadOnlyList<CatalogPackage> Items)> _cache = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<CatalogPackage>>>> _catalogLoads = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ProviderView> GetProviders() =>
    [
        new("thunderstore", "Thunderstore", "ready", "Catálogo e downloads públicos; a conta não é necessária para instalar mods.", false, true),
        new("hexium", "Hexium", "ready", "Catálogo público compatível com Thunderstore; token de equipe não é necessário para instalar mods.", false, true),
        new("nexus", "Nexus Mods", "ready", "Catálogo completo com busca paginada; a instalação direta depende das permissões da conta Nexus.", true, true),
        new("curseforge", "CurseForge", "not-implemented", "O conector oficial ainda não foi implementado. A integração requer cadastro do aplicativo e chave oficial.", true, false)
    ];

    public async Task<IReadOnlyList<ProviderStatusView>> CheckAvailabilityAsync(CancellationToken cancellationToken)
    {
        var providers = GetProviders();
        var checks = providers.Select(async provider =>
        {
            var checkedAt = DateTimeOffset.UtcNow;
            if (provider.Id is "nexus" or "curseforge")
            {
                var key = await credentials.GetAsync(provider.Id);
                if (string.IsNullOrWhiteSpace(key))
                    return new ProviderStatusView(provider, "not-configured", checkedAt,
                        provider.Id == "nexus" ? "Informe sua chave de API Nexus para validar a conexão." : "Informe a chave oficial do aplicativo CurseForge para validar a conexão.");
                return await CheckCredentialAsync(provider, key, checkedAt, cancellationToken);
            }
            if (provider.State is not ("ready" or "experimental"))
                return new ProviderStatusView(provider, "not-configured", checkedAt, provider.Detail);

            var endpoint = provider.Id == "thunderstore"
                ? "https://thunderstore.io/c/valheim/api/v1/package/"
                : "https://valheim.hexium.gg/api/v1/package/";
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(7));
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                request.Headers.UserAgent.ParseAdd("DedicatedServerStudio/0.3.6");
                using var response = await clients.CreateClient("catalog").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var connection = response.StatusCode == HttpStatusCode.TooManyRequests ? "rate-limited"
                    : response.IsSuccessStatusCode ? "reachable" : "unavailable";
                var message = connection switch
                {
                    "reachable" => "O endpoint respondeu com sucesso.",
                    "rate-limited" => "O endpoint está respondendo, mas pediu uma pausa.",
                    _ => $"O endpoint respondeu HTTP {(int)response.StatusCode}."
                };
                return new ProviderStatusView(provider, connection, checkedAt, message);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new ProviderStatusView(provider, "unavailable", checkedAt, "A verificação expirou após 7 segundos.");
            }
            catch (HttpRequestException)
            {
                return new ProviderStatusView(provider, "unavailable", checkedAt, "Não foi possível alcançar o endpoint a partir deste host.");
            }
        });
        return await Task.WhenAll(checks);
    }

    public async Task<ProviderStatusView> TestCredentialAsync(string providerId, CancellationToken cancellationToken)
    {
        var provider = GetProviders().FirstOrDefault(item => item.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        if (provider is null || provider.Id is not ("nexus" or "curseforge"))
            throw new ProviderException("Somente Nexus Mods e CurseForge aceitam uma chave de API nesta tela.");
        var key = await credentials.GetAsync(provider.Id);
        if (string.IsNullOrWhiteSpace(key)) throw new ProviderException("Salve uma chave de API antes de testar a conexão.");
        return await CheckCredentialAsync(provider, key, DateTimeOffset.UtcNow, cancellationToken);
    }

    private async Task<ProviderStatusView> CheckCredentialAsync(ProviderView provider, string key,
        DateTimeOffset checkedAt, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(9));
        var endpoint = provider.Id == "nexus"
            ? "https://api.nexusmods.com/v1/users/validate.json"
            : "https://api.curseforge.com/v1/games/455";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.UserAgent.ParseAdd("DedicatedServerStudio/0.3.6");
        if (provider.Id == "nexus")
        {
            request.Headers.TryAddWithoutValidation("apikey", key);
            request.Headers.TryAddWithoutValidation("Application-Name", "Dedicated Server Studio");
            request.Headers.TryAddWithoutValidation("Application-Version", "0.3.6");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }
        else request.Headers.TryAddWithoutValidation("x-api-key", key);

        try
        {
            using var response = await clients.CreateClient("catalog").SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = response.StatusCode == HttpStatusCode.TooManyRequests ? "rate-limited"
                : response.IsSuccessStatusCode ? "reachable"
                : response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "invalid-credential"
                : response.StatusCode == HttpStatusCode.NotFound && provider.Id == "nexus" ? "endpoint-error"
                : "unavailable";
            var message = status switch
            {
                "reachable" => provider.Id == "nexus" ? "Chave aceita. A busca paginada está disponível; o download direto depende das permissões da conta Nexus." : "Chave aceita e API do provedor acessível.",
                "rate-limited" => "A chave foi enviada, mas a API pediu uma pausa. Tente novamente mais tarde.",
                "invalid-credential" => "A API recusou a chave. Confira a chave e as permissões do aplicativo.",
                "endpoint-error" => "O endpoint oficial de validação do Nexus Mods não foi encontrado (HTTP 404). Atualize o DSS e tente novamente.",
                _ => $"A API respondeu HTTP {(int)response.StatusCode}; confira o acesso habilitado para este aplicativo."
            };
            return new ProviderStatusView(provider, status, checkedAt, message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProviderStatusView(provider, "unavailable", checkedAt, "A conexão expirou após 9 segundos.");
        }
        catch (HttpRequestException)
        {
            return new ProviderStatusView(provider, "unavailable", checkedAt, "Não foi possível alcançar a API a partir deste host.");
        }
    }

    public async Task<CatalogSearchPage> SearchAsync(string provider, string query, IReadOnlyCollection<string> fields,
        int page, int pageSize, string sort, string direction, CancellationToken cancellationToken, bool includeTotal = true)
    {
        if (provider.Equals("nexus", StringComparison.OrdinalIgnoreCase))
            return await SearchNexusAsync(query, fields, page, pageSize, sort, direction, cancellationToken);
        var all = await GetCatalogAsync(provider, cancellationToken);
        var filtered = FilterPackages(all, query, fields);
        var safePageSize = Math.Clamp(pageSize, 6, 48);
        var total = includeTotal ? filtered.Count() : (int?)null;
        var totalPages = total.HasValue ? Math.Max(1, (int)Math.Ceiling(total.Value / (double)safePageSize)) : (int?)null;
        var safePage = totalPages.HasValue ? Math.Clamp(page, 1, totalPages.Value) : Math.Max(1, page);
        var ordered = SortPackages(filtered, sort, direction);
        var items = ordered.Skip((safePage - 1) * safePageSize).Take(safePageSize).ToArray();
        return new CatalogSearchPage(items, total, safePage, totalPages, safePageSize);
    }

    public async Task<CatalogSearchCount> CountAsync(string provider, string query, IReadOnlyCollection<string> fields,
        int pageSize, CancellationToken cancellationToken)
    {
        if (provider.Equals("nexus", StringComparison.OrdinalIgnoreCase))
        {
            var page = await SearchNexusAsync(query, fields, 1, 1, "downloads", "desc", cancellationToken);
            var nexusTotal = page.Total ?? page.Items.Count;
            var nexusPageSize = Math.Clamp(pageSize, 6, 48);
            return new CatalogSearchCount(nexusTotal, Math.Max(1, (int)Math.Ceiling(nexusTotal / (double)nexusPageSize)), nexusPageSize);
        }
        var all = await GetCatalogAsync(provider, cancellationToken);
        var total = FilterPackages(all, query, fields).Count();
        var safePageSize = Math.Clamp(pageSize, 6, 48);
        return new CatalogSearchCount(total, Math.Max(1, (int)Math.Ceiling(total / (double)safePageSize)), safePageSize);
    }

    private static IEnumerable<CatalogPackage> FilterPackages(IReadOnlyList<CatalogPackage> all, string query,
        IReadOnlyCollection<string> fields)
    {
        var selectedFields = fields.Select(field => field.ToLowerInvariant()).Where(field => field is "title" or "creator" or "description" or "packageid" or "version" or "tags")
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (selectedFields.Length == 0) selectedFields = ["title", "creator", "description", "packageid"];
        if (string.IsNullOrWhiteSpace(query)) return all;

        var needle = query.Trim();
        var creatorNeedle = NormalizeCreatorQuery(needle);
        return all.Where(item => selectedFields.Any(field => field switch
        {
            "title" => item.Name.Contains(needle, StringComparison.OrdinalIgnoreCase),
            "creator" => CreatorMatches(item, creatorNeedle),
            "description" => item.Description.Contains(needle, StringComparison.OrdinalIgnoreCase),
            "packageid" => item.PackageId.Contains(needle, StringComparison.OrdinalIgnoreCase),
            "version" => item.Version.Contains(needle, StringComparison.OrdinalIgnoreCase),
            "tags" => item.Categories?.Any(category => category.Contains(needle, StringComparison.OrdinalIgnoreCase)) == true,
            _ => false
        }));
    }

    private static IEnumerable<CatalogPackage> SortPackages(IEnumerable<CatalogPackage> filtered, string sort, string direction)
    {
        var descending = !direction.Equals("asc", StringComparison.OrdinalIgnoreCase);
        return sort switch
        {
            "title" => descending ? filtered.OrderByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase) : filtered.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            "creator" => descending ? filtered.OrderByDescending(item => item.Owner, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase) : filtered.OrderBy(item => item.Owner, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            "recent" => descending ? filtered.OrderByDescending(item => item.UpdatedAt ?? DateTimeOffset.MinValue) : filtered.OrderBy(item => item.UpdatedAt ?? DateTimeOffset.MinValue),
            "downloads" => descending ? filtered.OrderByDescending(item => item.TotalDownloads).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase) : filtered.OrderBy(item => item.TotalDownloads).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            _ => descending ? filtered.OrderByDescending(item => item.TotalDownloads).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase) : filtered.OrderBy(item => item.TotalDownloads).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
        };
    }

    public async Task<IReadOnlyList<CatalogPackage>> RefreshAsync(string provider, CancellationToken cancellationToken)
    {
        if (provider.Equals("nexus", StringComparison.OrdinalIgnoreCase))
            return (await SearchNexusAsync("", [], 1, 48, "downloads", "desc", cancellationToken)).Items;
        _cache.TryRemove(provider.Trim().ToLowerInvariant(), out _);
        return await GetCatalogAsync(provider, cancellationToken);
    }

    public async Task<CatalogPackage?> FindPackageAsync(string provider, string packageId, CancellationToken cancellationToken,
        string? version = null, string? minimumVersion = null, bool includeDownloadLink = false)
    {
        if (provider.Equals("nexus", StringComparison.OrdinalIgnoreCase))
        {
            var nexusPackage = await GetNexusPackageByIdAsync(packageId, cancellationToken);
            if (nexusPackage is null) return null;
            nexusPackage = await LoadNexusFileVersionsAsync(nexusPackage, cancellationToken);
            var nexusVersions = nexusPackage.Versions?.ToList() ?? [];
            CatalogVersion? nexusSelected = null;
            if (!string.IsNullOrWhiteSpace(version))
                nexusSelected = nexusVersions.FirstOrDefault(item => item.Version.Equals(version, StringComparison.OrdinalIgnoreCase));
            else if (!string.IsNullOrWhiteSpace(minimumVersion))
                nexusSelected = nexusVersions.Where(item => IsVersionAtLeast(item.Version, minimumVersion))
                    .OrderByDescending(item => item.Version, ModVersionComparer.Instance).FirstOrDefault();
            else
                return nexusPackage;
            if (nexusSelected is null) return null;
            var nexusDownloadUrl = nexusSelected.DownloadUrl;
            if (includeDownloadLink)
            {
                if (string.IsNullOrWhiteSpace(nexusSelected.ProviderVersionId))
                    throw new ProviderException("O Nexus não retornou um identificador de arquivo para a versão selecionada.");
                nexusDownloadUrl = await GetNexusDownloadUrlAsync(nexusPackage.PackageId, nexusSelected.ProviderVersionId, cancellationToken);
            }
            return nexusPackage with { Version = nexusSelected.Version, Description = nexusSelected.Description,
                IconUrl = nexusSelected.IconUrl, DownloadUrl = nexusDownloadUrl, FileSize = nexusSelected.FileSize,
                Dependencies = nexusSelected.Dependencies, UpdatedAt = nexusSelected.CreatedAt };
        }
        var all = await GetCatalogAsync(provider, cancellationToken);
        var package = all.FirstOrDefault(item => string.Equals(item.PackageId, packageId, StringComparison.OrdinalIgnoreCase));
        if (package is null && (provider.Equals("thunderstore", StringComparison.OrdinalIgnoreCase) || provider.Equals("ts", StringComparison.OrdinalIgnoreCase) || provider.Equals("hexium", StringComparison.OrdinalIgnoreCase) || provider.Equals("hex", StringComparison.OrdinalIgnoreCase)))
        {
            // Thunderstore's API can expose the package team as `owner` while older responses use
            // `namespace`; match both forms so dependencies resolve by their canonical team-name ID.
            var separator = packageId.IndexOf('-');
            if (separator > 0 && separator < packageId.Length - 1)
            {
                var team = packageId[..separator];
                var name = packageId[(separator + 1)..];
                package = all.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    (string.Equals(item.Owner, team, StringComparison.OrdinalIgnoreCase) ||
                     item.PackageId.StartsWith(team + "-", StringComparison.OrdinalIgnoreCase)));
            }
        }
        if (package is null) return null;
        var versions = package.Versions?.ToList() ?? [];
        if (versions.All(item => !item.Version.Equals(package.Version, StringComparison.OrdinalIgnoreCase)))
            versions.Add(new CatalogVersion(package.Version, package.Description, package.IconUrl, package.DownloadUrl,
                package.FileSize, package.Dependencies, package.UpdatedAt, package.TotalDownloads));

        CatalogVersion? selected;
        if (!string.IsNullOrWhiteSpace(version))
            selected = versions.FirstOrDefault(item => item.Version.Equals(version, StringComparison.OrdinalIgnoreCase));
        else if (!string.IsNullOrWhiteSpace(minimumVersion))
            selected = versions.Where(item => IsVersionAtLeast(item.Version, minimumVersion))
                .OrderByDescending(item => item.Version, ModVersionComparer.Instance).FirstOrDefault();
        else return package;

        if (selected is null || (!string.IsNullOrWhiteSpace(minimumVersion) && !IsVersionAtLeast(selected.Version, minimumVersion))) return null;
        var downloadUrl = selected.DownloadUrl;
        return package with { Version = selected.Version, Description = selected.Description,
            IconUrl = selected.IconUrl, DownloadUrl = downloadUrl, FileSize = selected.FileSize,
            Dependencies = selected.Dependencies, UpdatedAt = selected.CreatedAt };
    }

    private async Task<CatalogPackage?> GetNexusPackageByIdAsync(string packageId, CancellationToken cancellationToken)
    {
        if (!long.TryParse(packageId, out _))
            throw new ProviderException("O identificador do mod Nexus deve ser numérico.");
        var apiKey = await credentials.GetAsync("nexus");
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ProviderException("Configure sua chave de API Nexus na tela Integrações para consultar este mod.");
        var endpoint = $"https://api.nexusmods.com/v1/games/valheim/mods/{Uri.EscapeDataString(packageId)}.json";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        AddNexusHeaders(request, apiKey);
        using var response = await clients.CreateClient("catalog").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new ProviderException("Nexus Mods limitou a consulta deste mod. Aguarde e tente novamente.");
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new ProviderException("Nexus Mods recusou a consulta do mod. Revise a chave e as permissões da conta.");
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = document.RootElement;
        var name = StringProperty(root, "name");
        var modId = StringProperty(root, "mod_id", "modId", "id") ?? packageId;
        if (string.IsNullOrWhiteSpace(name)) return null;
        var projectUrl = $"https://www.nexusmods.com/valheim/mods/{Uri.EscapeDataString(modId)}";
        return new CatalogPackage("nexus", modId, name, StringProperty(root, "author", "uploaded_by") ?? "Nexus Mods",
            StringProperty(root, "version") ?? "", StringProperty(root, "summary", "description") ?? name,
            StringProperty(root, "picture_url", "pictureUrl"), projectUrl, null,
            ReadLongProperty(root, "file_size", "fileSize"), [], ReadLongProperty(root, "mod_downloads", "downloads") ?? 0,
            NexusDate(root, "updated_timestamp", "updatedAt"), null, NexusDate(root, "created_timestamp", "createdAt"),
            ReadLongProperty(root, "endorsement_count", "endorsements"), []);
    }

    private async Task<CatalogPackage> LoadNexusFileVersionsAsync(CatalogPackage package, CancellationToken cancellationToken)
    {
        if (!long.TryParse(package.PackageId, out _))
            throw new ProviderException("O Nexus retornou um identificador de mod inválido.");
        var apiKey = await credentials.GetAsync("nexus");
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ProviderException("Configure sua chave de API Nexus na tela Integrações para consultar as versões.");
        var endpoint = $"https://api.nexusmods.com/v1/games/valheim/mods/{Uri.EscapeDataString(package.PackageId)}/files.json";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        AddNexusHeaders(request, apiKey);
        using var response = await clients.CreateClient("catalog").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new ProviderException("Nexus Mods limitou a consulta de versões. Aguarde e tente novamente.");
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new ProviderException("Nexus Mods recusou o acesso aos arquivos deste mod. Revise a chave e as permissões da conta.");
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var fileArray = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement
            : document.RootElement.TryGetProperty("files", out var files) ? files : default;
        if (fileArray.ValueKind != JsonValueKind.Array) throw new JsonException("O Nexus retornou arquivos em formato inesperado.");
        var mainFiles = new List<NexusFileVersion>();
        foreach (var file in fileArray.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object) continue;
            var categoryName = StringProperty(file, "category_name", "categoryName") ?? "";
            var categoryId = ReadLongProperty(file, "category_id", "categoryId");
            if (!categoryName.Contains("main", StringComparison.OrdinalIgnoreCase) && categoryId != 1) continue;
            var fileId = StringProperty(file, "file_id", "fileId", "id");
            if (string.IsNullOrWhiteSpace(fileId)) continue;
            var version = StringProperty(file, "mod_version", "version");
            if (string.IsNullOrWhiteSpace(version)) version = StringProperty(file, "name") ?? fileId;
            var timestamp = NexusDate(file, "uploaded_timestamp", "uploaded_time", "uploadedAt");
            var size = ReadLongProperty(file, "size", "file_size", "fileSize");
            var downloads = ReadLongProperty(file, "downloads", "mod_downloads") ?? 0;
            var isPrimary = file.TryGetProperty("is_primary", out var primary) &&
                (primary.ValueKind == JsonValueKind.True || primary.ValueKind == JsonValueKind.Number && primary.TryGetInt32(out var primaryFlag) && primaryFlag == 1);
            mainFiles.Add(new NexusFileVersion(fileId, version, StringProperty(file, "name") ?? package.Name,
                StringProperty(file, "description") ?? package.Description, size, timestamp, downloads, isPrimary));
        }
        if (mainFiles.Count == 0)
            throw new ProviderException("Este mod não tem arquivos principais disponíveis para instalação pelo DSS.");
        var versions = mainFiles
            .OrderByDescending(item => item.IsPrimary)
            .ThenByDescending(item => item.UploadedAt ?? DateTimeOffset.MinValue)
            .GroupBy(item => item.Version, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(item => new CatalogVersion(item.Version, item.Description, package.IconUrl, null, item.Size,
                [], item.UploadedAt, item.Downloads, item.FileId))
            .ToArray();
        var current = versions.OrderByDescending(item => item.Version, ModVersionComparer.Instance).First();
        var latestFile = mainFiles.OrderByDescending(item => item.IsPrimary)
            .ThenByDescending(item => item.UploadedAt ?? DateTimeOffset.MinValue).First();
        return package with { Version = current.Version, Description = current.Description, FileSize = current.FileSize,
            UpdatedAt = latestFile.UploadedAt ?? package.UpdatedAt, Versions = versions };
    }

    private async Task<string> GetNexusDownloadUrlAsync(string modId, string fileId, CancellationToken cancellationToken)
    {
        var apiKey = await credentials.GetAsync("nexus");
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ProviderException("Configure sua chave de API Nexus antes de baixar mods.");
        var temporaryAuthorization = nexusDownloadAuthorizations.Get(modId, fileId);
        var endpoint = $"https://api.nexusmods.com/v1/games/valheim/mods/{Uri.EscapeDataString(modId)}/files/{Uri.EscapeDataString(fileId)}/download_link.json";
        if (temporaryAuthorization is not null)
            endpoint += $"?key={Uri.EscapeDataString(temporaryAuthorization.Key)}&expires={temporaryAuthorization.Expires}";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        AddNexusHeaders(request, apiKey);
        using var response = await clients.CreateClient("catalog").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new ProviderException("Nexus Mods limitou o download. Aguarde e tente novamente.");
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            if (temporaryAuthorization is not null) nexusDownloadAuthorizations.Remove(modId, fileId);
            throw new ProviderException(temporaryAuthorization is null
                ? "O Nexus recusou o link direto. Em contas sem Premium, abra o arquivo exato no Nexus, copie o link de ‘Download with Manager’ e autorize-o em Integrações → Nexus. A chave pessoal sozinha não substitui essa autorização."
                : "O Nexus recusou a autorização temporária. Confira se o link NXM corresponde a este arquivo e se ainda não expirou.");
        }
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = document.RootElement;
        var link = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().Select(item => StringProperty(item, "URI", "uri", "url")).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            : StringProperty(root, "URI", "uri", "url");
        if (!Uri.TryCreate(link, UriKind.Absolute, out var downloadUri) || downloadUri.Scheme != Uri.UriSchemeHttps)
            throw new ProviderException("O Nexus não retornou um link HTTPS válido para este arquivo.");
        if (temporaryAuthorization is not null) nexusDownloadAuthorizations.Remove(modId, fileId);
        return downloadUri.AbsoluteUri;
    }

    private sealed record NexusFileVersion(string FileId, string Version, string Name, string Description,
        long? Size, DateTimeOffset? UploadedAt, long Downloads, bool IsPrimary);

    private static bool IsVersionAtLeast(string candidate, string minimum) => ModVersionComparer.Instance.Compare(candidate, minimum) >= 0;

    private static string NormalizeCreatorQuery(string query)
    {
        var creator = query.Trim().TrimStart('@');
        if (Uri.TryCreate(creator, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var packageIndex = Array.FindIndex(segments, segment => segment.Equals("p", StringComparison.OrdinalIgnoreCase));
            if (packageIndex >= 0 && packageIndex + 1 < segments.Length) creator = Uri.UnescapeDataString(segments[packageIndex + 1]);
        }
        return NormalizeSearch(creator);
    }

    private static bool CreatorMatches(CatalogPackage item, string normalizedCreator)
    {
        var normalizedOwner = NormalizeSearch(item.Owner);
        var namespaceFromId = item.PackageId.Split('-', 2)[0];
        return !string.IsNullOrEmpty(normalizedCreator) && (normalizedOwner.Contains(normalizedCreator, StringComparison.Ordinal) ||
            NormalizeSearch(namespaceFromId).Contains(normalizedCreator, StringComparison.Ordinal));
    }

    private static string NormalizeSearch(string value) => string.Concat(value.Where(char.IsLetterOrDigit)).ToUpperInvariant();

    private sealed class ModVersionComparer : IComparer<string>
    {
        public static ModVersionComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            static (Version? Core, bool PreRelease, string Raw) Parse(string value)
            {
                var normalized = value.Trim().TrimStart('v', 'V');
                var pieces = normalized.Split(['-', '+'], 2, StringSplitOptions.TrimEntries);
                Version.TryParse(pieces[0], out var core);
                return (core, pieces[0].Length != normalized.Length && normalized.Contains('-'), normalized);
            }
            var a = Parse(left);
            var b = Parse(right);
            if (a.Core is not null && b.Core is not null)
            {
                var coreOrder = a.Core.CompareTo(b.Core);
                if (coreOrder != 0) return coreOrder;
                if (a.PreRelease != b.PreRelease) return a.PreRelease ? -1 : 1;
            }
            return StringComparer.OrdinalIgnoreCase.Compare(a.Raw, b.Raw);
        }
    }

    private async Task<CatalogSearchPage> SearchNexusAsync(string query, IReadOnlyCollection<string> fields,
        int page, int pageSize, string sort, string direction, CancellationToken cancellationToken)
    {
        var safePageSize = Math.Clamp(pageSize, 6, 48);
        var safePage = Math.Max(1, page);
        var selectedFields = fields.Select(field => field.Trim().ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedFields.Count == 0) selectedFields = new HashSet<string>(["title", "creator", "description", "packageid"], StringComparer.OrdinalIgnoreCase);

        // An exact numeric Nexus id is resolved directly. Normal search only requests the page
        // the user is viewing, instead of downloading and filtering the entire game catalog.
        if (!string.IsNullOrWhiteSpace(query) && selectedFields.Contains("packageid") && long.TryParse(query.Trim(), out _))
        {
            var direct = await GetNexusPackageByIdAsync(query.Trim(), cancellationToken);
            var exactMatch = direct is not null && direct.PackageId.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase);
            CatalogPackage[] directItems = exactMatch ? [direct!] : [];
            return new CatalogSearchPage(directItems, directItems.Length, 1, 1, safePageSize);
        }

        var apiKey = await credentials.GetAsync("nexus");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ProviderException("Configure sua chave de API Nexus na tela Integrações para consultar os mods.");

        var clauses = new List<Dictionary<string, object?>>();
        void AddWildcard(string field, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                clauses.Add(new Dictionary<string, object?> { [field] = new { value = $"*{value.Trim()}*", op = "WILDCARD" } });
        }
        var term = query.Trim();
        if (!string.IsNullOrWhiteSpace(term))
        {
            if (selectedFields.Contains("title")) AddWildcard("name", term);
            if (selectedFields.Contains("creator"))
            {
                AddWildcard("author", term);
                AddWildcard("uploader", term);
            }
            if (selectedFields.Contains("description")) AddWildcard("description", term);
            if (selectedFields.Contains("tags")) AddWildcard("tag", term);
            // Nexus does not expose mod version or numeric mod id as wildcard search filters.
            // For those-only searches, keep the query bounded to one upstream page.
        }

        var filter = new Dictionary<string, object?>
        {
            ["gameDomainName"] = new[] { new { value = "valheim", op = "EQUALS" } }
        };
        if (!string.IsNullOrWhiteSpace(term) && clauses.Count == 0)
            return new CatalogSearchPage([], 0, 1, 1, safePageSize);
        if (clauses.Count > 0) filter["filter"] = new[] { new { filter = clauses, op = "OR" } };

        var sortField = sort.ToLowerInvariant() switch
        {
            "title" => "name",
            "recent" => "updatedAt",
            "creator" => "name",
            _ => "downloads"
        };
        var payload = new
        {
            query = "query NexusModsPage($filter: ModsFilter, $sort: [ModsSort!], $count: Int!, $offset: Int!) { mods(filter: $filter, sort: $sort, count: $count, offset: $offset) { totalCount nodes { modId name summary author version pictureUrl downloads endorsements updatedAt createdAt } } }",
            variables = new
            {
                filter,
                sort = new[] { new Dictionary<string, object> { [sortField] = new { direction = direction.Equals("asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC" } } },
                count = safePageSize,
                offset = checked((safePage - 1) * safePageSize)
            }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.nexusmods.com/v2/graphql") { Content = JsonContent.Create(payload) };
        AddNexusHeaders(request, apiKey);
        using var response = await clients.CreateClient("catalog").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new ProviderException("Nexus Mods limitou a busca. Aguarde e tente novamente.");
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new ProviderException("Nexus Mods recusou a chave para consultar o catálogo. Revise a chave e as permissões em Integrações.");
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (document.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            var detail = string.Join("; ", errors.EnumerateArray().Select(error => StringProperty(error, "message")).Where(message => !string.IsNullOrWhiteSpace(message)));
            throw new ProviderException(string.IsNullOrWhiteSpace(detail) ? "A API do Nexus não aceitou a busca." : "A API do Nexus recusou a busca: " + detail);
        }
        if (!document.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("mods", out var mods) ||
            !mods.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
            throw new JsonException("O Nexus retornou a busca em formato inesperado.");

        var total = mods.TryGetProperty("totalCount", out var totalValue) && totalValue.TryGetInt32(out var parsedTotal) ? Math.Max(0, parsedTotal) : 0;
        var pageItems = new List<CatalogPackage>();
        foreach (var item in nodes.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = StringProperty(item, "modId");
            var name = StringProperty(item, "name");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;
            pageItems.Add(new CatalogPackage("nexus", id, name, StringProperty(item, "author") ?? "Nexus Mods",
                StringProperty(item, "version") ?? "", StringProperty(item, "summary") ?? name,
                StringProperty(item, "pictureUrl"), $"https://www.nexusmods.com/valheim/mods/{Uri.EscapeDataString(id)}",
                null, null, [], ReadLongProperty(item, "downloads") ?? 0, NexusDate(item, "updatedAt"), null,
                NexusDate(item, "createdAt"), ReadLongProperty(item, "endorsements"), []));
        }
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)safePageSize));
        return new CatalogSearchPage(pageItems, total, Math.Min(safePage, totalPages), totalPages, safePageSize);
    }

    private async Task<IReadOnlyList<CatalogPackage>> GetCatalogAsync(string provider, CancellationToken cancellationToken)
    {
        var normalizedProvider = provider.Trim().ToLowerInvariant();
        var cacheKey = normalizedProvider switch { "ts" => "thunderstore", "hex" => "hexium", _ => normalizedProvider };
        if (TryGetCached(cacheKey, out var cached)) return cached;
        if (cacheKey is not ("thunderstore" or "hexium"))
        {
            throw new ProviderException("Este provedor usa uma busca paginada e não carrega o catálogo completo.");
        }

        var load = _catalogLoads.GetOrAdd(cacheKey, key => new Lazy<Task<IReadOnlyList<CatalogPackage>>>(
            () => LoadCatalogAsync(key), LazyThreadSafetyMode.ExecutionAndPublication));
        var catalogTask = load.Value;
        _ = catalogTask.ContinueWith(_ => RemoveCatalogLoad(cacheKey, load), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await catalogTask.WaitAsync(cancellationToken);
    }

    private void RemoveCatalogLoad(string key, Lazy<Task<IReadOnlyList<CatalogPackage>>> load) =>
        ((ICollection<KeyValuePair<string, Lazy<Task<IReadOnlyList<CatalogPackage>>>>>)_catalogLoads)
            .Remove(new KeyValuePair<string, Lazy<Task<IReadOnlyList<CatalogPackage>>>>(key, load));

    private async Task<IReadOnlyList<CatalogPackage>> LoadCatalogAsync(string provider)
    {
        // One cold request feeds both the first result page and the separate total-count request.
        // A bounded independent token lets a caller changing tabs stop waiting without starting
        // another copy of the provider's full catalog download.
        using var timeout = new CancellationTokenSource(provider == "nexus" ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(45));
        return provider switch
        {
            "thunderstore" => await GetThunderstoreAsync(timeout.Token),
            "hexium" => await GetHexiumAsync(timeout.Token),
            _ => throw new ProviderException("Unknown provider.")
        };
    }

    private static void AddNexusHeaders(HttpRequestMessage request, string apiKey)
    {
        request.Headers.TryAddWithoutValidation("apikey", apiKey);
        request.Headers.TryAddWithoutValidation("Application-Name", "Dedicated Server Studio");
        request.Headers.TryAddWithoutValidation("Application-Version", "0.3.6");
        request.Headers.UserAgent.ParseAdd("DedicatedServerStudio/0.3.6");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static DateTimeOffset? NexusDate(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds))
            {
                try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
                catch (ArgumentOutOfRangeException) { return null; }
            }
            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (long.TryParse(text, out var unixSeconds))
                {
                    try { return DateTimeOffset.FromUnixTimeSeconds(unixSeconds); }
                    catch (ArgumentOutOfRangeException) { return null; }
                }
                if (DateTimeOffset.TryParse(text, out var parsed)) return parsed;
            }
        }
        return null;
    }

    private async Task<IReadOnlyList<CatalogPackage>> GetThunderstoreAsync(CancellationToken cancellationToken)
    {
        if (TryGetCached("thunderstore", out var cached)) return cached;
        var result = await GetThunderstoreCompatibleAsync("thunderstore", "https://thunderstore.io/c/valheim/api/v1/package/", cancellationToken);
        SetCached("thunderstore", result);
        return result;
    }

    private async Task<IReadOnlyList<CatalogPackage>> GetThunderstoreCompatibleAsync(string provider, string endpoint, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.UserAgent.ParseAdd("DedicatedServerStudio/0.3.6 (+self-hosted Valheim server manager)");
        var response = await clients.CreateClient("catalog").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new ProviderException($"{(provider == "hexium" ? "Hexium" : "Thunderstore")} pediu uma pausa. Aguarde o Retry-After informado pelo provedor antes de tentar novamente.");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var packages = await JsonSerializer.DeserializeAsync<List<ThunderstorePackage>>(stream, cancellationToken: cancellationToken) ?? [];
        return packages.Where(package => package.IsDeprecated != true && package.Versions is { Count: > 0 })
            .Select(package =>
            {
                var versions = package.Versions!;
                var version = versions.FirstOrDefault(item => item.IsActive) ?? versions[0];
                var allVersions = versions.Where(item => !string.IsNullOrWhiteSpace(item.VersionNumber)).Select(item =>
                    new CatalogVersion(item.VersionNumber!, item.Description ?? package.Name, item.Icon ?? package.PakeIcon,
                        item.DownloadUrl, item.FileSize, item.Dependencies ?? [], ParseDate(item.DateCreated), item.Downloads)).ToArray();
                var versionDates = allVersions.Where(item => item.CreatedAt.HasValue).Select(item => item.CreatedAt!.Value).ToArray();
                var createdAt = ParseDate(package.DateCreated) ?? (versionDates.Length > 0 ? versionDates.Min() : null);
                var updatedAt = ParseDate(package.DateUpdated) ?? (versionDates.Length > 0 ? versionDates.Max() : null);
                var team = package.Namespace;
                if (string.IsNullOrWhiteSpace(team) && !string.IsNullOrWhiteSpace(package.FullName))
                {
                    var fullNameSeparator = package.FullName.IndexOf('-');
                    if (fullNameSeparator > 0) team = package.FullName[..fullNameSeparator];
                }
                if (string.IsNullOrWhiteSpace(team)) team = package.Owner;
                var owner = team;
                var packageId = string.IsNullOrWhiteSpace(package.FullName) ? $"{team}-{package.Name}" : package.FullName;
                return new CatalogPackage(provider, packageId, package.Name,
                    owner, version.VersionNumber ?? "", version.Description ?? package.Name,
                    version.Icon ?? package.PakeIcon, package.PackageUrl, version.DownloadUrl, version.FileSize,
                    version.Dependencies ?? [], package.TotalDownloads > 0 ? package.TotalDownloads : versions.Sum(item => item.Downloads),
                    updatedAt ?? ParseDate(version.DateCreated), allVersions, createdAt,
                    ReadLong(package.RatingScore), package.Categories ?? []);
            })
            .OrderByDescending(item => item.Version, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<IReadOnlyList<CatalogPackage>> GetHexiumAsync(CancellationToken cancellationToken)
    {
        if (TryGetCached("hexium", out var cached)) return cached;
        var result = await GetThunderstoreCompatibleAsync("hexium", "https://valheim.hexium.gg/api/v1/package/", cancellationToken);
        SetCached("hexium", result);
        return result;
    }

    private static string? StringProperty(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
            if (value.ValueKind == JsonValueKind.Number) return value.ToString();
            if (value.ValueKind == JsonValueKind.Object)
            {
                var nested = StringProperty(value, "version_number", "name", "url", "download_url");
                if (nested is not null) return nested;
            }
        }
        return null;
    }

    private static long? LongProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : null;

    private static long? ReadLongProperty(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            var parsed = ReadLong(value);
            if (parsed.HasValue) return parsed;
        }
        return null;
    }

    private static DateTimeOffset? ParseDate(string? value) => DateTimeOffset.TryParse(value, out var result) ? result : null;

    private static long? ReadLong(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number)) return number;
        return null;
    }

    private bool TryGetCached(string key, out IReadOnlyList<CatalogPackage> items)
    {
        if (_cache.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow)
        {
            items = entry.Items;
            return true;
        }
        items = [];
        return false;
    }

    private void SetCached(string key, IReadOnlyList<CatalogPackage> items) =>
        _cache[key] = (DateTimeOffset.UtcNow.Add(CacheLifetime), items);

    private sealed class ThunderstorePackage
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("namespace")] public string Namespace { get; set; } = "";
        [JsonPropertyName("owner")] public string Owner { get; set; } = "";
        [JsonPropertyName("full_name")] public string? FullName { get; set; }
        [JsonPropertyName("package_url")] public string? PackageUrl { get; set; }
        [JsonPropertyName("icon")] public string? PakeIcon { get; set; }
        [JsonPropertyName("is_deprecated")] public bool? IsDeprecated { get; set; }
        [JsonPropertyName("total_downloads")] public long TotalDownloads { get; set; }
        [JsonPropertyName("date_created")] public string? DateCreated { get; set; }
        [JsonPropertyName("date_updated")] public string? DateUpdated { get; set; }
        [JsonPropertyName("rating_score")] public JsonElement RatingScore { get; set; }
        [JsonPropertyName("categories")] public List<string>? Categories { get; set; }
        [JsonPropertyName("versions")] public List<ThunderstoreVersion>? Versions { get; set; }
    }

    private sealed class ThunderstoreVersion
    {
        [JsonPropertyName("version_number")] public string? VersionNumber { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("icon")] public string? Icon { get; set; }
        [JsonPropertyName("is_active")] public bool IsActive { get; set; }
        [JsonPropertyName("download_url")] public string? DownloadUrl { get; set; }
        [JsonPropertyName("file_size")] public long? FileSize { get; set; }
        [JsonPropertyName("dependencies")] public List<string>? Dependencies { get; set; }
        [JsonPropertyName("date_created")] public string? DateCreated { get; set; }
        [JsonPropertyName("downloads")] public long Downloads { get; set; }
    }
}

public sealed class ProviderException(string message) : Exception(message);
public sealed record ProviderStatusView(ProviderView Provider, string Connectivity, DateTimeOffset CheckedAt, string Message);
public sealed record CatalogSearchPage(IReadOnlyList<CatalogPackage> Items, int? Total, int Page, int? TotalPages, int PageSize);
public sealed record CatalogSearchCount(int Total, int TotalPages, int PageSize);
