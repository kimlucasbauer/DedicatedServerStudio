using System.Diagnostics;
using DedicatedServerStudio.Models;

namespace DedicatedServerStudio.Services;

/// <summary>Game-specific launch and shutdown behavior. A new game can be added without changing the web API.</summary>
public interface IGameAdapter
{
    string Id { get; }
    string DisplayName { get; }
    string ExecutableName { get; }
    string SteamAppId { get; }
    int PortSpan { get; }
    string FirewallProtocol { get; }
    ProcessStartInfo BuildStartInfo(ServerProfile profile, string saveDirectory);
    Task<(bool Success, string Message)> StopAsync(IManagedServerProcess process, ServerProfile profile);
}

public sealed class GameAdapterRegistry(IEnumerable<IGameAdapter> adapters)
{
    private readonly IReadOnlyDictionary<string, IGameAdapter> _adapters =
        adapters.ToDictionary(adapter => adapter.Id, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<GameView> List() => _adapters.Values
        .OrderBy(adapter => adapter.DisplayName)
        .Select(adapter => new GameView(adapter.Id, adapter.DisplayName, adapter.ExecutableName, adapter.PortSpan, Available: true, adapter.SteamAppId))
        .ToArray();

    public bool TryGet(string? gameId, out IGameAdapter adapter) =>
        _adapters.TryGetValue(string.IsNullOrWhiteSpace(gameId) ? "valheim" : gameId, out adapter!);

    public int PortSpan(string? gameId) => TryGet(gameId, out var adapter) ? adapter.PortSpan : 1;
}

public sealed class ValheimGameAdapter : IGameAdapter
{
    public string Id => "valheim";
    public string DisplayName => "Valheim";
    public string ExecutableName => "valheim_server.exe";
    public string SteamAppId => "896660";
    public int PortSpan => 2;
    public string FirewallProtocol => "UDP";

    public ProcessStartInfo BuildStartInfo(ServerProfile profile, string saveDirectory)
    {
        var executable = Path.Combine(profile.InstallPath, ExecutableName);
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = profile.InstallPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        Add(start, "-nographics", "-batchmode", "-name", profile.ServerName, "-port", profile.Port.ToString(),
            "-world", profile.WorldName, "-password", profile.GamePassword, "-public", profile.IsPublic ? "1" : "0",
            "-savedir", saveDirectory);
        if (profile.Crossplay) start.ArgumentList.Add("-crossplay");
        AddNumber(start, profile, "saveInterval", "-saveinterval", 1800);
        AddNumber(start, profile, "backups", "-backups", 4);
        AddNumber(start, profile, "backupShort", "-backupshort", 7200);
        AddNumber(start, profile, "backupLong", "-backuplong", 43200);
        AddString(start, profile, "logFile", "-logFile");
        AddString(start, profile, "instanceId", "-instanceid");
        if (TryGetString(profile, "preset", out var preset) && !string.IsNullOrWhiteSpace(preset))
            Add(start, "-preset", preset.Trim().ToLowerInvariant());
        AddWorldModifiers(start, profile);
        foreach (var argument in profile.ExtraArguments.Where(value => !string.IsNullOrWhiteSpace(value)))
            start.ArgumentList.Add(argument);
        return start;
    }

    public async Task<(bool Success, string Message)> StopAsync(IManagedServerProcess process, ServerProfile profile)
    {
        // If a stdin console plugin is installed, save flushes immediately. Ctrl+Break then
        // follows Iron Gate's documented clean-shutdown path for the dedicated server.
        try { await process.SendCommandAsync("save"); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException) { }
        await Task.Delay(TimeSpan.FromSeconds(2));
        if (process.HasExited) return (true, "The server stopped after its save request.");
        var stopped = await process.SendCtrlBreakAsync(TimeSpan.FromSeconds(30));
        return stopped
            ? (true, "The server received Ctrl+Break and shut down cleanly.")
            : (false, "O Windows não confirmou uma parada limpa. O servidor continua ativo para proteger o mundo; use o encerramento forçado somente se aceitar o risco de perder o progresso desde o último salvamento.");
    }

    private static void Add(ProcessStartInfo start, params string[] arguments)
    {
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
    }

    private static void AddNumber(ProcessStartInfo start, ServerProfile profile, string key, string argument, int fallback)
    {
        var value = TryGetInt(profile, key, out var configured) ? configured : fallback;
        Add(start, argument, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void AddString(ProcessStartInfo start, ServerProfile profile, string key, string argument)
    {
        if (TryGetString(profile, key, out var value) && !string.IsNullOrWhiteSpace(value)) Add(start, argument, value.Trim());
    }

    private static bool TryGetString(ServerProfile profile, string key, out string value)
    {
        value = "";
        if (!profile.GameSettings.TryGetValue(key, out var setting) || setting.ValueKind != System.Text.Json.JsonValueKind.String) return false;
        value = setting.GetString() ?? "";
        return true;
    }

    private static bool TryGetInt(ServerProfile profile, string key, out int value)
    {
        value = 0;
        return profile.GameSettings.TryGetValue(key, out var setting) && setting.ValueKind == System.Text.Json.JsonValueKind.Number &&
            setting.TryGetInt32(out value);
    }

    private static void AddWorldModifiers(ProcessStartInfo start, ServerProfile profile)
    {
        if (!profile.GameSettings.TryGetValue("modifiers", out var setting) || setting.ValueKind != System.Text.Json.JsonValueKind.Object) return;
        foreach (var category in new[] { "combat", "deathPenalty", "resources", "raids", "portals" })
        {
            if (setting.TryGetProperty(category, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString())) Add(start, "-modifier", category, value.GetString()!.Trim().ToLowerInvariant());
        }
        if (!profile.GameSettings.TryGetValue("setKeys", out var keys) || keys.ValueKind != System.Text.Json.JsonValueKind.Array) return;
        var allowed = new HashSet<string>(["nobuildcost", "playerevents", "passivemobs", "nomap"], StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys.EnumerateArray().Where(item => item.ValueKind == System.Text.Json.JsonValueKind.String)
                     .Select(item => item.GetString()?.Trim()).Where(item => !string.IsNullOrWhiteSpace(item) && allowed.Contains(item!)))
            Add(start, "-setkey", key!);
    }
}
