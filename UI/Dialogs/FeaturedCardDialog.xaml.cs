using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Vermilune.UI.Dialogs;

public partial class FeaturedCardDialog : Window
{
    private const string DefaultTitle = "Vermilune 1.2分支";
    private const string DefaultSubtitle = "这个卡片可以自定义哦！";
    private const string DefaultImage = "pack://application:,,,/Assets/VermiluneRing.png";

    private string _imagePath;

    public string ImagePath => _imagePath;
    public string CardTitle => TitleBox.Text.Trim();
    public string CardSubtitle => SubtitleBox.Text.Trim();
    public string CardTitleColor { get; private set; } = "";
    public string CardSubtitleColor { get; private set; } = "";

    public FeaturedCardDialog(string imagePath, string title, string subtitle, string titleColor, string subtitleColor)
    {
        InitializeComponent();
        _imagePath = imagePath ?? "";
        TitleBox.Text = title ?? "";
        SubtitleBox.Text = subtitle ?? "";
        TitleColorBox.Text = titleColor ?? "";
        SubtitleColorBox.Text = subtitleColor ?? "";
        UpdatePreview();
        UpdateColorPreviews();
    }

    private void ChooseImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择首页卡片图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.webp|所有文件|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        _imagePath = dialog.FileName;
        UpdatePreview();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _imagePath = "";
        TitleBox.Text = DefaultTitle;
        SubtitleBox.Text = DefaultSubtitle;
        TitleColorBox.Text = "";
        SubtitleColorBox.Text = "";
        UpdatePreview();
        UpdateColorPreviews();
    }

    private void UpdatePreview()
    {
        try
        {
            var source = !string.IsNullOrWhiteSpace(_imagePath) &&
                         (_imagePath.StartsWith("pack://", StringComparison.OrdinalIgnoreCase) || File.Exists(_imagePath))
                ? _imagePath
                : DefaultImage;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 224;
            bitmap.UriSource = new Uri(source, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            PreviewImage.Source = bitmap;
            ImagePathText.Text = string.IsNullOrWhiteSpace(_imagePath)
                ? "未选择，使用默认图片"
                : (_imagePath.StartsWith("pack://", StringComparison.OrdinalIgnoreCase)
                    ? "内置 Satori 图片"
                    : Path.GetFileName(_imagePath));
        }
        catch
        {
            PreviewImage.Source = null;
            ImagePathText.Text = "图片无法读取，保存后会继续使用默认图片";
        }
    }

    private void TitleColorBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => UpdateColorPreviews();

    private void SubtitleColorBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => UpdateColorPreviews();

    private void UpdateColorPreviews()
    {
        if (TitleColorPreview is null || SubtitleColorPreview is null) return;
        var themeText = (Brush)FindResource("TextBrush");
        TitleColorPreview.Background = string.IsNullOrWhiteSpace(TitleColorBox.Text)
            ? themeText
            : (TryColor(TitleColorBox.Text, out var title) ? new SolidColorBrush(title) : Brushes.Transparent);
        SubtitleColorPreview.Background = string.IsNullOrWhiteSpace(SubtitleColorBox.Text)
            ? themeText
            : (TryColor(SubtitleColorBox.Text, out var subtitle) ? new SolidColorBrush(subtitle) : Brushes.Transparent);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(TitleColorBox.Text) && !TryColor(TitleColorBox.Text, out _))
        {
            ErrorText.Text = "大字颜色格式不正确，请使用 #RRGGBB 或 #AARRGGBB。";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        if (!string.IsNullOrWhiteSpace(SubtitleColorBox.Text) && !TryColor(SubtitleColorBox.Text, out _))
        {
            ErrorText.Text = "小字颜色格式不正确，请使用 #RRGGBB 或 #AARRGGBB。";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        CardTitleColor = NormalizeColor(TitleColorBox.Text);
        CardSubtitleColor = NormalizeColor(SubtitleColorBox.Text);
        DialogResult = true;
    }

    private static string NormalizeColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var value = text.Trim();
        if (!value.StartsWith('#')) value = "#" + value;
        return TryColor(value, out var color) ? color.ToString() : "";
    }

    private static bool TryColor(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim();
        if (!value.StartsWith('#')) value = "#" + value;
        try
        {
            if (ColorConverter.ConvertFromString(value) is not Color parsed) return false;
            color = parsed;
            return true;
        }
        catch { return false; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
