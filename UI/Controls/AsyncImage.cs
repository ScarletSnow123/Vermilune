using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Vermilune.Core;

namespace Vermilune.UI.Controls;

/// <summary>异步加载远端图标，带内存/磁盘缓存，避免列表滚动时卡住 UI。</summary>
public sealed class AsyncImage : Image
{
    private static readonly ConcurrentDictionary<string, BitmapSource> Memory = new();
    private static readonly ConcurrentDictionary<string, Task<BitmapSource?>> Pending = new();

    public static readonly DependencyProperty UriProperty = DependencyProperty.Register(
        nameof(Uri), typeof(string), typeof(AsyncImage),
        new PropertyMetadata("", OnUriChanged));

    public string Uri
    {
        get => (string)GetValue(UriProperty);
        set => SetValue(UriProperty, value);
    }

    private static void OnUriChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AsyncImage image) _ = image.LoadAsync(e.NewValue as string);
    }

    public static readonly DependencyProperty CropProperty = DependencyProperty.Register(
        nameof(Crop), typeof(Int32Rect), typeof(AsyncImage),
        new PropertyMetadata(new Int32Rect(0, 0, 0, 0), OnCropChanged));

    public Int32Rect Crop
    {
        get => (Int32Rect)GetValue(CropProperty);
        set => SetValue(CropProperty, value);
    }

    private static void OnCropChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AsyncImage image) _ = image.LoadAsync(image.Uri);
    }

    private async Task LoadAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var full = Crop.Width > 0 && Crop.Height > 0;
        var key = full ? url + "|full" : url;
        var source = CropBitmap(Memory.TryGetValue(key, out var cached) ? cached : await LoadSourceAsync(url, full));
        if (string.Equals(Uri, url, StringComparison.Ordinal)) Source = source;
    }

    private async Task<BitmapSource?> LoadSourceAsync(string url, bool full)
    {
        var key = full ? url + "|full" : url;
        var task = Pending.GetOrAdd(key, _ => LoadCoreAsync(url, full));
        var bitmap = await task;
        Pending.TryRemove(key, out _);
        if (bitmap is null) return null;
        Memory[key] = bitmap;
        return bitmap;
    }

    private BitmapSource? CropBitmap(BitmapSource? bitmap)
    {
        if (bitmap is null) return null;
        var crop = Crop;
        if (crop.Width <= 0 || crop.Height <= 0) return bitmap;
        if (crop.X < 0 || crop.Y < 0 || crop.X + crop.Width > bitmap.PixelWidth || crop.Y + crop.Height > bitmap.PixelHeight)
            return bitmap;
        return new CroppedBitmap(bitmap, crop);
    }

    private static async Task<BitmapSource?> LoadCoreAsync(string url, bool full)
    {
        try
        {
            var cache = CachePath(url);
            byte[] bytes;
            if (File.Exists(cache) && new FileInfo(cache).Length > 32)
            {
                bytes = await File.ReadAllBytesAsync(cache);
            }
            else
            {
                bytes = await HttpService.GetBytesAsync(url, CancellationToken.None);
                Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
                await File.WriteAllBytesAsync(cache, bytes);
            }

            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            // 列表图标只需要 128px；只有需要精确裁剪的贴图才保留原始分辨率。
            if (!full) image.DecodePixelWidth = 128;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static string CachePath(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        return Path.Combine(AppPaths.CacheDir, "icons", hash + ".img");
    }
}
