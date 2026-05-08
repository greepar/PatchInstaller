using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PatchInstaller;

internal static class InstallerBuildConfig
{
    private static readonly RuntimeConfig? Runtime = LoadRuntimeConfig();
    private static readonly Assembly Assembly = typeof(InstallerBuildConfig).Assembly;

    public static string ProductName => GetValue(
        [Runtime?.ProductName, GetMetadata("InstallerName")],
        Assembly.GetName().Name ?? "PatchInstaller");

    public static string DisplayVersion => GetInformationalVersion()
        ?? Assembly.GetName().Version?.ToString(3)
        ?? "1.0.0";

    public static string UserAgent => $"PatchInstaller v{DisplayVersion} ({GetSystemVersion()})";

    public static string DefaultPatchUrl => GetValue(
        [Runtime?.DefaultPatchUrl, GetMetadata("DefaultPatchUrl")],
        string.Empty);

    public static string PatchFilePrefix => GetValue(
        [Runtime?.PatchFilePrefix, GetMetadata("PatchFilePrefix")],
        string.Empty);

    public static string SteamGameFolderName => GetValue(
        [Runtime?.SteamGameFolderName, GetMetadata("SteamGameFolderName")],
        "Mainichikisushite");

    public static string CheckUpdateApi => GetValue(
        [Runtime?.CheckUpdateApi, GetMetadata("CheckUpdateApi")],
        string.Empty);

    public static bool HasCheckUpdateApi => !string.IsNullOrWhiteSpace(CheckUpdateApi);

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
        return Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))?
            .Value;
    }

    private static string? GetInformationalVersion()
    {
        var informationalVersion = Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion?
            .Trim();

        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion;
        }

        var suffixSeparatorIndex = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        if (suffixSeparatorIndex < 0)
        {
            return informationalVersion;
        }

        var version = informationalVersion[..suffixSeparatorIndex];
        var revision = informationalVersion[(suffixSeparatorIndex + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(revision))
        {
            return version;
        }

        var shortRevision = revision.Length > 7
            ? revision[..7]
            : revision;

        return $"{version}+{shortRevision}";
    }

    private static string GetSystemVersion()
    {
        var description = RuntimeInformation.OSDescription.Trim();
        if (!string.IsNullOrWhiteSpace(description))
        {
            return description;
        }

        return Environment.OSVersion.VersionString;
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
                    GetString(root, "steamGameFolderName"),
                    GetString(root, "checkUpdateApi"));
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
        string? SteamGameFolderName,
        string? CheckUpdateApi);
}
