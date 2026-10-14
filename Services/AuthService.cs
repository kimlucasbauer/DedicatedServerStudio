using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DedicatedServerStudio.Models;

namespace DedicatedServerStudio.Services;

public sealed class AuthService(StateStore store)
{
    private const int Iterations = 240_000;
    private const int LoginLockoutThreshold = 10;
    private const int MaxFailedLoginAuditEntries = 1_000;
    private sealed record SessionState(DateTimeOffset ExpiresAt, Guid UserId);
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private static readonly string DummyPasswordHash = HashPassword("DSS dummy credential used only to normalize failed-login work");

    public Task<bool> IsSetupAsync() => store.ReadAsync(data => data.Users.Any(user => user.Role == "admin") || !string.IsNullOrWhiteSpace(data.AdminPasswordHash));

    public async Task<bool> SetupAsync(string password)
    {
        if (password.Length < 11) return false;
        var saved = HashPassword(password);
        return await store.UpdateAsync(data =>
        {
            if (!string.IsNullOrWhiteSpace(data.AdminPasswordHash)) return false;
            data.AdminPasswordHash = saved;
            data.Users.Add(new DssUser { Username = "admin", PasswordHash = saved, Role = "admin" });
            return true;
        });
    }

    public async Task<bool> VerifyPasswordAsync(string password)
    {
        var saved = await store.ReadAsync(data => data.AdminPasswordHash);
        if (string.IsNullOrWhiteSpace(saved)) return false;
        var parts = saved.Split('.', 2);
        if (parts.Length != 2) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[0]);
            var expected = Convert.FromBase64String(parts[1]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException) { return false; }
    }

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return Convert.ToBase64String(salt) + "." + Convert.ToBase64String(hash);
    }

    public static bool VerifyPassword(string password, string? saved)
    {
        if (string.IsNullOrWhiteSpace(saved)) return false;
        var parts = saved.Split('.', 2);
        if (parts.Length != 2) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[0]);
            var expected = Convert.FromBase64String(parts[1]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException) { return false; }
    }

    public async Task<Guid?> AuthenticateAsync(string? username, string? password, string ipAddress)
    {
        var attemptedUsername = string.IsNullOrWhiteSpace(username) ? "admin" : username.Trim();
        var normalizedUsername = attemptedUsername.Length > 256 ? attemptedUsername[..256] : attemptedUsername;
        var displayUsername = attemptedUsername.Length > 64 ? attemptedUsername[..64] : attemptedUsername;
        var candidatePassword = password is { Length: <= 256 } ? password : "";
        var passwordWithinLimit = password is { Length: <= 256 };
        var snapshot = await store.ReadAsync(data =>
        {
            var user = data.Users.FirstOrDefault(item => string.Equals(item.Username, normalizedUsername, StringComparison.OrdinalIgnoreCase));
            var passwordHash = user?.PasswordHash ?? (string.Equals(normalizedUsername, "admin", StringComparison.OrdinalIgnoreCase) ? data.AdminPasswordHash : null);
            return (UserId: user?.Id, PasswordHash: passwordHash);
        });
        // Do the same expensive password derivation for unknown usernames, so responses do not
        // provide a useful timing signal for account discovery.
        var passwordMatches = VerifyPassword(candidatePassword, snapshot.PasswordHash ?? DummyPasswordHash) && passwordWithinLimit;
        var now = DateTimeOffset.UtcNow;

        var shouldResolveDeviceName = false;
        var result = await store.UpdateAsync(data =>
        {
            var user = data.Users.FirstOrDefault(item => string.Equals(item.Username, normalizedUsername, StringComparison.OrdinalIgnoreCase));
            var currentHash = user?.PasswordHash ?? (string.Equals(normalizedUsername, "admin", StringComparison.OrdinalIgnoreCase) ? data.AdminPasswordHash : null);
            var currentPasswordMatches = string.Equals(currentHash, snapshot.PasswordHash, StringComparison.Ordinal)
                ? passwordMatches
                : VerifyPassword(candidatePassword, currentHash ?? DummyPasswordHash) && passwordWithinLimit;
            var locked = user is not null && (user.LoginLocked || user.LoginLockedUntil is { } until && until > now);

            if (user is not null && currentPasswordMatches && !locked)
            {
                user.FailedLoginCount = 0;
                user.FailedLoginWindowStartedAt = null;
                user.LoginLockedUntil = null;
                user.LoginLocked = false;
                data.FailedLoginAttempts.RemoveAll(item => string.Equals(item.AttemptedUsername, user.Username, StringComparison.OrdinalIgnoreCase));
                return (Guid?)user.Id;
            }

            data.FailedLoginAttempts ??= [];
            data.FailedLoginAttemptsAggregated = true;
            var normalizedIp = NormalizeIpAddress(ipAddress);
            var attempt = data.FailedLoginAttempts.FirstOrDefault(item =>
                string.Equals(item.IpAddress, normalizedIp, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.AttemptedUsername, displayUsername, StringComparison.OrdinalIgnoreCase));
            if (attempt is null)
            {
                attempt = new FailedLoginAttempt
                {
                    FirstOccurredAt = now,
                    OccurredAt = now,
                    AttemptedUsername = displayUsername,
                    IpAddress = normalizedIp,
                    AttemptCount = 0
                };
                data.FailedLoginAttempts.Add(attempt);
            }
            attempt.OccurredAt = now;
            attempt.AttemptedUsername = displayUsername;
            attempt.AttemptCount++;
            attempt.DuringLockout = locked;
            if (locked) attempt.DuringLockoutCount++;
            if (data.FailedLoginAttempts.Count > MaxFailedLoginAuditEntries)
            {
                data.FailedLoginAttempts = data.FailedLoginAttempts
                    .OrderByDescending(item => item.OccurredAt)
                    .Take(MaxFailedLoginAuditEntries)
                    .ToList();
            }

            if (user is not null && !locked)
            {
                user.FailedLoginCount++;
                if (user.FailedLoginCount >= LoginLockoutThreshold)
                {
                    user.LoginLocked = true;
                    user.LoginLockedUntil = null;
                }
            }
            shouldResolveDeviceName = true;
            return null;
        });
        if (shouldResolveDeviceName) _ = ResolveDeviceNameAsync(NormalizeIpAddress(ipAddress));
        return result;
    }

    private async Task ResolveDeviceNameAsync(string ipAddress)
    {
        if (!IPAddress.TryParse(ipAddress, out var address)) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var entry = await Dns.GetHostEntryAsync(address).WaitAsync(timeout.Token);
            var hostName = entry.HostName.Trim().TrimEnd('.');
            if (string.IsNullOrWhiteSpace(hostName) || IPAddress.TryParse(hostName, out _)) return;
            await store.UpdateAsync(data =>
            {
                var attempts = data.FailedLoginAttempts.Where(item => string.Equals(item.IpAddress, ipAddress, StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrWhiteSpace(item.DeviceName)).ToArray();
                if (attempts.Length == 0) return false;
                foreach (var attempt in attempts) attempt.DeviceName = hostName;
                return true;
            });
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException or InvalidOperationException)
        {
            // Reverse DNS is best effort and must never delay or change the login response.
        }
    }

    private static string NormalizeIpAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        return IPAddress.TryParse(value.Trim(), out var address)
            ? (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString()
            : value.Trim();
    }

    public string CreateSession(HttpContext context, Guid userId)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var now = DateTimeOffset.UtcNow;
        foreach (var expired in _sessions.Where(item => item.Value.ExpiresAt <= now).Select(item => item.Key)) _sessions.TryRemove(expired, out _);
        _sessions[token] = new SessionState(now.AddHours(12), userId);
        context.Response.Cookies.Append("vhs_session", token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            IsEssential = true,
            Expires = now.AddHours(12)
        });
        return token;
    }

    public bool IsAuthenticated(HttpContext context)
    {
        return GetSessionUserId(context) is not null;
    }

    public Guid? GetSessionUserId(HttpContext context)
    {
        var token = context.Request.Cookies["vhs_session"];
        if (token is null || !_sessions.TryGetValue(token, out var session)) return null;
        if (session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _sessions.TryRemove(token, out _);
            return null;
        }
        return session.UserId;
    }

    public void SignOut(HttpContext context)
    {
        var token = context.Request.Cookies["vhs_session"];
        if (token is not null) _sessions.TryRemove(token, out _);
        context.Response.Cookies.Delete("vhs_session", new CookieOptions { SameSite = SameSiteMode.Strict, Secure = context.Request.IsHttps });
    }
}
