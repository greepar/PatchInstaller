using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
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

internal sealed class PatchDownloadException(string message, Exception? innerException = null)
    : Exception(message, innerException);

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
        try
        {
            var metadata = await GetMetadataAsync(url, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            DeleteIfExists(destinationPath);
            DeleteIfExists($"{destinationPath}.download");

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

            await using var downloader = new DownloadService(configuration);

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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            DeleteIfExists(destinationPath);
            DeleteIfExists($"{destinationPath}.download");
            throw new PatchDownloadException("下载超时或连接被中断，请检查网络后重试。");
        }
        catch (Exception ex) when (TryTranslateDownloadException(ex, out var message))
        {
            DeleteIfExists(destinationPath);
            DeleteIfExists($"{destinationPath}.download");
            if (message != null) throw new PatchDownloadException(message, ex);
        }
        catch
        {
            DeleteIfExists(destinationPath);
            DeleteIfExists($"{destinationPath}.download");
            throw;
        }
    }

    private static bool TryTranslateDownloadException(Exception ex, out string? message)
    {
        if (ex is PatchDownloadException patchDownloadException)
        {
            message = patchDownloadException.Message;
            return true;
        }

        if (TryFindException<HttpRequestException>(ex, out var httpRequestException))
        {
            if (httpRequestException is { StatusCode: { } statusCode })
            {
                message = $"下载失败，服务器返回 HTTP {(int)statusCode} {statusCode}。";
                return true;
            }

            if (httpRequestException != null && TryFindException<SocketException>(httpRequestException, out var socketException))
            {
                if (socketException != null)
                    message = socketException.SocketErrorCode switch
                    {
                        SocketError.HostNotFound or SocketError.NoData => "下载失败，无法解析服务器地址，请检查网络或下载地址。",
                        SocketError.ConnectionRefused => "下载失败，服务器拒绝连接，请稍后重试。",
                        SocketError.TimedOut => "下载失败，连接服务器超时，请检查网络后重试。",
                        SocketError.NetworkDown or SocketError.NetworkUnreachable => "下载失败，当前网络不可用，请检查网络连接。",
                        _ => "下载失败，网络连接异常，请检查网络后重试。"
                    };
                message = null;
                return true;
            }

            message = "下载失败，无法连接到服务器，请检查网络后重试。";
            return true;
        }

        if (TryFindException<SocketException>(ex, out var directSocketException))
        {
            if (directSocketException != null)
                message = directSocketException.SocketErrorCode switch
                {
                    SocketError.HostNotFound or SocketError.NoData => "下载失败，无法解析服务器地址，请检查网络或下载地址。",
                    SocketError.ConnectionRefused => "下载失败，服务器拒绝连接，请稍后重试。",
                    SocketError.TimedOut => "下载失败，连接服务器超时，请检查网络后重试。",
                    SocketError.NetworkDown or SocketError.NetworkUnreachable => "下载失败，当前网络不可用，请检查网络连接。",
                    _ => "下载失败，网络连接异常，请检查网络后重试。"
                };
            message = null;
            return true;
        }

        message = string.Empty;
        return false;
    }

    private static bool TryFindException<TException>(Exception exception, out TException? matched)
        where TException : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException found)
            {
                matched = found;
                return true;
            }
        }

        matched = null;
        return false;
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
        return string.IsNullOrWhiteSpace(fileName) ? null : fileName.Trim().Trim('"');
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
            //
        }
    }
}
