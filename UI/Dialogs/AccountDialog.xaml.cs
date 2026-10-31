using System.Windows;
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
        var account = LauncherApp.I.Settings.Account;
        AccountService.ApplyProfile(account, profile);
        account.Type = "microsoft";
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
            OfflineError.Text = "昵称需为 1-16 个字符，不能包含控制字符。";
            OfflineError.Visibility = Visibility.Visible;
            return;
        }
        var account = LauncherApp.I.Settings.Account;
        account.Type = "offline";
        account.Name = name;
        account.Uuid = AccountService.OfflineUuid(name);
        account.SkinUrl = "";
        account.Xuid = "";
        account.YggdrasilServer = "";
        account.ClientTokenProtected = "";
        account.RefreshTokenProtected = "";
        account.AccessTokenProtected = "";
        account.AccessTokenExpiresUtc = DateTime.MinValue;
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
            var account = LauncherApp.I.Settings.Account;
            YggdrasilService.Apply(account, profile);
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
