using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace PatchInstaller.Services;

internal static partial class ArchiveInstaller
{
    private static readonly Regex MultipartArchiveRegex = MyRegex();
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly ReaderOptions ZipReaderOptions = CreateZipReaderOptions();

    public static bool IsArchiveValid(string archivePath, string? temporaryDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            return false;
        }

        try
        {
            using var preparedArchive = PrepareArchiveForRead(archivePath, temporaryDirectory);

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
                    using (var archive = ZipArchive.OpenArchive(preparedArchive.ArchivePath, ZipReaderOptions))
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

    public static async Task ValidateAsync(
        string archivePath,
        CancellationToken cancellationToken,
        string? temporaryDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
            throw new ArgumentException("archivePath 不能为空", nameof(archivePath));

        if (!File.Exists(archivePath))
            throw new FileNotFoundException("找不到压缩包文件", archivePath);

        using var preparedArchive = PrepareArchiveForRead(archivePath, temporaryDirectory);
        await (preparedArchive.ArchiveExtension switch
        {
            ".7z" => ValidateSevenZipAsync(preparedArchive.ArchivePath, cancellationToken),
            ".zip" => ValidateEntriesAsync(ZipArchive.OpenArchive(preparedArchive.ArchivePath, ZipReaderOptions), cancellationToken),
            ".rar" => ValidateEntriesAsync(RarArchive.OpenArchive(preparedArchive.ArchivePath, ReaderOptions.ForFilePath), cancellationToken),
            _ => Task.FromException(new NotSupportedException($"不支持的补丁格式: {preparedArchive.ArchiveExtension}"))
        });
    }

    public static async Task ExtractAsync(
        string archivePath,
        string extractPath,
        Action<int, int, string>? progress = null,
        CancellationToken cancellationToken = default,
        string? temporaryDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
            throw new ArgumentException("archivePath 不能为空", nameof(archivePath));

        if (!File.Exists(archivePath))
            throw new FileNotFoundException("找不到压缩包文件", archivePath);

        Directory.CreateDirectory(extractPath);

        using var preparedArchive = PrepareArchiveForRead(archivePath, temporaryDirectory);

        await (preparedArchive.ArchiveExtension switch
        {
            ".7z" => ExtractSevenZipAsync(preparedArchive.ArchivePath, extractPath, progress, cancellationToken),
            ".zip" => ExtractZipAsync(preparedArchive.ArchivePath, extractPath, progress, cancellationToken),
            ".rar" => ExtractRarAsync(preparedArchive.ArchivePath, extractPath, progress, cancellationToken),
            _ => Task.FromException(new NotSupportedException($"不支持的补丁格式: {preparedArchive.ArchiveExtension}"))
        });
    }

    private static async Task ValidateSevenZipAsync(string archivePath, CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            using var archive = SevenZipArchive.OpenArchive(archivePath, ReaderOptions.ForFilePath);
            using var reader = archive.ExtractAllEntries();
            var hasFiles = false;
            while (reader.MoveToNextEntry())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!reader.Entry.IsDirectory)
                {
                    reader.WriteEntryTo(Stream.Null);
                    hasFiles = true;
                }
            }

            if (!hasFiles)
                throw new InvalidDataException("压缩包不包含任何文件。");
        }, cancellationToken);
    }

    private static async Task ValidateEntriesAsync(IArchive archive, CancellationToken cancellationToken)
    {
        using (archive)
        {
            await Task.Run(() =>
            {
                var hasFiles = false;
                foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    entry.WriteTo(Stream.Null);
                    hasFiles = true;
                }

                if (!hasFiles)
                    throw new InvalidDataException("压缩包不包含任何文件。");
            }, cancellationToken);
        }
    }

    private static async Task ExtractSevenZipAsync(string archivePath, string extractPath, Action<int, int, string>? progress, CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            using var archive = SevenZipArchive.OpenArchive(archivePath, ReaderOptions.ForFilePath);
            using var reader = archive.ExtractAllEntries();

            var completedEntries = 0;
            progress?.Invoke(0, 0, string.Empty);

            while (reader.MoveToNextEntry())
            {
                cancellationToken.ThrowIfCancellationRequested();
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
        }, cancellationToken);
    }

    private static async Task ExtractZipAsync(string archivePath, string extractPath, Action<int, int, string>? progress, CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            using var archive = ZipArchive.OpenArchive(archivePath, ZipReaderOptions);
            ExtractEntries(archive.Entries, extractPath, progress, cancellationToken);
        }, cancellationToken);
    }

    private static async Task ExtractRarAsync(string archivePath, string extractPath, Action<int, int, string>? progress, CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            using var archive = RarArchive.OpenArchive(archivePath, ReaderOptions.ForFilePath);
            ExtractEntries(archive.Entries, extractPath, progress, cancellationToken);
        }, cancellationToken);
    }

    private static void ExtractEntries(System.Collections.Generic.IEnumerable<SharpCompress.Archives.IArchiveEntry> entries, string extractPath, Action<int, int, string>? progress, CancellationToken cancellationToken)
    {
        var files = entries
            .Where(entry => !entry.IsDirectory)
            .ToArray();
        var totalEntries = files.Length;
        var completedEntries = 0;

        progress?.Invoke(0, totalEntries, string.Empty);

        foreach (var entry in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entry.WriteToDirectory(extractPath, new ExtractionOptions
            {
                ExtractFullPath = true,
                Overwrite = true
            });

            completedEntries++;
            progress?.Invoke(completedEntries, totalEntries, entry.Key ?? string.Empty);
        }
    }

    private static ReaderOptions CreateZipReaderOptions()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return ReaderOptions.ForFilePath with
        {
            ArchiveEncoding = new ArchiveEncoding
            {
                CustomDecoder = (bytes, index, count, type) => type == EncodingType.UTF8
                    ? Encoding.UTF8.GetString(bytes, index, count)
                    : DecodeLegacyZipName(bytes, index, count)
            }
        };
    }

    private static string DecodeLegacyZipName(byte[] bytes, int index, int count)
    {
        try
        {
            return StrictUtf8.GetString(bytes, index, count);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(936).GetString(bytes, index, count);
        }
    }

    public static void CopyDirectory(string sourceDirectory, string targetDirectory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var directoryPath in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destinationDirectory = directoryPath.Replace(sourceDirectory, targetDirectory, StringComparison.OrdinalIgnoreCase);
            Directory.CreateDirectory(destinationDirectory);
        }

        foreach (var filePath in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
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

    private static PreparedArchive PrepareArchiveForRead(string archivePath, string? temporaryDirectory = null)
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

        var combineDirectory = string.IsNullOrWhiteSpace(temporaryDirectory)
            ? Path.GetDirectoryName(archivePath) ?? Path.GetTempPath()
            : temporaryDirectory;

        var combinedArchivePath = Path.Combine(
            combineDirectory,
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
        if (archiveExtension is ".7z" or ".zip" or ".rar")
        {
            return true;
        }

        if (TryGetArchiveExtensionFromSignature(archivePath, out archiveExtension))
        {
            return true;
        }

        return false;
    }

    private static bool TryGetArchiveExtensionFromSignature(string archivePath, out string archiveExtension)
    {
        archiveExtension = string.Empty;

        try
        {
            using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> header = stackalloc byte[8];
            var bytesRead = stream.Read(header);

            if (bytesRead >= 6 &&
                header[..6].SequenceEqual(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }))
            {
                archiveExtension = ".7z";
                return true;
            }

            if (bytesRead >= 8 &&
                header[..8].SequenceEqual(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00, 0x00 }) ||
                bytesRead >= 8 &&
                header[..8].SequenceEqual(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 }))
            {
                archiveExtension = ".rar";
                return true;
            }

            if (bytesRead >= 4 &&
                header[..4].SequenceEqual(new byte[] { 0x50, 0x4B, 0x03, 0x04 }) ||
                bytesRead >= 4 &&
                header[..4].SequenceEqual(new byte[] { 0x50, 0x4B, 0x05, 0x06 }) ||
                bytesRead >= 4 &&
                header[..4].SequenceEqual(new byte[] { 0x50, 0x4B, 0x07, 0x08 }))
            {
                archiveExtension = ".zip";
                return true;
            }
        }
        catch
        {
            // ignored
        }

        return false;
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

    [GeneratedRegex(@"\.(zip|rar)\.(\d{3})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MyRegex();
}
