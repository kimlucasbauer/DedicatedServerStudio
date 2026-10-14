using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace DedicatedServerStudio.Services;

public sealed record DssReleaseInfo(
    string CurrentVersion,
    string LatestVersion,
    bool UpdateAvailable,
    string ReleaseName,
    string ReleasePageUrl,
    string? ReleaseNotes,
    DateTimeOffset? PublishedAt,
    string? PackageName,
    long? PackageSize,
    string? PackageUrl,
    string? PackageDigest)
{
    public bool PackageAvailable => !string.IsNullOrWhiteSpace(PackageName) && !string.IsNullOrWhiteSpace(PackageUrl);
}

public sealed class GitHubReleaseService(HttpClient client)
{
    private const string Repository = "kimlucasbauer/DedicatedServerStudio";
    private const string ApiUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
    private const string AssetBaseUrl = "https://github.com/" + Repository + "/releases/download/";
    private const long MaximumArchiveSize = 2L * 1024 * 1024 * 1024;

    public string CurrentVersion => GetCurrentVersion();

    public async Task<DssReleaseInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync(ApiUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var tag = ReadString(root, "tag_name");
        if (string.IsNullOrWhiteSpace(tag)) throw new InvalidDataException("A release mais recente não contém uma tag de versão.");
        var latestVersion = NormalizeVersion(tag);
        if (!Version.TryParse(latestVersion, out var latest)) throw new InvalidDataException("A tag da release não usa uma versão válida, como v0.3.6.");
        if (!Version.TryParse(CurrentVersion, out var current)) throw new InvalidOperationException("A versão atual do DSS não pôde ser identificada.");

        var expectedAsset = $"DedicatedServerStudio-{latest.ToString(3)}-production.zip";
        var asset = root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array
            ? assets.EnumerateArray().FirstOrDefault(item => string.Equals(ReadString(item, "name"), expectedAsset, StringComparison.OrdinalIgnoreCase))
            : default;
        var assetName = asset.ValueKind == JsonValueKind.Object ? ReadString(asset, "name") : null;
        var assetUrl = asset.ValueKind == JsonValueKind.Object ? ReadString(asset, "browser_download_url") : null;
        var assetSize = asset.ValueKind == JsonValueKind.Object && asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var parsedSize)
            ? parsedSize : (long?)null;
        if (assetSize is < 0 or > MaximumArchiveSize) throw new InvalidDataException("O pacote da release excede o tamanho permitido.");
        var digest = asset.ValueKind == JsonValueKind.Object ? ReadString(asset, "digest") : null;
        if (!string.IsNullOrWhiteSpace(assetUrl) && !assetUrl.StartsWith(AssetBaseUrl, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O pacote da release não aponta para o repositório DSS esperado.");

        var releaseUrl = ReadString(root, "html_url") ?? "https://github.com/" + Repository + "/releases";
        if (!releaseUrl.StartsWith("https://github.com/" + Repository + "/releases/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A página da release não aponta para o repositório DSS esperado.");
        var body = ReadString(root, "body");
        if (body?.Length > 12000) body = body[..12000];
        DateTimeOffset? publishedAt = root.TryGetProperty("published_at", out var published) && published.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(published.GetString(), out var parsedPublished) ? parsedPublished : null;

        return new DssReleaseInfo(CurrentVersion, latest.ToString(3), latest > current,
            ReadString(root, "name") ?? tag, releaseUrl, body, publishedAt,
            assetName, assetSize, assetUrl, digest);
    }

    public async Task<string> DownloadPackageAsync(DssReleaseInfo release, string destinationPath, CancellationToken cancellationToken = default)
    {
        if (!release.PackageAvailable || release.PackageUrl is null ||
            !release.PackageUrl.StartsWith(AssetBaseUrl, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A release não contém o pacote de produção esperado.");

        using var response = await client.GetAsync(release.PackageUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumArchiveSize)
            throw new InvalidDataException("O pacote de atualização excede o limite de 2 GB.");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > MaximumArchiveSize) throw new InvalidDataException("O pacote de atualização excede o limite de 2 GB.");
            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        await destination.FlushAsync(cancellationToken);
        var sha256 = Convert.ToHexString(hash.GetHashAndReset());
        if (!string.IsNullOrWhiteSpace(release.PackageDigest))
        {
            const string prefix = "sha256:";
            if (!release.PackageDigest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(sha256), Convert.FromHexString(release.PackageDigest[prefix.Length..])))
                throw new InvalidDataException("A verificação SHA-256 do pacote da release falhou.");
        }
        if (total == 0) throw new InvalidDataException("O pacote da release está vazio.");
        return sha256;
    }

    private static string GetCurrentVersion()
    {
        var version = typeof(GitHubReleaseService).Assembly.GetName().Version;
        if (version is null || version.Build < 0) return "0.0.0";
        return new Version(version.Major, version.Minor, version.Build).ToString(3);
    }

    private static string NormalizeVersion(string tag)
    {
        var value = tag.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        var suffix = value.IndexOfAny(['-', '+']);
        if (suffix >= 0) value = value[..suffix];
        return value;
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;
}
