using System.Net.Http;
using System.Reflection;
using System.Text.Json.Nodes;

namespace Vermilune.Core;

public sealed record UpdateInfo(string Version, string Title, string Url, string Notes);

public static class UpdateService
{
    private const string Repo = "ScarletSnow123/Vermilune";

    public static string CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        var current = ParseVersion(CurrentVersion);
        try
        {
            var url = $"https://api.github.com/repos/{Repo}/releases/latest";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Vermilune/" + CurrentVersion);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await HttpService.Client.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                if (JsonNode.Parse(text) is JsonObject release)
                {
                    var tag = release["tag_name"]?.GetValue<string>() ?? "";
                    var latest = ParseVersion(tag);
                    if (latest is not null && current is not null && latest > current)
                    {
                        return new UpdateInfo(
                            latest.ToString(3),
                            release["name"]?.GetValue<string>() ?? tag,
                            release["html_url"]?.GetValue<string>() ?? $"https://github.com/{Repo}/releases",
                            release["body"]?.GetValue<string>() ?? "");
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        // 仓库可能只打了 tag 还没建 Release，退回到 tag 列表。
        try
        {
            var url = $"https://api.github.com/repos/{Repo}/tags?per_page=1";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Vermilune/" + CurrentVersion);
            using var response = await HttpService.Client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            var text = await response.Content.ReadAsStringAsync(ct);
            if (JsonNode.Parse(text) is JsonArray tags && tags.Count > 0 && tags[0] is JsonObject tagNode)
            {
                var tag = tagNode["name"]?.GetValue<string>() ?? "";
                var latest = ParseVersion(tag);
                if (latest is not null && current is not null && latest > current)
                    return new UpdateInfo(latest.ToString(3), "Vermilune " + tag,
                        $"https://github.com/{Repo}/releases/tag/{tag}", "");
            }
        }
        catch { }
        return null;
    }

    private static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim().TrimStart('v', 'V');
        var dash = value.IndexOf('-');
        if (dash >= 0) value = value[..dash];
        return Version.TryParse(value, out var version) ? version : null;
    }
}
