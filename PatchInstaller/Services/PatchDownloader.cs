using System;
using System.Threading;
using System.Threading.Tasks;
using LightDl;

namespace PatchInstaller.Services;

internal static class PatchDownloader
{
    public static async Task<LightDownloadResult> DownloadAsync(
        Uri url,
        string destinationDirectory,
        Action<LightDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var request = LightDownloadRequest.ToDirectory(url, destinationDirectory);
        if (progress is not null)
            request.OnProgress(progress);

        using var downloader = new LightDownloader();
        return await downloader.DownloadAsync(request, cancellationToken);
    }
}
