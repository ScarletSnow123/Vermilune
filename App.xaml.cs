using System.Windows;
using System.Windows.Threading;
using Vermilune.Core;
using Vermilune.UI;

namespace Vermilune;

public partial class App : Application
{
    public static MainWindow? Window { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Error(args.ExceptionObject as Exception ?? new Exception("未知未处理异常"), "AppDomain 未处理异常");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "未观察的任务异常");
            args.SetObserved();
        };

        try
        {
            Log.Start();
            AppPaths.EnsureInitialized();
            ThemeManager.Apply("#E5B567", 0.96, false);
            var window = new MainWindow();
            Window = window;
            window.Show();
            await LauncherApp.I.InitializeAsync();
            window.OnAppReady();
            if (e.Args.Length >= 2 && string.Equals(e.Args[0], "--launch", StringComparison.OrdinalIgnoreCase))
            {
                window.LaunchVersion(e.Args[1]);
            }
            else if (e.Args.Length >= 2 && string.Equals(e.Args[0], "--page", StringComparison.OrdinalIgnoreCase))
            {
                window.NavigateTo(e.Args[1]);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动失败");
            MessageBox.Show("Vermilune 启动失败：\n" + ex.Message + "\n\n日志：" + Log.CurrentFile,
                "Vermilune", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "UI 线程未处理异常");
        // 不跳转任何外部页面；只在本启动器内提示，并保留继续运行的可能。
        MessageBox.Show("Vermilune 遇到一个错误：\n" + e.Exception.Message +
                        "\n\n日志已写入：\n" + Log.CurrentFile +
                        "\n\n可以关闭此提示后继续使用，或重启启动器。",
            "Vermilune", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { LauncherApp.I.Save(); } catch { }
        Log.Info("Vermilune 退出");
        Log.Shutdown();
        base.OnExit(e);
    }
}
