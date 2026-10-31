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
        CurrentText.Text = $"当前：{account.Name}（{(account.IsMicrosoft ? "Microsoft 正版" : "离线")}）";
        OfflineName.Text = account.IsMicrosoft ? "" : account.Name;
    }

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
        account.RefreshTokenProtected = "";
        account.AccessTokenProtected = "";
        account.AccessTokenExpiresUtc = DateTime.MinValue;
        LauncherApp.I.Save();
        LauncherApp.I.RaiseSettingsChanged();
        Changed = true;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
