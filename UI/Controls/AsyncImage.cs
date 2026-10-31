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

    private async Task LoadAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (Memory.TryGetValue(url, out var cached))
        {
            Source = cached;
            return;
        }

        var task = Pending.GetOrAdd(url, LoadCoreAsync);
        var bitmap = await task;
        Pending.TryRemove(url, out _);
        if (bitmap is null || !string.Equals(Uri, url, StringComparison.Ordinal)) return;
        Memory[url] = bitmap;
        Source = bitmap;
    }

    private static async Task<BitmapSource?> LoadCoreAsync(string url)
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
            image.DecodePixelWidth = 128;
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
