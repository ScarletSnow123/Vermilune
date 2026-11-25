using System.IO;
using System.Windows;
using Vermilune.Core;

namespace Vermilune.UI.Dialogs;

public partial class InstanceResourcesDialog : Window
{
    private readonly VersionInfo _version;
    private readonly string _gameDir;

    public InstanceResourcesDialog(VersionInfo version)
    {
        InitializeComponent();
        _version = version;
        var instance = InstanceStore.Load(version.Id);
        _gameDir = LauncherApp.I.Versions.GetGameDir(version.Id, instance, LauncherApp.I.Settings.VersionIsolation);
        InstanceText.Text = $"{version.DisplayName} · {version.MinecraftVersion} · {version.LoaderBadge}";
        ModCount.Text = CountDir("mods", ".jar", ".jar.disabled").ToString();
        PackCount.Text = CountDir("resourcepacks", ".zip", ".jar").ToString();
        SaveCount.Text = Directory.Exists(Path.Combine(_gameDir, "saves"))
            ? Directory.EnumerateDirectories(Path.Combine(_gameDir, "saves")).Count().ToString()
            : "0";
        ShaderCount.Text = CountDir("shaderpacks", ".zip", ".jar").ToString();
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }

    private int CountDir(string child, params string[] extensions)
    {
        var path = Path.Combine(_gameDir, child);
        if (!Directory.Exists(path)) return 0;
        return Directory.EnumerateFiles(path)
            .Count(f => extensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)));
    }

    private void OpenMods_Click(object sender, RoutedEventArgs e)
    {
        var dir = LauncherApp.I.Versions.GetModsDir(_version.Id, null, LauncherApp.I.Settings.VersionIsolation);
        Directory.CreateDirectory(dir);
        Shell.OpenFolder(dir);
    }

    private void OpenPacks_Click(object sender, RoutedEventArgs e) => OpenSub("resourcepacks");
    private void OpenSaves_Click(object sender, RoutedEventArgs e) => OpenSub("saves");
    private void OpenShaders_Click(object sender, RoutedEventArgs e) => OpenSub("shaderpacks");

    private void OpenSub(string child)
    {
        var dir = Path.Combine(_gameDir, child);
        Directory.CreateDirectory(dir);
        Shell.OpenFolder(dir);
    }

    private void OpenInstance_Click(object sender, RoutedEventArgs e) => Shell.OpenFolder(_gameDir);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
