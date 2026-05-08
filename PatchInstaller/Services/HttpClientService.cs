using System;
using System.Net;
using System.Net.Http;

namespace PatchInstaller.Services;

internal static class HttpClientService
{
    public static HttpClient Create(TimeSpan timeout, bool automaticGZipDecompression = false)
    {
        var client = automaticGZipDecompression
            ? new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.GZip })
            : new HttpClient();

        client.Timeout = timeout;
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", InstallerBuildConfig.UserAgent);
        return client;
    }
}
