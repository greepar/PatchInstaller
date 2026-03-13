using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace PatchInstaller;

internal static class InstallerBuildConfig
{
    private static readonly RuntimeConfig? Runtime = LoadRuntimeConfig();

    public static string ProductName => GetValue(
        [Runtime?.ProductName, GetMetadata("InstallerProductName")],
        typeof(InstallerBuildConfig).Assembly.GetName().Name ?? "PatchInstaller");

    public static string DefaultPatchUrl => GetValue(
        [Runtime?.DefaultPatchUrl, GetMetadata("InstallerDefaultPatchUrl")],
        string.Empty);

    public static string PatchFilePrefix => GetValue(
        [Runtime?.PatchFilePrefix, GetMetadata("InstallerPatchFilePrefix")],
        string.Empty);

    public static string SteamGameFolderName => GetValue(
        [Runtime?.SteamGameFolderName, GetMetadata("InstallerSteamGameFolderName")],
        string.Empty);

    private static string GetValue(IEnumerable<string?> candidates, string fallback)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return fallback;
    }

    private static string? GetMetadata(string key)
    {
        var assembly = typeof(InstallerBuildConfig).Assembly;
        return assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))?
            .Value;
    }

    private static RuntimeConfig? LoadRuntimeConfig()
    {
        foreach (var path in GetRuntimeConfigCandidates())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var stream = File.OpenRead(path);
                using var document = JsonDocument.Parse(stream);
                var root = document.RootElement;

                return new RuntimeConfig(
                    GetString(root, "productName"),
                    GetString(root, "defaultPatchUrl"),
                    GetString(root, "patchFilePrefix"),
                    GetString(root, "steamGameFolderName"));
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetRuntimeConfigCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "PatchInstaller.json");
        yield return Path.Combine(AppContext.BaseDirectory, "installer.json");
    }

    private static string? GetString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private sealed record RuntimeConfig(
        string? ProductName,
        string? DefaultPatchUrl,
        string? PatchFilePrefix,
        string? SteamGameFolderName);
}
