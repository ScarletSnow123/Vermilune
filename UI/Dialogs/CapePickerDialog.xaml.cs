using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Vermilune.Core;

namespace Vermilune.UI.Dialogs;

public partial class CapePickerDialog : Window
{
    private sealed class CapeChoice
    {
        public string? Id { get; init; }
        public string DisplayName { get; init; } = "";
        public string ImageUrl { get; init; } = "";
        public bool IsNone { get; init; }
        public bool IsActive { get; init; }
        public string StateText => IsActive ? "使用中" : "";
    }

    private readonly CancellationTokenSource _cts = new();
    private int _previewVersion;

    public CapePickerDialog()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => { try { _cts.Cancel(); } catch { } };
    }

    public static bool Show(Window? owner)
    {
        var dialog = new CapePickerDialog { Owner = owner };
        if (owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true;
    }

    private async Task LoadAsync()
    {
        AccountText.Text = LauncherApp.I.Settings.Account.Name;
        Preview.SetView(-24, 6);
        try
        {
            var capes = await LauncherApp.I.Auth.GetCapesAsync(_cts.Token);
            var list = new List<CapeChoice>();
            if (!capes.Any(c => c.Active))
                list.Add(new CapeChoice { IsNone = true, DisplayName = "无披风", IsActive = true });
            else
                list.Add(new CapeChoice { IsNone = true, DisplayName = "无披风" });
            foreach (var cape in capes)
                list.Add(new CapeChoice
                {
                    Id = cape.Id,
                    DisplayName = cape.DisplayName,
                    ImageUrl = ToHttp(cape.Url),
                    IsActive = cape.Active
                });
            CapeList.ItemsSource = list;
            CapeList.SelectedItem = list.FirstOrDefault(c => c.IsActive) ?? list.FirstOrDefault();
            LoadingText.Visibility = Visibility.Collapsed;
            ApplyButton.IsEnabled = true;
            if (capes.Count == 0) StatusText.Text = "该账号当前没有可用的披风。";
            await RefreshPreviewAsync();
        }
        catch (Exception ex)
        {
            LoadingText.Visibility = Visibility.Collapsed;
            StatusText.Text = "读取披风失败：" + ex.Message;
            Log.Warn("读取披风失败：" + ex.Message);
        }
    }

    private static string ToHttp(string url) =>
        url.Replace("https://textures.minecraft.net/", "http://textures.minecraft.net/", StringComparison.OrdinalIgnoreCase);

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (CapeList.SelectedItem is not CapeChoice choice) return;
        if (!choice.IsNone && string.IsNullOrEmpty(choice.Id))
        {
            StatusText.Text = "当前网络只能读取到正在使用的披风，无法切换其他披风。";
            return;
        }
        ApplyButton.IsEnabled = false;
        StatusText.Text = choice.IsNone ? "正在取消披风…" : "正在切换披风…";
        try
        {
            await LauncherApp.I.Auth.SetCapeAsync(choice.IsNone ? null : choice.Id, _cts.Token);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            ApplyButton.IsEnabled = true;
            Log.Warn("切换披风失败：" + ex.Message);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private async void CapeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyButton.IsEnabled = CapeList.SelectedItem is CapeChoice;
        await RefreshPreviewAsync();
    }

    private async Task RefreshPreviewAsync()
    {
        var version = ++_previewVersion;
        if (CapeList.SelectedItem is not CapeChoice choice)
        {
            Preview.SetTextures(null, null, false);
            SelectedName.Text = "未选择";
            return;
        }

        SelectedName.Text = choice.DisplayName;
        try
        {
            var skin = await LauncherApp.I.Skins.LoadSkinAsync(_cts.Token);
            BitmapSource? cape = null;
            if (!choice.IsNone && !string.IsNullOrEmpty(choice.ImageUrl))
                cape = await LauncherApp.I.Skins.LoadTextureAsync(choice.ImageUrl, _cts.Token);
            if (version != _previewVersion) return;
            Preview.SetTextures(skin, cape, LauncherApp.I.Skins.IsSlim);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("披风预览失败：" + ex.Message);
        }
    }
}
