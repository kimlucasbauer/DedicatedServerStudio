using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DedicatedServerStudio.Models;

namespace DedicatedServerStudio.Services;

public sealed class R2ModmanProfileService(IHttpClientFactory clients)
{
    private const int MaxArchiveBytes = 25 * 1024 * 1024;
    private const long MaxExpandedBytes = 200L * 1024 * 1024;
    private const long MaxConfigBytes = 30L * 1024 * 1024;
    private const int MaxEntries = 10_000;
    private const int MaxMods = 200;
    private const int MaxConfigFileBytes = 10 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> ConfigExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".cfg", ".json", ".yaml", ".yml", ".toml", ".xml", ".txt" };

    public async Task<byte[]> DownloadCodeAsync(string code, CancellationToken cancellationToken)
    {
        code = (code ?? string.Empty).Trim();
        if (!Guid.TryParseExact(code, "D", out _))
            throw new ArgumentException("Informe o código UUID do perfil r2modman.");

        var url = $"https://thunderstore.io/api/experimental/legacyprofile/get/{Uri.EscapeDataString(code)}/";
        using var response = await clients.CreateClient("r2modman").GetAsync(url,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new KeyNotFoundException("O código não existe ou expirou. Confira o UUID ou importe o arquivo .r2z.");
        if ((int)response.StatusCode == 429)
            throw new InvalidOperationException("O serviço de perfis limitou as consultas. Aguarde um pouco e tente novamente.");
        response.EnsureSuccessStatusCode();

        var payload = await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(cancellationToken), MaxArchiveBytes * 2L,
            cancellationToken);
        var encoded = Encoding.UTF8.GetString(payload);
        const string prefix = "#r2modman";
        if (!encoded.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException("O serviço retornou um perfil r2modman em formato inválido.");
        try
        {
            var archive = Convert.FromBase64String(encoded[prefix.Length..].Trim());
            if (archive.Length == 0 || archive.Length > MaxArchiveBytes)
                throw new InvalidDataException("O arquivo do perfil excede o limite de 25 MB.");
            return archive;
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("O conteúdo do código não é um arquivo .r2z válido.", ex);
        }
    }

    public R2ModmanArchive ParseArchive(byte[] bytes)
    {
        if (bytes.Length is 0 or > MaxArchiveBytes)
            throw new InvalidDataException("O arquivo .r2z está vazio ou excede o limite de 25 MB.");
        try
        {
            using var memory = new MemoryStream(bytes, writable: false);
            using var zip = new ZipArchive(memory, ZipArchiveMode.Read, leaveOpen: false);
            if (zip.Entries.Count > MaxEntries) throw new InvalidDataException("O arquivo contém entradas demais para importar com segurança.");
            var expandedBytes = zip.Entries.Sum(entry => entry.Length);
            if (expandedBytes > MaxExpandedBytes) throw new InvalidDataException("O conteúdo descompactado do perfil excede o limite de segurança.");

            var manifests = zip.Entries.Where(entry =>
                Path.GetFileName(entry.FullName.Replace('\\', '/')).Equals("export.r2x", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (manifests.Length > 1) throw new InvalidDataException("O .r2z contém mais de um manifesto export.r2x.");
            var manifest = manifests.FirstOrDefault();
            if (manifest is null || manifest.Length is 0 or > 2_000_000)
                throw new InvalidDataException("O .r2z não contém um manifesto export.r2x válido.");
            using var manifestStream = manifest.Open();
            using var reader = new StreamReader(manifestStream, StrictUtf8, detectEncodingFromByteOrderMarks: true);
            var manifestText = reader.ReadToEnd();
            var (profileName, mods) = ParseManifest(manifestText);

            var configs = new List<R2ModmanConfig>();
            long configBytes = 0;
            var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/') || !TryGetConfigRelativePath(entry.FullName, out var relativePath)) continue;
                if (!ConfigExtensions.Contains(Path.GetExtension(relativePath))) continue;
                if (!destinations.Add(relativePath)) throw new InvalidDataException($"O arquivo contém configurações repetidas: {relativePath}.");
                if (entry.Length > MaxConfigFileBytes)
                    throw new InvalidDataException($"A configuração {relativePath} excede o limite de 10 MB aceito pelo DSS.");
                configBytes += entry.Length;
                if (configBytes > MaxConfigBytes) throw new InvalidDataException("As configurações do perfil excedem o limite total de 30 MB.");
                using var stream = entry.Open();
                using var configMemory = new MemoryStream((int)entry.Length);
                stream.CopyTo(configMemory);
                string content;
                try { content = StrictUtf8.GetString(configMemory.GetBuffer(), 0, (int)configMemory.Length); }
                catch (DecoderFallbackException ex) { throw new InvalidDataException($"A configuração {relativePath} não está em UTF-8 válido.", ex); }
                configs.Add(new(relativePath, content, entry.Length));
            }

            return new(string.IsNullOrWhiteSpace(profileName) ? "Perfil r2modman" : profileName, mods, configs, configBytes);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException("O arquivo não é um pacote .r2z válido.", ex);
        }
    }

    public int CountExistingConfigs(string installPath, IReadOnlyList<R2ModmanConfig> configs) =>
        configs.Count(config => File.Exists(GetConfigDestination(installPath, config.RelativePath)));

    public async Task ImportConfigsAsync(string installPath, IReadOnlyList<R2ModmanConfig> configs,
        CancellationToken cancellationToken)
    {
        if (configs.Count == 0) return;
        var staged = new List<StagedConfig>(configs.Count);
        var committed = new List<StagedConfig>();
        try
        {
            foreach (var config in configs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = GetConfigDestination(installPath, config.RelativePath);
                var directory = Path.GetDirectoryName(destination)!;
                EnsureNoReparsePoint(installPath, directory);
                Directory.CreateDirectory(directory);
                var existed = File.Exists(destination);
                var previous = existed ? await File.ReadAllBytesAsync(destination, cancellationToken) : null;
                var temporary = destination + $".{Guid.NewGuid():N}.dss-import";
                await File.WriteAllTextAsync(temporary, config.Content, new UTF8Encoding(false), cancellationToken);
                staged.Add(new(destination, temporary, existed, previous));
            }

            foreach (var item in staged)
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(item.TemporaryPath, item.Destination, overwrite: true);
                committed.Add(item);
            }
        }
        catch
        {
            foreach (var item in committed.AsEnumerable().Reverse())
            {
                try
                {
                    if (item.Existed && item.PreviousBytes is not null)
                    {
                        var restore = item.Destination + $".{Guid.NewGuid():N}.dss-restore";
                        await File.WriteAllBytesAsync(restore, item.PreviousBytes, CancellationToken.None);
                        File.Move(restore, item.Destination, overwrite: true);
                    }
                    else if (File.Exists(item.Destination)) File.Delete(item.Destination);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            throw;
        }
        finally
        {
            foreach (var item in staged)
                try { if (File.Exists(item.TemporaryPath)) File.Delete(item.TemporaryPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static (string ProfileName, IReadOnlyList<R2ModmanPackage> Mods) ParseManifest(string manifest)
    {
        string profileName = "";
        var mods = new List<R2ModmanPackage>();
        string? packageId = null, scalarVersion = null;
        int? major = null, minor = null, patch = null;
        var enabled = true;
        var inMods = false;
        var inVersion = false;

        void FinishMod()
        {
            if (packageId is null) return;
            if (!Regex.IsMatch(packageId, "^[A-Za-z0-9_.-]{1,160}$"))
                throw new InvalidDataException($"O identificador de pacote '{packageId}' não é válido para Thunderstore.");
            var version = scalarVersion;
            if (version is null && major.HasValue && minor.HasValue && patch.HasValue)
                version = $"{major.Value}.{minor.Value}.{patch.Value}";
            if (version is null || !Regex.IsMatch(version, "^\\d+(?:\\.\\d+){2,3}(?:[-+][A-Za-z0-9.-]+)?$"))
                throw new InvalidDataException($"O manifesto não contém uma versão compatível para {packageId}.");
            if (mods.Any(mod => mod.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"O manifesto contém o pacote duplicado {packageId}.");
            mods.Add(new(packageId, version, enabled));
            if (mods.Count > MaxMods) throw new InvalidDataException("O perfil contém mais de 200 mods; divida a importação em perfis menores.");
        }

        foreach (var line in manifest.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("profileName:", StringComparison.Ordinal))
                profileName = Unquote(trimmed["profileName:".Length..].Trim());
            if (trimmed.Equals("mods:", StringComparison.Ordinal)) { inMods = true; continue; }
            if (!inMods) continue;
            var nameMatch = Regex.Match(line, "^\\s{2}-\\s+name:\\s*(.+?)\\s*$");
            if (nameMatch.Success)
            {
                FinishMod();
                packageId = Unquote(nameMatch.Groups[1].Value.Trim());
                scalarVersion = null; major = minor = patch = null; enabled = true; inVersion = false;
                continue;
            }
            if (packageId is null) continue;
            var versionMatch = Regex.Match(line, "^\\s{4}version:\\s*(.*?)\\s*$");
            if (versionMatch.Success)
            {
                var value = Unquote(versionMatch.Groups[1].Value.Trim());
                scalarVersion = value.Length == 0 ? null : value;
                inVersion = value.Length == 0;
                continue;
            }
            if (inVersion && Regex.Match(line, "^\\s{6}major:\\s*(\\d+)\\s*$") is { Success: true } majorMatch)
                major = int.Parse(majorMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            else if (inVersion && Regex.Match(line, "^\\s{6}minor:\\s*(\\d+)\\s*$") is { Success: true } minorMatch)
                minor = int.Parse(minorMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            else if (inVersion && Regex.Match(line, "^\\s{6}patch:\\s*(\\d+)\\s*$") is { Success: true } patchMatch)
                patch = int.Parse(patchMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var enabledMatch = Regex.Match(line, "^\\s{4}enabled:\\s*(true|false)\\s*$", RegexOptions.IgnoreCase);
            if (enabledMatch.Success) enabled = bool.Parse(enabledMatch.Groups[1].Value);
        }
        FinishMod();
        if (mods.Count == 0) throw new InvalidDataException("O manifesto r2modman não lista nenhum pacote instalável.");
        return (profileName, mods);
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            try { return JsonSerializer.Deserialize<string>(value) ?? ""; }
            catch (JsonException) { return value[1..^1]; }
        }
        return value.Length >= 2 && value[0] == '\'' && value[^1] == '\''
            ? value[1..^1].Replace("''", "'", StringComparison.Ordinal) : value;
    }

    private static bool TryGetConfigRelativePath(string archivePath, out string relativePath)
    {
        relativePath = "";
        var segments = archivePath.Replace('\\', '/').Split('/');
        var configIndex = -1;
        for (var index = 0; index + 1 < segments.Length; index++)
        {
            if (segments[index].Equals("BepInEx", StringComparison.OrdinalIgnoreCase) &&
                segments[index + 1].Equals("config", StringComparison.OrdinalIgnoreCase))
            {
                configIndex = index + 1;
                break;
            }
        }
        if (configIndex < 0)
            configIndex = Array.FindIndex(segments, segment => segment.Equals("config", StringComparison.OrdinalIgnoreCase));
        if (configIndex < 0 || configIndex + 1 >= segments.Length) return false;
        var relativeSegments = segments[(configIndex + 1)..];
        var relative = string.Join('/', relativeSegments);
        if (relative.StartsWith('/') || relative.Contains(':') || relative.Any(char.IsControl))
            throw new InvalidDataException("O perfil contém um caminho de configuração inválido.");
        if (relativeSegments.Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException("O perfil contém um caminho de configuração fora da pasta permitida.");
        relativePath = relative;
        return true;
    }

    private static string GetConfigDestination(string installPath, string relativePath)
    {
        if (!Path.IsPathFullyQualified(installPath)) throw new InvalidDataException("A pasta de instalação do perfil não é válida.");
        var root = Path.GetFullPath(Path.Combine(installPath, "BepInEx", "config"));
        var destination = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O caminho de configuração sai da pasta BepInEx/config.");
        return destination;
    }

    private static void EnsureNoReparsePoint(string installPath, string directory)
    {
        var root = Path.GetFullPath(Path.Combine(installPath, "BepInEx", "config"));
        if (IsReparsePoint(root)) throw new InvalidDataException("A pasta BepInEx/config aponta para outro local do disco; a importação foi bloqueada.");
        var current = root;
        var relative = Path.GetRelativePath(root, directory);
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     .Where(segment => segment.Length > 0 && segment != "."))
        {
            current = Path.Combine(current, segment);
            if (IsReparsePoint(current)) throw new InvalidDataException("Uma pasta de configuração aponta para outro local do disco; a importação foi bloqueada.");
        }
    }

    private static bool IsReparsePoint(string path) => Directory.Exists(path) &&
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, long maxBytes, CancellationToken cancellationToken)
    {
        await using (stream)
        using (var output = new MemoryStream())
        {
            var buffer = new byte[32 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                if (output.Length + read > maxBytes) throw new InvalidDataException("O perfil baixado excede o limite de tamanho.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            return output.ToArray();
        }
    }

    private sealed record StagedConfig(string Destination, string TemporaryPath, bool Existed, byte[]? PreviousBytes);
}

public sealed record R2ModmanPackage(string PackageId, string Version, bool Enabled);
public sealed record R2ModmanConfig(string RelativePath, string Content, long Size);
public sealed record R2ModmanArchive(string ProfileName, IReadOnlyList<R2ModmanPackage> Mods,
    IReadOnlyList<R2ModmanConfig> Configs, long ConfigBytes);
