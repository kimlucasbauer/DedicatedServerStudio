using System.Security.Cryptography;

namespace DedicatedServerStudio.Services;

/// <summary>Stores provider API keys encrypted under an installation-local key.</summary>
public sealed class ProviderCredentialVault(StateStore state)
{
    private readonly SemaphoreSlim _keyLock = new(1, 1);
    private byte[]? _key;

    public async Task<bool> IsConfiguredAsync(string provider) =>
        !string.IsNullOrWhiteSpace(await GetAsync(provider));

    public async Task<string?> GetAsync(string provider)
    {
        var keyName = Normalize(provider);
        var encrypted = await state.ReadAsync(data => data.ProtectedProviderApiKeys.GetValueOrDefault(keyName));
        if (string.IsNullOrWhiteSpace(encrypted)) return null;
        try { return Decrypt(encrypted, await GetKeyAsync()); }
        catch (CryptographicException) { return null; }
        catch (FormatException) { return null; }
    }

    public async Task SetAsync(string provider, string? apiKey)
    {
        var keyName = Normalize(provider);
        var encrypted = string.IsNullOrWhiteSpace(apiKey) ? null : Encrypt(apiKey.Trim(), await GetKeyAsync());
        await state.UpdateAsync(data =>
        {
            if (encrypted is null) data.ProtectedProviderApiKeys.Remove(keyName);
            else data.ProtectedProviderApiKeys[keyName] = encrypted;
            return true;
        });
    }

    private static string Normalize(string provider) => provider.Trim().ToLowerInvariant() switch
    {
        "nexus" or "nexusmods" => "nexus",
        "curseforge" or "curse-forge" => "curseforge",
        _ => throw new ArgumentException("Este provedor não aceita chave de API.", nameof(provider))
    };

    private async Task<byte[]> GetKeyAsync()
    {
        if (_key is not null) return _key;
        await _keyLock.WaitAsync();
        try
        {
            if (_key is not null) return _key;
            var path = Path.Combine(state.DataDirectory, "provider-secrets.key");
            if (File.Exists(path))
            {
                _key = await File.ReadAllBytesAsync(path);
                if (_key.Length != 32) throw new CryptographicException("A chave local de criptografia está inválida.");
                return _key;
            }

            var generated = RandomNumberGenerator.GetBytes(32);
            try
            {
                await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await file.WriteAsync(generated);
                await file.FlushAsync();
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                _key = generated;
            }
            catch (IOException) when (File.Exists(path))
            {
                CryptographicOperations.ZeroMemory(generated);
                _key = await File.ReadAllBytesAsync(path);
                if (_key.Length != 32) throw new CryptographicException("A chave local de criptografia está inválida.");
            }
            return _key;
        }
        finally { _keyLock.Release(); }
    }

    private static string Encrypt(string value, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = System.Text.Encoding.UTF8.GetBytes(value);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plain, cipher, tag);
        var payload = new byte[nonce.Length + tag.Length + cipher.Length];
        nonce.CopyTo(payload, 0);
        tag.CopyTo(payload, nonce.Length);
        cipher.CopyTo(payload, nonce.Length + tag.Length);
        CryptographicOperations.ZeroMemory(plain);
        return "v1." + Convert.ToBase64String(payload);
    }

    private static string Decrypt(string encrypted, byte[] key)
    {
        if (!encrypted.StartsWith("v1.", StringComparison.Ordinal)) throw new FormatException("Formato inválido.");
        var payload = Convert.FromBase64String(encrypted[3..]);
        if (payload.Length < 28) throw new FormatException("Conteúdo inválido.");
        var plain = new byte[payload.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(payload.AsSpan(0, 12), payload.AsSpan(28), payload.AsSpan(12, 16), plain);
        try { return System.Text.Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
