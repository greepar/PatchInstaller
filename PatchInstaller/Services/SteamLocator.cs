using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PatchInstaller.Services;

internal static partial class SteamLocator
{
    private static List<string>? _diagnostics;

    public static string? FindGamePath()
    {
        _diagnostics = [];

        try
        {
            var gameFolderName = InstallerBuildConfig.SteamGameFolderName;
            if (string.IsNullOrWhiteSpace(gameFolderName))
            {
                Log("SteamGameFolderName is empty.");
                return null;
            }

            Log($"Target game: {gameFolderName}");

            if (OperatingSystem.IsWindows()) return FindWindowsGamePath(gameFolderName);
            if (OperatingSystem.IsMacOS()) return FindMacGamePath(gameFolderName);
            if (OperatingSystem.IsLinux()) return FindLinuxGamePath(gameFolderName);

            Log("Unsupported operating system.");
            return null;
        }
        finally
        {
            string.Join(Environment.NewLine, _diagnostics);
            _diagnostics = null;
        }
    }

    // Windows

    [SupportedOSPlatform("windows")]
    private static string? FindWindowsGamePath(string gameFolderName)
    {
        var roots = new[]
        {
            GetRegistryValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath"),
            GetRegistryValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamExe"),
            GetRegistryValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            GetRegistryValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath"),
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam"
        };

        return FindGamePathFromSteamRoots(gameFolderName, roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => root!.EndsWith("steam.exe", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(root)!
                : root));
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

    // macOS

    private static string? FindMacGamePath(string gameFolderName)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home)) return null;

        return FindGamePathFromSteamRoots(gameFolderName,
            [Path.Combine(home, "Library", "Application Support", "Steam")]);
    }

    // Linux

    private static string? FindLinuxGamePath(string gameFolderName)
    {
        var roots = new List<string>();

        const string procRoot = "/proc";
        if (Directory.Exists(procRoot))
        {
            foreach (var procDir in EnumerateDirectories(procRoot))
            {
                var name = Path.GetFileName(procDir);
                if (!int.TryParse(name, out _)) continue;

                var comm = TryReadAllText(Path.Combine(procDir, "comm"));
                if (string.IsNullOrWhiteSpace(comm) ||
                    !comm.Contains("steam", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var linkName in new[] { "exe", "cwd" })
                {
                    var hint = TryResolveLink(Path.Combine(procDir, linkName));
                    if (string.IsNullOrWhiteSpace(hint)) continue;

                    Log($"steam process {linkName}: {hint}");
                    var root = TryGetSteamRootFromHint(hint);
                    if (!string.IsNullOrWhiteSpace(root)) roots.Add(root);
                }
            }
        }

        string? output = null;
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "whereis",
                ArgumentList = { "steam" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is not null)
            {
                output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(1500);
                Log($"whereis steam: {output.Trim()}");
            }
        }
        catch
        {
            Log("whereis steam failed.");
        }

        foreach (var token in (output ?? string.Empty).Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var path = token.TrimEnd(':');
            if (!path.StartsWith('/')) continue;

            Log($"whereis path: {path}");
            var root = TryGetSteamRootFromHint(path);
            if (!string.IsNullOrWhiteSpace(root)) roots.Add(root);

            foreach (var scriptHint in GetSteamScriptHints(path))
            {
                Log($"whereis script hint: {scriptHint}");
                root = TryGetSteamRootFromHint(scriptHint);
                if (!string.IsNullOrWhiteSpace(root)) roots.Add(root);
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            roots.Add(Path.Combine(home, ".local", "share", "Steam"));
            roots.Add(Path.Combine(home, ".steam", "debian-installation"));
            roots.Add(Path.Combine(home, ".steam", "steam"));
            roots.Add(Path.Combine(home, ".steam", "root"));
            roots.Add(Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"));
            roots.Add(Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam"));
        }

        return FindGamePathFromSteamRoots(gameFolderName, roots);
    }

    private static IEnumerable<string> GetSteamScriptHints(string path)
    {
        var content = TryReadAllText(path);
        if (string.IsNullOrWhiteSpace(content)) yield break;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home)) yield break;

        foreach (Match match in SteamRootPathRegex().Matches(content))
        {
            yield return match.Value
                .Replace("$HOME", home, StringComparison.Ordinal)
                .Replace("${HOME}", home, StringComparison.Ordinal)
                .Replace("~", home, StringComparison.Ordinal);
        }
    }

    private static string? TryGetSteamRootFromHint(string hint)
    {
        var path = File.Exists(hint) ? Path.GetDirectoryName(hint) : hint;

        while (!string.IsNullOrWhiteSpace(path))
        {
            if (Directory.Exists(Path.Combine(path, "steamapps")))
            {
                Log($"steam root from hint: {hint} -> {path}");
                return path;
            }

            var parent = Path.GetDirectoryName(path);
            if (string.Equals(parent, path, StringComparison.Ordinal)) break;

            path = parent;
        }

        Log($"no steam root from hint: {hint}");
        return null;
    }

    // Common library parsing

    private static string? FindGamePathFromSteamRoots(string gameFolderName, IEnumerable<string> roots)
    {
        var steamRoots = roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Distinct(GetPathComparer())
            .ToArray();

        Log($"Steam roots: {steamRoots.Length}");
        foreach (var root in steamRoots) Log($"  {root}");

        foreach (var steamRoot in steamRoots)
        {
            Log($"Checking root: {steamRoot}");

            var gamePath = FindGamePathInLibrary(steamRoot, gameFolderName);
            if (!string.IsNullOrWhiteSpace(gamePath))
            {
                Log($"Found game in root: {gamePath}");
                return gamePath;
            }

            var libraryConfigPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryConfigPath))
            {
                Log($"  No library config: {libraryConfigPath}");
                continue;
            }

            Log($"  Reading library config: {libraryConfigPath}");
            var libraryPaths = GetLibraryPaths(libraryConfigPath).ToArray();
            Log($"  Library paths: {libraryPaths.Length}");

            foreach (var libraryPath in libraryPaths)
            {
                Log($"    {libraryPath}");
                gamePath = FindGamePathInLibrary(libraryPath, gameFolderName);
                if (!string.IsNullOrWhiteSpace(gamePath))
                {
                    Log($"Found game in library: {gamePath}");
                    return gamePath;
                }
            }
        }

        Log("Game not found.");
        return null;
    }

    private static string? FindGamePathInLibrary(string libraryRoot, string gameName)
    {
        var steamAppsPath = Path.Combine(libraryRoot, "steamapps");
        var commonPath = Path.Combine(steamAppsPath, "common");
        Log($"  Checking library root: {libraryRoot}");

        foreach (var manifestPath in GetAppManifestPaths(steamAppsPath))
        {
            var manifest = ReadAppManifest(manifestPath);
            if (manifest is null) continue;

            var matchesGame = string.Equals(manifest.Name, gameName, StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(manifest.InstallDir, gameName, StringComparison.OrdinalIgnoreCase);

            Log($"    Manifest {Path.GetFileName(manifestPath)}: name={manifest.Name ?? "<empty>"}, installdir={manifest.InstallDir ?? "<empty>"}, match={matchesGame}");
            if (!matchesGame || string.IsNullOrWhiteSpace(manifest.InstallDir)) continue;

            var candidate = Path.Combine(commonPath, manifest.InstallDir);
            if (Directory.Exists(candidate)) return candidate;
        }

        var directPath = Path.Combine(commonPath, gameName);
        Log($"    Direct path: {directPath}, exists={Directory.Exists(directPath)}");
        return Directory.Exists(directPath) ? directPath : null;
    }

    private static IEnumerable<string> GetLibraryPaths(string libraryConfigPath)
    {
        foreach (var line in GetLibraryConfigLines(libraryConfigPath))
        {
            var match = LibraryPathRegex().Match(line);
            if (match.Success) yield return match.Groups[1].Value.Replace(@"\\", @"\");
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
            if (installDirMatch.Success) installDir = installDirMatch.Groups[1].Value.Replace(@"\\", @"\");
        }

        return string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(installDir)
            ? null
            : new AppManifest(name, installDir);
    }

    // Common helpers

    private static IEnumerable<string> EnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static string? TryResolveLink(string path)
    {
        try
        {
            return File.ResolveLinkTarget(path, true)?.FullName;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadAllText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch
        {
            return null;
        }
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

    private static void Log(string message)
    {
        _diagnostics?.Add(message);
    }

    private sealed record AppManifest(string? Name, string? InstallDir);

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.Compiled)]
    private static partial Regex LibraryPathRegex();

    [GeneratedRegex("\"name\"\\s+\"([^\"]+)\"", RegexOptions.Compiled)]
    private static partial Regex AppManifestNameRegex();

    [GeneratedRegex("\"installdir\"\\s+\"([^\"]+)\"", RegexOptions.Compiled)]
    private static partial Regex AppManifestInstallDirRegex();

    [GeneratedRegex("(?:\\$HOME|\\$\\{HOME\\}|~)?/\\.steam/[^\\s\"']+|(?:\\$HOME|\\$\\{HOME\\}|~)?/\\.local/share/Steam", RegexOptions.Compiled)]
    private static partial Regex SteamRootPathRegex();
}
