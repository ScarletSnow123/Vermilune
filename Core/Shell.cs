using System.Diagnostics;
using System.IO;

namespace Vermilune.Core;

public static class Shell
{
    public static void OpenFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                // 始终交给 explorer.exe 打开目录，避免 ShellExecute 把同名文件当成程序启动。
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
                    return;
                }
                catch
                {
                    Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
                    return;
                }
            }
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
            else
                Log.Warn("打开目录失败，路径不存在：" + path);
        }
        catch (Exception ex) { Log.Warn("打开目录失败：" + ex.Message); }
    }

    public static void OpenUrl(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
            if (uri.Scheme is not ("http" or "https")) return;
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn("打开网页失败：" + ex.Message); }
    }

    public static void OpenFile(string path)
    {
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn("打开文件失败：" + ex.Message); }
    }

    public static void Run(string exe, string args = "")
    {
        try
        {
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn("运行程序失败：" + ex.Message); }
    }

    public static void OpenLogs()
    {
        var latest = Directory.Exists(AppPaths.LogDir)
            ? Directory.EnumerateFiles(AppPaths.LogDir, "*.log").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        if (latest is not null) OpenFile(latest);
        else OpenFolder(AppPaths.LogDir);
    }
}
