using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace Vermilune.Core;

/// <summary>轻量、线程安全的日志。UI 只订阅事件，不直接读文件。</summary>
public static class Log
{
    private static readonly object Lock = new();
    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>());
    private static readonly Thread Worker;
    private static bool _started;

    public static event Action<string>? LineWritten;

    static Log()
    {
        Worker = new Thread(WriteLoop) { IsBackground = true, Name = "Vermilune.Log" };
        Worker.Start();
    }

    public static string CurrentFile => Path.Combine(AppPaths.LogDir, $"launcher-{DateTime.Now:yyyyMMdd}.log");

    public static void Start()
    {
        if (_started) return;
        _started = true;
        AppPaths.EnsureInitialized();
        Info("========== Vermilune 启动 ==========");
        Info($"版本 {typeof(Log).Assembly.GetName().Version} · {Environment.OSVersion} · .NET {Environment.Version}");
        Info($"程序目录 {AppPaths.ExeDir}");
    }

    public static void Info(string message) => Write("I", message);
    public static void Warn(string message) => Write("W", message);
    public static void Error(string message) => Write("E", message);
    public static void Error(Exception ex, string? note = null)
        => Write("E", (note is null ? "" : note + "：") + ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex.StackTrace);

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {level} {message}";
        try { LineWritten?.Invoke(line); } catch { }
        try { Queue.Add(line); } catch { }
    }

    private static void WriteLoop()
    {
        foreach (var line in Queue.GetConsumingEnumerable())
        {
            try
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                File.AppendAllText(CurrentFile, line + Environment.NewLine, Encoding.UTF8);
                var info = new FileInfo(CurrentFile);
                if (info.Exists && info.Length > 8 * 1024 * 1024)
                {
                    var bak = Path.Combine(AppPaths.LogDir, $"launcher-{DateTime.Now:yyyyMMdd-HHmmss}.old.log");
                    try { File.Move(CurrentFile, bak, true); } catch { }
                }
            }
            catch { }
        }
    }

    public static void Shutdown()
    {
        try { Queue.CompleteAdding(); } catch { }
    }
}
