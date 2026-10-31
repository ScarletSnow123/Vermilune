using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vermilune.Core;

public sealed class AssetObject
{
    public string Hash { get; init; } = "";
    public long Size { get; init; }
}

public sealed class AssetIndex
{
    public bool Virtual { get; init; }
    public bool MapToResources { get; init; }
    public Dictionary<string, AssetObject> Objects { get; init; } = new(StringComparer.Ordinal);

    public static AssetIndex Parse(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject;
        var index = new AssetIndex
        {
            Virtual = root?["virtual"]?.GetValue<bool>() ?? false,
            MapToResources = root?["map_to_resources"]?.GetValue<bool>() ?? false
        };
        if (root?["objects"] is JsonObject objects)
        {
            foreach (var kv in objects)
            {
                if (kv.Value is not JsonObject o) continue;
                index.Objects[kv.Key] = new AssetObject
                {
                    Hash = o["hash"]?.GetValue<string>() ?? "",
                    Size = o["size"]?.GetValue<long>() ?? 0
                };
            }
        }
        return index;
    }
}

public static class AssetService
{
    public static string ObjectPath(string assetsDir, string hash)
        => Path.Combine(assetsDir, "objects", hash[..2], hash);

    public static async Task EnsureIndexAsync(ResolvedVersion version, string mcDir, IProgress<string>? status,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(version.AssetIndexPath)) return;
        if (File.Exists(version.AssetIndexPath) && new FileInfo(version.AssetIndexPath).Length > 10) return;
        if (string.IsNullOrEmpty(version.AssetIndexUrl))
        {
            Log.Warn("版本缺少资源索引地址，跳过资源检查：" + version.Id);
            return;
        }
        status?.Report("下载资源索引…");
        var json = await HttpService.GetStringAsync(version.AssetIndexUrl, ct);
        Directory.CreateDirectory(Path.GetDirectoryName(version.AssetIndexPath)!);
        await File.WriteAllTextAsync(version.AssetIndexPath, json, ct);
    }

    /// <summary>只下载缺失的资源，避免每次启动全量校验。</summary>
    public static async Task<(int Downloaded, int Missing)> EnsureObjectsAsync(
        ResolvedVersion version, string mcDir, IProgress<(long Done, long Total, string Detail)>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(version.AssetIndexPath) || !File.Exists(version.AssetIndexPath))
            return (0, 0);
        var index = AssetIndex.Parse(await File.ReadAllTextAsync(version.AssetIndexPath, ct));
        var assetsDir = Path.Combine(mcDir, "assets");
        var missing = new List<AssetObject>();
        foreach (var obj in index.Objects.Values)
        {
            if (string.IsNullOrEmpty(obj.Hash) || obj.Hash.Length < 2) continue;
            var path = ObjectPath(assetsDir, obj.Hash);
            if (!File.Exists(path) || (obj.Size > 0 && new FileInfo(path).Length != obj.Size))
                missing.Add(obj);
        }
        if (missing.Count == 0) return (0, 0);

        statusReport(progress, 0, missing.Count, "补全游戏资源…");
        var done = 0;
        var semaphore = new SemaphoreSlim(16);
        var failed = 0;
        var tasks = missing.DistinctBy(o => o.Hash).Select(async obj =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var path = ObjectPath(assetsDir, obj.Hash);
                var url = $"https://resources.download.minecraft.net/{obj.Hash[..2]}/{obj.Hash}";
                await HttpService.DownloadFileAsync(url, path, obj.Hash, null, ct, 2);
            }
            catch (OperationCanceledException) { throw; }
            catch { Interlocked.Increment(ref failed); }
            finally
            {
                var d = Interlocked.Increment(ref done);
                if (d % 8 == 0 || d == missing.Count)
                    statusReport(progress, d, missing.Count, $"补全游戏资源 {d}/{missing.Count}");
                semaphore.Release();
            }
        });
        await Task.WhenAll(tasks);

        if (index.Virtual || index.MapToResources)
        {
            var virtualDir = Path.Combine(assetsDir, "virtual", version.AssetsId);
            foreach (var kv in index.Objects)
            {
                var src = ObjectPath(assetsDir, kv.Value.Hash);
                if (!File.Exists(src)) continue;
                var target = Path.Combine(virtualDir, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(target)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                try { File.Copy(src, target, true); } catch { }
            }
        }
        return (missing.Count - failed, failed);
    }

    private static void statusReport(IProgress<(long Done, long Total, string Detail)>? progress, long done, long total, string detail)
        => progress?.Report((done, total, detail));
}
