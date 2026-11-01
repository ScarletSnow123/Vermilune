using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Vermilune.Core;

public sealed class CrashFinding
{
    public ModIssueSeverity Severity { get; init; }
    public string Code { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Suggestion { get; init; } = "";
    public string Evidence { get; init; } = "";
    public List<string> Files { get; init; } = new();
    public List<string> ModIds { get; init; } = new();
}

public sealed class CrashAnalysis
{
    public string VersionId { get; init; } = "";
    public bool HasCrash { get; init; }
    public string? CrashFile { get; init; }
    public string? LogFile { get; init; }
    public string Summary { get; init; } = "";
    public string Description { get; init; } = "";
    public List<CrashFinding> Findings { get; init; } = new();
    public ModScanReport? Mods { get; init; }
    public DateTime AnalyzedAt { get; init; } = DateTime.Now;
    public int CriticalCount => Findings.Count(f => f.Severity == ModIssueSeverity.Critical);
    public bool IsHealthy => CriticalCount == 0 && (Mods?.CriticalCount ?? 0) == 0;
}

public static class CrashAnalyzer
{
    public static string MinecraftDir { get; set; } = "";

    public static string? FindLatestCrashReport(string gameDir, DateTime sinceUtc)
    {
        try
        {
            var dir = Path.Combine(gameDir, "crash-reports");
            if (!Directory.Exists(dir)) return null;
            var files = Directory.EnumerateFiles(dir, "crash-*.txt")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
            var recent = files.FirstOrDefault(f => f.LastWriteTimeUtc >= sinceUtc.AddSeconds(-5));
            return (recent ?? files.FirstOrDefault())?.FullName;
        }
        catch { return null; }
    }

    public static CrashAnalysis Analyze(string versionId, string gameDir, LoaderKind loader, string mcVersion,
        bool includeMods = true, DateTime? sinceUtc = null)
    {
        var findings = new List<CrashFinding>();
        var logFile = FindLatestLog(gameDir);
        var crashFile = FindCrashFile(gameDir, sinceUtc);
        var crashText = ReadTail(crashFile, 512 * 1024);
        var logText = ReadTail(logFile, 768 * 1024);
        var all = (crashText ?? "") + "\n" + (logText ?? "");
        var hasCrash = !string.IsNullOrEmpty(crashText) && crashFile is not null;

        string description = "";
        if (hasCrash)
        {
            var m = Regex.Match(crashText!, @"Description:\s*(.+)");
            if (m.Success) description = m.Groups[1].Value.Trim();
            if (string.IsNullOrEmpty(description))
            {
                var f = Regex.Match(crashText!, @"Failure message:\s*(.+)");
                if (f.Success) description = f.Groups[1].Value.Trim();
            }
        }

        AddPattern(findings, all, "OOM", ModIssueSeverity.Critical,
            @"OutOfMemoryError|Java heap space|GC overhead limit exceeded|insufficient memory",
            "内存不足",
            "游戏在分配内存时失败，通常是整合包所需内存超过 -Xmx 设置。",
            "在版本「修改」里调高内存，并确认使用 64 位 Java；如果已经给了 8G 以上，检查是否装了内存泄漏类 Mod。",
            @"OutOfMemoryError|Java heap space");

        AddPattern(findings, all, "JAVA_VERSION", ModIssueSeverity.Critical,
            @"UnsupportedClassVersionError|class file version \d+|requires Java \d+|Java \d+ .*not supported|Only Java \d+",
            "Java 主版本不兼容",
            "当前 Java 版本无法运行这个 Minecraft / 加载器 / Mod。",
            "在版本「修改」中改用该版本要求的 Java（1.16.5 及以下通常用 Java 8，1.17 用 16/17，1.18-1.20.4 用 17，1.20.5+ 用 21）。",
            @"UnsupportedClassVersionError|class file version|requires Java");

        AddDependencyFindings(findings, all);
        AddPattern(findings, all, "MOD_DUPLICATE", ModIssueSeverity.Critical,
            @"Duplicate mods|duplicate mod|Found duplicate mods|DuplicateModsFoundException|duplicate Mod ID",
            "Mod 重复安装",
            "同一个 Mod（或同一个 Mod ID）出现了多个文件，加载器拒绝继续。",
            "打开 mods 文件夹，只保留一个版本；注意检查 .jar 和 .jar.disabled 是否同时存在。",
            @"Duplicate mods|DuplicateModsFoundException|duplicate mod");

        AddPattern(findings, all, "MIXIN", ModIssueSeverity.Critical,
            @"Mixin apply failed|MixinApplyError|Critical injection failure|MixinTransformerError|org\.spongepowered\.asm\.mixin.*(?:failed|error)",
            "Mixin 注入失败",
            "某个 Mod 的 Mixin 与游戏或其他 Mod 的代码冲突，常见于版本不匹配或两个优化 Mod 同时安装。",
            "优先移除最近安装的 Mod，或检查其是否适配当前 Minecraft / 加载器版本；OptiFine 与 Sodium 类模组不要混装。",
            @"Mixin apply failed|Critical injection failure|MixinApplyError");

        AddPattern(findings, all, "LINKAGE", ModIssueSeverity.Critical,
            @"NoSuchMethodError|NoClassDefFoundError|ClassNotFoundException|IncompatibleClassChangeError|AbstractMethodError|NoSuchFieldError",
            "Mod 之间 API 不兼容",
            "某个 Mod 调用了不存在的类或方法，通常是 Mod 版本与游戏/加载器/前置版本不匹配。",
            "根据下方堆栈定位 Mod，更新或移除它；同时确认所有前置 Mod 与游戏版本一致。",
            @"NoSuchMethodError|NoClassDefFoundError|ClassNotFoundException|IncompatibleClassChangeError");

        var moduleConflict = Regex.Match(all,
            @"java\.lang\.module\.ResolutionException:\s*Modules?\s+([^\s]+)\s+and\s+([^\s]+)\s+export package\s+([^\s]+)\s+to module\s+([^\s]+)");
        if (moduleConflict.Success)
        {
            var a = moduleConflict.Groups[1].Value;
            var b = moduleConflict.Groups[2].Value;
            var package = moduleConflict.Groups[3].Value;
            var target = moduleConflict.Groups[4].Value;
            findings.Add(new CrashFinding
            {
                Code = "MODULE_CONFLICT",
                Severity = ModIssueSeverity.Critical,
                Title = "Mod 模块冲突（重复导出包）",
                Detail = $"模块 {a} 和 {b} 都向 {target} 导出了包 {package}，Java 模块系统拒绝启动。\n" +
                         "这通常出现在 Forge 上使用 Sinytra Connector 加载 Fabric 渲染 Mod 时。",
                Suggestion = "先在 mods 文件夹里搜索并移除与 " + target + " 对应的 Mod（常见是 Sodium Extras / Embeddium Extras）。\n" +
                             "只保留 Sodium 或 Embeddium 其中一个；如果仍然报错，更新或移除 Sinytra Connector 以及依赖它的 Fabric Mod。",
                Evidence = $"模块 {a} / {b} 重复导出 {package}；目标模块：{target}"
            });
        }
        else
        {
            AddPattern(findings, all, "MODULE_CONFLICT", ModIssueSeverity.Critical,
                @"java\.lang\.module\.ResolutionException|export package .* to module",
                "Mod 模块冲突",
                "多个 Mod / 加载器模块同时导出了同一个 Java 包，模块系统拒绝启动。",
                "优先移除最近安装的渲染类 Mod；Forge + Sinytra Connector 环境下先检查 Sodium Extras、Embeddium、Sodium 是否混装。",
                @"java\.lang\.module\.ResolutionException|export package .* to module");
        }

        AddPattern(findings, all, "GRAPHICS", ModIssueSeverity.Warning,
            @"GLFW error|Failed to create window|Pixel format not accelerated|The driver does not appear to support OpenGL|EXCEPTION_ACCESS_VIOLATION|nvoglv|atio6axx|ig\d+icd|OpenGL (?:error|failed|context)",
            "显卡 / OpenGL 初始化异常",
            "游戏无法创建窗口或图形上下文，可能由显卡驱动、远程桌面、双显卡切换或渲染 Mod 引起。",
            "更新显卡驱动；笔记本切换到独显；关闭 OptiFine/Sodium 等渲染 Mod 后重试；避免在远程桌面里启动。",
            @"GLFW error|EXCEPTION_ACCESS_VIOLATION|Pixel format not accelerated");

        AddPattern(findings, all, "CORRUPT_JAR", ModIssueSeverity.Critical,
            @"ZipException|invalid CEN header|Error reading zip|zip END header not found|Invalid signature file",
            "Mod / 运行库文件损坏",
            "某个 jar 下载不完整或已损坏，Java 无法读取。",
            "重新下载对应的 Mod 或运行库；检查磁盘空间与杀毒软件隔离区。",
            @"ZipException|invalid CEN header|Error reading zip");

        AddPattern(findings, all, "ACCESS", ModIssueSeverity.Warning,
            @"AccessDeniedException|Permission denied|拒绝访问|The process cannot access the file",
            "文件被占用或没有写入权限",
            "游戏无法读取/写入配置、日志或 Mod 文件，常见于杀毒软件、同步盘或只读目录。",
            "把游戏目录移出 OneDrive/同步盘；将启动器与 .minecraft 加入杀毒软件白名单；避免在 Program Files 下运行。",
            @"AccessDeniedException|Permission denied|The process cannot access the file");

        AddPattern(findings, all, "FABRIC_INCOMPAT", ModIssueSeverity.Critical,
            @"Incompatible mod set!|A potential solution has been determined|incompatible mods|which is missing!",
            "Fabric 判定 Mod 组合不兼容",
            "Fabric Loader 检测到缺失前置或版本冲突，通常会在日志里列出具体 Mod。",
            "按日志中列出的 Mod 名称检查版本；先更新 Fabric API，再逐个排查最近安装的 Mod。",
            @"Incompatible mod set!|A potential solution has been determined");

        AddPattern(findings, all, "FORGE_DEP", ModIssueSeverity.Critical,
            @"Missing or unsupported mandatory dependencies|Missing dependencies|Mod ID: '",
            "Forge 缺少强制前置",
            "Forge 在加载阶段发现某个 Mod 的 mandatory 依赖不存在。",
            "按日志里的 Mod ID 安装对应前置，或移除要求它的 Mod。",
            @"Missing or unsupported mandatory dependencies|Missing dependencies");

        // 从崩溃报告里提取 Suspected Mods
        var suspected = new List<string>();
        if (hasCrash)
        {
            var sm = Regex.Match(crashText!, @"Suspected Mods:\s*(.+)");
            if (sm.Success)
            {
                foreach (var part in sm.Groups[1].Value.Split(','))
                {
                    var name = part.Trim();
                    if (name.Length > 0 && !name.Equals("unknown", StringComparison.OrdinalIgnoreCase)) suspected.Add(name);
                }
            }
        }

        // Mod 目录扫描
        ModScanReport? mods = null;
        if (includeMods)
        {
            try
            {
                mods = ModScanner.Scan(Path.Combine(gameDir, "mods"), loader, mcVersion);
                foreach (var issue in mods.Issues.Where(i => i.Severity == ModIssueSeverity.Critical).Take(20))
                {
                    if (findings.Any(f => f.Code == issue.Code && f.Title == issue.Title)) continue;
                    findings.Add(new CrashFinding
                    {
                        Severity = issue.Severity,
                        Code = "MODS_" + issue.Code,
                        Title = issue.Title,
                        Detail = issue.Detail,
                        Suggestion = issue.Suggestion,
                        Files = issue.Files,
                        ModIds = issue.ModIds
                    });
                }
            }
            catch (Exception ex) { Log.Warn("扫描 Mod 失败：" + ex.Message); }
        }

        // 堆栈中出现的 Mod 文件名 / ID
        if (mods is not null)
        {
            foreach (var mod in mods.Mods.Where(m => m.Enabled && !m.Bundled))
            {
                var stem = Path.GetFileNameWithoutExtension(mod.FileName);
                if (stem.Length >= 4 && all.Contains(stem, StringComparison.OrdinalIgnoreCase))
                    suspected.Add(mod.Display);
            }
        }
        suspected = suspected.Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
        if (suspected.Count > 0)
        {
            findings.Insert(0, new CrashFinding
            {
                Severity = ModIssueSeverity.Info,
                Code = "SUSPECTED_MODS",
                Title = hasCrash ? "崩溃信息中提到的 Mod" : "日志中提到的 Mod",
                Detail = string.Join("、", suspected),
                Suggestion = "这些 Mod 与日志相关，排查时优先从它们开始。"
            });
        }

        // 没有崩溃报告时，latest.log 里的 LinkageError / Mixin 报错可能被 Mod 捕获，不应全部标记为致命。
        if (!hasCrash)
        {
            findings = findings.Select(f =>
                f.Severity == ModIssueSeverity.Critical && f.Code is "LINKAGE" or "MIXIN" or "FABRIC_INCOMPAT"
                    ? new CrashFinding
                    {
                        Severity = ModIssueSeverity.Warning,
                        Code = f.Code,
                        Title = f.Title,
                        Detail = f.Detail + "（日志中出现，但本次没有生成崩溃报告，可能已被 Mod 捕获。）",
                        Suggestion = f.Suggestion,
                        Evidence = f.Evidence,
                        Files = f.Files,
                        ModIds = f.ModIds
                    }
                    : f).ToList();
        }

        if (hasCrash && findings.Count == 0)
        {
            findings.Add(new CrashFinding
            {
                Severity = ModIssueSeverity.Critical,
                Code = "CRASH_UNKNOWN",
                Title = "游戏崩溃，但没有匹配到已知模式",
                Detail = string.IsNullOrEmpty(description) ? "崩溃报告里没有可识别的描述。" : description,
                Suggestion = "打开崩溃报告查看第一个 Caused by；尝试移除最近安装的 Mod，或把日志发到社区求助。",
                Files = crashFile is null ? new List<string>() : new List<string> { crashFile }
            });
        }

        var summary = hasCrash
            ? (findings.FirstOrDefault(f => f.Severity == ModIssueSeverity.Critical)?.Title ?? "检测到崩溃，但没有明确结论")
            : (findings.Count > 0 ? findings[0].Title : "未发现崩溃记录");
        if (hasCrash && !string.IsNullOrEmpty(description)) summary = description;

        return new CrashAnalysis
        {
            VersionId = versionId,
            HasCrash = hasCrash,
            CrashFile = crashFile,
            LogFile = logFile,
            Summary = summary,
            Description = description,
            Findings = findings,
            Mods = mods
        };
    }

    private static string? FindCrashFile(string gameDir, DateTime? sinceUtc)
    {
        try
        {
            var dir = Path.Combine(gameDir, "crash-reports");
            if (!Directory.Exists(dir)) return null;
            var files = Directory.EnumerateFiles(dir, "crash-*.txt")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
            if (sinceUtc is not null)
                return files.FirstOrDefault(f => f.LastWriteTimeUtc >= sinceUtc.Value.AddSeconds(-5))?.FullName;
            return files.FirstOrDefault()?.FullName;
        }
        catch { return null; }
    }

    private static string? FindLatestLog(string gameDir)
    {
        try
        {
            var latest = Path.Combine(gameDir, "logs", "latest.log");
            if (File.Exists(latest)) return latest;
            var dir = Path.Combine(gameDir, "logs");
            if (!Directory.Exists(dir)) return null;
            return Directory.EnumerateFiles(dir, "*.log")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
        catch { return null; }
    }

    private static string? ReadTail(string? path, int maxBytes)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = fs.Length;
            var take = (int)Math.Min(maxBytes, length);
            fs.Seek(-take, SeekOrigin.End);
            var buffer = new byte[take];
            var read = 0;
            while (read < take)
            {
                var n = fs.Read(buffer, read, take - read);
                if (n <= 0) break;
                read += n;
            }
            var text = Encoding.UTF8.GetString(buffer, 0, read);
            if (text.Contains('\uFFFD'))
            {
                try { text = Encoding.GetEncoding(936).GetString(buffer, 0, read); } catch { }
            }
            return text;
        }
        catch { return null; }
    }

    private static void AddPattern(List<CrashFinding> list, string text, string code, ModIssueSeverity severity,
        string pattern, string title, string detail, string suggestion, string evidencePattern)
    {
        if (string.IsNullOrEmpty(text)) return;
        Match m;
        try { m = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline); }
        catch { return; }
        if (!m.Success) return;
        var evidence = ExtractEvidence(text, evidencePattern);
        list.Add(new CrashFinding
        {
            Severity = severity,
            Code = code,
            Title = title,
            Detail = detail,
            Suggestion = suggestion,
            Evidence = evidence
        });
    }

    private static void AddDependencyFindings(List<CrashFinding> list, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (!Regex.IsMatch(text, @"Missing or unsupported mandatory dependencies|requires .* which is missing|Missing dependency",
                RegexOptions.IgnoreCase)) return;
        var ids = Regex.Matches(text, @"Mod ID:\s*'?([A-Za-z0-9_\-\.]+)'?")
            .Select(m => m.Groups[1].Value)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6).ToList();
        var detail = ids.Count > 0
            ? "日志中提到的前置 Mod ID：" + string.Join("、", ids)
            : "日志显示某个 Mod 的强制前置缺失。";
        list.Add(new CrashFinding
        {
            Severity = ModIssueSeverity.Critical,
            Code = "MISSING_DEPENDENCY",
            Title = "前置 Mod 未安装",
            Detail = detail,
            Suggestion = "安装对应前置（注意与游戏、加载器版本一致）；如果已经安装，检查是否被禁用或版本范围不符。",
            Evidence = ExtractEvidence(text, @"Missing or unsupported mandatory dependencies|Mod ID: '"),
            ModIds = ids
        });
    }

    private static string ExtractEvidence(string text, string pattern)
    {
        try
        {
            var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (!m.Success) return "";
            var start = Math.Max(0, text.LastIndexOf('\n', m.Index) + 1);
            var end = text.IndexOf('\n', m.Index);
            if (end < 0) end = Math.Min(text.Length, start + 300);
            var line = text[start..end].Trim();
            return line.Length > 400 ? line[..400] : line;
        }
        catch { return ""; }
    }
}
