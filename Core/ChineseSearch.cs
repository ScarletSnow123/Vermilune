using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Vermilune.Core;

/// <summary>
/// 中文 Mod 名到英文搜索词的映射。
/// 搜索接口本身只按英文标题匹配，这里做一层常用中文别名转换。
/// </summary>
public static class ChineseSearch
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["输入法冲突修复"] = "IMBlocker",
        ["输入法冲突"] = "IMBlocker",
        ["输入法"] = "IMBlocker",
        ["物品管理器"] = "JEI",
        ["配方查看器"] = "REI",
        ["苹果皮"] = "AppleSkin",
        ["旅行地图"] = "JourneyMap",
        ["小地图"] = "Xaero's Minimap",
        ["世界地图"] = "Xaero's World Map",
        ["鼠标手势"] = "Mouse Tweaks",
        ["机械动力"] = "Create",
        ["通用机械"] = "Mekanism",
        ["沉浸工程"] = "Immersive Engineering",
        ["热力膨胀"] = "Thermal Expansion",
        ["植物魔法"] = "Botania",
        ["匠魂"] = "Tinkers' Construct",
        ["更多箱子"] = "Iron Chests",
        ["背包"] = "Traveler's Backpack",
        ["饰品栏"] = "Curios",
        ["帕秋莉手册"] = "Patchouli",
        ["玉"] = "Jade",
        ["连接纹理"] = "Continuity",
        ["高清修复"] = "OptiFine",
        ["钠"] = "Sodium",
        ["锂"] = "Lithium",
        ["磷"] = "Phosphor",
        ["铟"] = "Indium",
        ["钠附加"] = "Sodium Extra",
        ["自动钓鱼"] = "AutoFish",
        ["中文字体修复"] = "Smooth Font",
        ["光影前置"] = "Iris Shaders",
        ["光影"] = "Iris Shaders",
        ["优化"] = "Sodium",
        ["家具"] = "MrCrayfish's Furniture Mod",
        ["农业"] = "Farming",
        ["暮色森林"] = "Twilight Forest",
        ["虚无世界"] = "Advent of Ascension",
        ["星系"] = "Galacticraft",
        ["工业时代"] = "Industrial Craft",
        ["建筑"] = "BuildCraft",
        ["铁路"] = "Railcraft",
        ["林业"] = "Forestry",
        ["神秘时代"] = "Thaumcraft",
        ["血魔法"] = "Blood Magic",
        ["交错次元"] = "The Betweenlands",
        ["深渊国度"] = "AbyssalCraft",
        ["游戏内信息"] = "WTHIT",
        ["信息显示"] = "Jade",
        ["耐久显示"] = "Durability Viewer",
        ["伤害显示"] = "ToroHealth",
        ["血量显示"] = "Health Overlay",
        ["战利品"] = "Lootr",
        ["任务"] = "FTB Quests",
        ["商店"] = "Shop",
        ["经济"] = "Economy",
    };

    public static string Expand(string query)
    {
        var text = (query ?? "").Trim();
        if (text.Length == 0) return text;

        if (Aliases.TryGetValue(text, out var exact)) return exact;

        // 去掉“模组 / mod / 整合包”等后缀再匹配。
        var cleaned = text
            .Replace("模组", "", StringComparison.OrdinalIgnoreCase)
            .Replace("整合包", "", StringComparison.OrdinalIgnoreCase)
            .Replace("mod", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
        if (cleaned.Length > 0 && Aliases.TryGetValue(cleaned, out var cleanedMatch)) return cleanedMatch;

        return text;
    }

    public static bool HasAlias(string query) =>
        Aliases.ContainsKey((query ?? "").Trim());

    private static readonly ConcurrentDictionary<string, string> Resolved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 先查本地别名；未收录的中文名再去 MC 百科搜索一次，提取括号里的英文名。
    /// 这样不需要把 PCL 的整份 WikiEntries 数据库打进程序。
    /// </summary>
    public static async Task<string> ResolveAsync(string query, CancellationToken ct = default)
    {
        var text = (query ?? "").Trim();
        if (text.Length == 0) return text;

        var local = Expand(text);
        if (!string.Equals(local, text, StringComparison.OrdinalIgnoreCase)) return local;

        if (!Regex.IsMatch(text, @"[\u4e00-\u9fbb]")) return text;
        if (Resolved.TryGetValue(text, out var cached)) return cached;

        try
        {
            var url = "https://search.mcmod.cn/s?key=" + Uri.EscapeDataString(text);
            var html = await HttpService.GetStringAsync(url, ct);
            var match = Regex.Match(html,
                @"<div class=""head"">.*?<a[^>]*href=""https://www\.mcmod\.cn/class/\d+\.html""[^>]*>(.*?)</a>",
                RegexOptions.Singleline);
            if (match.Success)
            {
                var title = Regex.Replace(match.Groups[1].Value, "<.*?>", "");
                title = WebUtility.HtmlDecode(title).Trim();
                var english = Regex.Match(title, @"\(([^()]*)\)\s*$");
                if (english.Success)
                {
                    var candidate = english.Groups[1].Value.Trim();
                    if (candidate.Length > 1 && candidate.All(c => c < 128))
                    {
                        Resolved[text] = candidate;
                        return candidate;
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn("中文名解析失败：" + ex.Message);
        }

        Resolved[text] = text;
        return text;
    }
}
