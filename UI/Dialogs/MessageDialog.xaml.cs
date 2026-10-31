using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Vermilune.Core;
using Vermilune.UI;

namespace Vermilune.UI.Dialogs;

public partial class MessageDialog : Window
{
    private bool _result;

    public MessageDialog()
    {
        InitializeComponent();
        MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };
    }

    public static bool Show(Window? owner, string title, string message,
        string primary = "好的", string? secondary = null, bool danger = false, bool info = false)
    {
        var dialog = new MessageDialog { Owner = owner };
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.PrimaryButton.Content = primary;
        if (secondary is not null)
        {
            dialog.SecondaryButton.Content = secondary;
            dialog.SecondaryButton.Visibility = Visibility.Visible;
        }
        if (danger)
        {
            dialog.IconPath.Data = (Geometry)Application.Current.Resources["IconWarning"];
            dialog.IconPath.Stroke = (Brush)Application.Current.Resources["DangerBrush"];
            dialog.IconBox.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xEB, 0x6B, 0x6B));
        }
        else if (info)
        {
            dialog.IconPath.Data = (Geometry)Application.Current.Resources["IconInfo"];
        }
        if (owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        return dialog._result;
    }

    private void Primary_Click(object sender, RoutedEventArgs e) { _result = true; Close(); }
    private void Secondary_Click(object sender, RoutedEventArgs e) { _result = false; Close(); }
}
