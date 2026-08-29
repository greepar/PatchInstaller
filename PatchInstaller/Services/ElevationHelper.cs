using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PatchInstaller.Services;

internal static class ElevationHelper
{
    private const string ElevatedCopyFlag = "--elevated-copy";

    public static async Task<bool> CopyWithElevationFallbackAsync(
        string sourceDirectory,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(() => ArchiveInstaller.CopyDirectory(sourceDirectory, targetDirectory, cancellationToken), cancellationToken);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await RunElevatedCopyAsync(sourceDirectory, targetDirectory);
        }
        catch (IOException ex) when (IsAccessDenied(ex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await RunElevatedCopyAsync(sourceDirectory, targetDirectory);
        }
    }

    public static bool TryHandleElevatedCopy(string[] args)
    {
        if (!args.Contains(ElevatedCopyFlag, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var sourceDirectory = GetArgumentValue(args, "--source");
        var targetDirectory = GetArgumentValue(args, "--target");

        if (string.IsNullOrWhiteSpace(sourceDirectory) || string.IsNullOrWhiteSpace(targetDirectory))
        {
            throw new InvalidOperationException("Missing elevated copy arguments.");
        }

        ArchiveInstaller.CopyDirectory(sourceDirectory, targetDirectory);
        return true;
    }

    private static async Task<bool> RunElevatedCopyAsync(string sourceDirectory, string targetDirectory)
    {
        var executablePath = Environment.ProcessPath ??
                             throw new InvalidOperationException("Unable to locate the current executable.");

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Verb = "runas",
            UseShellExecute = true,
            Arguments = $"{ElevatedCopyFlag} --source \"{sourceDirectory}\" --target \"{targetDirectory}\""
        };

        try
        {
            using var process = Process.Start(startInfo) ??
                                throw new InvalidOperationException("Unable to start elevated copy process.");
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false;
        }
    }

    private static bool IsAccessDenied(IOException exception) =>
        exception.HResult == unchecked((int)0x80070005);

    private static string? GetArgumentValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
