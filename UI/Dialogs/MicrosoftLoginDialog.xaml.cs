using System.Windows;
using System.Windows.Input;
using Vermilune.Core;

namespace Vermilune.UI.Dialogs;

public partial class MicrosoftLoginDialog : Window
{
    private CancellationTokenSource? _cts;
    private DeviceCodeInfo? _code;
    private bool _completed;

    public MicrosoftProfile? Profile { get; private set; }

    public MicrosoftLoginDialog()
    {
        InitializeComponent();
        MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };
        Loaded += async (_, _) => await RunAsync();
        Closed += (_, _) => { try { _cts?.Cancel(); } catch { } };
    }

    public static MicrosoftProfile? Show(Window? owner)
    {
        var dialog = new MicrosoftLoginDialog { Owner = owner };
        if (owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        return dialog.Profile;
    }

    private async Task RunAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        ErrorText.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
        Spinner.Visibility = Visibility.Visible;
        OpenButton.IsEnabled = false;
        CopyButton.IsEnabled = false;
        CodeText.Text = "······";
        StatusText.Text = "正在获取登录代码…";
        var deviceProgress = new Progress<DeviceCodeInfo>(code =>
        {
            _code = code;
            CodeText.Text = code.UserCode;
            UrlText.Text = code.VerificationUri;
            OpenButton.IsEnabled = true;
            CopyButton.IsEnabled = true;
            var copied = false;
            try
            {
                if (!string.IsNullOrWhiteSpace(code.UserCode))
                {
                    Clipboard.SetText(code.UserCode);
                    copied = true;
                }
            }
            catch { }
            StatusText.Text = copied
                ? "登录码已自动复制。浏览器已打开，粘贴后完成授权。"
                : "已打开浏览器。完成授权后，这里会自动继续。";
            Shell.OpenUrl(code.VerificationUri);
        });
        var status = new Progress<string>(s => StatusText.Text = s);
        try
        {
            var profile = await LauncherApp.I.Auth.LoginMicrosoftAsync(deviceProgress, status, _cts.Token);
            Profile = profile;
            _completed = true;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            Close();
        }
        catch (Exception ex)
        {
            Spinner.Visibility = Visibility.Collapsed;
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
            RetryButton.Visibility = Visibility.Visible;
            StatusText.Text = "登录未完成。";
            Log.Warn("微软登录失败：" + ex.Message);
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_code is not null) Shell.OpenUrl(_code.VerificationUri);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_code is not null) Clipboard.SetText(_code.UserCode);
            StatusText.Text = "代码已复制到剪贴板。";
        }
        catch { }
    }

    private void Retry_Click(object sender, RoutedEventArgs e) => _ = RunAsync();

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        if (!_completed)
        {
            DialogResult = false;
        }
    }
}
