using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Vermilune.Core;

/// <summary>
/// 用隐藏的 WebView2 渲染 Web 壁纸，再以固定帧率抓成位图交给 WPF 窗口显示。
/// 这样不会出现 WebView2 的 HWND 盖住启动器界面的问题。
/// </summary>
public sealed class WebWallpaperRenderer : IDisposable
{
    private Window? _host;
    private WebView2? _view;
    private DispatcherTimer? _timer;
    private bool _capturing;
    private bool _ready;

    public event Action<BitmapSource>? FrameReady;
    public event Action<string>? Failed;

    public async Task StartAsync(string htmlPath)
    {
        try
        {
            _host = new Window
            {
                Width = 1280,
                Height = 720,
                Left = -20000,
                Top = -20000,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Background = System.Windows.Media.Brushes.Black,
                Content = null
            };
            _view = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.Black };
            _host.Content = _view;
            _host.Show();

            var userData = Path.Combine(AppPaths.CacheDir, "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(null, userData);
            await _view.EnsureCoreWebView2Async(environment);
            _view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _view.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _view.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _view.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnNavigation(object? sender, CoreWebView2NavigationCompletedEventArgs e)
            {
                if (!e.IsSuccess) completed.TrySetException(new InvalidOperationException("Web 壁纸加载失败：" + e.WebErrorStatus));
                else completed.TrySetResult(true);
            }
            _view.CoreWebView2.NavigationCompleted += OnNavigation;
            try
            {
                _view.Source = new Uri(htmlPath, UriKind.Absolute);
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            }
            finally { _view.CoreWebView2.NavigationCompleted -= OnNavigation; }

            _ready = true;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _timer.Tick += async (_, _) => await CaptureAsync();
            _timer.Start();
            await CaptureAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("Web 壁纸渲染失败：" + ex.Message);
            Failed?.Invoke(ex.Message);
            Dispose();
        }
    }

    private async Task CaptureAsync()
    {
        if (!_ready || _capturing || _view?.CoreWebView2 is null) return;
        _capturing = true;
        try
        {
            using var stream = new MemoryStream();
            await _view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            stream.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = stream;
            bmp.EndInit();
            bmp.Freeze();
            FrameReady?.Invoke(bmp);
        }
        catch (Exception ex)
        {
            Log.Warn("抓取 Web 壁纸帧失败：" + ex.Message);
        }
        finally { _capturing = false; }
    }

    public void Stop()
    {
        _ready = false;
        _timer?.Stop();
        _timer = null;
        try { _view?.Dispose(); } catch { }
        _view = null;
        try { _host?.Close(); } catch { }
        _host = null;
    }

    public void Dispose() => Stop();
}
