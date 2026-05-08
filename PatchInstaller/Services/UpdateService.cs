using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PatchInstaller.Services;

internal static class UpdateService
{
    private static readonly HttpClient UpdateCheckHttpClient = HttpClientService.Create(
        TimeSpan.FromSeconds(20),
        automaticGZipDecompression: true);

    private static readonly HttpClient DownloadHttpClient = HttpClientService.Create(TimeSpan.FromMinutes(5));

    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        var endpointUrl = InstallerBuildConfig.CheckUpdateApi;
        if (string.IsNullOrWhiteSpace(endpointUrl) ||
            !Uri.TryCreate(endpointUrl, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            return UpdateCheckResult.Unavailable;
        }

        var md5 = GetCurrentMd5();
        if (string.IsNullOrWhiteSpace(md5)) return UpdateCheckResult.Unavailable;

        var request = new UpdateCheckRequest(md5, GetPlatformName());
        using var response = await SendCheckRequestAsync(endpoint, request, compressRequestBody: true, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return await ReadCheckResultAsync(response, cancellationToken);
        }

        if (!ShouldRetryWithoutRequestCompression(response.StatusCode))
        {
            response.EnsureSuccessStatusCode();
        }

        using var fallbackResponse = await SendCheckRequestAsync(endpoint, request, compressRequestBody: false, cancellationToken);
        fallbackResponse.EnsureSuccessStatusCode();
        return await ReadCheckResultAsync(fallbackResponse, cancellationToken);
    }

    public static string? GetCurrentMd5()
    {
        try
        {
            var selfPath = GetSelfPath();
            return string.IsNullOrWhiteSpace(selfPath) || !File.Exists(selfPath)
                ? null
                : ComputeMd5(selfPath);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<HttpResponseMessage> SendCheckRequestAsync(
        Uri endpoint,
        UpdateCheckRequest request,
        bool compressRequestBody,
        CancellationToken cancellationToken)
    {
        using var httpRequest = CreateRequest(endpoint, request, compressRequestBody);
        return await UpdateCheckHttpClient.SendAsync(httpRequest, cancellationToken);
    }

    private static HttpRequestMessage CreateRequest(Uri endpoint, UpdateCheckRequest request, bool compressRequestBody)
    {
        var json = JsonSerializer.Serialize(request, UpdateCheckJsonContext.Default.UpdateCheckRequest);
        var content = compressRequestBody
            ? new ByteArrayContent(CompressUtf8(json))
            : new StringContent(json, Encoding.UTF8, "application/json");

        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (compressRequestBody)
        {
            content.Headers.ContentEncoding.Add("gzip");
        }

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = content
        };
        httpRequest.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return httpRequest;
    }

    private static async Task<UpdateCheckResult> ReadCheckResultAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var result = await JsonSerializer.DeserializeAsync(
            responseStream,
            UpdateCheckJsonContext.Default.UpdateCheckResponse,
            cancellationToken);

        if (result is null || string.IsNullOrWhiteSpace(result.Result))
        {
            return UpdateCheckResult.Unavailable;
        }

        return string.Equals(result.Result, "same", StringComparison.OrdinalIgnoreCase)
            ? UpdateCheckResult.Latest
            : UpdateCheckResult.UpdateAvailable(result.Result);
    }

    private static bool ShouldRetryWithoutRequestCompression(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.BadRequest
            or HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden
            or HttpStatusCode.UnsupportedMediaType;
    }

    private static byte[] CompressUtf8(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(bytes, 0, bytes.Length);
        }

        return output.ToArray();
    }

    private static string? GetSelfPath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            File.Exists(processPath) &&
            !IsDotNetHost(processPath))
        {
            return processPath;
        }

        var assemblyFileName = $"{typeof(UpdateService).Assembly.GetName().Name}.dll";
        var assemblyPath = Path.Combine(AppContext.BaseDirectory, assemblyFileName);
        if (File.Exists(assemblyPath))
        {
            return assemblyPath;
        }

        return processPath;
    }

    private static bool IsDotNetHost(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        return string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase);
    }

    public static string GetPlatformName()
    {
        if (OperatingSystem.IsWindows()) return "Win";
        if (OperatingSystem.IsMacOS()) return "Mac";
        if (OperatingSystem.IsLinux()) return "Linux";

        return Environment.OSVersion.Platform.ToString();
    }

    private static string ComputeMd5(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1024 * 1024,
            options: FileOptions.SequentialScan);

        var hash = MD5.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static async Task PrepareAndLaunchAsync(
        string updateUrl,
        IProgress<SelfUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(updateUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("更新链接无效。");
        }

        var targetPath = GetSelfExecutablePath();
        if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath))
        {
            throw new InvalidOperationException("无法定位当前程序文件。");
        }

        if (IsDotNetHost(targetPath))
        {
            throw new InvalidOperationException("当前是开发调试启动方式，无法执行自我更新。请使用发布后的独立程序测试。");
        }

        var updateRoot = Path.Combine(Path.GetTempPath(), "PatchInstaller", "self-update");
        Directory.CreateDirectory(updateRoot);

        var updatePath = Path.Combine(updateRoot, Path.GetFileName(targetPath));
        await DownloadFileAsync(uri, updatePath, progress, cancellationToken);

        var scriptPath = OperatingSystem.IsWindows()
            ? CreateWindowsUpdateScript(updateRoot, updatePath, targetPath)
            : CreateUnixUpdateScript(updateRoot, updatePath, targetPath);

        StartUpdateScript(scriptPath);
    }

    private static async Task DownloadFileAsync(
        Uri uri,
        string destinationPath,
        IProgress<SelfUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        DeleteIfExists(destinationPath);
        progress?.Report(new SelfUpdateProgress(0, null, 0));

        try
        {
            using var response = await DownloadHttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                options: FileOptions.SequentialScan);

            var buffer = new byte[1024 * 1024];
            long downloadedBytes = 0;

            while (true)
            {
                var bytesRead = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (bytesRead <= 0) break;

                await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                downloadedBytes += bytesRead;

                var percent = totalBytes is > 0 ? downloadedBytes * 100d / totalBytes.Value : 0d;
                progress?.Report(new SelfUpdateProgress(downloadedBytes, totalBytes, percent));
            }

            progress?.Report(new SelfUpdateProgress(downloadedBytes, totalBytes, 100));
        }
        catch
        {
            DeleteIfExists(destinationPath);
            throw;
        }
    }

    private static string CreateWindowsUpdateScript(string updateRoot, string updatePath, string targetPath)
    {
        var scriptPath = Path.Combine(updateRoot, "apply-update.cmd");
        var pid = Environment.ProcessId;
        var script = $"""
@echo off
setlocal
set "PID={pid}"
set "SRC={updatePath}"
set "DST={targetPath}"

:wait
tasklist /FI "PID eq %PID%" | find "%PID%" >nul
if not errorlevel 1 (
    timeout /t 1 /nobreak >nul
    goto wait
)

copy /Y "%SRC%" "%DST%" >nul
if errorlevel 1 exit /b 1
start "" "%DST%"
del "%SRC%" >nul 2>nul
del "%~f0" >nul 2>nul
""";
        File.WriteAllText(scriptPath, script);
        return scriptPath;
    }

    private static string CreateUnixUpdateScript(string updateRoot, string updatePath, string targetPath)
    {
        var scriptPath = Path.Combine(updateRoot, "apply-update.sh");
        var pid = Environment.ProcessId;
        var script = $"""
#!/bin/sh
pid='{pid}'
src='{EscapeShellSingleQuoted(updatePath)}'
dst='{EscapeShellSingleQuoted(targetPath)}'

while kill -0 "$pid" 2>/dev/null; do
    sleep 1
done

cp "$src" "$dst" || exit 1
chmod +x "$dst"
"$dst" >/dev/null 2>&1 &
rm -f "$src"
rm -f "$0"
""";
        File.WriteAllText(scriptPath, script);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return scriptPath;
    }

    private static void StartUpdateScript(string scriptPath)
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            }
            : new ProcessStartInfo
            {
                FileName = "/bin/sh",
                Arguments = QuoteArgument(scriptPath),
                UseShellExecute = false,
                CreateNoWindow = true
            };

        Process.Start(startInfo);
    }

    private static string? GetSelfExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
        {
            return processPath;
        }

        var assemblyFileName = $"{typeof(UpdateService).Assembly.GetName().Name}.dll";
        var assemblyPath = Path.Combine(AppContext.BaseDirectory, assemblyFileName);
        return File.Exists(assemblyPath) ? assemblyPath : null;
    }

    private static string EscapeShellSingleQuoted(string value)
    {
        return value.Replace("'", "'\"'\"'", StringComparison.Ordinal);
    }

    private static string QuoteArgument(string value)
    {
        return $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // ignored
        }
    }
}

public sealed record UpdateCheckResult(bool IsAvailable, string? DownloadUrl)
{
    public static UpdateCheckResult Latest { get; } = new(true, null);
    public static UpdateCheckResult Unavailable { get; } = new(false, null);
    public static UpdateCheckResult UpdateAvailable(string downloadUrl) => new(true, downloadUrl);
}

internal sealed record UpdateCheckRequest(
    [property: JsonPropertyName("md5")] string Md5,
    [property: JsonPropertyName("platform")] string Platform);

internal sealed record UpdateCheckResponse(
    [property: JsonPropertyName("result")] string? Result);

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(UpdateCheckRequest))]
[JsonSerializable(typeof(UpdateCheckResponse))]
internal sealed partial class UpdateCheckJsonContext : JsonSerializerContext;

internal sealed record SelfUpdateProgress(long DownloadedBytes, long? TotalBytes, double ProgressPercent);
