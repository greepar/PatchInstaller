using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
    private static readonly Regex MultipartArchiveRegex = new(@"\.(zip|rar)\.(\d{3})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsArchiveValid(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            return false;
        }

        try
        {
            using var preparedArchive = PrepareArchiveForRead(archivePath);

            switch (preparedArchive.ArchiveExtension)
            {
                case ".7z":
                    using (var archive = SevenZipArchive.OpenArchive(preparedArchive.ArchivePath, ReaderOptions.ForFilePath))
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
                    using (var archive = ZipArchive.OpenArchive(preparedArchive.ArchivePath, ReaderOptions.ForFilePath))
                    {
                        return archive.Entries.Any();
                    }
                case ".rar":
                    using (var archive = RarArchive.OpenArchive(preparedArchive.ArchivePath, ReaderOptions.ForFilePath))
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

        using var preparedArchive = PrepareArchiveForRead(archivePath);

        await (preparedArchive.ArchiveExtension switch
        {
            ".7z" => ExtractSevenZipAsync(preparedArchive.ArchivePath, extractPath, progress),
            ".zip" => ExtractZipAsync(preparedArchive.ArchivePath, extractPath, progress),
            ".rar" => ExtractRarAsync(preparedArchive.ArchivePath, extractPath, progress),
            _ => Task.FromException(new NotSupportedException($"不支持的补丁格式: {preparedArchive.ArchiveExtension}"))
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

    public static bool IsSupportedArchivePath(string? archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            return false;
        }

        return TryGetArchiveExtension(archivePath, out _);
    }

    public static bool IsMultipartArchiveFirstSegment(string? archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            return false;
        }

        var match = MultipartArchiveRegex.Match(archivePath);
        return match.Success && string.Equals(match.Groups[2].Value, "001", StringComparison.Ordinal);
    }

    private static PreparedArchive PrepareArchiveForRead(string archivePath)
    {
        if (!TryGetArchiveExtension(archivePath, out var archiveExtension))
        {
            throw new NotSupportedException($"不支持的补丁格式: {Path.GetExtension(archivePath)}");
        }

        if (!TryGetMultipartSegments(archivePath, out var segments))
        {
            return new PreparedArchive(archivePath, archiveExtension, null);
        }

        if (segments.Length == 1)
        {
            var nextSegmentPath = GetMultipartSegmentPath(archivePath, 2);
            throw new FileNotFoundException($"缺少分片文件: {Path.GetFileName(nextSegmentPath)}", nextSegmentPath);
        }

        var combinedArchivePath = Path.Combine(
            Path.GetTempPath(),
            "PatchInstaller",
            "multipart",
            $"{Path.GetFileNameWithoutExtension(archivePath)}-{Guid.NewGuid():N}{archiveExtension}");

        CombineMultipartSegments(segments, combinedArchivePath);
        return new PreparedArchive(combinedArchivePath, archiveExtension, combinedArchivePath);
    }

    private static bool TryGetArchiveExtension(string archivePath, out string archiveExtension)
    {
        var extension = Path.GetExtension(archivePath);
        if (string.Equals(extension, ".001", StringComparison.OrdinalIgnoreCase))
        {
            var match = MultipartArchiveRegex.Match(archivePath);
            if (match.Success)
            {
                archiveExtension = "." + match.Groups[1].Value.ToLowerInvariant();
                return true;
            }
        }

        archiveExtension = extension.ToLowerInvariant();
        return archiveExtension is ".7z" or ".zip" or ".rar";
    }

    private static bool TryGetMultipartSegments(string archivePath, out string[] segments)
    {
        var match = MultipartArchiveRegex.Match(archivePath);
        if (!match.Success || !string.Equals(match.Groups[2].Value, "001", StringComparison.Ordinal))
        {
            segments = [];
            return false;
        }

        var collectedSegments = new System.Collections.Generic.List<string>();
        for (var index = 1; ; index++)
        {
            var segmentPath = GetMultipartSegmentPath(archivePath, index);
            if (!File.Exists(segmentPath))
            {
                break;
            }

            collectedSegments.Add(segmentPath);
        }

        segments = collectedSegments.ToArray();
        return segments.Length > 0;
    }

    private static string GetMultipartSegmentPath(string firstSegmentPath, int index)
    {
        if (index <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var suffixLength = Path.GetExtension(firstSegmentPath).Length;
        return firstSegmentPath[..^suffixLength] + $".{index:000}";
    }

    private static void CombineMultipartSegments(string[] segments, string combinedArchivePath)
    {
        var outputDirectory = Path.GetDirectoryName(combinedArchivePath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        using var output = new FileStream(combinedArchivePath, FileMode.Create, FileAccess.Write, FileShare.None);
        foreach (var segment in segments)
        {
            using var input = new FileStream(segment, FileMode.Open, FileAccess.Read, FileShare.Read);
            input.CopyTo(output);
        }
    }

    private sealed class PreparedArchive(string archivePath, string archiveExtension, string? temporaryArchivePath) : IDisposable
    {
        public string ArchivePath { get; } = archivePath;
        public string ArchiveExtension { get; } = archiveExtension;
        private string? TemporaryArchivePath { get; } = temporaryArchivePath;

        public void Dispose()
        {
            if (string.IsNullOrWhiteSpace(TemporaryArchivePath))
            {
                return;
            }

            try
            {
                File.Delete(TemporaryArchivePath);
            }
            catch
            {
                // ignored
            }
        }
    }
}
