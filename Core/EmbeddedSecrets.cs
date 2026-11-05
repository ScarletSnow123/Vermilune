using System.IO;
using System.Reflection;

namespace Vermilune.Core;

/// <summary>
/// 构建时注入的私有配置。密钥只存在于 exe 资源中，不写入源码。
/// </summary>
internal static class EmbeddedSecrets
{
    public static string CurseForgeKey { get; } = Read("Vermilune.Secrets.CurseForgeKey");

    public static bool HasCurseForgeKey => !string.IsNullOrWhiteSpace(CurseForgeKey);

    private static string Read(string name)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream is null) return "";
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Trim();
        }
        catch { return ""; }
    }
}
