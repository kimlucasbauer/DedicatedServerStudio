using System.Collections.Concurrent;

namespace DedicatedServerStudio.Services;

public sealed record NexusDownloadAuthorization(string Key, long Expires);

public sealed class NexusDownloadAuthorizationService
{
    private const int MaxAuthorizations = 1024;
    private readonly ConcurrentDictionary<(string ModId, string FileId), NexusDownloadAuthorization> _authorizations = new();

    public void Set(string modId, string fileId, NexusDownloadAuthorization authorization)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var expired in _authorizations.Where(item => item.Value.Expires <= now).Select(item => item.Key))
            _authorizations.TryRemove(expired, out _);
        if (_authorizations.Count >= MaxAuthorizations)
        {
            foreach (var oldest in _authorizations.OrderBy(item => item.Value.Expires)
                         .Take(_authorizations.Count - MaxAuthorizations + 1).Select(item => item.Key))
                _authorizations.TryRemove(oldest, out _);
        }
        _authorizations[(modId, fileId)] = authorization;
    }

    public NexusDownloadAuthorization? Get(string modId, string fileId)
    {
        if (!_authorizations.TryGetValue((modId, fileId), out var authorization)) return null;
        if (authorization.Expires > DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return authorization;
        _authorizations.TryRemove((modId, fileId), out _);
        return null;
    }

    public void Remove(string modId, string fileId) => _authorizations.TryRemove((modId, fileId), out _);
}
