using System.IO;
using System.IO.Compression;

namespace Vermilune.Core;

/// <summary>
/// 拖入窗口的整合包 zip：自动识别 CurseForge manifest.json 或 Modrinth modrinth.index.json。
/// </summary>
public static class LocalModpackService
{
    public static async Task<ResourceInstallResult> InstallAsync(string archive, string? fallbackName,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (!File.Exists(archive)) throw new InvalidOperationException("找不到整合包文件。");
        using var zip = ZipFile.OpenRead(archive);
        var hasCurseForge = zip.Entries.Any(e => string.Equals(e.FullName, "manifest.json", StringComparison.OrdinalIgnoreCase));
        var hasModrinth = zip.Entries.Any(e => string.Equals(e.FullName, "modrinth.index.json", StringComparison.OrdinalIgnoreCase));

        if (hasCurseForge)
            return await LauncherApp.I.CurseForge.InstallLocalModpackAsync(archive, fallbackName, progress, ct);
        if (hasModrinth)
            return await LauncherApp.I.Modrinth.InstallLocalModpackAsync(archive, fallbackName, progress, ct);

        throw new InvalidOperationException(
            "这个压缩包不是可识别的整合包：\n" +
            "CurseForge 整合包应包含 manifest.json，Modrinth 整合包应包含 modrinth.index.json。\n" +
            "普通整合包压缩包（只有 mods 文件夹）暂不支持直接安装，请解压后手动放入实例目录。");
    }
}
