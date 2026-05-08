using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PatchInstaller.Services;

internal static partial class SteamLocator
{
    public static string? FindGamePath()
    {
        var gameFolderName = InstallerBuildConfig.SteamGameFolderName;
        if (string.IsNullOrWhiteSpace(gameFolderName))
        {
            return null;
        }

        foreach (var steamRoot in GetSteamRoots().Distinct(GetPathComparer()))
        {
            var gamePath = FindGamePathInLibrary(steamRoot, gameFolderName);
            if (!string.IsNullOrWhiteSpace(gamePath))
            {
                return gamePath;
            }

            var libraryConfigPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryConfigPath))
            {
                continue;
            }

            foreach (var libraryPath in GetLibraryPaths(libraryConfigPath))
            {
                gamePath = FindGamePathInLibrary(libraryPath, gameFolderName);
                if (!string.IsNullOrWhiteSpace(gamePath))
                {
                    return gamePath;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> GetSteamRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            return GetWindowsSteamRoots();
        }

        if (OperatingSystem.IsMacOS())
        {
            return GetMacSteamRoots();
        }

        if (OperatingSystem.IsLinux())
        {
            return GetLinuxSteamRoots();
        }

        return [];
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> GetWindowsSteamRoots()
    {
        var results = new[]
        {
            GetRegistryValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath"),
            GetRegistryValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamExe"),
            GetRegistryValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            GetRegistryValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath"),
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam"
        };

        foreach (var result in results)
        {
            if (string.IsNullOrWhiteSpace(result))
            {
                continue;
            }

            if (result.EndsWith("steam.exe", StringComparison.OrdinalIgnoreCase))
            {
                yield return Path.GetDirectoryName(result)!;
                continue;
            }

            yield return result;
        }
    }

    private static IEnumerable<string> GetMacSteamRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            yield break;
        }

        yield return Path.Combine(home, "Library", "Application Support", "Steam");
    }

    private static IEnumerable<string> GetLinuxSteamRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            yield break;
        }

        yield return Path.Combine(home, ".local", "share", "Steam");
        yield return Path.Combine(home, ".steam", "steam");
        yield return Path.Combine(home, ".steam", "root");
        yield return Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
        yield return Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam");
    }

    [SupportedOSPlatform("windows")]
    private static string? GetRegistryValue(string keyName, string valueName)
    {
        try
        {
            return Registry.GetValue(keyName, valueName, null) as string;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindGamePathInLibrary(string libraryRoot, string gameName)
    {
        var steamAppsPath = Path.Combine(libraryRoot, "steamapps");
        var commonPath = Path.Combine(steamAppsPath, "common");

        foreach (var manifestPath in GetAppManifestPaths(steamAppsPath))
        {
            var manifest = ReadAppManifest(manifestPath);
            if (manifest is null)
            {
                continue;
            }

            var matchesGame = string.Equals(manifest.Name, gameName, StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(manifest.InstallDir, gameName, StringComparison.OrdinalIgnoreCase);
            if (!matchesGame || string.IsNullOrWhiteSpace(manifest.InstallDir))
            {
                continue;
            }

            var candidate = Path.Combine(commonPath, manifest.InstallDir);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        var directPath = Path.Combine(commonPath, gameName);
        return Directory.Exists(directPath) ? directPath : null;
    }

    private static IEnumerable<string> GetLibraryPaths(string libraryConfigPath)
    {
        foreach (var line in GetLibraryConfigLines(libraryConfigPath))
        {
            var match = LibraryPathRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            yield return match.Groups[1].Value.Replace(@"\\", @"\");
        }
    }

    private static IEnumerable<string> GetAppManifestPaths(string steamAppsPath)
    {
        try
        {
            return Directory.Exists(steamAppsPath)
                ? Directory.EnumerateFiles(steamAppsPath, "appmanifest_*.acf", SearchOption.TopDirectoryOnly).ToArray()
                : [];
        }
        catch
        {
            return [];
        }
    }

    private static AppManifest? ReadAppManifest(string manifestPath)
    {
        string? name = null;
        string? installDir = null;

        foreach (var line in GetLibraryConfigLines(manifestPath))
        {
            var nameMatch = AppManifestNameRegex().Match(line);
            if (nameMatch.Success)
            {
                name = nameMatch.Groups[1].Value;
                continue;
            }

            var installDirMatch = AppManifestInstallDirRegex().Match(line);
            if (installDirMatch.Success)
            {
                installDir = installDirMatch.Groups[1].Value.Replace(@"\\", @"\");
            }
        }

        return string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(installDir)
            ? null
            : new AppManifest(name, installDir);
    }

    private static string[] GetLibraryConfigLines(string libraryConfigPath)
    {
        try
        {
            return File.ReadAllLines(libraryConfigPath);
        }
        catch
        {
            return [];
        }
    }

    private static StringComparer GetPathComparer()
    {
        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    }

    private sealed record AppManifest(string? Name, string? InstallDir);

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.Compiled)]
    private static partial Regex LibraryPathRegex();

    [GeneratedRegex("\"name\"\\s+\"([^\"]+)\"", RegexOptions.Compiled)]
    private static partial Regex AppManifestNameRegex();

    [GeneratedRegex("\"installdir\"\\s+\"([^\"]+)\"", RegexOptions.Compiled)]
    private static partial Regex AppManifestInstallDirRegex();
}
