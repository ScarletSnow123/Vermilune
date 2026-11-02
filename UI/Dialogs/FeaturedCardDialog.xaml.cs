using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Vermilune.UI.Dialogs;

public partial class FeaturedCardDialog : Window
{
    private const string DefaultTitle = "←----觉真可爱";
    private const string DefaultSubtitle = "古明地觉 · 地灵殿的主人，今天也在吃蛋糕";
    private const string DefaultLink = "https://s1.imagehub.cc/images/2026/07/08/26dd444015c43ab6834afad1864c0aea.th.jpg";
    private const string DefaultImage = "pack://application:,,,/Assets/Satori.jpg";

    private string _imagePath;

    public string ImagePath => _imagePath;
    public string CardTitle => TitleBox.Text.Trim();
    public string CardSubtitle => SubtitleBox.Text.Trim();
    public string CardLink => LinkBox.Text.Trim();

    public FeaturedCardDialog(string imagePath, string title, string subtitle, string link)
    {
        InitializeComponent();
        _imagePath = imagePath ?? "";
        TitleBox.Text = string.IsNullOrWhiteSpace(title) ? DefaultTitle : title;
        SubtitleBox.Text = string.IsNullOrWhiteSpace(subtitle) ? DefaultSubtitle : subtitle;
        LinkBox.Text = string.IsNullOrWhiteSpace(link) ? DefaultLink : link;
        UpdatePreview();
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
        LinkBox.Text = DefaultLink;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        try
        {
            var source = string.Empty;
            if (!string.IsNullOrWhiteSpace(_imagePath) && File.Exists(_imagePath))
                source = _imagePath;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 224;
            bitmap.UriSource = new Uri(string.IsNullOrEmpty(source) ? DefaultImage : source, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            PreviewImage.Source = bitmap;
            ImagePathText.Text = string.IsNullOrEmpty(source) ? "未选择，使用默认图片" : Path.GetFileName(source);
            ErrorText.Visibility = Visibility.Collapsed;
        }
        catch
        {
            PreviewImage.Source = null;
            ImagePathText.Text = "图片无法读取，保存后会继续使用默认图片";
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text))
        {
            ErrorText.Text = "标题不能为空。";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
