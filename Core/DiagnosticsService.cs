using System.IO;
using System.IO.Compression;
using System.Text;

namespace Vermilune.Core;

public static class DiagnosticsService
{
    public static async Task<(string Path, int Files)> ExportAsync(VersionInfo? version, string targetPath,
        CancellationToken ct = default)
    {
        var files = 0;
        await Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            using var archive = ZipFile.Open(targetPath, ZipArchiveMode.Create);
            void AddFile(string path, string entryName)
            {
                if (!File.Exists(path)) return;
                archive.CreateEntryFromFile(path, entryName, CompressionLevel.Fastest);
                files++;
            }
            void AddDirectory(string dir, string prefix)
            {
                if (!Directory.Exists(dir)) return;
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    var name = Path.GetRelativePath(dir, file).Replace('\\', '/');
                    AddFile(file, prefix + "/" + name);
                }
            }

            AddDirectory(AppPaths.LogDir, "launcher-logs");
            if (version is not null)
            {
                try
                {
                    var gameDir = GameDataSyncService.GameDir(version);
                    AddFile(Path.Combine(gameDir, "logs", "latest.log"), "game/latest.log");
                    AddDirectory(Path.Combine(gameDir, "crash-reports"), "game/crash-reports");
                    AddFile(Path.Combine(gameDir, "options.txt"), "game/options.txt");
                    AddFile(Path.Combine(gameDir, "optionsof.txt"), "game/optionsof.txt");
                }
                catch { }
            }

            var sb = new StringBuilder();
            sb.AppendLine("Vermilune diagnostics");
            sb.AppendLine("Version: 1.3.7");
            sb.AppendLine("OS: " + Environment.OSVersion);
            sb.AppendLine(".NET: " + Environment.Version);
            sb.AppendLine("64-bit: " + Environment.Is64BitProcess);
            sb.AppendLine("Data: " + AppPaths.DataDir);
            sb.AppendLine("Game: " + (version?.DisplayName ?? "未选择"));
            sb.AppendLine("Versions: " + LauncherApp.I.Versions.Versions.Count);
            sb.AppendLine("Java:");
            foreach (var java in LauncherApp.I.Java.All)
                sb.AppendLine($"  {java.Major} · {java.Path}");
            sb.AppendLine();
            sb.AppendLine("日志可能包含账号名、路径和服务器地址，分享前请检查。");
            var entry = archive.CreateEntry("diagnostics.txt", CompressionLevel.Fastest);
            using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false))) writer.Write(sb.ToString());
            files++;
        }, ct);
        return (targetPath, files);
    }
}
