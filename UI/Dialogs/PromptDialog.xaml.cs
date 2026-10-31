using System.Windows;
using System.Windows.Input;
using Vermilune.Core;

namespace Vermilune.UI.Dialogs;

public partial class PromptDialog : Window
{
    private readonly Func<string, string?>? _validate;
    public string Value { get; private set; } = "";

    public PromptDialog(Func<string, string?>? validate = null)
    {
        InitializeComponent();
        _validate = validate;
        MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };
        Loaded += (_, _) => { Input.Focus(); Input.SelectAll(); };
    }

    public static string? Show(Window? owner, string title, string message, string initial = "",
        string? placeholder = null, Func<string, string?>? validate = null)
    {
        var dialog = new PromptDialog(validate) { Owner = owner };
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.Input.Text = initial;
        if (placeholder is not null) dialog.Input.Tag = placeholder;
        if (owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var value = Input.Text.Trim();
        var error = _validate?.Invoke(value) ?? (value.Length == 0 ? "内容不能为空。" : null);
        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        Value = value;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
