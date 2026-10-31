using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Vermilune.Core;

public enum ModIssueSeverity { Info, Warning, Critical }

public sealed class ModDependency
{
    public string ModId { get; init; } = "";
    public string Range { get; init; } = "";
    public bool Mandatory { get; init; } = true;
    public string Side { get; init; } = "";
}

public sealed class ModInfo
{
    public string FilePath { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Description { get; init; } = "";
    public string Authors { get; init; } = "";
    public string Environment { get; init; } = "";
    public string Loader { get; init; } = "";
    public bool MultiLoader { get; set; }
    public bool Enabled { get; init; } = true;
    public long Size { get; init; }
    public string Sha1 { get; set; } = "";
    public string ParentFile { get; init; } = "";
    public bool Bundled { get; set; }
    public List<ModDependency> Depends { get; init; } = new();
    public List<string> Breaks { get; init; } = new();
    public List<string> Provides { get; init; } = new();
    public string MinecraftRange { get; init; } = "";
    public string Display => string.IsNullOrEmpty(Name) ? FileName : Name;
}

public sealed class ModIssue
{
    public ModIssueSeverity Severity { get; init; }
    public string Code { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Suggestion { get; init; } = "";
    public List<string> Files { get; init; } = new();
    public List<string> ModIds { get; init; } = new();
}

public sealed class ModScanReport
{
    public string ModsDir { get; init; } = "";
    public List<ModInfo> Mods { get; init; } = new();
    public List<ModIssue> Issues { get; init; } = new();
    public int EnabledCount => Mods.Count(m => m.Enabled && !m.Bundled);
    public int DisabledCount => Mods.Count(m => !m.Enabled && !m.Bundled);
    public int CriticalCount => Issues.Count(i => i.Severity == ModIssueSeverity.Critical);
}

public static class ModScanner
{
    public static ModScanReport Scan(string modsDir, LoaderKind loader, string mcVersion)
    {
        var report = new ModScanReport { ModsDir = modsDir };
        if (!Directory.Exists(modsDir)) return report;
        var mods = new List<ModInfo>();
        foreach (var file in Directory.EnumerateFiles(modsDir)
                     .Where(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                                 f.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var enabled = !file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
            try
            {
                using var probe = ZipFile.OpenRead(file);
            }
            catch (Exception ex)
            {
                report.Issues.Add(new ModIssue
                {
                    Severity = ModIssueSeverity.Critical,
                    Code = "MOD_CORRUPT",
                    Title = "Mod 文件损坏，无法读取",
                    Detail = Path.GetFileName(file) + "：" + ex.Message,
                    Suggestion = "重新下载该 Mod；如果使用下载器，检查文件大小与 SHA-1。",
                    Files = new List<string> { file }
                });
                continue;
            }
            var info = ParseJar(file, enabled, loader);
            if (info is not null) mods.Add(info);
            else
            {
                report.Issues.Add(new ModIssue
                {
                    Severity = ModIssueSeverity.Warning,
                    Code = "MOD_UNREADABLE",
                    Title = "无法识别的 Mod 文件",
                    Detail = Path.GetFileName(file) + " 中没有找到 fabric.mod.json、mods.toml 或 mcmod.info。",
                    Suggestion = "确认文件是否为真正的 Mod；如果是整合包或资源包，请放到对应目录。",
                    Files = new List<string> { file }
                });
            }
        }
        report.Mods.AddRange(mods);

        // 重复 ID（只比较实际会加载的启用 Mod，且只比较与当前加载器兼容的 Mod）
        foreach (var group in mods.Where(m => !m.Bundled && m.Enabled && !string.IsNullOrEmpty(m.Id) && IsCompatible(m, loader))
                     .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            report.Issues.Add(new ModIssue
            {
                Severity = ModIssueSeverity.Critical,
                Code = "MOD_DUPLICATE_ID",
                Title = $"重复的 Mod ID：{group.Key}",
                Detail = "同一个 Mod 被安装了多次，Forge / Fabric 会拒绝启动，或出现两份代码互相冲突。",
                Suggestion = "只保留一个版本（通常是更新的那个），把其余文件移出 mods 文件夹。",
                Files = group.Select(m => m.FilePath).ToList(),
                ModIds = new List<string> { group.Key }
            });
        }

        // 完全相同的文件（只比较启用的文件）
        foreach (var group in mods.Where(m => !m.Bundled && m.Enabled).GroupBy(m => m.Size).Where(g => g.Count() > 1))
        {
            foreach (var g in group.GroupBy(m => HashFile(m.FilePath)).Where(g => g.Count() > 1))
            {
                report.Issues.Add(new ModIssue
                {
                    Severity = ModIssueSeverity.Critical,
                    Code = "MOD_DUPLICATE_FILE",
                    Title = "完全相同的 Mod 文件被重复放置",
                    Detail = "这些文件的 SHA-1 完全一致，游戏会把它当作重复 Mod。",
                    Suggestion = "只保留其中一份。",
                    Files = g.Select(m => m.FilePath).ToList()
                });
            }
        }

        // 加载器不匹配
        foreach (var mod in mods.Where(m => m.Enabled && !m.Bundled))
        {
            if (loader == LoaderKind.Fabric && mod.Loader == "Forge" && !mod.MultiLoader)
            {
                report.Issues.Add(new ModIssue
                {
                    Severity = ModIssueSeverity.Warning,
                    Code = "MOD_LOADER_MISMATCH",
                    Title = "把 Forge Mod 放进了 Fabric 版本",
                    Detail = $"{mod.FileName} 是 Forge Mod，但当前版本使用 Fabric。",
                    Suggestion = "Forge Mod 不能在 Fabric 上运行。请下载 Fabric 版本，或改用 Forge 整合包。",
                    Files = new List<string> { mod.FilePath },
                    ModIds = new List<string> { mod.Id }
                });
            }
            else if (loader is LoaderKind.Forge or LoaderKind.NeoForge && mod.Loader is "Fabric" or "Quilt" && !mod.MultiLoader)
            {
                report.Issues.Add(new ModIssue
                {
                    Severity = ModIssueSeverity.Warning,
                    Code = "MOD_LOADER_MISMATCH",
                    Title = "把 Fabric Mod 放进了 Forge 版本",
                    Detail = $"{mod.FileName} 是 Fabric Mod，但当前版本使用 {VersionService.LoaderDisplay(loader)}。",
                    Suggestion = "请下载 Forge 版本；如果它只是 Fabric API 的附属，通常无法在 Forge 上运行。",
                    Files = new List<string> { mod.FilePath },
                    ModIds = new List<string> { mod.Id }
                });
            }
        }

        // 缺失前置（只检查当前加载器实际会加载的 Mod）
        var active = mods.Where(m => m.Enabled && !m.Bundled && IsCompatible(m, loader)).ToList();
        var enabledIds = new HashSet<string>(active.Where(m => !string.IsNullOrEmpty(m.Id))
            .Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
        // Fabric API / 部分整合 Mod 会把子模块打包进 META-INF/jars，这些 ID 也算已提供。
        foreach (var m in active)
            foreach (var provided in m.Provides)
                if (!string.IsNullOrEmpty(provided)) enabledIds.Add(provided);
        var builtIn = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "minecraft", "forge", "neoforge", "fabricloader", "quilt_loader", "java", "fabric",
            "fml", "fmluserdev", "javafml", "lowcodefml", "mclanguage"
        };
        foreach (var mod in active)
        {
            foreach (var dep in mod.Depends.Where(d => d.Mandatory && !string.IsNullOrEmpty(d.ModId)))
            {
                if (builtIn.Contains(dep.ModId)) continue;
                if (dep.ModId.EndsWith("-api", StringComparison.OrdinalIgnoreCase) && enabledIds.Contains(dep.ModId)) continue;
                if (enabledIds.Contains(dep.ModId)) continue;
                report.Issues.Add(new ModIssue
                {
                    Severity = ModIssueSeverity.Critical,
                    Code = "MOD_MISSING_DEPENDENCY",
                    Title = $"缺少前置：{dep.ModId}",
                    Detail = $"{mod.Display}（{mod.FileName}）需要 {dep.ModId}" +
                             (string.IsNullOrEmpty(dep.Range) ? "" : $" {dep.Range}") + "，但 mods 文件夹里没有它。",
                    Suggestion = $"下载并安装 {dep.ModId} 的对应 Minecraft 版本，或者移除 {mod.Display}。",
                    Files = new List<string> { mod.FilePath },
                    ModIds = new List<string> { mod.Id, dep.ModId }
                });
            }
        }

        // Mod 自己声明会与之冲突的 ID
        foreach (var mod in active)
        {
            foreach (var other in mod.Breaks.Where(x => !string.IsNullOrEmpty(x) && !builtIn.Contains(x)))
            {
                if (!enabledIds.Contains(other)) continue;
                report.Issues.Add(new ModIssue
                {
                    Severity = ModIssueSeverity.Critical,
                    Code = "MOD_BREAKS",
                    Title = $"Mod 声明冲突：{mod.Id} ↔ {other}",
                    Detail = $"{mod.Display} 明确声明与 {other} 不兼容。",
                    Suggestion = "移除其中一个 Mod；如果确实需要共存，寻找作者提供的兼容版本。",
                    Files = new List<string> { mod.FilePath },
                    ModIds = new List<string> { mod.Id, other }
                });
            }
        }

        // 版本范围不匹配
        foreach (var mod in active.Where(m => !string.IsNullOrEmpty(m.MinecraftRange)))
        {
            if (!VersionRange.Matches(mcVersion, mod.MinecraftRange))
            {
                report.Issues.Add(new ModIssue
                {
                    Severity = ModIssueSeverity.Warning,
                    Code = "MOD_MC_VERSION",
                    Title = "Mod 标注的 Minecraft 版本不匹配",
                    Detail = $"{mod.Display} 要求 Minecraft {mod.MinecraftRange}，当前版本是 {mcVersion}。",
                    Suggestion = "确认该 Mod 是否有当前游戏版本的构建；强行加载可能崩溃。",
                    Files = new List<string> { mod.FilePath },
                    ModIds = new List<string> { mod.Id }
                });
            }
        }

        // 常见冲突
        var ids = active.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ids.Contains("optifine") && ids.Contains("sodium")) AddConflict(report, "OptiFine 与 Sodium 冲突", "两者都修改渲染管线，通常无法同时使用。", "只保留 Sodium（或 OptiFine），并安装对应的兼容模组。");
        if (ids.Contains("optifine") && ids.Contains("embeddium")) AddConflict(report, "OptiFine 与 Embeddium 冲突", "两者都替换渲染管线。", "只保留其中一个。");
        if (ids.Contains("rubidium") && ids.Contains("sodium")) AddConflict(report, "Rubidium 与 Sodium 冲突", "二者是同一类渲染优化模组。", "Forge 用 Rubidium/Embeddium，Fabric 用 Sodium，不要混装。");
        if (ids.Contains("fabric-api") && mods.Any(m => m.Enabled && m.Id == "fabric" && m.FileName.Contains("fabric-api", StringComparison.OrdinalIgnoreCase)))
            AddConflict(report, "Fabric API 可能重复", "检测到多个 Fabric API 提供者。", "只保留一个 Fabric API 主文件。");

        Log.Info($"Mods 扫描：{modsDir}，{mods.Count} 个文件，{report.Issues.Count} 个问题");
        foreach (var issue in report.Issues.Where(i => i.Severity == ModIssueSeverity.Critical).Take(40))
            Log.Info($" - [{issue.Code}] {issue.Title}：{issue.Detail}");
        return report;
    }

    private static void AddConflict(ModScanReport report, string title, string detail, string suggestion)
    {
        report.Issues.Add(new ModIssue
        {
            Severity = ModIssueSeverity.Critical,
            Code = "MOD_CONFLICT",
            Title = title,
            Detail = detail,
            Suggestion = suggestion
        });
    }

    private static ModInfo? ParseJar(string path, bool enabled, LoaderKind preferred)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var mod = ParseZip(zip, path, enabled, null, preferred);
            var provided = new List<string>();
            foreach (var entry in zip.Entries.Where(e =>
                         (e.FullName.StartsWith("META-INF/jars/", StringComparison.OrdinalIgnoreCase) ||
                          e.FullName.StartsWith("META-INF/jarjar/", StringComparison.OrdinalIgnoreCase)) &&
                         e.FullName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)).Take(240))
            {
                try
                {
                    using var ms = new MemoryStream();
                    using (var s = entry.Open()) s.CopyTo(ms);
                    ms.Position = 0;
                    using var nested = new ZipArchive(ms, ZipArchiveMode.Read, true);
                    var nestedMod = ParseZip(nested, path, enabled, Path.GetFileName(path), preferred);
                    if (nestedMod is null) continue;
                    provided.Add(nestedMod.Id);
                    if (mod is not null)
                    {
                        mod.Provides.Add(nestedMod.Id);
                        mod.Provides.AddRange(nestedMod.Provides);
                    }
                    else
                    {
                        mod = nestedMod;
                        // 容器 jar（例如 GroovyModLoader）本身没有 mod 元数据，
                        // 但它通过 JarJar 提供的模块会被加载，不能当作 Bundled 隐藏。
                        mod.Bundled = false;
                        mod.Provides.Add(nestedMod.Id);
                        mod.Provides.AddRange(nestedMod.Provides);
                    }
                }
                catch { }
            }
            if (mod is not null)
                foreach (var id in provided) if (!mod.Provides.Contains(id, StringComparer.OrdinalIgnoreCase)) mod.Provides.Add(id);
            return mod;
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static ModInfo? ParseZip(ZipArchive zip, string path, bool enabled, string? parent, LoaderKind preferred)
    {
        var fabric = ReadEntry(zip, "fabric.mod.json");
        var quilt = ReadEntry(zip, "quilt.mod.json");
        var toml = ReadEntry(zip, "META-INF/neoforge.mods.toml") ?? ReadEntry(zip, "META-INF/mods.toml");
        var legacy = ReadEntry(zip, "mcmod.info") ?? ReadEntry(zip, "META-INF/mcmod.info");
        var forgeText = !string.IsNullOrEmpty(toml) ? toml : legacy;
        ModInfo? mod = null;
        if (!string.IsNullOrEmpty(forgeText) && preferred is LoaderKind.Forge or LoaderKind.NeoForge)
            mod = !string.IsNullOrEmpty(toml)
                ? ParseForgeToml(toml!, path, enabled, parent)
                : ParseLegacyInfo(forgeText!, path, enabled, parent);
        if (mod is null && !string.IsNullOrEmpty(fabric))
            mod = ParseFabric(fabric!, path, enabled, parent);
        if (mod is null && !string.IsNullOrEmpty(quilt))
            mod = ParseQuilt(quilt!, path, enabled, parent);
        if (mod is null && !string.IsNullOrEmpty(forgeText))
            mod = !string.IsNullOrEmpty(toml)
                ? ParseForgeToml(toml!, path, enabled, parent)
                : ParseLegacyInfo(forgeText!, path, enabled, parent);
        if (mod is not null)
            mod.MultiLoader = !string.IsNullOrEmpty(fabric) && !string.IsNullOrEmpty(forgeText);
        return mod;
    }

    private static string? ReadEntry(ZipArchive zip, string name)
    {
        try
        {
            var entry = zip.GetEntry(name);
            if (entry is null) return null;
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8, true);
            return reader.ReadToEnd();
        }
        catch { return null; }
    }

    private static ModInfo? ParseFabric(string json, string path, bool enabled, string? parent)
    {
        try
        {
            var root = JsonNode.Parse(json) as JsonObject;
            if (root is null) return null;
            var id = root["id"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(id)) return null;
            var deps = new List<ModDependency>();
            if (root["depends"] is JsonObject d)
            {
                foreach (var kv in d)
                {
                    deps.Add(new ModDependency
                    {
                        ModId = kv.Key,
                        Range = kv.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : "",
                        Mandatory = true
                    });
                }
            }
            var breaks = new List<string>();
            if (root["breaks"] is JsonObject b)
                foreach (var kv in b) breaks.Add(kv.Key);
            return new ModInfo
            {
                FilePath = path,
                FileName = Path.GetFileName(path),
                Id = id,
                Name = root["name"]?.GetValue<string>() ?? id,
                Version = root["version"]?.GetValue<string>() ?? "",
                Description = root["description"]?.GetValue<string>() ?? "",
                Authors = JoinAuthors(root["authors"]),
                Environment = root["environment"]?.GetValue<string>() ?? "",
                Loader = "Fabric",
                Enabled = enabled,
                Size = TrySize(path),
                ParentFile = parent ?? "",
                Bundled = parent is not null,
                Depends = deps,
                Breaks = breaks,
                MinecraftRange = deps.FirstOrDefault(x => x.ModId == "minecraft")?.Range ?? ""
            };
        }
        catch { return null; }
    }

    private static ModInfo? ParseQuilt(string json, string path, bool enabled, string? parent)
    {
        try
        {
            var root = JsonNode.Parse(json) as JsonObject;
            var loader = root?["quilt_loader"] as JsonObject;
            var id = loader?["id"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(id)) return null;
            var deps = new List<ModDependency>();
            if (loader?["depends"] is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is not JsonObject o) continue;
                    var depId = o["id"]?.GetValue<string>() ?? "";
                    if (string.IsNullOrEmpty(depId)) continue;
                    var range = o["versions"]?.GetValue<string>() ?? "";
                    deps.Add(new ModDependency { ModId = depId, Range = range, Mandatory = o["optional"]?.GetValue<bool>() != true });
                }
            }
            var meta = loader?["metadata"] as JsonObject;
            return new ModInfo
            {
                FilePath = path,
                FileName = Path.GetFileName(path),
                Id = id,
                Name = meta?["name"]?.GetValue<string>() ?? id,
                Version = loader?["version"]?.GetValue<string>() ?? "",
                Description = meta?["description"]?.GetValue<string>() ?? "",
                Authors = "",
                Environment = "",
                Loader = "Quilt",
                Enabled = enabled,
                Size = TrySize(path),
                ParentFile = parent ?? "",
                Bundled = parent is not null,
                Depends = deps,
                MinecraftRange = deps.FirstOrDefault(x => x.ModId == "minecraft")?.Range ?? ""
            };
        }
        catch { return null; }
    }

    private static ModInfo? ParseForgeToml(string toml, string path, bool enabled, string? parent)
    {
        try
        {
            var result = TomlLite.ParseModsToml(toml);
            if (result.Mods.Count == 0)
            {
                // Modrinth 的 datapack/资源包转换器会写成 mods = [ { modId = '...' } ]，
                // 不是标准的 [[mods]] 段；至少提取 ID，避免被误判成 Fabric Mod 并报告虚假缺前置。
                var idMatch = Regex.Match(toml, @"modId\s*=\s*['""]([^'""]+)['""]", RegexOptions.IgnoreCase);
                if (!idMatch.Success) return null;
                var nameMatch = Regex.Match(toml, @"displayName\s*=\s*['""]([^'""]*)['""]", RegexOptions.IgnoreCase);
                var versionMatch = Regex.Match(toml, @"version\s*=\s*['""]([^'""]*)['""]", RegexOptions.IgnoreCase);
                return new ModInfo
                {
                    FilePath = path,
                    FileName = Path.GetFileName(path),
                    Id = idMatch.Groups[1].Value,
                    Name = nameMatch.Success ? nameMatch.Groups[1].Value : idMatch.Groups[1].Value,
                    Version = versionMatch.Success ? versionMatch.Groups[1].Value : "",
                    Description = "",
                    Authors = "",
                    Environment = "",
                    Loader = "Forge",
                    Enabled = enabled,
                    Size = TrySize(path),
                    ParentFile = parent ?? "",
                    Bundled = parent is not null,
                    Depends = new List<ModDependency>()
                };
            }
            var main = result.Mods[0];
            return new ModInfo
            {
                FilePath = path,
                FileName = Path.GetFileName(path),
                Id = main.GetValueOrDefault("modId", ""),
                Name = main.GetValueOrDefault("displayName", main.GetValueOrDefault("modId", "")),
                Version = main.GetValueOrDefault("version", ""),
                Description = main.GetValueOrDefault("description", ""),
                Authors = main.GetValueOrDefault("authors", ""),
                Environment = main.GetValueOrDefault("displayTest", ""),
                Loader = "Forge",
                Enabled = enabled,
                Size = TrySize(path),
                ParentFile = parent ?? "",
                Bundled = parent is not null,
                Depends = result.Dependencies.Select(d => new ModDependency
                {
                    ModId = d.GetValueOrDefault("modId", ""),
                    Range = d.GetValueOrDefault("versionRange", ""),
                    Mandatory = !d.GetValueOrDefault("mandatory", "true").Equals("false", StringComparison.OrdinalIgnoreCase),
                    Side = d.GetValueOrDefault("side", "")
                }).ToList(),
                MinecraftRange = result.Dependencies.FirstOrDefault(d => d.GetValueOrDefault("modId") == "minecraft")?.GetValueOrDefault("versionRange", "") ?? ""
            };
        }
        catch { return null; }
    }

    private static ModInfo? ParseLegacyInfo(string json, string path, bool enabled, string? parent)
    {
        try
        {
            var node = JsonNode.Parse(json);
            JsonObject? o = null;
            if (node is JsonArray arr && arr.Count > 0) o = arr[0] as JsonObject;
            else if (node is JsonObject obj) o = obj["modList"] is JsonArray ml && ml.Count > 0 ? ml[0] as JsonObject : obj;
            if (o is null) return null;
            var id = o["modid"]?.GetValue<string>() ?? o["modId"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(id)) return null;
            var deps = new List<ModDependency>();
            if (o["requiredMods"] is JsonArray req)
                foreach (var item in req) if (item is JsonValue v && v.TryGetValue<string>(out var s)) deps.Add(new ModDependency { ModId = s.Split(':')[0] });
            return new ModInfo
            {
                FilePath = path,
                FileName = Path.GetFileName(path),
                Id = id,
                Name = o["name"]?.GetValue<string>() ?? id,
                Version = o["version"]?.GetValue<string>() ?? "",
                Description = o["description"]?.GetValue<string>() ?? "",
                Authors = JoinAuthors(o["authorList"]),
                Loader = "Forge",
                Enabled = enabled,
                Size = TrySize(path),
                ParentFile = parent ?? "",
                Bundled = parent is not null,
                Depends = deps
            };
        }
        catch { return null; }
    }

    private static string JoinAuthors(JsonNode? node)
    {
        if (node is JsonArray arr) return string.Join(", ", arr.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0));
        return node?.ToString() ?? "";
    }

    private static long TrySize(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    private static bool IsCompatible(ModInfo mod, LoaderKind loader)
    {
        if (mod.MultiLoader) return true;
        return loader switch
        {
            LoaderKind.Fabric => mod.Loader is "Fabric" or "Quilt",
            LoaderKind.Quilt => mod.Loader is "Fabric" or "Quilt",
            LoaderKind.Forge or LoaderKind.NeoForge => mod.Loader == "Forge",
            _ => true
        };
    }

    public static string HashFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(SHA1.HashData(fs));
        }
        catch { return ""; }
    }

    public static (bool Ok, string Error, string NewPath) Toggle(string filePath, bool enable)
    {
        try
        {
            if (!File.Exists(filePath) && !File.Exists(filePath + ".disabled"))
                return (false, "文件不存在。", filePath);
            var current = File.Exists(filePath + ".disabled") ? filePath + ".disabled" : filePath;
            var isDisabled = current.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
            var target = enable
                ? (isDisabled ? current[..^".disabled".Length] : current)
                : (isDisabled ? current : current + ".disabled");
            if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase)) return (true, "", current);
            if (File.Exists(target)) return (false, "目标文件已存在，可能已有同名 Mod。", current);
            File.Move(current, target);
            return (true, "", target);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, filePath);
        }
    }

    public static (bool Ok, string Error) Delete(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(filePath,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            return (true, "");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}

public static class VersionRange
{
    public static bool Matches(string version, string range)
    {
        if (string.IsNullOrWhiteSpace(range) || range.Trim() == "*") return true;
        var v = Parse(version);
        if (v is null) return true;
        range = range.Trim();

        // Forge 的区间写法：[1.20.1,1.21) / [1.20,) / (1.16,1.17]
        if ((range.StartsWith('[') || range.StartsWith('(')) && range.Contains(','))
        {
            var lowerInclusive = range[0] == '[';
            var upperInclusive = range[^1] == ']';
            var inner = range[1..^1];
            var parts = inner.Split(',');
            if (parts.Length >= 2)
            {
                var low = Parse(parts[0]);
                if (low is not null && (lowerInclusive ? v < low : v <= low)) return false;
                if (!string.IsNullOrWhiteSpace(parts[1]))
                {
                    var high = Parse(parts[1]);
                    if (high is not null && (upperInclusive ? v > high : v >= high)) return false;
                }
                return true;
            }
        }

        var cleaned = range.Replace("[", "").Replace("]", "").Replace("(", "").Replace(")", "");
        var tokens = cleaned.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return true;
        try
        {
            foreach (var raw in tokens)
            {
                var token = raw.Trim();
                if (token is "*" or "x" or "X") continue;
                // Fabric 的 ~ 表示同一 minor；Forge 的 [1.20,1.21) 已去掉括号
                if (token.StartsWith(">=") && v < Parse(token[2..])!) return false;
                else if (token.StartsWith("<=") && v > Parse(token[2..])!) return false;
                else if (token.StartsWith(">") && v <= Parse(token[1..])!) return false;
                else if (token.StartsWith("<") && v >= Parse(token[1..])!) return false;
                else if (token.StartsWith("~") || token.StartsWith("^"))
                {
                    var baseV = Parse(token[1..]);
                    if (baseV is null) continue;
                    if (v < baseV) return false;
                    if (v.Major != baseV.Major) return false;
                    if (token.StartsWith("~") && v.Minor != baseV.Minor) return false;
                }
                else if (token.StartsWith("=")) { if (v != Parse(token[1..])) return false; }
                else
                {
                    if (token.Contains('x') || token.Contains('X'))
                    {
                        var prefix = token.Split('x')[0].TrimEnd('.');
                        if (!version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
                    }
                    else
                    {
                        var tv = Parse(token);
                        if (tv is not null && v != tv) return false;
                    }
                }
            }
        }
        catch { return true; }
        return true;
    }

    private static Version? Parse(string s)
    {
        try
        {
            s = s.Trim().TrimStart('v', 'V');
            var m = Regex.Match(s, @"^(\d+)(?:\.(\d+))?(?:\.(\d+))?");
            if (!m.Success) return null;
            return new Version(
                int.Parse(m.Groups[1].Value),
                m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0,
                m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
        }
        catch { return null; }
    }
}

/// <summary>只解析 mods.toml 中我们需要的字段，避免引入完整 TOML 依赖。</summary>
public static class TomlLite
{
    public sealed class ModsTomlResult
    {
        public List<Dictionary<string, string>> Mods { get; } = new();
        public List<Dictionary<string, string>> Dependencies { get; } = new();
    }

    public static ModsTomlResult ParseModsToml(string text)
    {
        var result = new ModsTomlResult();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        Dictionary<string, string>? current = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = StripComment(lines[i]).Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("[[mods]]", StringComparison.OrdinalIgnoreCase))
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                result.Mods.Add(current);
                continue;
            }
            if (line.StartsWith("[[dependencies", StringComparison.OrdinalIgnoreCase))
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                result.Dependencies.Add(current);
                continue;
            }
            if (line.StartsWith('['))
            {
                current = null;
                continue;
            }
            if (current is null) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.StartsWith("\"\"\"") || value.StartsWith("'''"))
            {
                var quote = value[..3];
                var rest = value[3..];
                if (rest.EndsWith(quote)) value = rest[..^3];
                else
                {
                    var sb = new StringBuilder(rest);
                    while (i + 1 < lines.Length)
                    {
                        i++;
                        var next = lines[i];
                        if (next.TrimEnd().EndsWith(quote))
                        {
                            sb.Append('\n').Append(next.TrimEnd()[..^3]);
                            break;
                        }
                        sb.Append('\n').Append(next);
                    }
                    value = sb.ToString();
                }
            }
            else value = Unquote(value);
            current[key] = value;
        }
        return result;
    }

    private static string StripComment(string line)
    {
        var inQuote = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inQuote = !inQuote;
            if (line[i] == '#' && !inQuote) return line[..i];
        }
        return line;
    }

    private static string Unquote(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') return value[1..^1].Replace("\\\"", "\"");
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') return value[1..^1];
        return value;
    }
}
