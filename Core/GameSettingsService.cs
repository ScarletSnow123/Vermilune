using System.IO;

namespace Vermilune.Core;

[Flags]
public enum GameSettingsScope
{
    None = 0,
    Keybinds = 1,
    Game = 2,
    Language = 4,
    ResourcePacks = 8,
    All = Keybinds | Game | Language | ResourcePacks
}

/// <summary>
/// 跨实例同步 options.txt 中的游戏设置。按键、语言、资源包和其余游戏选项可以分开选择。
/// </summary>
public static class GameSettingsService
{
    public sealed record Result(int Changed, int TargetCount, string? BackupPath);

    public static string OptionsPath(VersionInfo version)
    {
        var instance = InstanceStore.Load(version.Id);
        var gameDir = LauncherApp.I.Versions.GetGameDir(version.Id, instance, LauncherApp.I.Settings.VersionIsolation);
        return Path.Combine(gameDir, "options.txt");
    }

    public static Result Apply(VersionInfo source, IEnumerable<VersionInfo> targets, GameSettingsScope scope)
    {
        var sourcePath = OptionsPath(source);
        if (!File.Exists(sourcePath)) throw new InvalidOperationException("源实例没有 options.txt，先进一次游戏再同步。");
        var sourceLines = File.ReadAllLines(sourcePath);
        var sourceValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in sourceLines)
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            sourceValues[line[..colon]] = line[(colon + 1)..];
        }
        var picked = sourceValues
            .Where(pair => Matches(pair.Key, scope))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        if (picked.Count == 0) throw new InvalidOperationException("源实例里没有可同步的选项。");

        var targetCount = 0;
        string? backupPath = null;
        foreach (var target in targets)
        {
            var targetPath = OptionsPath(target);
            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(targetDir)) Directory.CreateDirectory(targetDir);
            var lines = File.Exists(targetPath) ? File.ReadAllLines(targetPath).ToList() : new List<string>();
            if (File.Exists(targetPath))
            {
                backupPath ??= targetPath + ".vermilune.bak";
                try { File.Copy(targetPath, backupPath, true); } catch { }
            }
            var changed = 0;
            foreach (var (key, value) in picked)
            {
                var index = lines.FindIndex(line =>
                    line.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase));
                var replacement = key + ":" + value;
                if (index >= 0)
                {
                    if (lines[index] == replacement) continue;
                    lines[index] = replacement;
                }
                else
                {
                    lines.Add(replacement);
                }
                changed++;
            }
            if (changed > 0)
            {
                File.WriteAllLines(targetPath, lines);
                targetCount++;
            }
        }
        return new Result(targetCount, targetCount, backupPath);
    }

    private static bool Matches(string key, GameSettingsScope scope)
    {
        if (key.StartsWith("key_", StringComparison.OrdinalIgnoreCase)) return scope.HasFlag(GameSettingsScope.Keybinds);
        if (key.Equals("lang", StringComparison.OrdinalIgnoreCase)) return scope.HasFlag(GameSettingsScope.Language);
        if (key.Equals("resourcePacks", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("incompatibleResourcePacks", StringComparison.OrdinalIgnoreCase))
            return scope.HasFlag(GameSettingsScope.ResourcePacks);
        return scope.HasFlag(GameSettingsScope.Game);
    }
}
