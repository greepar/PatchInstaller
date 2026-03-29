using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Downloader;

namespace PatchInstaller.Services;

internal sealed record DownloadProgressInfo(
    long DownloadedBytes,
    long? TotalBytes,
    double ProgressPercent,
    double BytesPerSecond);

internal sealed record DownloadMetadata(
    string Url,
    long? TotalBytes,
    bool SupportsRanges,
    string? SuggestedFileName);

internal static class PatchDownloader
{
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
        DeleteIfExists(destinationPath);
        DeleteIfExists($"{destinationPath}.download");

        try
        {
            var preferParallelDownload = metadata.TotalBytes is > 0 && parts > 1;
            var configuration = new DownloadConfiguration
            {
                ChunkCount = preferParallelDownload ? parts : 1,
                ParallelCount = preferParallelDownload ? parts : 1,
                ParallelDownload = preferParallelDownload,
                MaxTryAgainOnFailure = retries,
                BufferBlockSize = 10240,
                MinimumSizeOfChunking = 1024 * 1024,
                MinimumChunkSize = 1024 * 1024,
                CheckDiskSizeBeforeDownload = false,
                ClearPackageOnCompletionWithFailure = true,
                EnableAutoResumeDownload = false,
                DownloadFileExtension = ".download",
                FileExistPolicy = FileExistPolicy.Delete,
                RequestConfiguration =
                {
                    Accept = "*/*",
                    KeepAlive = true,
                    ProtocolVersion = HttpVersion.Version11,
                    UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0 Safari/537.36"
                }
            };

            using var downloader = new DownloadService(configuration);

            downloader.DownloadProgressChanged += (_, e) =>
            {
                if (progress is null)
                {
                    return;
                }

                var bytesPerSecond = e.BytesPerSecondSpeed > 0d
                    ? e.BytesPerSecondSpeed
                    : e.AverageBytesPerSecondSpeed;

                progress(new DownloadProgressInfo(
                    e.ReceivedBytesSize,
                    e.TotalBytesToReceive > 0 ? e.TotalBytesToReceive : metadata.TotalBytes,
                    e.ProgressPercentage,
                    bytesPerSecond));
            };

            await downloader.DownloadFileTaskAsync(url.ToString(), destinationPath, cancellationToken);
        }
        catch
        {
            DeleteIfExists(destinationPath);
            DeleteIfExists($"{destinationPath}.download");
            throw;
        }
    }

    public static async Task<string?> GetSuggestedFileNameAsync(Uri url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return GetFileNameFromUri(url);
            }

            return GetFileNameFromContentDisposition(response.Content.Headers.ContentDisposition)
                   ?? GetFileNameFromUri(response.RequestMessage?.RequestUri)
                   ?? GetFileNameFromUri(url);
        }
        catch
        {
            return GetFileNameFromUri(url);
        }
    }

    private static async Task<DownloadMetadata> GetMetadataAsync(Uri url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return new DownloadMetadata(
                url.ToString(),
                null,
                false,
                GetFileNameFromUri(url));
        }

        return new DownloadMetadata(
            url.ToString(),
            response.Content.Headers.ContentLength,
            response.Headers.AcceptRanges.Any(value => string.Equals(value, "bytes", StringComparison.OrdinalIgnoreCase)),
            GetFileNameFromContentDisposition(response.Content.Headers.ContentDisposition)
            ?? GetFileNameFromUri(response.RequestMessage?.RequestUri)
            ?? GetFileNameFromUri(url));
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
}
