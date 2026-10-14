using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using DedicatedServerStudio.Models;

namespace DedicatedServerStudio.Services;

public sealed class PlayerManagementService
{
    private static readonly Regex SteamConnection = new(@"Got connection SteamID\s+(?<id>[A-Za-z0-9_:-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PlayFabConnection = new(@"Got connection PlayFab(?:ID| ID)?\s+(?<id>[A-Za-z0-9_:-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CharacterSpawn = new(@"Got character ZDOID from\s+(?<name>.+?)\s*:\s*-?\d+\s*:\s*-?\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SocketClosed = new(@"Closing socket\s+(?<id>[A-Za-z0-9_:-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedLog> _logCache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<PlayerManagementSnapshot> GetAsync(ServerProfile profile, string saveDirectory, string logPath,
        bool running, CancellationToken cancellationToken)
    {
        var admins = await ReadListAsync(saveDirectory, "adminlist.txt", cancellationToken);
        var bans = await ReadListAsync(saveDirectory, "bannedlist.txt", cancellationToken);
        var permitted = await ReadListAsync(saveDirectory, "permittedlist.txt", cancellationToken);
        var records = await ReadLogAsync(logPath, cancellationToken);
        var query = running && profile.GameId.Equals("valheim", StringComparison.OrdinalIgnoreCase)
            ? await QueryServerAsync(profile.Port + 1, cancellationToken)
            : null;
        var all = new Dictionary<string, PlayerManagementPlayer>(StringComparer.OrdinalIgnoreCase);
        var adminSet = admins.ToHashSet(StringComparer.Ordinal);
        var banSet = bans.ToHashSet(StringComparer.Ordinal);
        foreach (var record in records.Values)
        {
            var active = running && record.ActiveSince.HasValue;
            var session = active ? Math.Max(0, (DateTimeOffset.UtcNow - record.ActiveSince!.Value).TotalSeconds) : 0;
            var userId = record.UserId;
            var name = string.IsNullOrWhiteSpace(record.Name) ? userId ?? "Jogador sem nome" : record.Name.Trim();
            all[PlayerKey(userId, name)] = new PlayerManagementPlayer(userId, name, active, session,
                record.TotalSeconds + session, record.LastSeenAt, userId is not null && adminSet.Contains(userId),
                userId is not null && banSet.Contains(userId), userId is not null && permitted.Contains(userId, StringComparer.Ordinal));
        }

        // CharacterSpawn can arrive after the connection has already been closed, so
        // the log reader may have one record keyed by platform ID and another by name.
        // Fold the name-only entry into the unique ID-backed record before presenting it.
        MergeNameOnlyRecordsIntoUniqueIds(all);

        if (query?.Players is { } online)
        {
            foreach (var livePlayer in online)
            {
                var liveName = NormalizePlayerName(livePlayer.Name);
                var sameName = all.Values.Where(player =>
                    string.Equals(NormalizePlayerName(player.Name), liveName, StringComparison.OrdinalIgnoreCase)).ToArray();
                var idMatches = sameName.Where(player => player.UserId is not null).ToArray();
                var match = idMatches.Length == 1
                    ? idMatches[0]
                    : idMatches.Length == 0 ? sameName.FirstOrDefault() : null;
                var id = match?.UserId;
                var name = string.IsNullOrWhiteSpace(liveName) ? match?.Name ?? "Jogador" : liveName;
                var key = PlayerKey(id, name);
                var loggedSession = match?.SessionSeconds ?? 0;
                var session = Math.Max(livePlayer.DurationSeconds, loggedSession);
                all[key] = new PlayerManagementPlayer(id, name, true, session,
                    Math.Max(match?.TotalSeconds ?? 0, (match?.TotalSeconds ?? 0) - loggedSession + session),
                    DateTimeOffset.UtcNow - TimeSpan.FromSeconds(session), id is not null && adminSet.Contains(id),
                    id is not null && banSet.Contains(id), id is not null && permitted.Contains(id, StringComparer.Ordinal));

                if (id is not null)
                {
                    foreach (var anonymous in sameName.Where(player => player.UserId is null))
                        all.Remove(PlayerKey(null, anonymous.Name));
                }
            }
        }

        // Also covers a live name learned during this request when the log has a single
        // matching platform identity. Ambiguous same-name accounts are left separate.
        MergeNameOnlyRecordsIntoUniqueIds(all);

        foreach (var id in admins.Concat(bans).Concat(permitted).Distinct(StringComparer.Ordinal))
        {
            if (all.Values.Any(player => string.Equals(player.UserId, id, StringComparison.Ordinal))) continue;
            all[PlayerKey(id, id)] = new PlayerManagementPlayer(id, id, false, 0, 0, null,
                adminSet.Contains(id), banSet.Contains(id), permitted.Contains(id, StringComparer.Ordinal));
        }

        var trackedCount = records.Values.Count(record => record.ActiveSince.HasValue);
        int? activeCount = !running ? 0 : query?.PlayerCount ?? (trackedCount > 0 ? trackedCount : null);
        var source = !running ? "Servidor parado" : query?.PlayerCount is not null
            ? "Consulta direta ao servidor" : activeCount is not null ? "Estimativa pelos registros da sessão" : "Consulta indisponível";
        return new PlayerManagementSnapshot(activeCount, source, query?.Available == true, all.Values
            .OrderByDescending(player => player.IsActive).ThenBy(player => player.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            admins, bans, permitted, running, DateTimeOffset.UtcNow,
            query?.Players?.Select(player => NormalizePlayerName(player.Name)).ToArray());
    }

    public async Task<PlayerListMutation> UpdateAccessAsync(string saveDirectory, string action, string userId, bool running,
        CancellationToken cancellationToken)
    {
        userId = (userId ?? string.Empty).Trim();
        if (userId.Length is 0 or > 160 || !Regex.IsMatch(userId, "^[A-Za-z0-9_:-]+$"))
            throw new ArgumentException("Informe um ID de plataforma válido (letras, números, _ , : ou -).", nameof(userId));
        var targetList = action switch
        {
            "admin" or "unadmin" => "adminlist.txt",
            "ban" or "unban" => "bannedlist.txt",
            "allow" or "unallow" => "permittedlist.txt",
            _ => throw new ArgumentException("Ação de gestão desconhecida.", nameof(action))
        };
        var gate = _fileLocks.GetOrAdd(Path.GetFullPath(saveDirectory), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(saveDirectory);
            var targetPath = Path.Combine(saveDirectory, targetList);
            var lines = await ReadLinesSafeAsync(targetPath, cancellationToken);
            var exists = lines.Any(line => string.Equals(line.Trim(), userId, StringComparison.Ordinal));
            var shouldAdd = action is "admin" or "ban" or "allow";
            var changed = false;
            if (shouldAdd && !exists) { lines.Add(userId); changed = true; }
            else if (!shouldAdd)
            {
                var kept = lines.Where(line => !string.Equals(line.Trim(), userId, StringComparison.Ordinal)).ToList();
                changed = kept.Count != lines.Count;
                lines = kept;
            }
            if (action == "admin" && changed) await RemoveIdAsync(saveDirectory, "bannedlist.txt", userId, cancellationToken);
            if (action == "ban" && changed)
            {
                await RemoveIdAsync(saveDirectory, "adminlist.txt", userId, cancellationToken);
                await RemoveIdAsync(saveDirectory, "permittedlist.txt", userId, cancellationToken);
            }
            if (action == "allow" && changed) await RemoveIdAsync(saveDirectory, "bannedlist.txt", userId, cancellationToken);
            if (changed) await WriteLinesAtomicAsync(targetPath, lines, cancellationToken);
            var status = !changed ? "O ID já estava nesse estado." : action switch
            {
                "admin" => "Admin promovido.",
                "unadmin" => "Permissão de admin removida.",
                "ban" => "Jogador adicionado à lista de banimento.",
                "unban" => "Jogador removido da lista de banimento.",
                "allow" => "Jogador adicionado à lista de permitidos. Apenas os IDs dessa lista poderão entrar.",
                _ => "Jogador removido da lista de permitidos."
            };
            if (changed && running) status += action is "allow" or "unallow" or "admin" or "unadmin"
                ? " O arquivo foi salvo; o Valheim pode exigir que o jogador reconecte ou que o servidor reinicie para aplicar a permissão."
                : " A alteração foi enviada durante a execução e também ficará salva para os próximos inícios.";
            return new PlayerListMutation(changed, status, false);
        }
        finally { gate.Release(); }
    }

    private async Task RemoveIdAsync(string root, string fileName, string userId, CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, fileName);
        var lines = await ReadLinesSafeAsync(path, cancellationToken);
        var kept = lines.Where(line => !string.Equals(line.Trim(), userId, StringComparison.Ordinal)).ToList();
        if (kept.Count != lines.Count) await WriteLinesAtomicAsync(path, kept, cancellationToken);
    }

    private static async Task<IReadOnlyList<string>> ReadListAsync(string root, string fileName, CancellationToken token) =>
        (await ReadLinesSafeAsync(Path.Combine(root, fileName), token)).Select(line => line.Trim().TrimStart('\uFEFF'))
            // Valheim list files may contain example/header comments. Only platform IDs
            // are actionable; comments must never appear as players in the management UI.
            .Where(line => line.Length is > 0 and <= 160 && !line.StartsWith('#') && !line.StartsWith("//") &&
                Regex.IsMatch(line, "^[A-Za-z0-9_:-]+$"))
            .Distinct(StringComparer.Ordinal).ToArray();

    private static async Task<List<string>> ReadLinesSafeAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return [];
        try { return (await File.ReadAllLinesAsync(path, token)).ToList(); }
        catch (IOException) { return []; }
    }

    private static async Task WriteLinesAtomicAsync(string path, IReadOnlyList<string> lines, CancellationToken token)
    {
        var temporary = path + ".dss.tmp";
        await File.WriteAllTextAsync(temporary, lines.Count == 0 ? string.Empty : string.Join(Environment.NewLine, lines) + Environment.NewLine,
            new UTF8Encoding(false), token);
        File.Move(temporary, path, true);
    }

    private async Task<IReadOnlyDictionary<string, LogPlayer>> ReadLogAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return new Dictionary<string, LogPlayer>(StringComparer.OrdinalIgnoreCase);
        DateTime lastWrite; long length;
        try { var info = new FileInfo(path); lastWrite = info.LastWriteTimeUtc; length = info.Length; }
        catch (IOException) { return new Dictionary<string, LogPlayer>(StringComparer.OrdinalIgnoreCase); }
        if (_logCache.TryGetValue(path, out var cached) && cached.Length == length && cached.LastWriteUtc == lastWrite)
            return cached.Players;
        string[] lines;
        try { lines = await File.ReadAllLinesAsync(path, token); }
        catch (IOException) { return new Dictionary<string, LogPlayer>(StringComparer.OrdinalIgnoreCase); }
        var players = new Dictionary<string, LogPlayer>(StringComparer.OrdinalIgnoreCase);
        var open = new Dictionary<string, (LogPlayer Player, DateTimeOffset Started)>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var (timestamp, message) = ParseLogLine(line);
            var connection = SteamConnection.Match(message);
            if (!connection.Success) connection = PlayFabConnection.Match(message);
            if (connection.Success)
            {
                var id = connection.Groups["id"].Value;
                if (open.TryGetValue(id, out var previous)) Close(previous.Player, previous.Started, timestamp);
                if (!players.TryGetValue(id, out var player)) players[id] = player = new LogPlayer(id);
                player.LastSeenAt = timestamp;
                open[id] = (player, timestamp);
                player.ActiveSince = timestamp;
                continue;
            }
            var spawn = CharacterSpawn.Match(message);
            if (spawn.Success)
            {
                var name = spawn.Groups["name"].Value.Trim();
                var candidate = open.Where(entry => string.IsNullOrEmpty(entry.Value.Player.Name) &&
                        timestamp - entry.Value.Started <= TimeSpan.FromMinutes(5))
                    .OrderByDescending(entry => entry.Value.Started).FirstOrDefault();
                if (!string.IsNullOrEmpty(candidate.Key))
                {
                    candidate.Value.Player.Name = name;
                    candidate.Value.Player.LastSeenAt = timestamp;
                }
                else
                {
                    var nameKey = "name:" + name;
                    if (!players.TryGetValue(nameKey, out var player)) players[nameKey] = player = new LogPlayer(null);
                    player.Name = name;
                    player.LastSeenAt = timestamp;
                }
                continue;
            }
            var close = SocketClosed.Match(message);
            if (close.Success && open.Remove(close.Groups["id"].Value, out var session))
            {
                Close(session.Player, session.Started, timestamp);
                continue;
            }
            if (message.Contains("Server process exited", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var activeSession in open.Values) Close(activeSession.Player, activeSession.Started, timestamp);
                open.Clear();
            }
        }
        foreach (var session in open.Values) session.Player.ActiveSince = session.Started;
        var snapshot = players.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        _logCache[path] = new CachedLog(length, lastWrite, snapshot);
        return snapshot;
    }

    private static (DateTimeOffset Timestamp, string Message) ParseLogLine(string line)
    {
        if (line.Length > 2 && line[0] == '[')
        {
            var end = line.IndexOf(']');
            if (end > 1 && DateTimeOffset.TryParse(line.AsSpan(1, end - 1), out var timestamp))
                return (timestamp, line[(end + 1)..].TrimStart());
        }
        var colon = line.IndexOf(": ", StringComparison.Ordinal);
        if (colon > 0 && DateTimeOffset.TryParse(line.AsSpan(0, colon), out var legacy))
            return (legacy, line[(colon + 2)..]);
        return (DateTimeOffset.UtcNow, line);
    }

    private static void Close(LogPlayer player, DateTimeOffset started, DateTimeOffset ended)
    {
        if (ended > started) player.TotalSeconds += (ended - started).TotalSeconds;
        player.LastSeenAt = ended;
        player.ActiveSince = null;
    }

    private static void MergeNameOnlyRecordsIntoUniqueIds(Dictionary<string, PlayerManagementPlayer> players)
    {
        var anonymousGroups = players.Values
            .Where(player => player.UserId is null && !string.IsNullOrWhiteSpace(player.Name))
            .GroupBy(player => NormalizePlayerName(player.Name), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var group in anonymousGroups)
        {
            var identified = players.Values.Where(player => player.UserId is not null &&
                string.Equals(NormalizePlayerName(player.Name), group.Key, StringComparison.OrdinalIgnoreCase)).ToArray();
            var candidate = identified.Length == 1
                ? identified[0]
                : identified.Count(player => player.IsActive) == 1
                    ? identified.First(player => player.IsActive)
                    : null;
            if (candidate is null) continue;

            var anonymous = group.ToArray();
            var merged = candidate with
            {
                SessionSeconds = Math.Max(candidate.SessionSeconds, anonymous.Max(player => player.SessionSeconds)),
                TotalSeconds = Math.Max(candidate.TotalSeconds, anonymous.Max(player => player.TotalSeconds)),
                LastSeenAt = Max(candidate.LastSeenAt, anonymous.Max(player => player.LastSeenAt))
            };
            players[PlayerKey(candidate.UserId, candidate.Name)] = merged;
            foreach (var duplicate in anonymous) players.Remove(PlayerKey(null, duplicate.Name));
        }
    }

    private static string NormalizePlayerName(string? name) => name?.Trim() ?? string.Empty;

    private static DateTimeOffset? Max(DateTimeOffset? first, DateTimeOffset? second) =>
        first is null ? second : second is null ? first : first > second ? first : second;

    private static string PlayerKey(string? id, string name) => id ?? "name:" + NormalizePlayerName(name).ToLowerInvariant();

    private static async Task<ServerQuery?> QueryServerAsync(int port, CancellationToken cancellationToken)
    {
        if (port is < 1 or > 65535) return null;
        try
        {
            using var client = new UdpClient(AddressFamily.InterNetwork);
            client.Connect(IPAddress.Loopback, port);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(1500));
            var infoRequest = new byte[] { 0xff, 0xff, 0xff, 0xff, 0x54, (byte)'S', (byte)'o', (byte)'u', (byte)'r', (byte)'c', (byte)'e', (byte)' ', (byte)'E', (byte)'n', (byte)'g', (byte)'i', (byte)'n', (byte)'e', (byte)' ', (byte)'Q', (byte)'u', (byte)'e', (byte)'r', (byte)'y', 0 };
            var info = await SendInfoQueryAsync(client, infoRequest, timeout.Token);
            if (info.Buffer.Length < 6 || info.Buffer[4] != 0x49) return null;
            var playerCount = ReadInfoPlayerCount(info.Buffer);
            if (playerCount is null) return null;
            IReadOnlyList<QueryPlayer>? players;
            try { players = await QueryPlayersAsync(client, timeout.Token); }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or InvalidOperationException) { players = null; }
            return new ServerQuery(playerCount.Value, players, true);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static async Task<UdpReceiveResult> SendInfoQueryAsync(UdpClient client, byte[] request, CancellationToken token)
    {
        await client.SendAsync(request, token);
        var response = await client.ReceiveAsync(token);
        if (response.Buffer.Length < 9 || response.Buffer[4] != 0x41) return response;

        // Source servers may require the A2S_INFO challenge handshake before returning player counts.
        var challengedRequest = new byte[request.Length + 4];
        Buffer.BlockCopy(request, 0, challengedRequest, 0, request.Length);
        Buffer.BlockCopy(response.Buffer, 5, challengedRequest, request.Length, 4);
        await client.SendAsync(challengedRequest, token);
        return await client.ReceiveAsync(token);
    }
    private static async Task<IReadOnlyList<QueryPlayer>?> QueryPlayersAsync(UdpClient client, CancellationToken token)
    {
        var request = new byte[9];
        request[0] = request[1] = request[2] = request[3] = 0xff;
        request[4] = 0x55;
        BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(5), -1);
        await client.SendAsync(request, token);
        var response = await client.ReceiveAsync(token);
        if (response.Buffer.Length < 6) return null;
        if (response.Buffer[4] == 0x41)
        {
            if (response.Buffer.Length < 9) return null;
            response.Buffer.AsSpan(5, 4).CopyTo(request.AsSpan(5));
            await client.SendAsync(request, token);
            response = await client.ReceiveAsync(token);
        }
        var data = response.Buffer;
        if (data.Length < 6 || data[4] != 0x44) return null;
        var count = data[5];
        var cursor = 6;
        var result = new List<QueryPlayer>(count);
        for (var index = 0; index < count; index++)
        {
            if (cursor >= data.Length) return null;
            cursor++; // Player index.
            var name = ReadCString(data, ref cursor);
            if (name is null || cursor + 8 > data.Length) return null;
            cursor += 4; // Score.
            var duration = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(cursor, 4)));
            cursor += 4;
            result.Add(new QueryPlayer(name, double.IsFinite(duration) ? Math.Max(0, duration) : 0));
        }
        return result;
    }

    private static int? ReadInfoPlayerCount(byte[] packet)
    {
        var cursor = 6;
        for (var value = 0; value < 4; value++) if (ReadCString(packet, ref cursor) is null) return null;
        if (cursor + 5 > packet.Length) return null;
        cursor += 2; // App ID.
        return packet[cursor];
    }

    private static string? ReadCString(byte[] bytes, ref int cursor)
    {
        if (cursor >= bytes.Length) return null;
        var end = Array.IndexOf(bytes, (byte)0, cursor);
        if (end < 0) return null;
        var value = Encoding.UTF8.GetString(bytes, cursor, end - cursor);
        cursor = end + 1;
        return value;
    }

    private sealed record CachedLog(long Length, DateTime LastWriteUtc, IReadOnlyDictionary<string, LogPlayer> Players);
    private sealed record ServerQuery(int PlayerCount, IReadOnlyList<QueryPlayer>? Players, bool Available);
    private sealed record QueryPlayer(string Name, double DurationSeconds);
    private sealed class LogPlayer(string? id)
    {
        public string? UserId { get; } = id;
        public string Name { get; set; } = "";
        public double TotalSeconds { get; set; }
        public DateTimeOffset? ActiveSince { get; set; }
        public DateTimeOffset? LastSeenAt { get; set; }
        public LogPlayer Clone() => new(UserId) { Name = Name, TotalSeconds = TotalSeconds, ActiveSince = ActiveSince, LastSeenAt = LastSeenAt };
    }
}

public sealed record PlayerManagementSnapshot(int? ActiveCount, string CountSource, bool QueryAvailable,
    IReadOnlyList<PlayerManagementPlayer> Players, IReadOnlyList<string> Admins, IReadOnlyList<string> Banned,
    IReadOnlyList<string> Permitted, bool ServerRunning, DateTimeOffset UpdatedAt,
    IReadOnlyList<string>? LivePlayerNames = null);

public sealed record PlayerManagementPlayer(string? UserId, string Name, bool IsActive, double SessionSeconds, double TotalSeconds,
    DateTimeOffset? LastSeenAt, bool IsAdmin, bool IsBanned, bool IsPermitted);

public sealed record PlayerListMutation(bool Changed, string Message, bool RequiresRestart);
