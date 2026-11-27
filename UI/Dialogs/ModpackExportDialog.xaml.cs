using System.IO;
using System.Windows;
using Microsoft.Win32;
using Vermilune.Core;

namespace Vermilune.UI.Dialogs;

public partial class ModpackExportDialog : Window
{
    private readonly VersionInfo _version;

    public ModpackExportDialog(VersionInfo version)
    {
        InitializeComponent();
        _version = version;
        NameBox.Text = version.DisplayName;
        VersionBox.Text = "1.0.0";
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var name = (NameBox.Text ?? "").Trim();
        if (name.Length == 0)
        {
            StatusText.Text = "写个名字";
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "导出整合包",
            Filter = "整合包 (*.zip)|*.zip",
            FileName = name + "-" + (VersionBox.Text ?? "1.0.0").Trim() + ".zip",
            InitialDirectory = AppPaths.DataDir
        };
        if (dialog.ShowDialog(this) != true) return;

        ExportButton.IsEnabled = false;
        StatusText.Text = "正在打包…";
        try
        {
            var options = new ModpackExportOptions
            {
                Name = name,
                Version = string.IsNullOrWhiteSpace(VersionBox.Text) ? "1.0.0" : VersionBox.Text.Trim(),
                IncludeMods = ModsCheck.IsChecked == true,
                IncludeConfig = ConfigCheck.IsChecked == true,
                IncludeResourcePacks = PacksCheck.IsChecked == true,
                IncludeShaderPacks = ShadersCheck.IsChecked == true,
                IncludeSaves = SavesCheck.IsChecked == true,
                IncludeOptions = OptionsCheck.IsChecked == true
            };
            var progress = new Progress<string>(p => StatusText.Text = "打包中：" + p);
            var count = await ModpackExporter.ExportAsync(_version, dialog.FileName, options, progress);
            StatusText.Text = $"完成，{count} 个文件";
            LauncherApp.I.Notify("整合包已导出", Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex)
        {
            StatusText.Text = "导出失败：" + ex.Message;
        }
        finally { ExportButton.IsEnabled = true; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
