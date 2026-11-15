using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace Vermilune.Core;

public static class InstanceBackupService
{
    private static readonly string[] DataDirectories =
    {
        "saves", "mods", "config", "configs", "defaultconfigs", "resourcepacks", "shaderpacks",
        "journeymap", "xaero", "XaeroWaypoints", "XaeroWorldMap", "voxelmap", "trees", "CustomSkinLoader"
    };

    private static readonly string[] DataFiles =
    {
        "options.txt", "optionsof.txt", "optionsshaders.txt", "servers.dat", "servers.dat_old",
        "usernamecache.json", "usercache.json"
    };

    public static string BackupRoot => Path.Combine(AppPaths.DataDir, "backups");

    public static async Task<(string Path, int Files)> CreateAsync(VersionInfo version, string targetPath,
        IProgress<string>? progress, CancellationToken ct = default)
    {
        var gameDir = GameDataSyncService.GameDir(version);
        if (!Directory.Exists(gameDir)) throw new InvalidOperationException("实例目录不存在");
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var files = 0;
        await Task.Run(() =>
        {
            using var archive = ZipFile.Open(targetPath, ZipArchiveMode.Create);
            var meta = JsonSerializer.Serialize(new
            {
                id = version.Id,
                name = version.DisplayName,
                created = DateTime.Now,
                format = 1
            });
            var entry = archive.CreateEntry("vermilune-backup.json", CompressionLevel.Fastest);
            using (var writer = new StreamWriter(entry.Open())) writer.Write(meta);

            foreach (var relative in DataDirectories)
            {
                var source = Path.Combine(gameDir, relative);
                if (!Directory.Exists(source)) continue;
                foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    var name = Path.GetRelativePath(gameDir, file).Replace('\\', '/');
                    if (name.Contains("../", StringComparison.Ordinal)) continue;
                    archive.CreateEntryFromFile(file, name, CompressionLevel.Fastest);
                    files++;
                    if (files % 40 == 0) progress?.Report($"已打包 {files} 个文件");
                }
            }
            foreach (var relative in DataFiles)
            {
                var file = Path.Combine(gameDir, relative);
                if (!File.Exists(file)) continue;
                archive.CreateEntryFromFile(file, relative.Replace('\\', '/'), CompressionLevel.Fastest);
                files++;
            }
        }, ct);
        return (targetPath, files);
    }

    public static async Task<int> RestoreAsync(VersionInfo version, string archivePath, CancellationToken ct = default)
    {
        var gameDir = GameDataSyncService.GameDir(version);
        Directory.CreateDirectory(gameDir);
        var count = 0;
        await Task.Run(() =>
        {
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(entry.Name)) continue;
                if (string.Equals(entry.FullName, "vermilune-backup.json", StringComparison.OrdinalIgnoreCase)) continue;
                var relative = entry.FullName.Replace('/', '\\');
                if (Path.IsPathRooted(relative) || relative.Contains("..", StringComparison.Ordinal)) continue;
                var target = Path.GetFullPath(Path.Combine(gameDir, relative));
                if (!target.StartsWith(Path.GetFullPath(gameDir), StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, true);
                count++;
            }
        }, ct);
        return count;
    }
}
