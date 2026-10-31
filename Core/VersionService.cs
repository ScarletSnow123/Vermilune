using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vermilune.Core;

public sealed class VersionService
{
    private readonly object _lock = new();
    private List<VersionInfo> _versions = new();

    public string MinecraftDir { get; private set; } = "";

    public VersionService(string mcDir) => SetMinecraftDir(mcDir);

    public void SetMinecraftDir(string dir)
    {
        MinecraftDir = dir;
        _resolver = new VersionResolver(dir);
    }

    private VersionResolver _resolver = null!;

    public IReadOnlyList<VersionInfo> Versions
    {
        get { lock (_lock) return _versions.ToList(); }
    }

    public string VersionsDir => Path.Combine(MinecraftDir, "versions");

    public List<VersionInfo> Refresh()
    {
        var result = new List<VersionInfo>();
        try
        {
            if (!Directory.Exists(VersionsDir)) return result;
            HideInheritedVanillaBases();
            foreach (var dir in Directory.EnumerateDirectories(VersionsDir))
            {
                var folder = Path.GetFileName(dir);
                // Fabric / Forge 自动补全的原版基础版本会写入这个标记，不在版本列表里重复显示。
                if (File.Exists(Path.Combine(dir, ".vermilune-hidden"))) continue;
                var jsonPath = _resolver.FindJsonPath(folder);
                if (string.IsNullOrEmpty(jsonPath)) continue;
                try
                {
                    var resolved = _resolver.Resolve(folder);
                    var modsDir = Path.Combine(dir, "mods");
                    var modCount = Directory.Exists(modsDir)
                        ? Directory.EnumerateFiles(modsDir)
                            .Count(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                                        f.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
                        : 0;
                    var info = new VersionInfo
                    {
                        Id = folder,
                        Folder = dir,
                        JsonPath = jsonPath,
                        Type = resolved?.Type ?? "release",
                        Loader = resolved?.Loader ?? LoaderKind.Unknown,
                        LoaderName = LoaderDisplay(resolved?.Loader ?? LoaderKind.Unknown),
                        MinecraftVersion = resolved?.MinecraftVersion ?? "",
                        MainClass = resolved?.MainClass ?? "",
                        JavaMajor = resolved?.JavaMajor ?? 17,
                        LastWriteUtc = File.GetLastWriteTimeUtc(jsonPath),
                        ModCount = modCount
                    };
                    var inst = InstanceStore.Load(folder);
                    info.DisplayName = string.IsNullOrWhiteSpace(inst.DisplayName) ? folder : inst.DisplayName;
                    result.Add(info);
                }
                catch (Exception ex)
                {
                    Log.Warn($"跳过无法解析的版本 {folder}：{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "扫描版本目录失败");
        }

        result = result
            .OrderByDescending(v => v.LastWriteUtc)
            .ThenBy(v => v.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        lock (_lock) _versions = result;
        return result;
    }

    private void HideInheritedVanillaBases()
    {
        try
        {
            var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Directory.EnumerateDirectories(VersionsDir))
            {
                var id = Path.GetFileName(dir);
                var json = _resolver.FindJsonPath(id);
                if (string.IsNullOrEmpty(json)) continue;
                try
                {
                    var root = JsonNode.Parse(File.ReadAllText(json)) as JsonObject;
                    var parent = root?["inheritsFrom"]?.GetValue<string>() ?? "";
                    if (!string.IsNullOrEmpty(parent)) parents.Add(parent);
                }
                catch { }
            }

            foreach (var parent in parents)
            {
                var dir = Path.Combine(VersionsDir, parent);
                if (!Directory.Exists(dir)) continue;
                if (File.Exists(Path.Combine(dir, ".vermilune-visible"))) continue;
                if (File.Exists(Path.Combine(dir, ".vermilune-hidden"))) continue;
                var resolved = _resolver.Resolve(parent);
                if (resolved is null || resolved.Loader != LoaderKind.Vanilla) continue;
                var mods = Path.Combine(dir, "mods");
                if (Directory.Exists(mods) && Directory.EnumerateFiles(mods).Any()) continue;
                try { File.WriteAllText(Path.Combine(dir, ".vermilune-hidden"), "auto hidden vanilla base"); }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("自动隐藏原版基础版本失败：" + ex.Message);
        }
    }

    public VersionInfo? Find(string id)
    {
        lock (_lock)
            return _versions.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public ResolvedVersion? Resolve(string id) => _resolver.Resolve(id);

    public InstanceSettings GetInstance(string id) => InstanceStore.Load(id);

    public string GetGameDir(string id, InstanceSettings? instance = null, bool? globalIsolation = null)
    {
        instance ??= InstanceStore.Load(id);
        var isolate = instance.Isolate ?? globalIsolation ?? true;
        var versionDir = Path.Combine(MinecraftDir, "versions", id);
        return isolate ? versionDir : MinecraftDir;
    }

    public string GetModsDir(string id, InstanceSettings? instance = null, bool? globalIsolation = null)
        => Path.Combine(GetGameDir(id, instance, globalIsolation), "mods");

    public string GetNativesDir(string id) => Path.Combine(MinecraftDir, "versions", id, id + "-natives");

    public static string LoaderDisplay(LoaderKind kind) => kind switch
    {
        LoaderKind.Forge => "Forge",
        LoaderKind.NeoForge => "NeoForge",
        LoaderKind.Fabric => "Fabric",
        LoaderKind.Quilt => "Quilt",
        LoaderKind.Unknown => "未知",
        _ => "原版"
    };

    public static bool IsValidName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Trim() != name) return false;
        if (name.Length > 80) return false;
        if (name is "." or "..") return false;
        return name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    public bool VersionExists(string name) =>
        Directory.Exists(Path.Combine(VersionsDir, name)) ||
        File.Exists(Path.Combine(VersionsDir, name + ".json"));

    /// <summary>重命名版本目录与 JSON，并修复指向它的 inheritsFrom。</summary>
    public (bool Ok, string Error) Rename(string oldId, string newId)
    {
        if (!IsValidName(newId)) return (false, "名称包含非法字符，或为空。");
        if (string.Equals(oldId, newId, StringComparison.Ordinal)) return (true, "");
        if (VersionExists(newId)) return (false, "已存在同名版本。");
        var oldDir = Path.Combine(VersionsDir, oldId);
        var newDir = Path.Combine(VersionsDir, newId);
        if (!Directory.Exists(oldDir)) return (false, "原版本目录不存在。");

        try
        {
            var oldJson = FindJson(oldDir, oldId);
            Directory.Move(oldDir, newDir);
            // 客户端 jar 与 natives 目录通常以版本名命名，重命名文件夹后必须同步，
            // 否则解析器会去下载一个新的原版 jar，导致整合包直接损坏。
            try
            {
                var oldJar = Path.Combine(newDir, oldId + ".jar");
                var newJar = Path.Combine(newDir, newId + ".jar");
                if (File.Exists(oldJar) && !File.Exists(newJar)) File.Move(oldJar, newJar);
                var oldNatives = Path.Combine(newDir, oldId + "-natives");
                var newNatives = Path.Combine(newDir, newId + "-natives");
                if (Directory.Exists(oldNatives) && !Directory.Exists(newNatives)) Directory.Move(oldNatives, newNatives);
            }
            catch (Exception ex) { Log.Warn("重命名版本附属文件失败：" + ex.Message); }

            if (!string.IsNullOrEmpty(oldJson))
            {
                var fileName = Path.GetFileName(oldJson);
                var target = Path.Combine(newDir, fileName);
                var newJson = Path.Combine(newDir, newId + ".json");
                if (File.Exists(target) && !string.Equals(target, newJson, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Move(target, newJson, true); } catch { }
                }
                else if (!File.Exists(newJson) && File.Exists(target))
                {
                    File.Move(target, newJson);
                }
                if (File.Exists(newJson))
                {
                    var root = JsonNode.Parse(File.ReadAllText(newJson)) as JsonObject;
                    if (root is not null && root["id"] is not null)
                    {
                        root["id"] = newId;
                        File.WriteAllText(newJson, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    }
                }
            }

            // 更新其他版本中的 inheritsFrom
            foreach (var dir in Directory.EnumerateDirectories(VersionsDir))
            {
                var json = FindJson(dir, Path.GetFileName(dir));
                if (string.IsNullOrEmpty(json)) continue;
                try
                {
                    var text = File.ReadAllText(json);
                    if (!text.Contains("\"" + oldId + "\"", StringComparison.Ordinal)) continue;
                    var root = JsonNode.Parse(text) as JsonObject;
                    if (root?["inheritsFrom"] is JsonValue iv && iv.GetValue<string>() == oldId)
                    {
                        root["inheritsFrom"] = newId;
                        File.WriteAllText(json, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    }
                }
                catch { }
            }

            var inst = InstanceStore.Load(oldId);
            InstanceStore.Save(newId, inst);
            InstanceStore.Delete(oldId);
            return (true, "");
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"重命名版本失败 {oldId} -> {newId}");
            return (false, ex.Message);
        }
    }

    public (bool Ok, string Error) Delete(string id, bool recycle = true)
    {
        var dir = Path.Combine(VersionsDir, id);
        if (!Directory.Exists(dir)) return (false, "版本目录不存在。");
        try
        {
            if (recycle)
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(dir,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            else
            {
                Directory.Delete(dir, true);
            }
            InstanceStore.Delete(id);
            return (true, "");
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"删除版本失败 {id}");
            return (false, ex.Message);
        }
    }

    public static string? FindJson(string dir, string folder)
    {
        var preferred = Path.Combine(dir, folder + ".json");
        if (File.Exists(preferred)) return preferred;
        return Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly).FirstOrDefault();
    }
}
