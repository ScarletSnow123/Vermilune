using System.IO;

namespace Vermilune.Core;

public sealed record DataSyncSelection(
    GameSettingsScope OptionsScope,
    bool Xaero,
    bool JourneyMap,
    bool Shaderpacks,
    IReadOnlyList<string> CustomPaths);

public sealed record DataSyncResult(int TargetsChanged, int FilesCopied, IReadOnlyList<string> Errors);

/// <summary>
/// 跨实例同步 options.txt、Xaero 地图缓存、JourneyMap 以及用户自定义的相对路径。
/// </summary>
public static class GameDataSyncService
{
    public static readonly string[] XaeroPaths =
    {
        "XaeroWaypoints",
        "XaeroWorldMap",
        "xaero",
        Path.Combine("config", "xaero")
    };

    public static DataSyncResult Apply(VersionInfo source, IEnumerable<VersionInfo> targets, DataSyncSelection selection)
    {
        var errors = new List<string>();
        var targetList = targets.Where(t => t.Id != source.Id).ToList();
        var sourceDir = GameDir(source);
        var copied = 0;
        var changed = 0;
        foreach (var target in targetList)
        {
            var targetChanged = false;
            if (selection.OptionsScope != GameSettingsScope.None)
            {
                try
                {
                    var result = GameSettingsService.Apply(source, new[] { target }, selection.OptionsScope);
                    if (result.TargetCount > 0) targetChanged = true;
                }
                catch (Exception ex) { errors.Add($"{target.DisplayName}：{ex.Message}"); }
            }

            var paths = new List<string>();
            if (selection.Xaero) paths.AddRange(XaeroPaths);
            if (selection.JourneyMap) paths.Add("journeymap");
            if (selection.Shaderpacks) paths.Add("shaderpacks");
            paths.AddRange(selection.CustomPaths);
            foreach (var relative in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var count = CopyRelative(sourceDir, GameDir(target), relative);
                    if (count > 0) { targetChanged = true; copied += count; }
                }
                catch (Exception ex) { errors.Add($"{target.DisplayName} / {relative}：{ex.Message}"); }
            }
            if (targetChanged) changed++;
        }
        return new DataSyncResult(changed, copied, errors);
    }

    public static string GameDir(VersionInfo version)
    {
        var instance = InstanceStore.Load(version.Id);
        return LauncherApp.I.Versions.GetGameDir(version.Id, instance, LauncherApp.I.Settings.VersionIsolation);
    }

    private static int CopyRelative(string sourceRoot, string targetRoot, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return 0;
        var rel = relative.Replace('/', '\\').TrimStart('\\');
        if (rel.Contains("..", StringComparison.Ordinal)) return 0;
        var source = Path.GetFullPath(Path.Combine(sourceRoot, rel));
        var target = Path.GetFullPath(Path.Combine(targetRoot, rel));
        if (!source.StartsWith(Path.GetFullPath(sourceRoot), StringComparison.OrdinalIgnoreCase) ||
            !target.StartsWith(Path.GetFullPath(targetRoot), StringComparison.OrdinalIgnoreCase)) return 0;
        if (File.Exists(source))
        {
            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
            File.Copy(source, target, true);
            return 1;
        }
        if (!Directory.Exists(source)) return 0;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var sub = Path.GetRelativePath(source, file);
            var dest = Path.Combine(target, sub);
            var parent = Path.GetDirectoryName(dest);
            if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
            File.Copy(file, dest, true);
            count++;
        }
        return count;
    }
}
