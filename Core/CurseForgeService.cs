using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace Vermilune.Core;

public sealed class CurseForgeService
{
    private const string Base = "https://api.curseforge.com/v1";
    private const int GameId = 432;

    public bool HasKey => !string.IsNullOrWhiteSpace(LauncherApp.I.Settings.CurseForgeApiKey);

    private static HttpRequestMessage Request(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("x-api-key", LauncherApp.I.Settings.CurseForgeApiKey.Trim());
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    private async Task<JsonObject> GetJsonAsync(string url, CancellationToken ct)
    {
        if (!HasKey) throw new InvalidOperationException("CurseForge 搜索需要 API Key。请到「设置 → 下载」填写。");
        using var request = Request(url);
        using var response = await HttpService.Client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"CurseForge 返回 {(int)response.StatusCode}：{text[..Math.Min(200, text.Length)]}");
        return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
    }

    public async Task<List<ResourceProject>> SearchAsync(ResourceType type, string query, string? mcVersion,
        string? loader, int offset, CancellationToken ct)
    {
        var classId = type switch
        {
            ResourceType.Modpack => 4471,
            ResourceType.ResourcePack => 12,
            ResourceType.Shader => 6552,
            _ => 6
        };
        var url = $"{Base}/mods/search?gameId={GameId}&classId={classId}&pageSize=20&index={Math.Max(0, offset)}" +
                  $"&searchFilter={Uri.EscapeDataString(query ?? "")}&sortField=2&sortOrder=desc";
        if (!string.IsNullOrWhiteSpace(mcVersion)) url += "&gameVersion=" + Uri.EscapeDataString(mcVersion);
        var loaderType = LoaderType(loader);
        if (loaderType > 0 && type is ResourceType.Mod or ResourceType.Modpack) url += "&modLoaderType=" + loaderType;
        var root = await GetJsonAsync(url, ct);
        var result = new List<ResourceProject>();
        if (root["data"] is JsonArray data)
        {
            foreach (var node in data.OfType<JsonObject>())
            {
                var id = node["id"]?.GetValue<int>() ?? 0;
                if (id <= 0) continue;
                var logo = node["logo"] as JsonObject;
                result.Add(new ResourceProject
                {
                    Source = ResourceSource.CurseForge,
                    Id = id.ToString(),
                    Slug = node["slug"]?.GetValue<string>() ?? id.ToString(),
                    Title = node["name"]?.GetValue<string>() ?? id.ToString(),
                    Summary = node["summary"]?.GetValue<string>() ?? "",
                    Author = (node["authors"] as JsonArray)?.FirstOrDefault()?["name"]?.GetValue<string>() ?? "",
                    IconUrl = logo?["thumbnailUrl"]?.GetValue<string>() ?? logo?["url"]?.GetValue<string>() ?? "",
                    Downloads = node["downloadCount"]?.GetValue<long>() ?? 0,
                    Type = type,
                    WebsiteUrl = node["links"]?["websiteUrl"]?.GetValue<string>() ?? ""
                });
            }
        }
        return result;
    }

    public async Task<List<ResourceFileInfo>> GetFilesAsync(ResourceProject project, string? mcVersion,
        string? loader, CancellationToken ct)
    {
        if (!int.TryParse(project.Id, out var modId)) return new();
        var result = new List<ResourceFileInfo>();
        var seen = new HashSet<int>();
        var loaderType = LoaderType(loader);
        for (var index = 0; index < 500; index += 50)
        {
            var url = $"{Base}/mods/{modId}/files?pageSize=50&index={index}";
            if (!string.IsNullOrWhiteSpace(mcVersion)) url += "&gameVersion=" + Uri.EscapeDataString(mcVersion);
            if (loaderType > 0 && project.Type is ResourceType.Mod or ResourceType.Modpack) url += "&modLoaderType=" + loaderType;
            var root = await GetJsonAsync(url, ct);
            if (root["data"] is not JsonArray data || data.Count == 0) break;
            var added = 0;
            foreach (var node in data.OfType<JsonObject>())
            {
                var id = node["id"]?.GetValue<int>() ?? 0;
                var fileName = node["fileName"]?.GetValue<string>() ?? "";
                if (id <= 0 || string.IsNullOrEmpty(fileName) || !seen.Add(id)) continue;
                var deps = new List<ResourceDependency>();
                if (node["dependencies"] is JsonArray da)
                {
                    foreach (var d in da.OfType<JsonObject>())
                        deps.Add(new ResourceDependency
                        {
                            ProjectId = d["modId"]?.GetValue<int>().ToString() ?? "",
                            VersionId = d["fileId"]?.GetValue<int>().ToString() ?? "",
                            Kind = (d["relationType"]?.GetValue<int>() ?? 3) == 3 ? "required" : "optional"
                        });
                }
                result.Add(new ResourceFileInfo
                {
                    Id = id.ToString(),
                    ProjectId = project.Id,
                    Name = node["displayName"]?.GetValue<string>() ?? fileName,
                    VersionNumber = node["displayName"]?.GetValue<string>() ?? "",
                    FileName = fileName,
                    Url = DownloadUrl(id, fileName, node["downloadUrl"]?.GetValue<string>()),
                    Sha1 = node["hashes"] is JsonArray hashes
                        ? hashes.OfType<JsonObject>().FirstOrDefault(x => x["algo"]?.GetValue<int>() == 1)?["value"]?.GetValue<string>() ?? ""
                        : "",
                    Size = node["fileLength"]?.GetValue<long>() ?? 0,
                    Published = DateTime.TryParse(node["fileDate"]?.GetValue<string>(), out var date) ? date : DateTime.MinValue,
                    GameVersions = (node["gameVersions"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList() ?? new(),
                    Loaders = new List<string>(),
                    Dependencies = deps
                });
                added++;
            }
            if (data.Count < 50 || added == 0) break;
        }
        return result.OrderByDescending(x => x.Published).ToList();
    }

    private async Task<ResourceFileInfo?> GetFileAsync(string modId, string fileId, CancellationToken ct)
    {
        var root = await GetJsonAsync($"{Base}/mods/{modId}/files/{fileId}", ct);
        if (root["data"] is not JsonObject o) return null;
        var fileName = o["fileName"]?.GetValue<string>() ?? "";
        var id = o["id"]?.GetValue<int>() ?? 0;
        if (id <= 0 || string.IsNullOrEmpty(fileName)) return null;
        var deps = new List<ResourceDependency>();
        if (o["dependencies"] is JsonArray da)
        {
            foreach (var d in da.OfType<JsonObject>())
                deps.Add(new ResourceDependency
                {
                    ProjectId = d["modId"]?.GetValue<int>().ToString() ?? "",
                    VersionId = d["fileId"]?.GetValue<int>().ToString() ?? "",
                    Kind = (d["relationType"]?.GetValue<int>() ?? 3) == 3 ? "required" : "optional"
                });
        }
        return new ResourceFileInfo
        {
            Id = id.ToString(),
            ProjectId = modId,
            Name = o["displayName"]?.GetValue<string>() ?? fileName,
            VersionNumber = o["displayName"]?.GetValue<string>() ?? "",
            FileName = fileName,
            Url = DownloadUrl(id, fileName, o["downloadUrl"]?.GetValue<string>()),
            Sha1 = o["hashes"] is JsonArray hashes
                ? hashes.OfType<JsonObject>().FirstOrDefault(x => x["algo"]?.GetValue<int>() == 1)?["value"]?.GetValue<string>() ?? ""
                : "",
            Size = o["fileLength"]?.GetValue<long>() ?? 0,
            Published = DateTime.TryParse(o["fileDate"]?.GetValue<string>(), out var date) ? date : DateTime.MinValue,
            GameVersions = (o["gameVersions"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList() ?? new(),
            Dependencies = deps
        };
    }

    public async Task<ResourceInstallResult> InstallModAsync(ResourceProject project, ResourceFileInfo file,
        VersionInfo target, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var instance = LauncherApp.I.Versions.GetInstance(target.Id);
        var gameDir = LauncherApp.I.Versions.GetGameDir(target.Id, instance, LauncherApp.I.Settings.VersionIsolation);
        var folder = Path.Combine(gameDir, "mods");
        Directory.CreateDirectory(folder);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { project.Id };
        var installed = new List<string>();
        await InstallRecursiveAsync(file, folder, seen, progress, ct, installed);
        return new ResourceInstallResult { Id = target.Id, Name = file.FileName, Path = folder, Files = installed.Count };
    }

    private async Task InstallRecursiveAsync(ResourceFileInfo file, string folder, HashSet<string> seen,
        IProgress<DownloadProgress>? progress, CancellationToken ct, List<string> installed, int depth = 0)
    {
        if (depth > 5 || string.IsNullOrWhiteSpace(file.Url)) return;
        var target = Path.Combine(folder, Path.GetFileName(file.FileName));
        if (!File.Exists(target))
        {
            progress?.Report(new DownloadProgress { Stage = "下载 Mod", Detail = file.FileName });
            await HttpService.DownloadFileAsync(file.Url, target, file.Sha1, null, ct);
        }
        installed.Add(file.FileName);
        foreach (var dep in file.Dependencies.Where(d => d.Kind == "required"))
        {
            if (string.IsNullOrEmpty(dep.ProjectId) || !seen.Add(dep.ProjectId)) continue;
            ResourceFileInfo? depFile = null;
            if (!string.IsNullOrEmpty(dep.VersionId))
                depFile = await GetFileAsync(dep.ProjectId, dep.VersionId, ct);
            if (depFile is null)
            {
                var dummy = new ResourceProject { Id = dep.ProjectId, Type = ResourceType.Mod };
                depFile = (await GetFilesAsync(dummy, null, null, ct)).FirstOrDefault();
            }
            if (depFile is not null)
                await InstallRecursiveAsync(depFile, folder, seen, progress, ct, installed, depth + 1);
        }
    }

    public async Task<ResourceInstallResult> InstallResourceAsync(ResourceProject project, ResourceFileInfo file,
        VersionInfo target, ResourceType type, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var instance = LauncherApp.I.Versions.GetInstance(target.Id);
        var gameDir = LauncherApp.I.Versions.GetGameDir(target.Id, instance, LauncherApp.I.Settings.VersionIsolation);
        var folder = type == ResourceType.Shader ? Path.Combine(gameDir, "shaderpacks") : Path.Combine(gameDir, "resourcepacks");
        Directory.CreateDirectory(folder);
        var targetPath = Path.Combine(folder, Path.GetFileName(file.FileName));
        await HttpService.DownloadFileAsync(file.Url, targetPath, file.Sha1, null, ct);
        return new ResourceInstallResult { Id = target.Id, Name = file.FileName, Path = targetPath, Files = 1 };
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
        var manifestEntry = zip.GetEntry("manifest.json")
                            ?? throw new InvalidOperationException("不是有效的 CurseForge 整合包（缺少 manifest.json）。");
        string manifestText;
        using (var reader = new StreamReader(manifestEntry.Open()))
            manifestText = await reader.ReadToEndAsync(ct);
        var manifest = JsonNode.Parse(manifestText) as JsonObject ?? throw new InvalidOperationException("整合包 manifest 解析失败。");
        var mc = manifest["minecraft"]?["version"]?.GetValue<string>()
                 ?? throw new InvalidOperationException("整合包没有声明 Minecraft 版本。");
        var name = manifest["name"]?.GetValue<string>() ?? project.Title;
        var loaderId = manifest["minecraft"]?["modLoaders"] as JsonArray;
        var primaryLoader = loaderId?.OfType<JsonObject>().FirstOrDefault(x => x["primary"]?.GetValue<bool>() == true)
                            ?? loaderId?.OfType<JsonObject>().FirstOrDefault();
        var loaderString = primaryLoader?["id"]?.GetValue<string>() ?? "";
        string installedLoader;
        if (loaderString.StartsWith("forge-", StringComparison.OrdinalIgnoreCase))
            installedLoader = await ModpackInstaller.EnsureForgeAsync(mc, loaderString["forge-".Length..], progress, ct);
        else if (loaderString.StartsWith("fabric-", StringComparison.OrdinalIgnoreCase))
            installedLoader = await ModpackInstaller.EnsureFabricLoaderAsync(mc, loaderString["fabric-".Length..], progress, ct);
        else if (loaderString.StartsWith("neoforge-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("暂不支持一键安装 NeoForge 整合包。");
        else if (loaderString.StartsWith("quilt-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("暂不支持一键安装 Quilt 整合包。");
        else
            throw new InvalidOperationException("整合包没有声明受支持的加载器：" + loaderString);

        var id = ModpackInstaller.UniqueInstanceId(name);
        var gameDir = ModpackInstaller.CreateVersion(LauncherApp.I.MinecraftDir, id, installedLoader, mc, name);
        await ModpackInstaller.SaveIconAsync(project.IconUrl, gameDir, ct);
        var packFiles = new List<PackFile>();
        if (manifest["files"] is JsonArray files)
        {
            foreach (var node in files.OfType<JsonObject>())
            {
                var projectId = node["projectID"]?.GetValue<int>().ToString() ?? "";
                var fileId = node["fileID"]?.GetValue<int>().ToString() ?? "";
                if (string.IsNullOrEmpty(projectId) || string.IsNullOrEmpty(fileId)) continue;
                try
                {
                    var cfFile = await GetFileAsync(projectId, fileId, ct);
                    if (cfFile is null) continue;
                    packFiles.Add(new PackFile
                    {
                        Path = "mods/" + cfFile.FileName,
                        Url = cfFile.Url,
                        Sha1 = cfFile.Sha1,
                        Size = cfFile.Size
                    });
                }
                catch (Exception ex) { Log.Warn("CurseForge 整合包文件解析失败：" + projectId + "/" + fileId + " " + ex.Message); }
            }
        }
        var downloaded = await ModpackInstaller.DownloadFilesAsync(packFiles, gameDir, progress, ct);
        var overridesFolder = manifest["overrides"]?.GetValue<string>() ?? "overrides";
        var overrides = ModpackInstaller.ExtractOverrides(zip, gameDir, overridesFolder);
        progress?.Report(new DownloadProgress { Stage = "整合包安装完成", Current = 1, Total = 1, Detail = name });
        return new ResourceInstallResult { Id = id, Name = name, Path = gameDir, Files = downloaded + overrides };
    }

    public async Task<ResourceInstallResult> InstallLocalModpackAsync(string archive, string? fallbackName,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(LauncherApp.I.Settings.CurseForgeApiKey))
            throw new InvalidOperationException("安装 CurseForge 整合包需要在设置中填写 CurseForge API Key。\n" +
                                                "也可以在资源页改用 Modrinth 整合包，或先解压手动放入实例。");
        await using var stream = File.OpenRead(archive);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var manifestEntry = zip.GetEntry("manifest.json")
                            ?? throw new InvalidOperationException("不是有效的 CurseForge 整合包（缺少 manifest.json）。");
        string manifestText;
        using (var reader = new StreamReader(manifestEntry.Open()))
            manifestText = await reader.ReadToEndAsync(ct);
        var manifest = JsonNode.Parse(manifestText) as JsonObject ?? throw new InvalidOperationException("整合包 manifest 解析失败。");
        var mc = manifest["minecraft"]?["version"]?.GetValue<string>()
                 ?? throw new InvalidOperationException("整合包没有声明 Minecraft 版本。");
        var name = manifest["name"]?.GetValue<string>() ?? fallbackName ?? Path.GetFileNameWithoutExtension(archive);
        var loaderId = manifest["minecraft"]?["modLoaders"] as JsonArray;
        var primaryLoader = loaderId?.OfType<JsonObject>().FirstOrDefault(x => x["primary"]?.GetValue<bool>() == true)
                            ?? loaderId?.OfType<JsonObject>().FirstOrDefault();
        var loaderString = primaryLoader?["id"]?.GetValue<string>() ?? "";
        string installedLoader;
        if (loaderString.StartsWith("forge-", StringComparison.OrdinalIgnoreCase))
            installedLoader = await ModpackInstaller.EnsureForgeAsync(mc, loaderString["forge-".Length..], progress, ct);
        else if (loaderString.StartsWith("fabric-", StringComparison.OrdinalIgnoreCase))
            installedLoader = await ModpackInstaller.EnsureFabricLoaderAsync(mc, loaderString["fabric-".Length..], progress, ct);
        else if (loaderString.StartsWith("neoforge-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("暂不支持一键安装 NeoForge 整合包。");
        else if (loaderString.StartsWith("quilt-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("暂不支持一键安装 Quilt 整合包。");
        else
            throw new InvalidOperationException("整合包没有声明受支持的加载器：" + loaderString);

        var id = ModpackInstaller.UniqueInstanceId(name);
        var gameDir = ModpackInstaller.CreateVersion(LauncherApp.I.MinecraftDir, id, installedLoader, mc, name);
        var packFiles = new List<PackFile>();
        if (manifest["files"] is JsonArray files)
        {
            foreach (var node in files.OfType<JsonObject>())
            {
                var projectId = node["projectID"]?.GetValue<int>().ToString() ?? "";
                var fileId = node["fileID"]?.GetValue<int>().ToString() ?? "";
                if (string.IsNullOrEmpty(projectId) || string.IsNullOrEmpty(fileId)) continue;
                try
                {
                    var cfFile = await GetFileAsync(projectId, fileId, ct);
                    if (cfFile is null) continue;
                    packFiles.Add(new PackFile
                    {
                        Path = "mods/" + cfFile.FileName,
                        Url = cfFile.Url,
                        Sha1 = cfFile.Sha1,
                        Size = cfFile.Size
                    });
                }
                catch (Exception ex) { Log.Warn("CurseForge 整合包文件解析失败：" + projectId + "/" + fileId + " " + ex.Message); }
            }
        }
        var downloaded = await ModpackInstaller.DownloadFilesAsync(packFiles, gameDir, progress, ct);
        var overridesFolder = manifest["overrides"]?.GetValue<string>() ?? "overrides";
        var overrides = ModpackInstaller.ExtractOverrides(zip, gameDir, overridesFolder);
        progress?.Report(new DownloadProgress { Stage = "整合包安装完成", Current = 1, Total = 1, Detail = name });
        Log.Info($"本地 CurseForge 整合包安装完成：{name} → {id}，文件 {downloaded}，覆盖 {overrides}");
        return new ResourceInstallResult { Id = id, Name = name, Path = gameDir, Files = downloaded + overrides };
    }

    private static string DownloadUrl(int id, string fileName, string? apiUrl)
    {
        if (!string.IsNullOrWhiteSpace(apiUrl)) return apiUrl;
        var first = id / 1000;
        var second = id % 1000;
        return $"https://edge.forgecdn.net/files/{first}/{second}/{Uri.EscapeDataString(fileName)}";
    }

    private static int LoaderType(string? loader) => loader?.ToLowerInvariant() switch
    {
        "forge" => 1,
        "fabric" => 4,
        "quilt" => 5,
        "neoforge" => 6,
        _ => 0
    };
}
