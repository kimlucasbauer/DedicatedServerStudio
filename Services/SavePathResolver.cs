using DedicatedServerStudio.Models;

namespace DedicatedServerStudio.Services;

public static class SavePathResolver
{
    public static string Resolve(ServerProfile profile, string dataDirectory)
    {
        if (profile.UseInstallPathSaves && !string.IsNullOrWhiteSpace(profile.InstallPath))
            return Path.Combine(Path.GetFullPath(profile.InstallPath), "Saves");
        if (!string.IsNullOrWhiteSpace(profile.SaveDirectory))
            return Path.GetFullPath(profile.SaveDirectory);
        return Path.Combine(dataDirectory, "instances", profile.Id.ToString("N"), "worlds");
    }
}
