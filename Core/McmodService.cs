using System.Text.RegularExpressions;
using System.Collections.Concurrent;

namespace Vermilune.Core;

/// <summary>把 Mod 名解析成 MC 百科的 class 详情页。</summary>
public static class McmodService
{
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<string> GetDetailOrSearchAsync(string title, CancellationToken ct = default)
    {
        var key = (title ?? "").Trim();
        if (key.Length == 0) return "https://www.mcmod.cn/";
        if (Cache.TryGetValue(key, out var cached)) return cached;

        var search = "https://search.mcmod.cn/s?key=" + Uri.EscapeDataString(key);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            var html = await HttpService.GetStringAsync(search, cts.Token);
            var match = Regex.Match(html, @"https?://www\.mcmod\.cn/class/(\d+)\.html", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var url = "https://www.mcmod.cn/class/" + match.Groups[1].Value + ".html";
                Cache[key] = url;
                return url;
            }
        }
        catch (Exception ex) { Log.Warn("MC 百科详情解析失败：" + ex.Message); }
        return search;
    }
}
