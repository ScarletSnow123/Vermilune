using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Vermilune.UI.Converters;

/// <summary>从文件流读取图片，避免 UriSource 缓存导致透明度丢失。</summary>
public sealed class PathImageConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, BitmapSource> Cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
            if (Cache.TryGetValue(key, out var cached)) return cached;
            using var stream = new MemoryStream(File.ReadAllBytes(path));
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.DecodePixelWidth = 128;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            if (Cache.Count > 256) Cache.Clear();
            Cache[key] = image;
            return image;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
