using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PatchInstaller.Services;

[SupportedOSPlatform("windows")]
internal static partial class SteamLocator
{
    public static string? FindGamePath()
    {
        if (string.IsNullOrWhiteSpace(InstallerBuildConfig.SteamGameFolderName))
        {
            return null;
        }

        foreach (var steamRoot in GetSteamRoots().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var directPath = Path.Combine(steamRoot, "steamapps", "common", InstallerBuildConfig.SteamGameFolderName);
            if (Directory.Exists(directPath))
            {
                return directPath;
            }

            var libraryConfigPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryConfigPath))
            {
                continue;
            }

            foreach (var libraryPath in GetLibraryPaths(libraryConfigPath))
            {
                var candidate = Path.Combine(libraryPath, "steamapps", "common", InstallerBuildConfig.SteamGameFolderName);
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> GetSteamRoots()
    {
        var results = new[]
        {
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string,
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamExe", null) as string,
            Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string,
            Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null) as string,
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

    private static IEnumerable<string> GetLibraryPaths(string libraryConfigPath)
    {
        foreach (var line in File.ReadLines(libraryConfigPath))
        {
            var match = LibraryPathRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            yield return match.Groups[1].Value.Replace(@"\\", @"\");
        }
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.Compiled)]
    private static partial Regex LibraryPathRegex();
}
