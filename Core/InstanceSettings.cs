using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Vermilune.Core;

/// <summary>每个版本独立的启动设置，存放在 Vermilune/instances，不写入游戏目录。</summary>
public sealed class InstanceSettings
{
    public bool? Isolate { get; set; }
    public int? MemoryMb { get; set; }
    public string JavaPath { get; set; } = "";
    public bool AutoJava { get; set; } = true;
    public string JvmArgs { get; set; } = "";
    public string GameArgs { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Fullscreen { get; set; }
    public bool? OptimizeJvm { get; set; }
    public string DisplayName { get; set; } = "";
    public DateTime LastModifiedUtc { get; set; } = DateTime.UtcNow;

    public InstanceSettings Clone() => (InstanceSettings)MemberwiseClone();
}

public static class InstanceStore
{
    private static readonly JsonSerializerOptions Options = new(SettingsStore.JsonOptions)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };

    public static InstanceSettings Load(string versionId)
    {
        try
        {
            var path = AppPaths.InstanceSettingsFile(versionId);
            if (File.Exists(path))
            {
                var result = JsonSerializer.Deserialize<InstanceSettings>(File.ReadAllText(path), Options);
                if (result is not null)
                {
                    result.Width = Math.Clamp(result.Width, 0, 7680);
                    result.Height = Math.Clamp(result.Height, 0, 4320);
                    if (result.MemoryMb is int m) result.MemoryMb = Math.Clamp(m, 512, 65536);
                    return result;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取实例设置失败（{versionId}）：{ex.Message}");
        }
        return new InstanceSettings();
    }

    public static void Save(string versionId, InstanceSettings settings)
    {
        try
        {
            settings.LastModifiedUtc = DateTime.UtcNow;
            var path = AppPaths.InstanceSettingsFile(versionId);
            Directory.CreateDirectory(AppPaths.InstanceSettingsDir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options), new UTF8Encoding(false));
            File.Move(tmp, path, true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"保存实例设置失败（{versionId}）");
            throw;
        }
    }

    public static void Delete(string versionId)
    {
        try
        {
            var path = AppPaths.InstanceSettingsFile(versionId);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
}
