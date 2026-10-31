using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace Vermilune.Core;

public sealed class ModrinthService
{
    private const string Base = "https://api.modrinth.com/v2";

    public async Task<List<ResourceProject>> SearchAsync(ResourceType type, string query, string? mcVersion,
        string? loader, int offset, CancellationToken ct)
    {
        var facets = new List<string> { $"[\"project_type:{TypeName(type)}\"]" };
        if (!string.IsNullOrWhiteSpace(mcVersion)) facets.Add($"[\"versions:{mcVersion}\"]");
        if (!string.IsNullOrWhiteSpace(loader) && type is ResourceType.Mod or ResourceType.Modpack)
            facets.Add($"[\"categories:{loader}\"]");
        var url = $"{Base}/search?limit=20&offset={Math.Max(0, offset)}&index=relevance" +
                  $"&query={Uri.EscapeDataString(query ?? "")}" +
                  $"&facets={Uri.EscapeDataString("[" + string.Join(",", facets) + "]")}";
        var json = await HttpService.GetStringAsync(url, ct);
        var root = JsonNode.Parse(json) as JsonObject;
        var result = new List<ResourceProject>();
        if (root?["hits"] is JsonArray hits)
        {
            foreach (var node in hits)
            {
                if (node is not JsonObject o) continue;
                var id = o["project_id"]?.GetValue<string>() ?? o["slug"]?.GetValue<string>() ?? "";
                if (string.IsNullOrEmpty(id)) continue;
                result.Add(new ResourceProject
                {
                    Source = ResourceSource.Modrinth,
                    Id = id,
                    Slug = o["slug"]?.GetValue<string>() ?? id,
                    Title = o["title"]?.GetValue<string>() ?? id,
                    Summary = o["description"]?.GetValue<string>() ?? "",
                    Author = o["author"]?.GetValue<string>() ?? "",
                    IconUrl = o["icon_url"]?.GetValue<string>() ?? "",
                    Downloads = o["downloads"]?.GetValue<long>() ?? 0,
                    Type = type,
                    WebsiteUrl = "https://modrinth.com/" + TypeName(type) + "/" + (o["slug"]?.GetValue<string>() ?? id)
                });
            }
        }
        return result;
    }

    public async Task<List<ResourceFileInfo>> GetFilesAsync(ResourceProject project, string? mcVersion,
        string? loader, CancellationToken ct)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(mcVersion)) query.Add("game_versions=" + Uri.EscapeDataString("[\"" + mcVersion + "\"]"));
        if (!string.IsNullOrWhiteSpace(loader) && project.Type is ResourceType.Mod or ResourceType.Modpack)
            query.Add("loaders=" + Uri.EscapeDataString("[\"" + loader + "\"]"));
        var url = $"{Base}/project/{Uri.EscapeDataString(project.Id)}/version" + (query.Count > 0 ? "?" + string.Join("&", query) : "");
        var json = await HttpService.GetStringAsync(url, ct);
        var arr = JsonNode.Parse(json) as JsonArray ?? new JsonArray();
        var list = new List<ResourceFileInfo>();
        foreach (var node in arr)
        {
            if (node is not JsonObject o) continue;
            var files = o["files"] as JsonArray ?? new JsonArray();
            JsonObject? primary = null;
            foreach (var f in files.OfType<JsonObject>())
            {
                if (f["primary"]?.GetValue<bool>() == true) { primary = f; break; }
            }
            primary ??= files.OfType<JsonObject>().FirstOrDefault();
            if (primary is null) continue;
            var deps = new List<ResourceDependency>();
            if (o["dependencies"] is JsonArray da)
            {
                foreach (var d in da.OfType<JsonObject>())
                    deps.Add(new ResourceDependency
                    {
                        ProjectId = d["project_id"]?.GetValue<string>() ?? "",
                        VersionId = d["version_id"]?.GetValue<string>() ?? "",
                        Kind = d["dependency_type"]?.GetValue<string>() ?? ""
                    });
            }
            list.Add(new ResourceFileInfo
            {
                Id = o["id"]?.GetValue<string>() ?? "",
                ProjectId = o["project_id"]?.GetValue<string>() ?? project.Id,
                Name = o["name"]?.GetValue<string>() ?? "",
                VersionNumber = o["version_number"]?.GetValue<string>() ?? "",
                FileName = primary["filename"]?.GetValue<string>() ?? "",
                Url = primary["url"]?.GetValue<string>() ?? "",
                Sha1 = primary["hashes"]?["sha1"]?.GetValue<string>() ?? "",
                Size = primary["size"]?.GetValue<long>() ?? 0,
                Published = DateTime.TryParse(o["date_published"]?.GetValue<string>(), out var dt) ? dt : DateTime.MinValue,
                GameVersions = (o["game_versions"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList() ?? new(),
                Loaders = (o["loaders"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList() ?? new(),
                Dependencies = deps
            });
        }
        return list.OrderByDescending(x => x.Published).ToList();
    }

    public async Task<string?> InstallFabricApiAsync(string mcVersion, string gameDir,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        try
        {
            var folder = Path.Combine(gameDir, "mods");
            Directory.CreateDirectory(folder);
            var existing = Directory.EnumerateFiles(folder, "fabric-api*.jar").FirstOrDefault();
            if (existing is not null) return Path.GetFileName(existing);

            progress?.Report(new DownloadProgress { Stage = "匹配 Fabric API", Detail = mcVersion });
            var project = new ResourceProject
            {
                Source = ResourceSource.Modrinth,
                Id = "fabric-api",
                Slug = "fabric-api",
                Title = "Fabric API",
                Type = ResourceType.Mod
            };
            var files = await GetFilesAsync(project, mcVersion, "fabric", ct);
            var file = files.FirstOrDefault(f => f.GameVersions.Contains(mcVersion) && f.Loaders.Contains("fabric"))
                       ?? files.FirstOrDefault();
            if (file is null || string.IsNullOrWhiteSpace(file.Url))
            {
                Log.Warn("没有找到与 Minecraft " + mcVersion + " 匹配的 Fabric API。");
                return null;
            }
            var target = Path.Combine(folder, Path.GetFileName(file.FileName));
            progress?.Report(new DownloadProgress { Stage = "下载 Fabric API", Detail = file.FileName });
            await HttpService.DownloadFileAsync(file.Url, target, file.Sha1, null, ct);
            return Path.GetFileName(target);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("Fabric API 自动安装失败：" + ex.Message);
            return null;
        }
    }

    private async Task<ResourceFileInfo?> GetFileByIdAsync(string versionId, CancellationToken ct)
    {
        try
        {
            var json = await HttpService.GetStringAsync($"{Base}/version/{Uri.EscapeDataString(versionId)}", ct);
            var o = JsonNode.Parse(json) as JsonObject;
            if (o is null) return null;
            var projectId = o["project_id"]?.GetValue<string>() ?? "";
            var files = o["files"] as JsonArray ?? new JsonArray();
            var primary = files.OfType<JsonObject>().FirstOrDefault(f => f["primary"]?.GetValue<bool>() == true)
                          ?? files.OfType<JsonObject>().FirstOrDefault();
            if (primary is null) return null;
            return new ResourceFileInfo
            {
                Id = versionId,
                ProjectId = projectId,
                Name = o["name"]?.GetValue<string>() ?? "",
                VersionNumber = o["version_number"]?.GetValue<string>() ?? "",
                FileName = primary["filename"]?.GetValue<string>() ?? "",
                Url = primary["url"]?.GetValue<string>() ?? "",
                Sha1 = primary["hashes"]?["sha1"]?.GetValue<string>() ?? "",
                Size = primary["size"]?.GetValue<long>() ?? 0,
                GameVersions = (o["game_versions"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList() ?? new(),
                Loaders = (o["loaders"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList() ?? new()
            };
        }
        catch { return null; }
    }

    public async Task<ResourceInstallResult> InstallModAsync(ResourceProject project, ResourceFileInfo file,
        VersionInfo target, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var instance = LauncherApp.I.Versions.GetInstance(target.Id);
        var gameDir = LauncherApp.I.Versions.GetGameDir(target.Id, instance, LauncherApp.I.Settings.VersionIsolation);
        var folder = Path.Combine(gameDir, "mods");
        Directory.CreateDirectory(folder);
        var installed = new List<string>();
        var seenProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { project.Id };
        await InstallFileRecursiveAsync(file, folder, seenProjects, progress, ct, installed);
        progress?.Report(new DownloadProgress { Stage = "安装完成", Current = 1, Total = 1, Detail = file.FileName });
        return new ResourceInstallResult { Id = target.Id, Name = file.FileName, Path = folder, Files = installed.Count };
    }

    public async Task<ResourceInstallResult> InstallResourceAsync(ResourceProject project, ResourceFileInfo file,
        VersionInfo target, ResourceType type, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var instance = LauncherApp.I.Versions.GetInstance(target.Id);
        var gameDir = LauncherApp.I.Versions.GetGameDir(target.Id, instance, LauncherApp.I.Settings.VersionIsolation);
        var folder = type == ResourceType.Shader ? Path.Combine(gameDir, "shaderpacks") : Path.Combine(gameDir, "resourcepacks");
        Directory.CreateDirectory(folder);
        var targetPath = Path.Combine(folder, Path.GetFileName(file.FileName));
        progress?.Report(new DownloadProgress { Stage = "下载资源", Detail = file.FileName });
        await HttpService.DownloadFileAsync(file.Url, targetPath, file.Sha1,
            new Progress<(long Done, long Total)>(p => progress?.Report(new DownloadProgress
            {
                Stage = "下载资源", Current = p.Done, Total = p.Total, Detail = file.FileName
            })), ct);
        return new ResourceInstallResult { Id = target.Id, Name = file.FileName, Path = targetPath, Files = 1 };
    }

    private async Task InstallFileRecursiveAsync(ResourceFileInfo file, string folder, HashSet<string> seenProjects,
        IProgress<DownloadProgress>? progress, CancellationToken ct, List<string> installed, int depth = 0)
    {
        if (depth > 5 || string.IsNullOrWhiteSpace(file.Url)) return;
        var targetPath = Path.Combine(folder, Path.GetFileName(file.FileName));
        if (!File.Exists(targetPath))
        {
            progress?.Report(new DownloadProgress { Stage = "下载 Mod", Detail = file.FileName });
            await HttpService.DownloadFileAsync(file.Url, targetPath, file.Sha1, null, ct);
        }
        installed.Add(file.FileName);

        foreach (var dep in file.Dependencies.Where(d => d.Kind == "required"))
        {
            string projectKey = string.IsNullOrEmpty(dep.ProjectId) ? dep.VersionId : dep.ProjectId;
            if (string.IsNullOrEmpty(projectKey) || !seenProjects.Add(projectKey)) continue;
            ResourceFileInfo? depFile = null;
            if (!string.IsNullOrEmpty(dep.VersionId))
                depFile = await GetFileByIdAsync(dep.VersionId, ct);
            if (depFile is null && !string.IsNullOrEmpty(dep.ProjectId))
            {
                var dummy = new ResourceProject { Id = dep.ProjectId, Type = ResourceType.Mod };
                var versions = await GetFilesAsync(dummy, null, null, ct);
                depFile = versions.FirstOrDefault();
            }
            if (depFile is not null)
                await InstallFileRecursiveAsync(depFile, folder, seenProjects, progress, ct, installed, depth + 1);
        }
    }

    public async Task<ResourceInstallResult> InstallModpackAsync(ResourceProject project, ResourceFileInfo file,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var dir = Path.Combine(AppPaths.CacheDir, "modpacks");
        Directory.CreateDirectory(dir);
        var archive = Path.Combine(dir, AppPaths.SafeKey(project.Slug + "-" + file.FileName));
        if (!File.Exists(archive) || new FileInfo(archive).Length < 1024)
        {
            progress?.Report(new DownloadProgress { Stage = "下载整合包", Detail = file.FileName });
            await HttpService.DownloadFileAsync(file.Url, archive, file.Sha1, null, ct);
        }

        using var zip = ZipFile.OpenRead(archive);
        var indexEntry = zip.GetEntry("modrinth.index.json")
                         ?? throw new InvalidOperationException("不是有效的 Modrinth 整合包（缺少 modrinth.index.json）。");
        string indexText;
        using (var reader = new StreamReader(indexEntry.Open()))
            indexText = await reader.ReadToEndAsync(ct);
        var index = JsonNode.Parse(indexText) as JsonObject ?? throw new InvalidOperationException("整合包索引解析失败。");
        var name = index["name"]?.GetValue<string>() ?? project.Title;
        var mcVersion = index["dependencies"]?["minecraft"]?.GetValue<string>()
                        ?? throw new InvalidOperationException("整合包没有声明 Minecraft 版本。");
        var deps = index["dependencies"] as JsonObject ?? new JsonObject();
        var fabric = deps["fabric-loader"]?.GetValue<string>();
        var forge = deps["forge"]?.GetValue<string>();
        var quilt = deps["quilt-loader"]?.GetValue<string>();
        var neo = deps["neoforge"]?.GetValue<string>();

        progress?.Report(new DownloadProgress { Stage = "准备加载器", Detail = mcVersion });
        string loaderId;
        if (!string.IsNullOrEmpty(fabric))
            loaderId = await ModpackInstaller.EnsureFabricLoaderAsync(mcVersion, fabric, progress, ct);
        else if (!string.IsNullOrEmpty(forge))
            loaderId = await ModpackInstaller.EnsureForgeAsync(mcVersion, forge, progress, ct);
        else if (!string.IsNullOrEmpty(neo))
            throw new InvalidOperationException("暂不支持一键安装 NeoForge 整合包，请先手动安装对应 NeoForge，再安装整合包文件。");
        else if (!string.IsNullOrEmpty(quilt))
            throw new InvalidOperationException("暂不支持一键安装 Quilt 整合包。");
        else
            throw new InvalidOperationException("整合包没有声明受支持的加载器。");

        var id = ModpackInstaller.UniqueInstanceId(name);
        var gameDir = ModpackInstaller.CreateVersion(LauncherApp.I.MinecraftDir, id, loaderId, mcVersion, name);
        await ModpackInstaller.SaveIconAsync(project.IconUrl, gameDir, ct);
        progress?.Report(new DownloadProgress { Stage = "创建实例", Detail = id });

        var packFiles = new List<PackFile>();
        if (index["files"] is JsonArray files)
        {
            foreach (var node in files.OfType<JsonObject>())
            {
                var env = node["env"] as JsonObject;
                var client = env?["client"]?.GetValue<string>() ?? "required";
                var downloads = node["downloads"] as JsonArray;
                var url = downloads?.FirstOrDefault()?.GetValue<string>() ?? "";
                if (string.IsNullOrEmpty(url)) continue;
                packFiles.Add(new PackFile
                {
                    Path = node["path"]?.GetValue<string>() ?? "",
                    Url = url,
                    Sha1 = node["hashes"]?["sha1"]?.GetValue<string>() ?? "",
                    Size = node["fileSize"]?.GetValue<long>() ?? 0,
                    ClientSupported = client != "unsupported"
                });
            }
        }
        var count = await ModpackInstaller.DownloadFilesAsync(packFiles, gameDir, progress, ct);
        var overrides = ModpackInstaller.ExtractOverrides(zip, gameDir, "overrides", "client-overrides");
        progress?.Report(new DownloadProgress { Stage = "整合包安装完成", Current = 1, Total = 1, Detail = name });
        Log.Info($"Modrinth 整合包安装完成：{name} → {id}，文件 {count}，覆盖 {overrides}");
        return new ResourceInstallResult { Id = id, Name = name, Path = gameDir, Files = count + overrides };
    }

    public async Task<ResourceInstallResult> InstallLocalModpackAsync(string archive, string? fallbackName,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        await using var stream = File.OpenRead(archive);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var indexEntry = zip.GetEntry("modrinth.index.json")
                         ?? throw new InvalidOperationException("不是有效的 Modrinth 整合包（缺少 modrinth.index.json）。");
        string indexText;
        using (var reader = new StreamReader(indexEntry.Open()))
            indexText = await reader.ReadToEndAsync(ct);
        var index = JsonNode.Parse(indexText) as JsonObject ?? throw new InvalidOperationException("整合包索引解析失败。");
        var name = index["name"]?.GetValue<string>() ?? fallbackName ?? Path.GetFileNameWithoutExtension(archive);
        var mcVersion = index["dependencies"]?["minecraft"]?.GetValue<string>()
                        ?? throw new InvalidOperationException("整合包没有声明 Minecraft 版本。");
        var deps = index["dependencies"] as JsonObject ?? new JsonObject();
        var fabric = deps["fabric-loader"]?.GetValue<string>();
        var forge = deps["forge"]?.GetValue<string>();
        var neo = deps["neoforge"]?.GetValue<string>();
        var quilt = deps["quilt-loader"]?.GetValue<string>();

        progress?.Report(new DownloadProgress { Stage = "准备加载器", Detail = mcVersion });
        string loaderId;
        if (!string.IsNullOrEmpty(fabric))
            loaderId = await ModpackInstaller.EnsureFabricLoaderAsync(mcVersion, fabric, progress, ct);
        else if (!string.IsNullOrEmpty(forge))
            loaderId = await ModpackInstaller.EnsureForgeAsync(mcVersion, forge, progress, ct);
        else if (!string.IsNullOrEmpty(neo))
            throw new InvalidOperationException("暂不支持一键安装 NeoForge 整合包，请先手动安装对应 NeoForge，再安装整合包文件。");
        else if (!string.IsNullOrEmpty(quilt))
            throw new InvalidOperationException("暂不支持一键安装 Quilt 整合包。");
        else
            throw new InvalidOperationException("整合包没有声明受支持的加载器。");

        var id = ModpackInstaller.UniqueInstanceId(name);
        var gameDir = ModpackInstaller.CreateVersion(LauncherApp.I.MinecraftDir, id, loaderId, mcVersion, name);
        progress?.Report(new DownloadProgress { Stage = "创建实例", Detail = id });

        var packFiles = new List<PackFile>();
        if (index["files"] is JsonArray files)
        {
            foreach (var node in files.OfType<JsonObject>())
            {
                var env = node["env"] as JsonObject;
                var client = env?["client"]?.GetValue<string>() ?? "required";
                var downloads = node["downloads"] as JsonArray;
                var url = downloads?.FirstOrDefault()?.GetValue<string>() ?? "";
                if (string.IsNullOrEmpty(url)) continue;
                packFiles.Add(new PackFile
                {
                    Path = node["path"]?.GetValue<string>() ?? "",
                    Url = url,
                    Sha1 = node["hashes"]?["sha1"]?.GetValue<string>() ?? "",
                    Size = node["fileSize"]?.GetValue<long>() ?? 0,
                    ClientSupported = client != "unsupported"
                });
            }
        }
        var count = await ModpackInstaller.DownloadFilesAsync(packFiles, gameDir, progress, ct);
        var overrides = ModpackInstaller.ExtractOverrides(zip, gameDir, "overrides", "client-overrides");
        progress?.Report(new DownloadProgress { Stage = "整合包安装完成", Current = 1, Total = 1, Detail = name });
        Log.Info($"本地 Modrinth 整合包安装完成：{name} → {id}，文件 {count}，覆盖 {overrides}");
        return new ResourceInstallResult { Id = id, Name = name, Path = gameDir, Files = count + overrides };
    }

    private static string TypeName(ResourceType type) => type switch
    {
        ResourceType.Modpack => "modpack",
        ResourceType.ResourcePack => "resourcepack",
        ResourceType.Shader => "shader",
        _ => "mod"
    };
}
