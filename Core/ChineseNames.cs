using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Vermilune.Core;

/// <summary>
/// PCL/MC 百科中文名快照。数据来自 PCL WikiEntries，仅用于中文展示与搜索。
/// </summary>
public static class ChineseNames
{
    private static readonly Dictionary<string, string> ByText = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> BySlug = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();
    private static bool _loaded;

    public static string? Find(string? title, string? slug)
    {
        EnsureLoaded();
        if (!string.IsNullOrWhiteSpace(slug) && BySlug.TryGetValue(NormalizeSlug(slug), out var bySlug))
            return bySlug;
        if (!string.IsNullOrWhiteSpace(title) && ByText.TryGetValue(NormalizeText(title), out var byText))
            return byText;
        return null;
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (Gate)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var name = assembly.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("WikiEntries.txt", StringComparison.OrdinalIgnoreCase));
                if (name is null) return;
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is null) return;
                using var reader = new StreamReader(stream, Encoding.UTF8);
                while (reader.ReadLine() is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    foreach (var part in line.Split('¨', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var pieces = part.Split('|');
                        if (pieces.Length < 2) continue;
                        var display = Regex.Replace(pieces[^1].Trim(), @"\s+-\s+.*$", "").Trim();
                        var chinese = Regex.Replace(display, @"\s*\([^)]*\)\s*$", "").Trim().TrimEnd('*').Trim();
                        if (string.IsNullOrWhiteSpace(chinese) || !Regex.IsMatch(chinese, @"[\u4e00-\u9fbb]")) continue;
                        var english = Regex.Match(display, @"\(([^)]*)\)").Groups[1].Value.Trim();
                        if (english.Length > 0) ByText[NormalizeText(english)] = chinese;
                        for (var i = 0; i < pieces.Length - 1; i++)
                        {
                            var slug = NormalizeSlug(pieces[i]);
                            if (slug.Length > 0) BySlug[slug] = chinese;
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Warn("加载中文名库失败：" + ex.Message); }
        }
    }

    private static string NormalizeText(string text) =>
        Regex.Replace(text.Trim().ToLowerInvariant(), @"[^a-z0-9\u4e00-\u9fbb]+", "");

    private static string NormalizeSlug(string text) =>
        text.Trim().TrimStart('@').Split('@')[0].Trim().ToLowerInvariant();
}
