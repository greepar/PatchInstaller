using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.ComponentModel;
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

internal sealed record SourceProbeResult(
    Uri EffectiveUri,
    long SampleBytes,
    double BytesPerSecond);

internal sealed class PatchDownloadException(string message, Exception? innerException = null)
    : Exception(message, innerException);

internal static class PatchDownloader
{
    private const int ProbeSampleBytes = 20 * 1024 * 1024;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);
    private static readonly HttpClient HttpClient = HttpClientService.Create(Timeout.InfiniteTimeSpan);

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
            AsyncCompletedEventArgs? completedArgs = null;

            var preferParallelDownload = metadata is { SupportsRanges: true, TotalBytes: > 0 } && parts > 1;
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
                    UserAgent = InstallerBuildConfig.UserAgent
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

            downloader.DownloadFileCompleted += (_, e) => completedArgs = e;

            await downloader.DownloadFileTaskAsync(url.ToString(), destinationPath, cancellationToken);
            if (completedArgs?.Error is Exception completedError)
            {
                throw TranslateOrWrapDownloadException(completedError);
            }

            if (completedArgs?.Cancelled == true || downloader.Status != DownloadStatus.Completed)
            {
                throw new PatchDownloadException("下载失败，下载任务未完成，请重试。");
            }

            if (!File.Exists(destinationPath))
            {
                throw new PatchDownloadException("下载失败，下载文件未能正确保存到临时目录。请重试。");
            }

            ValidateDownloadedFile(destinationPath, metadata.TotalBytes);
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

    private static PatchDownloadException TranslateOrWrapDownloadException(Exception ex)
    {
        if (TryTranslateDownloadException(ex, out var message) && !string.IsNullOrWhiteSpace(message))
        {
            return new PatchDownloadException(message, ex);
        }

        return new PatchDownloadException("下载失败，下载文件未能正确保存到临时目录。请重试。", ex);
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
                message = socketException is null
                    ? "下载失败，无法连接到服务器，请检查网络后重试。"
                    : GetSocketErrorMessage(socketException.SocketErrorCode);
                return true;
            }

            message = "下载失败，无法连接到服务器，请检查网络后重试。";
            return true;
        }

        if (TryFindException<SocketException>(ex, out var directSocketException))
        {
            message = directSocketException is null
                ? "下载失败，网络连接异常，请检查网络后重试。"
                : GetSocketErrorMessage(directSocketException.SocketErrorCode);
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

    private static string GetSocketErrorMessage(SocketError socketError)
    {
        return socketError switch
        {
            SocketError.HostNotFound or SocketError.NoData => "下载失败，无法解析服务器地址，请检查网络或下载地址。",
            SocketError.ConnectionRefused => "下载失败，服务器拒绝连接，请稍后重试。",
            SocketError.TimedOut => "下载失败，连接服务器超时，请检查网络后重试。",
            SocketError.NetworkDown or SocketError.NetworkUnreachable => "下载失败，当前网络不可用，请检查网络连接。",
            _ => "下载失败，网络连接异常，请检查网络后重试。"
        };
    }

    public static async Task<string?> GetSuggestedFileNameAsync(Uri url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendMetadataRequestAsync(url, cancellationToken);
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

    public static async Task<SourceProbeResult?> ProbeSourceAsync(Uri url, CancellationToken cancellationToken)
    {
        try
        {
            using var probeCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeCancellationTokenSource.CancelAfter(ProbeTimeout);
            var probeCancellationToken = probeCancellationTokenSource.Token;

            using var resolvedResponse = await SendMetadataRequestAsync(url, probeCancellationToken);
            var effectiveUri = resolvedResponse.RequestMessage?.RequestUri ?? url;

            using var probeRequest = new HttpRequestMessage(HttpMethod.Get, effectiveUri);
            probeRequest.Headers.Range = new RangeHeaderValue(0, ProbeSampleBytes - 1);
            var stopwatch = Stopwatch.StartNew();
            using var response =
                await HttpClient.SendAsync(probeRequest, HttpCompletionOption.ResponseHeadersRead, probeCancellationToken);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.PartialContent)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(probeCancellationToken);
            var buffer = new byte[64 * 1024];
            long totalRead = 0;

            while (totalRead < ProbeSampleBytes)
            {
                var remaining = (int)Math.Min(buffer.Length, ProbeSampleBytes - totalRead);
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, remaining), probeCancellationToken);
                if (bytesRead <= 0)
                {
                    break;
                }

                totalRead += bytesRead;
            }

            stopwatch.Stop();
            if (totalRead <= 0 || stopwatch.Elapsed.TotalSeconds <= 0)
            {
                return null;
            }

            return new SourceProbeResult(
                response.RequestMessage?.RequestUri ?? effectiveUri,
                totalRead,
                totalRead / stopwatch.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<DownloadMetadata> GetMetadataAsync(Uri url, CancellationToken cancellationToken)
    {
        using var response = await SendMetadataRequestAsync(url, cancellationToken);
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
            GetTotalBytes(response),
            SupportsRanges(response),
            GetFileNameFromContentDisposition(response.Content.Headers.ContentDisposition)
            ?? GetFileNameFromUri(response.RequestMessage?.RequestUri)
            ?? GetFileNameFromUri(url));
    }

    private static async Task<HttpResponseMessage> SendMetadataRequestAsync(Uri url, CancellationToken cancellationToken)
    {
        try
        {
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
            var headResponse = await HttpClient.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (headResponse.IsSuccessStatusCode)
            {
                return headResponse;
            }

            if (headResponse.StatusCode != HttpStatusCode.NotImplemented &&
                headResponse.StatusCode != HttpStatusCode.MethodNotAllowed)
            {
                return headResponse;
            }

            headResponse.Dispose();
        }
        catch (HttpRequestException)
        {
            // Fall back to a ranged GET for sources that reject HEAD.
        }

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, url);
        getRequest.Headers.Range = new RangeHeaderValue(0, 0);
        return await HttpClient.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static long? GetTotalBytes(HttpResponseMessage response)
    {
        return response.Content.Headers.ContentRange?.Length
               ?? response.Content.Headers.ContentLength;
    }

    private static bool SupportsRanges(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.PartialContent || response.Content.Headers.ContentRange is not null)
        {
            return true;
        }

        return response.Headers.AcceptRanges.Any(value => string.Equals(value, "bytes", StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidateDownloadedFile(string destinationPath, long? expectedBytes)
    {
        if (expectedBytes is not > 0)
        {
            return;
        }

        var actualBytes = new FileInfo(destinationPath).Length;
        if (actualBytes != expectedBytes.Value)
        {
            throw new PatchDownloadException(
                $"下载文件不完整，预期 {expectedBytes.Value} 字节，实际 {actualBytes} 字节。请重试。");
        }
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
