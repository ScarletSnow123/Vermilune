using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Vermilune.Core;

namespace Vermilune.UI.Dialogs;

public partial class SkinPickerDialog : Window
{
    private sealed class SkinItem
    {
        public string Path { get; init; } = "";
        public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    }

    private readonly CancellationTokenSource _cts = new();
    private string? _selectedPath;
    private bool _loadingModel;

    public SkinPickerDialog()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => { try { _cts.Cancel(); } catch { } };
    }

    public static string? Show(Window? owner)
    {
        var dialog = new SkinPickerDialog { Owner = owner };
        if (owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true ? dialog._selectedPath : null;
    }

    private async Task LoadAsync()
    {
        RefreshList(LauncherApp.I.Settings.SkinPath);
        await RefreshPreviewAsync();
    }

    private void RefreshList(string? selectPath)
    {
        var files = LauncherApp.I.Skins.ListSkins();
        var items = files.Select(f => new SkinItem { Path = f }).ToList();
        SkinList.ItemsSource = items;
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var selected = items.FirstOrDefault(x => string.Equals(x.Path, selectPath, StringComparison.OrdinalIgnoreCase))
                       ?? items.FirstOrDefault();
        SkinList.SelectedItem = selected;
        StatusText.Text = $"{items.Count} 个本地皮肤";
    }

    private async void SkinList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedPath = (SkinList.SelectedItem as SkinItem)?.Path;
        ApplyButton.IsEnabled = _selectedPath is not null;
        ModelCombo.IsEnabled = _selectedPath is not null;
        await RefreshPreviewAsync();
    }

    private async Task RefreshPreviewAsync()
    {
        if (_selectedPath is null || !File.Exists(_selectedPath))
        {
            NameText.Text = "未选择";
            ModelText.Text = "";
            ModelCombo.IsEnabled = false;
            Preview.SetTextures(null, null, false);
            return;
        }
        try
        {
            var skin = LoadBitmap(_selectedPath);
            var slim = LauncherApp.I.Skins.GetSlim(_selectedPath);
            var cape = await LauncherApp.I.Skins.LoadCapeAsync(_cts.Token);
            Preview.SetTextures(skin, cape, slim);
            NameText.Text = Path.GetFileNameWithoutExtension(_selectedPath);
            ModelText.Text = slim ? "Alex / 细手臂" : "Steve / 粗手臂";
            _loadingModel = true;
            ModelCombo.IsEnabled = true;
            ModelCombo.SelectedIndex = slim ? 1 : 0;
            _loadingModel = false;
        }
        catch (Exception ex) { ModelText.Text = "预览失败：" + ex.Message; }
    }

    private static System.Windows.Media.Imaging.BitmapSource LoadBitmap(string path)
    {
        var image = new System.Windows.Media.Imaging.BitmapImage();
        image.BeginInit();
        image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (ImportButton.ContextMenu is not null)
        {
            ImportButton.ContextMenu.PlacementTarget = ImportButton;
            ImportButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            ImportButton.ContextMenu.IsOpen = true;
        }
    }

    private async void ImportImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入皮肤图片",
            Filter = "皮肤图片 (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg"
        };
        if (dialog.ShowDialog(this) != true) return;
        var target = LauncherApp.I.Skins.ImportSkin(dialog.FileName, activate: false);
        if (target is null)
        {
            MessageDialog.Show(this, "导入失败", "皮肤尺寸应为 64×64 或 64×32，且小于 24 KB。", "知道了", danger: true);
            return;
        }
        RefreshList(target);
        StatusText.Text = "已导入：" + Path.GetFileName(target);
        await RefreshPreviewAsync();
    }

    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "导入皮肤文件夹" };
        if (dialog.ShowDialog(this) != true) return;
        var (copied, skipped) = LauncherApp.I.Skins.ImportSkinFolder(dialog.FolderName);
        var files = LauncherApp.I.Skins.ListSkins();
        RefreshList(files.FirstOrDefault());
        StatusText.Text = skipped == 0
            ? $"已导入 {copied} 个皮肤"
            : $"已导入 {copied} 个，跳过 {skipped} 个（尺寸/格式不符合）";
        await RefreshPreviewAsync();
    }

    private async void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingModel || _selectedPath is null) return;
        var slim = ModelCombo.SelectedIndex == 1;
        LauncherApp.I.Skins.SetSkinModel(_selectedPath, slim);
        ModelText.Text = slim ? "Alex / 细手臂 · 已手动设置" : "Steve / 粗手臂 · 已手动设置";
        await RefreshPreviewAsync();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPath is null) return;
        LauncherApp.I.Skins.UseSkin(_selectedPath, ModelCombo.SelectedIndex == 1);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
}
