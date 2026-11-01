using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Vermilune.Core;

namespace Vermilune.UI.Dialogs;

public partial class AccountDialog : Window
{
    public bool Changed { get; private set; }

    public AccountDialog()
    {
        InitializeComponent();
        MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };
        var account = LauncherApp.I.Settings.Account;
        CurrentText.Text = $"当前：{account.Name}（{AccountTypeText(account)}）";
        OfflineName.Text = account.IsOnline ? "" : account.Name;
        if (account.IsYggdrasil)
        {
            YggServer.Text = account.YggdrasilServer;
            YggUser.Text = account.Name;
        }
        BuildSavedList();
    }

    private sealed class SavedAccountRow
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string TypeText { get; init; } = "";
        public string ActionText { get; init; } = "";
        public bool CanSwitch { get; init; }
    }

    private void BuildSavedList()
    {
        var currentId = LauncherApp.I.Settings.Account.Id;
        var rows = (LauncherApp.I.Settings.Accounts ?? new List<AccountSettings>())
            .Where(a => !string.IsNullOrEmpty(a.Id))
            .OrderByDescending(a => string.Equals(a.Id, currentId, StringComparison.OrdinalIgnoreCase))
            .ThenBy(a => a.Name)
            .Select(a => new SavedAccountRow
            {
                Id = a.Id,
                Name = string.IsNullOrWhiteSpace(a.Name) ? "Player" : a.Name,
                TypeText = AccountTypeText(a),
                ActionText = string.Equals(a.Id, currentId, StringComparison.OrdinalIgnoreCase) ? "使用中" : "切换",
                CanSwitch = !string.Equals(a.Id, currentId, StringComparison.OrdinalIgnoreCase)
            })
            .ToList();
        SavedList.ItemsSource = rows;
    }

    private void SwitchAccount_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: string id } || string.IsNullOrEmpty(id)) return;
        SwitchTo(id);
    }

    private void SavedAccount_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id } || string.IsNullOrEmpty(id)) return;
        e.Handled = true;
        SwitchTo(id);
    }

    private void SwitchTo(string id)
    {
        if (!LauncherApp.I.SwitchAccount(id)) return;
        Changed = true;
        Close();
    }

    private static string AccountTypeText(AccountSettings account) =>
        account.IsMicrosoft ? "Microsoft 正版" : account.IsYggdrasil ? "外置登录" : "离线";

    public static bool Show(Window? owner)
    {
        var dialog = new AccountDialog { Owner = owner };
        if (owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        return dialog.Changed;
    }

    private void Microsoft_Click(object sender, RoutedEventArgs e)
    {
        var profile = MicrosoftLoginDialog.Show(this);
        if (profile is null) return;
        var account = new AccountSettings();
        AccountService.ApplyProfile(account, profile);
        account.Type = "microsoft";
        LauncherApp.I.RememberAccount(account);
        LauncherApp.I.Save();
        LauncherApp.I.RaiseSettingsChanged();
        Changed = true;
        Close();
    }

    private void Offline_Click(object sender, RoutedEventArgs e)
    {
        var name = OfflineName.Text.Trim();
        if (!AccountService.IsValidOfflineName(name))
        {
            OfflineError.Text = "昵称需为 1-64 个字符，不能包含控制字符，且首尾不能有空格。";
            OfflineError.Visibility = Visibility.Visible;
            return;
        }
        var mcVersion = LauncherApp.I.CurrentVersion?.MinecraftVersion ?? "";
        if (Version.TryParse(mcVersion, out var version) && version >= new Version(1, 20, 3) && name.Length > 16)
        {
            OfflineError.Text = "Minecraft 1.20.3 及以后最多只能使用 16 个字符的玩家名。";
            OfflineError.Visibility = Visibility.Visible;
            return;
        }
        var account = new AccountSettings
        {
            Type = "offline",
            Name = name,
            Uuid = AccountService.OfflineUuid(name)
        };
        LauncherApp.I.RememberAccount(account);
        LauncherApp.I.Save();
        LauncherApp.I.RaiseSettingsChanged();
        Changed = true;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void Yggdrasil_Click(object sender, RoutedEventArgs e)
    {
        YggError.Visibility = Visibility.Collapsed;
        var serverText = YggServer.Text.Trim();
        var user = YggUser.Text.Trim();
        var password = YggPassword.Password;
        if (string.IsNullOrWhiteSpace(user))
        {
            ShowYggError("请填写邮箱或用户名。");
            return;
        }
        if (string.IsNullOrEmpty(password))
        {
            ShowYggError("请填写密码。");
            return;
        }

        YggButton.IsEnabled = false;
        YggButton.Content = "登录中…";
        try
        {
            var server = YggdrasilService.NormalizeServer(serverText);
            var profile = await YggdrasilService.AuthenticateAsync(server, user, password);
            var account = new AccountSettings();
            YggdrasilService.Apply(account, profile);
            LauncherApp.I.RememberAccount(account);
            LauncherApp.I.Save();
            LauncherApp.I.RaiseSettingsChanged();
            Changed = true;
            Close();
        }
        catch (Exception ex)
        {
            ShowYggError(ex.Message);
        }
        finally
        {
            YggButton.IsEnabled = true;
            YggButton.Content = "登录外置账号";
        }
    }

    private void ShowYggError(string message)
    {
        YggError.Text = message;
        YggError.Visibility = Visibility.Visible;
    }
}
