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
            YggUser.Text = account.YggdrasilLoginName;
        }
        if (account.IsNide)
        {
            NideServer.Text = account.NideServer;
            NideUser.Text = account.NideLoginName;
        }
        var showRoles = account.YggdrasilCharacters.Count > 1;
        YggRoleButton.Visibility = account.IsYggdrasil && showRoles ? Visibility.Visible : Visibility.Collapsed;
        NideRoleButton.Visibility = account.IsNide && showRoles ? Visibility.Visible : Visibility.Collapsed;
        if (account.IsYggdrasil && showRoles)
        {
            YggRoleHint.Text = $"已保存 {account.YggdrasilCharacters.Count} 个角色，可切换";
            YggRoleHint.Visibility = Visibility.Visible;
        }
        if (account.IsNide && showRoles)
        {
            NideRoleHint.Text = $"已保存 {account.YggdrasilCharacters.Count} 个角色，可切换";
            NideRoleHint.Visibility = Visibility.Visible;
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
        account.IsMicrosoft ? "Microsoft 正版" :
        account.IsYggdrasil ? "外置登录" :
        account.IsNide ? "统一通行证" : "离线";

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
            if (profile.Characters.Count > 1)
            {
                var chosen = YggdrasilCharacterDialog.Show(this, profile.Characters, profile.Uuid);
                if (chosen is null) return;
                profile = new YggdrasilProfile
                {
                    Name = chosen.Name,
                    Uuid = chosen.Id,
                    AccessToken = profile.AccessToken,
                    ClientToken = profile.ClientToken,
                    Server = profile.Server,
                    Characters = profile.Characters
                };
            }
            var account = new AccountSettings();
            YggdrasilService.Apply(account, profile);
            await YggdrasilService.UpdateTexturesAsync(account);
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

    private async void Nide_Click(object sender, RoutedEventArgs e)
    {
        NideError.Visibility = Visibility.Collapsed;
        var serverId = NideServer.Text.Trim();
        var user = NideUser.Text.Trim();
        var password = NidePassword.Password;
        if (string.IsNullOrWhiteSpace(user)) { ShowNideError("请填写邮箱或统一通行证账号。"); return; }
        if (string.IsNullOrEmpty(password)) { ShowNideError("请填写密码。"); return; }

        NideButton.IsEnabled = false;
        NideButton.Content = "登录中…";
        try
        {
            serverId = Nide8Service.NormalizeServerId(serverId);
            var profile = await Nide8Service.AuthenticateAsync(serverId, user, password);
            if (profile.Characters.Count > 1)
            {
                var chosen = YggdrasilCharacterDialog.Show(this, profile.Characters, profile.Uuid);
                if (chosen is null) return;
                profile = new YggdrasilProfile
                {
                    Name = chosen.Name,
                    Uuid = chosen.Id,
                    AccessToken = profile.AccessToken,
                    ClientToken = profile.ClientToken,
                    Server = profile.Server,
                    Characters = profile.Characters
                };
            }
            var account = new AccountSettings();
            Nide8Service.Apply(account, profile, serverId);
            await YggdrasilService.UpdateTexturesAsync(account);
            LauncherApp.I.RememberAccount(account);
            LauncherApp.I.Save();
            LauncherApp.I.RaiseSettingsChanged();
            Changed = true;
            Close();
        }
        catch (Exception ex)
        {
            ShowNideError(ex.Message);
        }
        finally
        {
            NideButton.IsEnabled = true;
            NideButton.Content = "登录统一通行证";
        }
    }

    private void ShowNideError(string message)
    {
        NideError.Text = message;
        NideError.Visibility = Visibility.Visible;
    }

    private async void ThirdPartyRole_Click(object sender, RoutedEventArgs e)
    {
        var account = LauncherApp.I.Settings.Account;
        if (account.YggdrasilCharacters.Count < 2) return;
        var chosen = YggdrasilCharacterDialog.Show(this, account.YggdrasilCharacters, account.Uuid);
        if (chosen is null) return;

        account.Name = chosen.Name;
        account.Uuid = chosen.Id;
        await YggdrasilService.UpdateTexturesAsync(account);
        LauncherApp.I.Save();
        LauncherApp.I.RaiseSettingsChanged();
        Changed = true;
        Close();
    }
}
