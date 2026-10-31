using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace Vermilune.Core;

public static class HttpService
{
    public const string UserAgent = "Vermilune/1.2";
    public static readonly HttpClient Client = CreateClient();
    public static string Source { get; set; } = "auto"; // auto | official | bmclapi

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            MaxConnectionsPerServer = 64,
            AllowAutoRedirect = true
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }

    public static string MapToBmcl(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        return url
            .Replace("https://launchermeta.mojang.com", "https://bmclapi2.bangbang93.com", StringComparison.OrdinalIgnoreCase)
            .Replace("https://piston-meta.mojang.com", "https://bmclapi2.bangbang93.com", StringComparison.OrdinalIgnoreCase)
            .Replace("https://piston-data.mojang.com", "https://bmclapi2.bangbang93.com", StringComparison.OrdinalIgnoreCase)
            .Replace("https://launcher.mojang.com", "https://bmclapi2.bangbang93.com", StringComparison.OrdinalIgnoreCase)
            .Replace("https://libraries.minecraft.net", "https://bmclapi2.bangbang93.com/maven", StringComparison.OrdinalIgnoreCase)
            .Replace("https://resources.download.minecraft.net", "https://bmclapi2.bangbang93.com/assets", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> CandidateUrls(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return Array.Empty<string>();
        var bmcl = MapToBmcl(url);
        return Source switch
        {
            "bmclapi" => new[] { bmcl },
            "official" => new[] { url },
            _ => bmcl == url ? new[] { url } : new[] { url, bmcl }
        };
    }

    public static async Task<string> GetStringAsync(string url, CancellationToken ct = default)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            foreach (var candidate in CandidateUrls(url))
            {
                try
                {
                    using var response = await Client.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (!response.IsSuccessStatusCode)
                    {
                        last = new HttpRequestException($"{candidate} 返回 {(int)response.StatusCode}");
                        continue;
                    }
                    return await response.Content.ReadAsStringAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    last = ex;
                }
            }
            if (attempt < 2) await Task.Delay(350 * (attempt + 1), ct);
        }
        throw last ?? new HttpRequestException("网络请求失败：" + url);
    }

    public static async Task<byte[]> GetBytesAsync(string url, CancellationToken ct = default)
    {
        Exception? last = null;
        foreach (var candidate in CandidateUrls(url))
        {
            try
            {
                using var response = await Client.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                {
                    last = new HttpRequestException($"{candidate} 返回 {(int)response.StatusCode}");
                    continue;
                }
                return await response.Content.ReadAsByteArrayAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
            }
        }
        throw last ?? new HttpRequestException("网络请求失败：" + url);
    }

    public static async Task DownloadFileAsync(string url, string destination, string expectedSha1,
        IProgress<(long Done, long Total)>? progress, CancellationToken ct, int retries = 3)
    {
        await DownloadFileAsync(CandidateUrls(url), destination, expectedSha1, progress, ct, retries);
    }

    public static async Task DownloadFileAsync(IReadOnlyList<string> urls, string destination, string expectedSha1,
        IProgress<(long Done, long Total)>? progress, CancellationToken ct, int retries = 3)
    {
        if (urls.Count == 0) throw new HttpRequestException("没有可用的下载地址。");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + ".part";
        Exception? last = null;
        for (var attempt = 0; attempt < retries; attempt++)
        {
            foreach (var url in urls)
            {
                try
                {
                    using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (!response.IsSuccessStatusCode)
                    {
                        last = new HttpRequestException($"{url} 返回 {(int)response.StatusCode}");
                        continue;
                    }
                    var total = response.Content.Headers.ContentLength ?? 0;
                    await using (var input = await response.Content.ReadAsStreamAsync(ct))
                    await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, true))
                    {
                        var buffer = new byte[1024 * 128];
                        long done = 0;
                        int read;
                        while ((read = await input.ReadAsync(buffer, ct)) > 0)
                        {
                            await output.WriteAsync(buffer.AsMemory(0, read), ct);
                            done += read;
                            if (progress is not null && total > 0) progress.Report((done, total));
                        }
                    }

                    if (!string.IsNullOrEmpty(expectedSha1) && !VerifySha1(temp, expectedSha1))
                    {
                        last = new InvalidDataException("文件校验失败：" + destination);
                        try { File.Delete(temp); } catch { }
                        continue;
                    }
                    File.Move(temp, destination, true);
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    last = ex;
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                }
            }
            if (attempt + 1 < retries) await Task.Delay(400 * (attempt + 1), ct);
        }
        throw last ?? new HttpRequestException("下载失败：" + destination);
    }

    public static bool VerifySha1(string path, string expected)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var hash = SHA1.HashData(stream);
            return Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static string Sha1OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(stream));
    }
}
