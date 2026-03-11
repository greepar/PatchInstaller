using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Avalonia_NativeAOT_SingleFile;

internal sealed record DownloadProgressInfo(
    long DownloadedBytes,
    long? TotalBytes,
    double ProgressPercent,
    double BytesPerSecond);

internal sealed record DownloadMetadata(
    string Url,
    long? TotalBytes,
    string? ETag,
    DateTimeOffset? LastModified,
    bool SupportsRanges,
    int Parts,
    string CompletedParts,
    bool IsComplete);

internal static class PatchDownloader
{
    private const int DefaultBufferSize = 256 * 1024;
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public static async Task DownloadAsync(
        Uri url,
        string destinationPath,
        int parts,
        int retries,
        Action<DownloadProgressInfo>? progress,
        CancellationToken cancellationToken)
    {
        var metadata = await GetMetadataAsync(url, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var metadataPath = GetMetadataPath(destinationPath);
        DeleteIfExists(destinationPath);
        DeleteIfExists(metadataPath);

        var downloadMetadata = new DownloadMetadata(
            url.ToString(),
            metadata.TotalBytes,
            metadata.ETag,
            metadata.LastModified,
            metadata.SupportsRanges,
            parts,
            string.Empty,
            false);
        SaveMetadata(metadataPath, downloadMetadata);

        try
        {
            if (!metadata.SupportsRanges || metadata.TotalBytes is null or <= 0 || parts <= 1)
            {
                await DownloadSequentialAsync(url, destinationPath, metadata.TotalBytes, progress, cancellationToken);
            }
            else
            {
                await DownloadParallelAsync(
                    url,
                    destinationPath,
                    metadata.TotalBytes.Value,
                    downloadMetadata,
                    metadataPath,
                    parts,
                    retries,
                    progress,
                    cancellationToken);
            }
        }
        catch
        {
            DeleteIfExists(destinationPath);
            DeleteIfExists(metadataPath);
            throw;
        }

        DeleteIfExists(metadataPath);
    }

    public static async Task<string?> GetSuggestedFileNameAsync(Uri url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        return GetFileNameFromContentDisposition(response.Content.Headers.ContentDisposition)
               ?? GetFileNameFromUri(response.RequestMessage?.RequestUri);
    }

    private static async Task<DownloadMetadata> GetMetadataAsync(Uri url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        return new DownloadMetadata(
            url.ToString(),
            response.Content.Headers.ContentLength,
            response.Headers.ETag?.Tag,
            response.Content.Headers.LastModified,
            response.Headers.AcceptRanges.Contains("bytes", StringComparer.OrdinalIgnoreCase),
            0,
            string.Empty,
            false);
    }

    private static string? GetFileNameFromContentDisposition(ContentDispositionHeaderValue? contentDisposition)
    {
        var fileName = contentDisposition?.FileNameStar ?? contentDisposition?.FileName;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        return fileName.Trim().Trim('"');
    }

    private static string? GetFileNameFromUri(Uri? uri)
    {
        if (uri is null)
        {
            return null;
        }

        var fileName = Path.GetFileName(Uri.UnescapeDataString(uri.LocalPath.TrimEnd('/')));
        return string.IsNullOrWhiteSpace(fileName) ? null : fileName;
    }

    private static async Task DownloadSequentialAsync(
        Uri url,
        string destinationPath,
        long? totalBytes,
        Action<DownloadProgressInfo>? progress,
        CancellationToken cancellationToken)
    {
        DeleteIfExists(destinationPath);

        using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, DefaultBufferSize, true);

        var buffer = new byte[DefaultBufferSize];
        long downloadedBytes = 0;
        long lastReportedBytes = 0;
        long speedWindowBytes = 0;
        double lastStableBytesPerSecond = 0;
        var speedWindowStartedAt = TimeSpan.Zero;
        var stopwatch = Stopwatch.StartNew();
        var lastReportedAt = stopwatch.Elapsed;

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read <= 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloadedBytes += read;

            if (ShouldReport(stopwatch.Elapsed, lastReportedAt, downloadedBytes, lastReportedBytes))
            {
                ReportProgress(progress, downloadedBytes, totalBytes, stopwatch.Elapsed, ref lastReportedBytes, ref lastReportedAt, ref speedWindowBytes, ref speedWindowStartedAt, ref lastStableBytesPerSecond);
            }
        }

        ReportProgress(progress, downloadedBytes, totalBytes, stopwatch.Elapsed, ref lastReportedBytes, ref lastReportedAt, ref speedWindowBytes, ref speedWindowStartedAt, ref lastStableBytesPerSecond, true);
    }

    private static async Task DownloadParallelAsync(
        Uri url,
        string destinationPath,
        long totalBytes,
        DownloadMetadata savedMetadata,
        string metadataPath,
        int parts,
        int retries,
        Action<DownloadProgressInfo>? progress,
        CancellationToken cancellationToken)
    {
        using var fileStream = new FileStream(destinationPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        fileStream.SetLength(totalBytes);
        var handle = fileStream.SafeFileHandle;

        var completedParts = ParseCompletedParts(savedMetadata.CompletedParts);
        var allRanges = CreateRanges(totalBytes, parts)
            .Select((range, index) => (range.Start, range.End, Index: index))
            .ToArray();
        var ranges = allRanges
            .Where(range => !completedParts.Contains(range.Index))
            .ToArray();
        var partProgress = new long[allRanges.Length];
        foreach (var completedPart in completedParts)
        {
            if (completedPart >= 0 && completedPart < allRanges.Length)
            {
                var range = allRanges[completedPart];
                partProgress[completedPart] = range.End - range.Start + 1;
            }
        }

        long downloadedBytes = CalculateDownloadedBytes(partProgress);
        long lastReportedBytes = downloadedBytes;
        long speedWindowBytes = 0;
        double lastStableBytesPerSecond = 0;
        var speedWindowStartedAt = TimeSpan.Zero;
        var stopwatch = Stopwatch.StartNew();
        var lastReportedAt = stopwatch.Elapsed;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var reporterTask = Task.Run(async () =>
        {
            try
            {
                while (await timer.WaitForNextTickAsync(linkedCts.Token))
                {
                    downloadedBytes = CalculateDownloadedBytes(partProgress);
                    ReportProgress(progress, Interlocked.Read(ref downloadedBytes), totalBytes, stopwatch.Elapsed, ref lastReportedBytes, ref lastReportedAt, ref speedWindowBytes, ref speedWindowStartedAt, ref lastStableBytesPerSecond);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, linkedCts.Token);

        try
        {
            ReportProgress(progress, downloadedBytes, totalBytes, stopwatch.Elapsed, ref lastReportedBytes, ref lastReportedAt, ref speedWindowBytes, ref speedWindowStartedAt, ref lastStableBytesPerSecond, true);

            var tasks = ranges.Select(range => DownloadRangeAsync(url, handle, range.Start, range.End, retries, bytesRead =>
            {
                partProgress[range.Index] = bytesRead;
            },
            () =>
            {
                lock (completedParts)
                {
                    completedParts.Add(range.Index);
                    partProgress[range.Index] = range.End - range.Start + 1;
                    SaveMetadata(metadataPath, savedMetadata with
                    {
                        CompletedParts = string.Join(",", completedParts.Order()),
                        IsComplete = completedParts.Count >= parts
                    });
                }
            },
            cancellationToken));

            await Task.WhenAll(tasks);
        }
        finally
        {
            linkedCts.Cancel();
            try
            {
                await reporterTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        SaveMetadata(metadataPath, savedMetadata with
        {
            CompletedParts = string.Join(",", Enumerable.Range(0, parts)),
            IsComplete = true
        });
        downloadedBytes = totalBytes;
        ReportProgress(progress, totalBytes, totalBytes, stopwatch.Elapsed, ref lastReportedBytes, ref lastReportedAt, ref speedWindowBytes, ref speedWindowStartedAt, ref lastStableBytesPerSecond, true);
    }

    private static async Task DownloadRangeAsync(
        Uri url,
        SafeFileHandle handle,
        long start,
        long end,
        int retries,
        Action<long> onProgress,
        Action onCompleted,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                onProgress(0);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new RangeHeaderValue(start, end);
                using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var buffer = new byte[DefaultBufferSize];
                long offset = start;
                long currentBytes = 0;

                while (true)
                {
                    var read = await stream.ReadAsync(buffer, cancellationToken);
                    if (read <= 0)
                    {
                        break;
                    }

                    await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), offset, cancellationToken);
                    offset += read;
                    currentBytes += read;
                    onProgress(currentBytes);
                }

                onCompleted();
                return;
            }
            catch when (++attempt < retries)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), cancellationToken);
            }
        }
    }

    private static IReadOnlyList<(long Start, long End)> CreateRanges(long totalBytes, int parts)
    {
        var chunkSize = (long)Math.Ceiling(totalBytes / (double)parts);
        var ranges = new List<(long Start, long End)>(parts);

        for (var index = 0; index < parts; index++)
        {
            var start = index * chunkSize;
            if (start >= totalBytes)
            {
                break;
            }

            var end = Math.Min(start + chunkSize - 1, totalBytes - 1);
            ranges.Add((start, end));
        }

        return ranges;
    }

    private static string GetMetadataPath(string destinationPath) => destinationPath + ".meta";

    private static void SaveMetadata(string metadataPath, DownloadMetadata metadata)
    {
        File.WriteAllLines(metadataPath,
        [
            metadata.Url,
            metadata.TotalBytes?.ToString() ?? string.Empty,
            metadata.ETag ?? string.Empty,
            metadata.LastModified?.ToString("O") ?? string.Empty,
            metadata.SupportsRanges ? "1" : "0",
            metadata.Parts.ToString(),
            metadata.CompletedParts,
            metadata.IsComplete ? "1" : "0"
        ]);
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static HashSet<int> ParseCompletedParts(string completedParts)
    {
        return completedParts
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out var index) ? index : -1)
            .Where(index => index >= 0)
            .ToHashSet();
    }

    private static long CalculateDownloadedBytes(long[] partProgress)
    {
        long total = 0;
        foreach (var progress in partProgress)
        {
            total += progress;
        }

        return total;
    }

    private static bool ShouldReport(TimeSpan elapsed, TimeSpan lastReportedAt, long downloadedBytes, long lastReportedBytes) =>
        elapsed - lastReportedAt >= TimeSpan.FromMilliseconds(200) ||
        downloadedBytes - lastReportedBytes >= 512 * 1024;

    private static void ReportProgress(
        Action<DownloadProgressInfo>? progress,
        long downloadedBytes,
        long? totalBytes,
        TimeSpan elapsed,
        ref long lastReportedBytes,
        ref TimeSpan lastReportedAt,
        ref long speedWindowBytes,
        ref TimeSpan speedWindowStartedAt,
        ref double lastStableBytesPerSecond,
        bool force = false)
    {
        if (!force && progress is null)
        {
            return;
        }

        var percent = totalBytes is > 0
            ? Math.Clamp(downloadedBytes * 100d / totalBytes.Value, 0d, 100d)
            : 0d;

        var deltaBytes = downloadedBytes - lastReportedBytes;
        if (speedWindowStartedAt == TimeSpan.Zero)
        {
            speedWindowStartedAt = elapsed;
        }

        speedWindowBytes += deltaBytes;
        var windowSeconds = (elapsed - speedWindowStartedAt).TotalSeconds;
        var speed = lastStableBytesPerSecond;
        if (windowSeconds >= 1d)
        {
            speed = speedWindowBytes / windowSeconds;
            lastStableBytesPerSecond = speed;
            speedWindowBytes = 0;
            speedWindowStartedAt = elapsed;
        }

        progress?.Invoke(new DownloadProgressInfo(downloadedBytes, totalBytes, percent, speed));

        lastReportedBytes = downloadedBytes;
        lastReportedAt = elapsed;
    }
}
