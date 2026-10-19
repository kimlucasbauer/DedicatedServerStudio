using System.Text.Json;

namespace DedicatedServerStudio.Services;

public sealed record DssUpdateProgress(
    Guid OperationId,
    string LatestVersion,
    string Phase,
    int Percent,
    string Message,
    long? DownloadedBytes,
    long? TotalBytes,
    string? Error,
    DateTimeOffset UpdatedAt);

public sealed class DssUpdateProgressStore(StateStore state)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
    private readonly object _gate = new();

    public string ProgressFilePath => Path.Combine(state.DataDirectory, "dss-update-progress.json");

    public DssUpdateProgress Begin(string version)
    {
        lock (_gate)
        {
            var previous = ReadUnsafe();
            if (previous is not null && previous.Phase is not ("completed" or "failed") &&
                DateTimeOffset.UtcNow - previous.UpdatedAt < TimeSpan.FromMinutes(10))
                throw new InvalidOperationException("Uma atualização do DSS já está em andamento.");

            var update = new DssUpdateProgress(Guid.NewGuid(), version, "checking", 3,
                "Pacote selecionado. Preparando a atualização…", null, null, null, DateTimeOffset.UtcNow);
            WriteUnsafe(update);
            return update;
        }
    }

    public DssUpdateProgress? Get(Guid? operationId = null)
    {
        lock (_gate)
        {
            var update = ReadUnsafe();
            return update is not null && (operationId is null || update.OperationId == operationId) ? update : null;
        }
    }

    public void Report(Guid operationId, string phase, int percent, string message,
        long? downloadedBytes = null, long? totalBytes = null, string? error = null)
    {
        lock (_gate)
        {
            var current = ReadUnsafe();
            if (current is null || current.OperationId != operationId) return;
            WriteUnsafe(current with
            {
                Phase = phase,
                Percent = Math.Clamp(percent, 0, 100),
                Message = message,
                DownloadedBytes = downloadedBytes,
                TotalBytes = totalBytes,
                Error = error,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
    }

    private DssUpdateProgress? ReadUnsafe()
    {
        if (!File.Exists(ProgressFilePath)) return null;
        try { return JsonSerializer.Deserialize<DssUpdateProgress>(File.ReadAllText(ProgressFilePath), JsonOptions); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private void WriteUnsafe(DssUpdateProgress update)
    {
        Directory.CreateDirectory(state.DataDirectory);
        var temporaryPath = ProgressFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(update, JsonOptions));
            File.Move(temporaryPath, ProgressFilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
