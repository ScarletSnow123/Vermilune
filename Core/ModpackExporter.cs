using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vermilune.Core;

public sealed class ModpackExportOptions
{
    public string Name { get; init; } = "";
    public string Version { get; init; } = "1.0.0";
    public bool IncludeMods { get; init; } = true;
    public bool IncludeConfig { get; init; } = true;
    public bool IncludeResourcePacks { get; init; } = true;
    public bool IncludeShaderPacks { get; init; } = true;
    public bool IncludeSaves { get; init; }
    public bool IncludeOptions { get; init; } = true;
}

public static class ModpackExporter
{
    public static Task<int> ExportAsync(VersionInfo version, string zipPath, ModpackExportOptions options,
        IProgress<string>? progress = null, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var instance = InstanceStore.Load(version.Id);
            var gameDir = LauncherApp.I.Versions.GetGameDir(version.Id, instance, LauncherApp.I.Settings.VersionIsolation);
            var modsDir = LauncherApp.I.Versions.GetModsDir(version.Id, instance, LauncherApp.I.Settings.VersionIsolation);
            var root = Path.GetDirectoryName(zipPath);
            if (!string.IsNullOrEmpty(root)) Directory.CreateDirectory(root);
            if (File.Exists(zipPath)) File.Delete(zipPath);

            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            var count = 0;
            void AddFile(string source, string entry)
            {
                if (!File.Exists(source)) return;
                ct.ThrowIfCancellationRequested();
                zip.CreateEntryFromFile(source, entry.Replace('\\', '/'), CompressionLevel.Optimal);
                count++;
                progress?.Report(entry);
            }
            void AddDir(string sourceDir, string entryRoot)
            {
                if (!Directory.Exists(sourceDir)) return;
                foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(sourceDir, file);
                    AddFile(file, Path.Combine(entryRoot, rel));
                }
            }

            var manifest = new JsonObject
            {
                ["name"] = options.Name,
                ["version"] = options.Version,
                ["minecraft"] = version.MinecraftVersion,
                ["loader"] = version.LoaderBadge,
                ["exportedAt"] = DateTime.UtcNow.ToString("s")
            };
            var manifestEntry = zip.CreateEntry("modpack.json", CompressionLevel.Optimal);
            using (var writer = new StreamWriter(manifestEntry.Open()))
                writer.Write(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            if (options.IncludeMods) AddDir(modsDir, "mods");
            if (options.IncludeConfig) AddDir(Path.Combine(gameDir, "config"), "config");
            if (options.IncludeResourcePacks) AddDir(Path.Combine(gameDir, "resourcepacks"), "resourcepacks");
            if (options.IncludeShaderPacks) AddDir(Path.Combine(gameDir, "shaderpacks"), "shaderpacks");
            if (options.IncludeSaves) AddDir(Path.Combine(gameDir, "saves"), "saves");
            if (options.IncludeOptions) AddFile(Path.Combine(gameDir, "options.txt"), "options.txt");

            progress?.Report("完成");
            return count;
        }, ct);
}
