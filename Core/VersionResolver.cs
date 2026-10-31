using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Vermilune.Core;

public sealed class VersionResolver
{
    private readonly string _mcDir;
    private readonly Dictionary<string, (DateTime Write, ResolvedVersion Value)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public VersionResolver(string mcDir) => _mcDir = mcDir;

    public string FindJsonPath(string folder)
    {
        var dir = Path.Combine(_mcDir, "versions", folder);
        if (!Directory.Exists(dir)) return "";
        var preferred = Path.Combine(dir, folder + ".json");
        if (File.Exists(preferred)) return preferred;

        foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("id", out _) ||
                    doc.RootElement.TryGetProperty("inheritsFrom", out _) ||
                    doc.RootElement.TryGetProperty("mainClass", out _))
                    return file;
            }
            catch { }
        }
        return "";
    }

    public ResolvedVersion? Resolve(string folder)
    {
        try
        {
            var jsonPath = FindJsonPath(folder);
            if (string.IsNullOrEmpty(jsonPath)) return null;
            var write = File.GetLastWriteTimeUtc(jsonPath);
            lock (_lock)
            {
                if (_cache.TryGetValue(folder, out var hit) && hit.Write == write) return hit.Value;
            }

            var chain = new List<(string Id, string Folder, JsonObject Root)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var currentFolder = folder;
            for (var depth = 0; depth < 12; depth++)
            {
                if (!seen.Add(currentFolder)) break;
                var path = FindJsonPath(currentFolder);
                if (string.IsNullOrEmpty(path)) break;
                var root = JsonNode.Parse(File.ReadAllText(path), null,
                    new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject;
                if (root is null) break;
                var id = root["id"]?.GetValue<string>() ?? currentFolder;
                chain.Add((id, currentFolder, root));
                var parent = root["inheritsFrom"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(parent)) break;
                currentFolder = parent;
            }
            if (chain.Count == 0) return null;

            var resolved = Merge(folder, chain);
            lock (_lock) { _cache[folder] = (write, resolved); }
            return resolved;
        }
        catch (Exception ex)
        {
            Log.Warn($"解析版本失败（{folder}）：{ex.Message}");
            return null;
        }
    }

    private ResolvedVersion Merge(string folder, List<(string Id, string Folder, JsonObject Root)> chain)
    {
        var baseToChild = Enumerable.Reverse(chain).ToList();
        var libs = new Dictionary<string, LibraryEntry>(StringComparer.OrdinalIgnoreCase);
        var libOrder = new List<string>();
        var gameArgs = new List<JsonNode?>();
        var jvmArgs = new List<JsonNode?>();
        string mainClass = "", type = "release", assets = "", clientVersion = "", legacyArgs = "", jarOverride = "";
        JsonObject? assetIndex = null, logging = null, javaVersion = null;
        JsonObject? clientDownload = null;
        string clientJarFolder = folder;
        string clientJarName = folder;

        foreach (var (_, verFolder, root) in baseToChild)
        {
            if (root["mainClass"] is JsonValue mc) mainClass = mc.GetValue<string>();
            if (root["type"] is JsonValue tv) type = tv.GetValue<string>();
            if (root["assets"] is JsonValue av) assets = av.GetValue<string>();
            if (root["clientVersion"] is JsonValue cv) clientVersion = cv.GetValue<string>();
            if (root["jar"] is JsonValue jv) jarOverride = jv.GetValue<string>();
            if (root["minecraftArguments"] is JsonValue la) legacyArgs = la.GetValue<string>();
            if (root["assetIndex"] is JsonObject ai) assetIndex = (JsonObject)ai.DeepClone();
            if (root["logging"] is JsonObject lg) logging = (JsonObject)lg.DeepClone();
            if (root["javaVersion"] is JsonObject jver) javaVersion = (JsonObject)jver.DeepClone();

            if (root["downloads"] is JsonObject dl && dl["client"] is JsonObject cl)
            {
                clientDownload = (JsonObject)cl.DeepClone();
                clientJarFolder = verFolder;
                clientJarName = root["id"]?.GetValue<string>() ?? verFolder;
            }

            if (root["arguments"] is JsonObject args)
            {
                if (args["game"] is JsonArray ga) foreach (var n in ga) gameArgs.Add(n?.DeepClone());
                if (args["jvm"] is JsonArray ja) foreach (var n in ja) jvmArgs.Add(n?.DeepClone());
            }

            if (root["libraries"] is JsonArray arr)
            {
                // 同一个版本内可能同时出现 ASM 9.6 与 9.10.1（Fabric 会主动检测重复类）。
                // 按 group:artifact[:classifier] 去重，同组保留版本更高的一项，再让子版本覆盖父版本。
                var local = new Dictionary<string, LibraryEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var node in arr)
                {
                    if (node is not JsonObject lo) continue;
                    var entry = ParseLibrary(lo);
                    if (entry is null) continue;
                    if (!local.TryGetValue(entry.ArtifactKey, out var existing) ||
                        CompareVersions(VersionOf(entry.Name), VersionOf(existing.Name)) >= 0)
                        local[entry.ArtifactKey] = entry;
                }
                foreach (var (key, entry) in local)
                {
                    if (!libs.ContainsKey(key)) libOrder.Add(key);
                    libs[key] = entry;
                }
            }
        }

        // 若子版本通过 jar 字段指定了其他版本的客户端 jar，则使用该版本目录
        if (!string.IsNullOrWhiteSpace(jarOverride))
        {
            clientJarFolder = jarOverride;
            clientJarName = jarOverride;
            var jarPath0 = Path.Combine(_mcDir, "versions", jarOverride, jarOverride + ".jar");
            if (!File.Exists(jarPath0) && clientDownload?["url"] is not null)
            {
                // 保持 jar 字段优先，下载到目标目录
            }
        }
        else if (clientDownload is not null)
        {
            clientJarName = clientJarFolder;
        }

        var jarPath = Path.Combine(_mcDir, "versions", clientJarFolder, clientJarName + ".jar");
        var mcVersion = !string.IsNullOrWhiteSpace(clientVersion) ? clientVersion : ExtractMcVersion(chain);
        // 某些 Forge 安装器写出的版本 JSON 没有 client 下载，但基础原版 jar 是存在的。
        if (!File.Exists(jarPath) && !string.IsNullOrWhiteSpace(mcVersion))
        {
            var vanillaJar = Path.Combine(_mcDir, "versions", mcVersion, mcVersion + ".jar");
            if (File.Exists(vanillaJar)) jarPath = vanillaJar;
        }
        var assetsId = assetIndex?["id"]?.GetValue<string>() ?? assets;
        var assetIndexPath = string.IsNullOrEmpty(assetsId) ? "" : Path.Combine(_mcDir, "assets", "indexes", assetsId + ".json");
        var javaMajor = javaVersion?["majorVersion"]?.GetValue<int>() ?? InferJavaMajor(clientVersion, chain[0].Id);

        var loggingConfig = ParseLogging(logging);
        var loader = DetectLoader(mainClass, chain);
        var libList = libOrder.Select(k => libs[k]).ToList();

        return new ResolvedVersion
        {
            Id = chain[0].Id,
            BaseId = chain[^1].Id,
            MainClass = mainClass,
            Type = type,
            AssetsId = assetsId,
            AssetIndexUrl = assetIndex?["url"]?.GetValue<string>() ?? "",
            AssetIndexSha1 = assetIndex?["sha1"]?.GetValue<string>() ?? "",
            AssetIndexPath = assetIndexPath,
            ClientJarPath = jarPath,
            ClientJarUrl = clientDownload?["url"]?.GetValue<string>() ?? "",
            ClientJarSha1 = clientDownload?["sha1"]?.GetValue<string>() ?? "",
            JavaMajor = javaMajor,
            MinecraftVersion = mcVersion,
            Loader = loader,
            Libraries = libList,
            GameArguments = gameArgs,
            JvmArguments = jvmArgs,
            LegacyArguments = legacyArgs,
            Logging = loggingConfig,
            Chain = chain.Select(c => c.Id).ToList(),
            HasCustomResolutionArg = gameArgs.Any(a => a is JsonObject o &&
                o["rules"]?.ToJsonString().Contains("has_custom_resolution", StringComparison.Ordinal) == true)
        };
    }

    private static LibraryEntry? ParseLibrary(JsonObject lo)
    {
        var name = lo["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name)) return null;
        var rules = lo["rules"] as JsonArray;
        var nativeMap = lo["natives"] as JsonObject;
        var downloads = lo["downloads"] as JsonObject;

        if (nativeMap is not null)
        {
            var key = nativeMap.FirstOrDefault(kv =>
                kv.Key.Equals("windows", StringComparison.OrdinalIgnoreCase));
            if (key.Key is null) return null;
            var classifier = key.Value?.GetValue<string>() ?? "";
            if (classifier.Contains("${arch}")) classifier = classifier.Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32");
            var artifact = downloads?["classifiers"]?[classifier] as JsonObject;
            var path = artifact?["path"]?.GetValue<string>() ?? MavenPath(name, classifier);
            var url = artifact?["url"]?.GetValue<string>() ?? "";
            var fallbacks = BuildFallbacks(path);
            if (string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(lo["url"]?.GetValue<string>()))
                url = CombineBase(lo["url"]!.GetValue<string>(), path);
            var excludeNode = lo["extract"]?["exclude"];
            var exclude = excludeNode is JsonArray ea
                ? string.Join(";", ea.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0))
                : excludeNode is JsonValue ev ? ev.GetValue<string>() : "";
            return new LibraryEntry
            {
                Name = name + ":" + classifier,
                ArtifactKey = ArtifactKeyOf(name, classifier),
                Path = path,
                Url = url,
                FallbackUrls = fallbacks,
                Sha1 = artifact?["sha1"]?.GetValue<string>() ?? "",
                Size = artifact?["size"]?.GetValue<long>() ?? 0,
                IsNative = true,
                NativeClassifier = classifier,
                ExtractExclude = exclude,
                Rules = (JsonArray?)rules?.DeepClone(),
                NativeOsKey = key.Key
            };
        }

        var art = downloads?["artifact"] as JsonObject;
        var libPath = art?["path"]?.GetValue<string>() ?? MavenPath(name, null);
        var libUrl = art?["url"]?.GetValue<string>() ?? "";
        var libFallbacks = BuildFallbacks(libPath);
        if (string.IsNullOrEmpty(libUrl) && lo["url"] is JsonValue uv)
            libUrl = CombineBase(uv.GetValue<string>(), libPath);

        return new LibraryEntry
        {
            Name = name,
            ArtifactKey = ArtifactKeyOf(name, null),
            Path = libPath,
            Url = libUrl,
            FallbackUrls = libFallbacks,
            Sha1 = art?["sha1"]?.GetValue<string>() ?? lo["sha1"]?.GetValue<string>() ?? "",
            Size = art?["size"]?.GetValue<long>() ?? 0,
            IsNative = false,
            Rules = (JsonArray?)rules?.DeepClone()
        };
    }

    private static List<string> BuildFallbacks(string path)
    {
        var normalized = path.Replace('\\', '/');
        return new List<string>
        {
            "https://libraries.minecraft.net/" + normalized,
            "https://maven.minecraftforge.net/" + normalized,
            "https://maven.fabricmc.net/" + normalized,
            "https://bmclapi2.bangbang93.com/maven/" + normalized
        };
    }

    private static string CombineBase(string baseUrl, string path)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return "";
        if (baseUrl.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) return baseUrl;
        return baseUrl.TrimEnd('/') + "/" + path.Replace('\\', '/');
    }

    public static string MavenPath(string name, string? classifierOverride = null)
    {
        var ext = ".jar";
        var at = name.IndexOf('@');
        if (at >= 0)
        {
            ext = "." + name[(at + 1)..];
            name = name[..at];
        }
        var parts = name.Split(':');
        var group = parts.Length > 0 ? parts[0] : "unknown";
        var artifact = parts.Length > 1 ? parts[1] : "unknown";
        var version = parts.Length > 2 ? parts[2] : "0";
        var classifier = classifierOverride ?? (parts.Length > 3 ? parts[3] : null);
        var file = artifact + "-" + version + (string.IsNullOrEmpty(classifier) ? "" : "-" + classifier) + ext;
        return group.Replace('.', '/') + "/" + artifact + "/" + version + "/" + file;
    }

    private static string ArtifactKeyOf(string name, string? classifierOverride)
    {
        var at = name.IndexOf('@');
        if (at >= 0) name = name[..at];
        var parts = name.Split(':');
        var group = parts.Length > 0 ? parts[0] : "unknown";
        var artifact = parts.Length > 1 ? parts[1] : "unknown";
        var classifier = classifierOverride ?? (parts.Length > 3 ? parts[3] : null);
        return group + ":" + artifact + (string.IsNullOrEmpty(classifier) ? "" : ":" + classifier);
    }

    private static string VersionOf(string name)
    {
        var at = name.IndexOf('@');
        if (at >= 0) name = name[..at];
        var parts = name.Split(':');
        return parts.Length > 2 ? parts[2] : "";
    }

    private static int CompareVersions(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return 0;
        var pa = a.Split('.', '-', '+', '_');
        var pb = b.Split('.', '-', '+', '_');
        var len = Math.Max(pa.Length, pb.Length);
        for (var i = 0; i < len; i++)
        {
            var sa = i < pa.Length ? pa[i] : "0";
            var sb = i < pb.Length ? pb[i] : "0";
            var na = int.TryParse(sa, out var x);
            var nb = int.TryParse(sb, out var y);
            if (na && nb)
            {
                if (x != y) return x.CompareTo(y);
            }
            else
            {
                var c = string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
            }
        }
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static LoggingConfig? ParseLogging(JsonObject? logging)
    {
        if (logging?["client"] is not JsonObject client) return null;
        var file = client["file"] as JsonObject;
        return new LoggingConfig
        {
            Argument = client["argument"]?.GetValue<string>() ?? "",
            FileId = file?["id"]?.GetValue<string>() ?? "",
            FileUrl = file?["url"]?.GetValue<string>() ?? "",
            FileSha1 = file?["sha1"]?.GetValue<string>() ?? "",
            FileSize = file?["size"]?.GetValue<long>() ?? 0
        };
    }

    private static LoaderKind DetectLoader(string mainClass, List<(string Id, string Folder, JsonObject Root)> chain)
    {
        var libText = "";
        foreach (var c in chain)
        {
            if (c.Root["libraries"] is JsonArray arr)
                foreach (var lib in arr)
                    if (lib?["name"] is JsonValue nv)
                        libText += " " + nv.GetValue<string>();
        }
        var text = (mainClass + " " + libText + " " + string.Join(" ", chain.Select(c => c.Id + " " + c.Folder))).ToLowerInvariant();
        if (text.Contains("neoforge") || text.Contains("net.neoforged")) return LoaderKind.NeoForge;
        if (text.Contains("quilt")) return LoaderKind.Quilt;
        if (text.Contains("fabric") || text.Contains("knotclient")) return LoaderKind.Fabric;
        if (text.Contains("forge") || text.Contains("fml") || text.Contains("bootstraplauncher")) return LoaderKind.Forge;
        return LoaderKind.Vanilla;
    }

    private static string ExtractMcVersion(List<(string Id, string Folder, JsonObject Root)> chain)
    {
        foreach (var item in chain)
        {
            var m = Regex.Match(item.Id, @"^\d+\.\d+(\.\d+)?$");
            if (m.Success) return m.Value;
        }
        foreach (var item in chain)
        {
            var m = Regex.Match(item.Id, @"\d+\.\d+(\.\d+)?");
            if (m.Success) return m.Value;
        }
        return "";
    }

    public static int InferJavaMajor(string mcVersion, string fallbackId)
    {
        var text = string.IsNullOrEmpty(mcVersion) ? fallbackId : mcVersion;
        var m = Regex.Match(text, @"(\d+)\.(\d+)(\.(\d+))?");
        if (!m.Success) return 17;
        var major = int.Parse(m.Groups[1].Value);
        var minor = int.Parse(m.Groups[2].Value);
        if (major >= 26) return 21;
        if (major == 1)
        {
            if (minor <= 16) return 8;
            if (minor == 17) return 16;
            if (minor <= 20) return 17;
            return 21;
        }
        return 17;
    }
}
