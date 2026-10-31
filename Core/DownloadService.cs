using System.IO;
using System.Text.Json.Nodes;

namespace Vermilune.Core;

public sealed class RemoteVersion
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = "";
    public string Url { get; init; } = "";
    public DateTime ReleaseTime { get; init; }
    public bool Installed { get; set; }
    public string DisplayType => Type switch
    {
        "release" => "正式版",
        "snapshot" => "快照",
        "old_beta" => "远古 Beta",
        "old_alpha" => "远古 Alpha",
        _ => Type
    };
    public string ReleaseDateText => ReleaseTime == DateTime.MinValue ? "" : ReleaseTime.ToLocalTime().ToString("yyyy.MM.dd");
}

public sealed class DownloadProgress
{
    public string Stage { get; init; } = "";
    public long Current { get; init; }
    public long Total { get; init; }
    public string Detail { get; init; } = "";
    public double Fraction => Total > 0 ? Math.Clamp((double)Current / Total, 0, 1) : 0;
}

public sealed class FabricLoaderInfo
{
    public string Version { get; init; } = "";
    public bool Stable { get; init; }
    public string Display => Version + (Stable ? " · 稳定版" : " · 测试版");
}

public sealed class DownloadService
{
    private static List<RemoteVersion>? _manifestCache;
    private static DateTime _manifestTime;
    private static readonly SemaphoreSlim ManifestLock = new(1, 1);

    public async Task<List<RemoteVersion>> GetVersionManifestAsync(bool force, CancellationToken ct)
    {
        await ManifestLock.WaitAsync(ct);
        try
        {
            if (!force && _manifestCache is not null && DateTime.UtcNow - _manifestTime < TimeSpan.FromMinutes(10))
                return _manifestCache.ToList();
            var urls = new List<string>();
            switch (HttpService.Source)
            {
                case "bmclapi":
                    urls.Add("https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json");
                    break;
                case "official":
                    urls.Add("https://launchermeta.mojang.com/mc/game/version_manifest_v2.json");
                    break;
                default:
                    urls.Add("https://launchermeta.mojang.com/mc/game/version_manifest_v2.json");
                    urls.Add("https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json");
                    break;
            }
            Exception? last = null;
            foreach (var url in urls)
            {
                try
                {
                    var json = await HttpService.GetStringAsync(url, ct);
                    var root = JsonNode.Parse(json) as JsonObject;
                    var list = new List<RemoteVersion>();
                    if (root?["versions"] is JsonArray arr)
                    {
                        foreach (var node in arr)
                        {
                            if (node is not JsonObject o) continue;
                            list.Add(new RemoteVersion
                            {
                                Id = o["id"]?.GetValue<string>() ?? "",
                                Type = o["type"]?.GetValue<string>() ?? "",
                                Url = o["url"]?.GetValue<string>() ?? "",
                                ReleaseTime = DateTime.TryParse(o["releaseTime"]?.GetValue<string>(), out var t) ? t : DateTime.MinValue
                            });
                        }
                    }
                    if (list.Count > 0)
                    {
                        _manifestCache = list;
                        _manifestTime = DateTime.UtcNow;
                        Log.Info($"版本清单加载完成，共 {list.Count} 个版本（{url}）");
                        return list.ToList();
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { last = ex; }
            }
            throw new InvalidOperationException("无法获取 Minecraft 版本清单：" + (last?.Message ?? "未知错误"));
        }
        finally { ManifestLock.Release(); }
    }

    public async Task EnsureVanillaAsync(string mcVersion, string mcDir,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (File.Exists(Path.Combine(mcDir, "versions", mcVersion, mcVersion + ".json"))) return;
        var manifest = await GetVersionManifestAsync(false, ct);
        var remote = manifest.FirstOrDefault(v => v.Id == mcVersion)
                     ?? throw new InvalidOperationException("版本清单中没有 Minecraft " + mcVersion);
        await InstallVanillaAsync(remote, mcDir, progress, ct);
    }

    public async Task InstallVanillaAsync(RemoteVersion remote, string mcDir,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(remote.Url)) throw new InvalidOperationException("该版本没有可用的 JSON 地址。");
        var versionDir = Path.Combine(mcDir, "versions", remote.Id);
        Directory.CreateDirectory(versionDir);
        progress?.Report(new DownloadProgress { Stage = "拉取版本信息", Current = 0, Total = 1, Detail = remote.Id });
        var json = await HttpService.GetStringAsync(remote.Url, ct);
        var jsonPath = Path.Combine(versionDir, remote.Id + ".json");
        await File.WriteAllTextAsync(jsonPath, json, ct);

        var resolver = new VersionResolver(mcDir);
        var resolved = resolver.Resolve(remote.Id) ?? throw new InvalidOperationException("版本 JSON 解析失败。");
        var features = new LaunchFeatures();

        // 客户端 jar
        if (!File.Exists(resolved.ClientJarPath))
        {
            if (string.IsNullOrEmpty(resolved.ClientJarUrl))
                throw new InvalidOperationException("该版本缺少客户端下载地址。");
            progress?.Report(new DownloadProgress { Stage = "下载客户端", Current = 0, Total = resolved.ClientJarSha1.Length > 0 ? 100 : 0, Detail = "client.jar" });
            await HttpService.DownloadFileAsync(resolved.ClientJarUrl, resolved.ClientJarPath, resolved.ClientJarSha1,
                new Progress<(long Done, long Total)>(p => progress?.Report(new DownloadProgress
                {
                    Stage = "下载客户端", Current = p.Done, Total = p.Total, Detail = "client.jar"
                })), ct);
        }

        await EnsureLibrariesAsync(resolved, mcDir, progress, ct);
        await EnsureAssetsFullAsync(resolved, mcDir, progress, ct);

        var log = resolved.Logging;
        if (log is { FileUrl.Length: > 0 })
        {
            var target = Path.Combine(mcDir, "assets", "log_configs", log.FileId);
            if (!File.Exists(target))
            {
                try { await HttpService.DownloadFileAsync(log.FileUrl, target, log.FileSha1, null, ct); }
                catch (Exception ex) { Log.Warn("日志配置下载失败：" + ex.Message); }
            }
        }
        progress?.Report(new DownloadProgress { Stage = "安装完成", Current = 1, Total = 1, Detail = remote.Id });
        Log.Info($"安装原版完成：{remote.Id} -> {versionDir}");
    }

    public async Task<List<FabricLoaderInfo>> GetFabricLoadersAsync(string mcVersion, CancellationToken ct)
    {
        var json = await HttpService.GetStringAsync($"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(mcVersion)}", ct);
        var parsed = JsonNode.Parse(json);
        var root = parsed as JsonArray ?? (parsed as JsonObject)?["value"] as JsonArray ?? new JsonArray();
        var list = new List<FabricLoaderInfo>();
        foreach (var node in root)
        {
            if (node is not JsonObject o) continue;
            var loader = o["loader"] as JsonObject;
            var version = loader?["version"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(version)) continue;
            list.Add(new FabricLoaderInfo
            {
                Version = version,
                Stable = loader?["stable"]?.GetValue<bool>() ?? false
            });
        }
        return list;
    }

    public async Task<string> InstallFabricAsync(string mcVersion, string? loaderVersion, string mcDir,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new DownloadProgress { Stage = "拉取 Fabric 版本", Detail = mcVersion });
        var loaders = await GetFabricLoadersAsync(mcVersion, ct);
        if (loaders.Count == 0) throw new InvalidOperationException("无法获取 Fabric Loader 列表。");
        var loader = string.IsNullOrWhiteSpace(loaderVersion)
            ? loaders.FirstOrDefault(l => l.Stable) ?? loaders[0]
            : loaders.FirstOrDefault(l => l.Version == loaderVersion) ?? loaders[0];

        // 先确保原版基础版本存在
        if (!File.Exists(Path.Combine(mcDir, "versions", mcVersion, mcVersion + ".json")))
        {
            var manifest = await GetVersionManifestAsync(false, ct);
            var baseVer = manifest.FirstOrDefault(v => v.Id == mcVersion)
                ?? throw new InvalidOperationException("找不到基础版本：" + mcVersion);
            await InstallVanillaAsync(baseVer, mcDir, progress, ct);
        }

        progress?.Report(new DownloadProgress { Stage = "生成 Fabric 配置", Detail = $"Loader {loader.Version}" });
        var profileUrl = $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(mcVersion)}/{Uri.EscapeDataString(loader.Version)}/profile/json";
        var profileJson = await HttpService.GetStringAsync(profileUrl, ct);
        var root = JsonNode.Parse(profileJson) as JsonObject ?? throw new InvalidOperationException("Fabric 配置解析失败。");
        var id = root["id"]?.GetValue<string>() ?? $"fabric-loader-{loader.Version}-{mcVersion}";
        var dir = Path.Combine(mcDir, "versions", id);
        Directory.CreateDirectory(dir);
        var jsonPath = Path.Combine(dir, id + ".json");
        root["id"] = id;
        await File.WriteAllTextAsync(jsonPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), ct);

        var resolver = new VersionResolver(mcDir);
        var resolved = resolver.Resolve(id);
        if (resolved is not null)
            await EnsureLibrariesAsync(resolved, mcDir, progress, ct);

        progress?.Report(new DownloadProgress { Stage = "安装完成", Current = 1, Total = 1, Detail = id });
        Log.Info($"安装 Fabric 完成：{id}");
        return id;
    }

    public static async Task EnsureLibrariesAsync(ResolvedVersion resolved, string mcDir,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var libDir = Path.Combine(mcDir, "libraries");
        var features = new LaunchFeatures();
        var libs = resolved.Libraries
            .Where(l => RuleEvaluator.IsAllowed(l.Rules, features))
            .ToList();
        var need = libs
            .Select(l => (Lib: l, Path: Path.Combine(libDir, l.Path)))
            .Where(x => !File.Exists(x.Path))
            .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        if (need.Count == 0) return;

        var done = 0;
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        var semaphore = new SemaphoreSlim(12);
        progress?.Report(new DownloadProgress { Stage = "下载运行库", Current = 0, Total = need.Count, Detail = $"{need.Count} 个文件" });
        var tasks = need.Select(async item =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var urls = item.Lib.AllUrls().ToList();
                if (urls.Count == 0) { errors.Add(item.Lib.Name + " 缺少下载地址"); return; }
                await HttpService.DownloadFileAsync(urls, item.Path, item.Lib.Sha1, null, ct, 3);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add(item.Lib.Name + ": " + ex.Message); }
            finally
            {
                var d = Interlocked.Increment(ref done);
                semaphore.Release();
                if (d % 2 == 0 || d == need.Count)
                    progress?.Report(new DownloadProgress { Stage = "下载运行库", Current = d, Total = need.Count, Detail = $"{d}/{need.Count}" });
            }
        });
        await Task.WhenAll(tasks);
        if (!errors.IsEmpty)
            throw new InvalidOperationException("部分运行库下载失败：\n" + string.Join("\n", errors.Take(8)));
    }

    public static async Task EnsureAssetsFullAsync(ResolvedVersion resolved, string mcDir,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(resolved.AssetIndexPath)) return;
        await AssetService.EnsureIndexAsync(resolved, mcDir, null, ct);
        if (!File.Exists(resolved.AssetIndexPath)) return;
        var index = AssetIndex.Parse(await File.ReadAllTextAsync(resolved.AssetIndexPath, ct));
        var assetsDir = Path.Combine(mcDir, "assets");
        var objects = index.Objects.Values
            .Where(o => !string.IsNullOrEmpty(o.Hash) && o.Hash.Length > 2)
            .GroupBy(o => o.Hash)
            .Select(g => g.First())
            .ToList();
        if (objects.Count == 0) return;
        var done = 0;
        var failed = 0;
        var semaphore = new SemaphoreSlim(20);
        progress?.Report(new DownloadProgress { Stage = "下载游戏资源", Current = 0, Total = objects.Count, Detail = $"{objects.Count} 个资源" });
        var tasks = objects.Select(async obj =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var path = AssetService.ObjectPath(assetsDir, obj.Hash);
                if (File.Exists(path) && (obj.Size <= 0 || new FileInfo(path).Length == obj.Size)) return;
                var url = $"https://resources.download.minecraft.net/{obj.Hash[..2]}/{obj.Hash}";
                await HttpService.DownloadFileAsync(url, path, obj.Hash, null, ct, 2);
            }
            catch (OperationCanceledException) { throw; }
            catch { Interlocked.Increment(ref failed); }
            finally
            {
                var d = Interlocked.Increment(ref done);
                semaphore.Release();
                if (d % 8 == 0 || d == objects.Count)
                    progress?.Report(new DownloadProgress { Stage = "下载游戏资源", Current = d, Total = objects.Count, Detail = $"{d}/{objects.Count}" });
            }
        });
        await Task.WhenAll(tasks);

        if (index.Virtual || index.MapToResources)
        {
            var virtualDir = Path.Combine(assetsDir, "virtual", resolved.AssetsId);
            foreach (var kv in index.Objects)
            {
                var src = AssetService.ObjectPath(assetsDir, kv.Value.Hash);
                if (!File.Exists(src)) continue;
                var target = Path.Combine(virtualDir, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(target)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                try { File.Copy(src, target, true); } catch { }
            }
        }
        if (failed > 0) Log.Warn($"资源下载有 {failed} 个失败，启动时会再次补全。");
    }
}
