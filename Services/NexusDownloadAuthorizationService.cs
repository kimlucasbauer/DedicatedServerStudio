using System.Collections.Concurrent;

namespace DedicatedServerStudio.Services;

public sealed record NexusDownloadAuthorization(string Key, long Expires);

public sealed class NexusDownloadAuthorizationService
{
    private readonly ConcurrentDictionary<(string ModId, string FileId), NexusDownloadAuthorization> _authorizations = new();

    public void Set(string modId, string fileId, NexusDownloadAuthorization authorization) =>
        _authorizations[(modId, fileId)] = authorization;

    public NexusDownloadAuthorization? Get(string modId, string fileId)
    {
        if (!_authorizations.TryGetValue((modId, fileId), out var authorization)) return null;
        if (authorization.Expires > DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return authorization;
        _authorizations.TryRemove((modId, fileId), out _);
        return null;
    }

    public void Remove(string modId, string fileId) => _authorizations.TryRemove((modId, fileId), out _);
}
