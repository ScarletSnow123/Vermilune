using System.IO;

namespace Vermilune.Core;

public static class BackgroundService
{
    private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".bmp" };

    public static string Folder
    {
        get
        {
            var own = Path.Combine(AppPaths.DataDir, "Pictures");
            Directory.CreateDirectory(own);
            return own;
        }
    }

    public static void EnsureFolder()
    {
        var folder = Folder;
        Directory.CreateDirectory(folder);
        var readme = Path.Combine(folder, "README.txt");
        if (!File.Exists(readme))
            File.WriteAllText(readme, "把背景图片放进这个文件夹，然后在启动器设置里点「刷新背景」或重新打开启动器。\n支持 png / jpg / jpeg / bmp。\n");
    }

    public static string? PickLatest()
    {
        try
        {
            EnsureFolder();
            return Directory.EnumerateFiles(Folder)
                .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => f.FullName)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    public static bool IsImage(string path) =>
        File.Exists(path) && Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
