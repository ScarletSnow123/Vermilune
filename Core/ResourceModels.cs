using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace Vermilune.Core;

public enum ResourceType { Modpack, Mod, ResourcePack, Shader }
public enum ResourceSource { Modrinth, CurseForge }

public sealed class ResourceProject
{
    public ResourceSource Source { get; init; }
    public string Id { get; init; } = "";
    public string Slug { get; init; } = "";
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Author { get; init; } = "";
    public string IconUrl { get; init; } = "";
    public long Downloads { get; init; }
    public ResourceType Type { get; init; }
    public string WebsiteUrl { get; init; } = "";
    public string Initial => string.IsNullOrEmpty(Title) ? "?" : Title[..1].ToUpperInvariant();
    public string DownloadsText => Downloads >= 1_000_000 ? $"{Downloads / 1_000_000.0:0.#}M" : Downloads >= 1000 ? $"{Downloads / 1000.0:0.#}K" : Downloads.ToString();
}

public sealed class ResourceDependency
{
    public string ProjectId { get; init; } = "";
    public string VersionId { get; init; } = "";
    public string Kind { get; init; } = ""; // required / optional / embedded / incompatible
}

public sealed class ResourceFileInfo
{
    public string Id { get; init; } = "";
    public string ProjectId { get; init; } = "";
    public string Name { get; init; } = "";
    public string VersionNumber { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Url { get; init; } = "";
    public string Sha1 { get; init; } = "";
    public long Size { get; init; }
    public DateTime Published { get; init; }
    public List<string> GameVersions { get; init; } = new();
    public List<string> Loaders { get; init; } = new();
    public List<ResourceDependency> Dependencies { get; init; } = new();
    public string Display
    {
        get
        {
            var mc = GameVersions.Count == 0 ? "" : " · " + string.Join("/", GameVersions.Take(2));
            var loader = Loaders.Count == 0 ? "" : " · " + string.Join("/", Loaders);
            return (string.IsNullOrEmpty(VersionNumber) ? Name : VersionNumber) + mc + loader;
        }
    }
}

public sealed class ResourceInstallResult
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public int Files { get; init; }
}

public sealed class PackFile
{
    public string Path { get; init; } = "";
    public string Url { get; init; } = "";
    public string Sha1 { get; init; } = "";
    public long Size { get; init; }
    public bool ClientSupported { get; init; } = true;
}

public static class ModpackInstaller
{
    public static string SanitizeRelativePath(string path)
    {
        path = path.Replace('\\', '/').TrimStart('/');
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var safe = new List<string>();
        foreach (var part in parts)
        {
            if (part == "." || part == "..") continue;
            if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
            safe.Add(part);
        }
        return string.Join(Path.DirectorySeparatorChar, safe);
    }

    public static string UniqueInstanceId(string name)
    {
        var baseId = AppPaths.SafeKey(string.IsNullOrWhiteSpace(name) ? "Modpack" : name);
        var id = baseId;
        var n = 2;
        while (Directory.Exists(Path.Combine(LauncherApp.I.MinecraftDir, "versions", id)))
            id = baseId + " (" + n++ + ")";
        return id;
    }

    public static string CreateVersion(string mcDir, string id, string loaderId, string mcVersion, string displayName = "")
    {
        var dir = Path.Combine(mcDir, "versions", id);
        Directory.CreateDirectory(dir);
        var json = new JsonObject
        {
            ["id"] = id,
            ["inheritsFrom"] = loaderId,
            ["type"] = "release",
            ["clientVersion"] = mcVersion,
            ["time"] = DateTime.UtcNow.ToString("s"),
            ["releaseTime"] = DateTime.UtcNow.ToString("s")
        };
        var path = Path.Combine(dir, id + ".json");
        File.WriteAllText(path, json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        InstanceStore.Save(id, new InstanceSettings { Isolate = true, AutoJava = true, DisplayName = displayName });
        return dir;
    }

    public static async Task<int> DownloadFilesAsync(IEnumerable<PackFile> files, string gameDir,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var list = files.Where(f => f.ClientSupported && !string.IsNullOrWhiteSpace(f.Url)).ToList();
        if (list.Count == 0) return 0;
        var done = 0;
        var failed = 0;
        var semaphore = new SemaphoreSlim(8);
        progress?.Report(new DownloadProgress { Stage = "下载整合包文件", Current = 0, Total = list.Count, Detail = $"{list.Count} 个文件" });
        var tasks = list.Select(async f =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var rel = SanitizeRelativePath(f.Path);
                if (string.IsNullOrEmpty(rel)) return;
                var target = Path.Combine(gameDir, rel);
                if (File.Exists(target) && (f.Size <= 0 || new FileInfo(target).Length == f.Size)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await HttpService.DownloadFileAsync(f.Url, target, f.Sha1, null, ct, 3);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Interlocked.Increment(ref failed); Log.Warn("整合包文件下载失败：" + f.Path + " " + ex.Message); }
            finally
            {
                var d = Interlocked.Increment(ref done);
                semaphore.Release();
                if (d % 4 == 0 || d == list.Count)
                    progress?.Report(new DownloadProgress { Stage = "下载整合包文件", Current = d, Total = list.Count, Detail = $"{d}/{list.Count}" });
            }
        });
        await Task.WhenAll(tasks);
        if (failed > 0) Log.Warn($"整合包有 {failed} 个文件下载失败，启动时会提示/可重新安装。");
        return list.Count - failed;
    }

    public static int ExtractOverrides(ZipArchive zip, string gameDir, params string[] prefixes)
    {
        var count = 0;
        foreach (var entry in zip.Entries)
        {
            var prefix = prefixes.FirstOrDefault(p => entry.FullName.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase));
            if (prefix is null || entry.FullName.EndsWith('/')) continue;
            var rel = SanitizeRelativePath(entry.FullName[prefix.Length..]);
            if (string.IsNullOrEmpty(rel)) continue;
            var target = Path.Combine(gameDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
            count++;
        }
        return count;
    }

    public static async Task SaveIconAsync(string url, string gameDir, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            var png = Path.Combine(gameDir, "icon.png");
            if (File.Exists(png)) return;
            Directory.CreateDirectory(gameDir);
            await HttpService.DownloadFileAsync(url, png, "", null, ct, 2);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("整合包图标下载失败：" + ex.Message);
        }
    }

    public static async Task<string> EnsureFabricLoaderAsync(string mc, string loaderVersion, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var existing = Directory.Exists(Path.Combine(LauncherApp.I.MinecraftDir, "versions", $"fabric-loader-{loaderVersion}-{mc}"));
        if (existing) return $"fabric-loader-{loaderVersion}-{mc}";
        return await LauncherApp.I.Downloads.InstallFabricAsync(mc, loaderVersion, LauncherApp.I.MinecraftDir, progress, ct);
    }

    public static async Task<string> EnsureForgeAsync(string mc, string forgeVersion, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        await LauncherApp.I.Downloads.EnsureVanillaAsync(mc, LauncherApp.I.MinecraftDir, progress, ct);
        await LauncherApp.I.EnsureJavaAsync();
        var major = VersionResolver.InferJavaMajor(mc, "");
        var java = LauncherApp.I.Java.Pick(major, LauncherApp.I.Settings.JavaPath)
                   ?? throw new InvalidOperationException($"安装 Forge 需要 Java {major}。");
        return await LauncherApp.I.Forge.InstallAsync(mc, forgeVersion, LauncherApp.I.MinecraftDir, java.Path, progress, ct);
    }
}
