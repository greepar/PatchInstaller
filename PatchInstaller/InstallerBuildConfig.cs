using System;
using System.Linq;
using System.Reflection;

namespace Avalonia_NativeAOT_SingleFile;

internal static class InstallerBuildConfig
{
    public static string ProductName => GetMetadata(
        "InstallerProductName",
        typeof(InstallerBuildConfig).Assembly.GetName().Name ?? "PatchInstaller");

    public static string DefaultPatchUrl => GetMetadata("InstallerDefaultPatchUrl", string.Empty);
    public static string PatchFilePrefix => GetMetadata("InstallerPatchFilePrefix", string.Empty);
    public static string SteamGameFolderName => GetMetadata("InstallerSteamGameFolderName", string.Empty);

    private static string GetMetadata(string key, string fallback)
    {
        var assembly = typeof(InstallerBuildConfig).Assembly;
        var value = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))?
            .Value;

        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
