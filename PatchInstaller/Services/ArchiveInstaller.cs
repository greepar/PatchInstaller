using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace PatchInstaller.Services;

internal static class ArchiveInstaller
{
    public static bool IsArchiveValid(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            return false;
        }

        try
        {
            var extension = Path.GetExtension(archivePath).ToLowerInvariant();
            switch (extension)
            {
                case ".7z":
                    using (var archive = SevenZipArchive.OpenArchive(archivePath, ReaderOptions.ForFilePath))
                    using (var reader = archive.ExtractAllEntries())
                    {
                        while (reader.MoveToNextEntry())
                        {
                            if (!reader.Entry.IsDirectory)
                            {
                                return true;
                            }
                        }
                    }
                    return true;
                case ".zip":
                    using (var archive = ZipArchive.OpenArchive(archivePath, ReaderOptions.ForFilePath))
                    {
                        return archive.Entries.Any();
                    }
                case ".rar":
                    using (var archive = RarArchive.OpenArchive(archivePath, ReaderOptions.ForFilePath))
                    {
                        return archive.Entries.Any();
                    }
                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    public static async Task ExtractAsync(
        string archivePath,
        string extractPath,
        Action<int, int, string>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
            throw new ArgumentException("archivePath 不能为空", nameof(archivePath));

        if (!File.Exists(archivePath))
            throw new FileNotFoundException("找不到压缩包文件", archivePath);

        Directory.CreateDirectory(extractPath);

        var extension = Path.GetExtension(archivePath).ToLowerInvariant();
        await (extension switch
        {
            ".7z" => ExtractSevenZipAsync(archivePath, extractPath, progress),
            ".zip" => ExtractZipAsync(archivePath, extractPath, progress),
            ".rar" => ExtractRarAsync(archivePath, extractPath, progress),
            _ => Task.FromException(new NotSupportedException($"不支持的补丁格式: {extension}"))
        });
    }

    private static async Task ExtractSevenZipAsync(string archivePath, string extractPath, Action<int, int, string>? progress)
    {
        await Task.Run(() =>
        {
            using var archive = SevenZipArchive.OpenArchive(archivePath, ReaderOptions.ForFilePath);
            using var reader = archive.ExtractAllEntries();

            var completedEntries = 0;
            progress?.Invoke(0, 0, string.Empty);

            while (reader.MoveToNextEntry())
            {
                if (reader.Entry.IsDirectory)
                {
                    continue;
                }

                reader.WriteEntryToDirectory(extractPath, new ExtractionOptions
                {
                    ExtractFullPath = true,
                    Overwrite = true
                });

                completedEntries++;
                progress?.Invoke(completedEntries, 0, reader.Entry.Key ?? string.Empty);
            }
        });
    }

    private static async Task ExtractZipAsync(string archivePath, string extractPath, Action<int, int, string>? progress)
    {
        await Task.Run(() =>
        {
            using var archive = ZipArchive.OpenArchive(archivePath, ReaderOptions.ForFilePath);
            ExtractEntries(archive.Entries, extractPath, progress);
        });
    }

    private static async Task ExtractRarAsync(string archivePath, string extractPath, Action<int, int, string>? progress)
    {
        await Task.Run(() =>
        {
            using var archive = RarArchive.OpenArchive(archivePath, ReaderOptions.ForFilePath);
            ExtractEntries(archive.Entries, extractPath, progress);
        });
    }

    private static void ExtractEntries(System.Collections.Generic.IEnumerable<SharpCompress.Archives.IArchiveEntry> entries, string extractPath, Action<int, int, string>? progress)
    {
        var files = entries
            .Where(entry => !entry.IsDirectory)
            .ToArray();
        var totalEntries = files.Length;
        var completedEntries = 0;

        progress?.Invoke(0, totalEntries, string.Empty);

        foreach (var entry in files)
        {
            entry.WriteToDirectory(extractPath, new ExtractionOptions
            {
                ExtractFullPath = true,
                Overwrite = true
            });

            completedEntries++;
            progress?.Invoke(completedEntries, totalEntries, entry.Key ?? string.Empty);
        }
    }

    public static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var directoryPath in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var destinationDirectory = directoryPath.Replace(sourceDirectory, targetDirectory, StringComparison.OrdinalIgnoreCase);
            Directory.CreateDirectory(destinationDirectory);
        }

        foreach (var filePath in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var destinationFile = filePath.Replace(sourceDirectory, targetDirectory, StringComparison.OrdinalIgnoreCase);
            var destinationFolder = Path.GetDirectoryName(destinationFile);
            if (!string.IsNullOrWhiteSpace(destinationFolder))
            {
                Directory.CreateDirectory(destinationFolder);
            }

            File.Copy(filePath, destinationFile, true);
        }
    }
}
