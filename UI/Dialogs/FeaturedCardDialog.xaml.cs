using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Vermilune.UI.Dialogs;

public partial class FeaturedCardDialog : Window
{
    private const string DefaultTitle = "←----觉真可爱";
    private const string DefaultSubtitle = "古明地觉 · 地灵殿的主人，今天也在吃蛋糕";
    private const string DefaultTitleColor = "#FFF4A6C0";
    private const string DefaultImage = "pack://application:,,,/Assets/Satori.jpg";

    private string _imagePath;

    public string ImagePath => _imagePath;
    public string CardTitle => TitleBox.Text.Trim();
    public string CardSubtitle => SubtitleBox.Text.Trim();
    public string CardTitleColor { get; private set; } = DefaultTitleColor;
    public string CardSubtitleColor { get; private set; } = "";

    public FeaturedCardDialog(string imagePath, string title, string subtitle, string titleColor, string subtitleColor)
    {
        InitializeComponent();
        _imagePath = imagePath ?? "";
        TitleBox.Text = title ?? "";
        SubtitleBox.Text = subtitle ?? "";
        TitleColorBox.Text = string.IsNullOrWhiteSpace(titleColor) ? DefaultTitleColor : titleColor;
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
        TitleColorBox.Text = DefaultTitleColor;
        SubtitleColorBox.Text = "";
        UpdatePreview();
        UpdateColorPreviews();
    }

    private void UpdatePreview()
    {
        try
        {
            var source = !string.IsNullOrWhiteSpace(_imagePath) && File.Exists(_imagePath)
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
            ImagePathText.Text = string.IsNullOrWhiteSpace(_imagePath) ? "未选择，使用默认图片" : Path.GetFileName(_imagePath);
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
        TitleColorPreview.Background = TryColor(TitleColorBox.Text, out var title)
            ? new SolidColorBrush(title)
            : Brushes.Transparent;
        SubtitleColorPreview.Background = TryColor(SubtitleColorBox.Text, out var subtitle)
            ? new SolidColorBrush(subtitle)
            : (Brush)FindResource("TextMutedBrush");
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryColor(TitleColorBox.Text, out var titleColor))
            titleColor = (Color)ColorConverter.ConvertFromString(DefaultTitleColor);
        if (!string.IsNullOrWhiteSpace(SubtitleColorBox.Text) && !TryColor(SubtitleColorBox.Text, out _))
        {
            ErrorText.Text = "小字颜色格式不正确，请使用 #RRGGBB 或 #AARRGGBB。";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        CardTitleColor = titleColor.ToString();
        CardSubtitleColor = string.IsNullOrWhiteSpace(SubtitleColorBox.Text)
            ? ""
            : ((Color)ColorConverter.ConvertFromString(SubtitleColorBox.Text.Trim())).ToString();
        DialogResult = true;
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
