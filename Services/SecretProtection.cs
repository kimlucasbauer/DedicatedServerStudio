using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DedicatedServerStudio.Services;

/// <summary>Protects per-machine secrets with Windows DPAPI. Secrets never go to the browser.</summary>
public static class SecretProtection
{
    private const uint CryptProtectUiForbidden = 0x1;
    private const uint CryptProtectLocalMachine = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description,
        IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        EnsureWindows();
        var bytes = Encoding.UTF8.GetBytes(value);
        try { return "u:" + Convert.ToBase64String(Transform(bytes, protect: true, localMachine: false)); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            // Windows may run the host without an interactive user profile (for example, as a service).
            // Machine-scope DPAPI still keeps the secret encrypted; the app's data directory ACL then
            // limits who can read the ciphertext.
            return "m:" + Convert.ToBase64String(Transform(bytes, protect: true, localMachine: true));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        EnsureWindows();
        var machineScope = value.StartsWith("m:", StringComparison.Ordinal);
        var payload = value.StartsWith("u:", StringComparison.Ordinal) || machineScope ? value[2..] : value;
        var bytes = Convert.FromBase64String(payload);
        try { return Encoding.UTF8.GetString(Transform(bytes, protect: false, localMachine: machineScope)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static byte[] Transform(byte[] bytes, bool protect, bool localMachine)
    {
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        var input = new DataBlob { Size = bytes.Length, Data = pinned.AddrOfPinnedObject() };
        DataBlob output;
        try
        {
            var ok = protect
                ? CryptProtectData(ref input, "Valheim Server Studio", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden | (localMachine ? CryptProtectLocalMachine : 0), out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Size];
            try { Marshal.Copy(output.Data, result, 0, output.Size); return result; }
            finally { LocalFree(output.Data); }
        }
        finally
        {
            pinned.Free();
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Local secret protection currently requires Windows DPAPI.");
    }
}
