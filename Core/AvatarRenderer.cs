using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Vermilune.Core;

/// <summary>
/// MCCAG v3 API 的 Minimal 头像渲染移植版。
/// 默认 head 类型：1000x1000 画布、600px 头部、41 倍帽层、15px 阴影。
/// </summary>
public static class AvatarRenderer
{
    private const int CanvasSize = 1000;

    /// <summary>
    /// 正面 Minecraft PFP：8×8 头部 + 帽层，按整数块直接写入像素，避免任何插值模糊。
    /// </summary>
    public static BitmapSource RenderFrontHead(BitmapSource skin, int outputSize = 168)
    {
        var ratio = Math.Max(1, skin.PixelWidth / 64);
        var head = ReadChunk(skin, 8 * ratio, 8 * ratio, 8 * ratio);
        var hat = skin.PixelHeight >= 64 * ratio ? ReadChunk(skin, 40 * ratio, 8 * ratio, 8 * ratio) : null;
        var scale = Math.Max(1, outputSize / 8);
        var size = scale * 8;
        var pixels = new byte[size * size * 4];
        for (var py = 0; py < 8; py++)
        for (var px = 0; px < 8; px++)
        {
            var index = (py * 8 + px) * 4;
            byte b = head[index], g = head[index + 1], r = head[index + 2], a = head[index + 3];
            if (hat is not null)
            {
                var hi = index;
                var ha = hat[hi + 3];
                if (ha > 0)
                {
                    var inv = 255 - ha;
                    b = (byte)((hat[hi] * ha + b * inv) / 255);
                    g = (byte)((hat[hi + 1] * ha + g * inv) / 255);
                    r = (byte)((hat[hi + 2] * ha + r * inv) / 255);
                    a = (byte)(ha + a * inv / 255);
                }
            }
            for (var y = 0; y < scale; y++)
            for (var x = 0; x < scale; x++)
            {
                var o = ((py * scale + y) * size + px * scale + x) * 4;
                pixels[o] = b; pixels[o + 1] = g; pixels[o + 2] = r; pixels[o + 3] = a;
            }
        }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        bitmap.Freeze();
        return bitmap;
    }

    public static DrawingImage RenderFrontHeadDrawing(BitmapSource skin)
    {
        var ratio = Math.Max(1, skin.PixelWidth / 64);
        var head = ReadChunk(skin, 8 * ratio, 8 * ratio, 8 * ratio);
        var hat = skin.PixelHeight >= 64 * ratio ? ReadChunk(skin, 40 * ratio, 8 * ratio, 8 * ratio) : null;
        var group = new DrawingGroup();
        for (var py = 0; py < 8; py++)
        for (var px = 0; px < 8; px++)
        {
            var i = (py * 8 + px) * 4;
            AddPixel(group, px, py, head[i + 3], head[i + 2], head[i + 1], head[i], 1);
        }
        if (hat is not null)
        {
            for (var py = 0; py < 8; py++)
            for (var px = 0; px < 8; px++)
            {
                var i = (py * 8 + px) * 4;
                if (hat[i + 3] == 0) continue;
                AddPixel(group, px, py, hat[i + 3], hat[i + 2], hat[i + 1], hat[i], hat[i + 3] / 255.0);
            }
        }
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private static void AddPixel(DrawingGroup group, int x, int y, byte a, byte r, byte g, byte b, double opacity)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b)) { Opacity = opacity };
        brush.Freeze();
        group.Children.Add(new GeometryDrawing(brush, null, new RectangleGeometry(new Rect(x, y, 1, 1))));
    }

    private static byte[] ReadChunk(BitmapSource skin, int x, int y, int size)
    {
        x = Math.Clamp(x, 0, Math.Max(0, skin.PixelWidth - size));
        y = Math.Clamp(y, 0, Math.Max(0, skin.PixelHeight - size));
        var cropped = new CroppedBitmap(skin, new Int32Rect(x, y, size, size));
        var converted = new FormatConvertedBitmap(cropped, PixelFormats.Bgra32, null, 0);
        var source = new byte[size * size * 4];
        converted.CopyPixels(source, size * 4, 0);
        var ratio = Math.Max(1, size / 8);
        var result = new byte[8 * 8 * 4];
        for (var py = 0; py < 8; py++)
        for (var px = 0; px < 8; px++)
        {
            var sx = Math.Min(size - 1, px * ratio + ratio / 2);
            var sy = Math.Min(size - 1, py * ratio + ratio / 2);
            var si = (sy * size + sx) * 4;
            var di = (py * 8 + px) * 4;
            result[di] = source[si];
            result[di + 1] = source[si + 1];
            result[di + 2] = source[si + 2];
            result[di + 3] = source[si + 3];
        }
        return result;
    }

    public static BitmapSource RenderMinimalHeadOnly(BitmapSource skin, int outputSize = 180, double dpiScale = 1.0)
    {
        var old = skin.PixelHeight * 2 == skin.PixelWidth;
        var source = NormalizeSkin(skin, old, out var ratio);
        var canvas = new RenderTargetBitmap(CanvasSize, CanvasSize, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        using (var dc = visual.RenderOpen())
        {
            DrawSlice(dc, source, (int)Math.Round(16 * ratio), (int)Math.Round(16 * ratio),
                Math.Max(1, (int)Math.Round(16 * ratio)), Math.Max(1, (int)Math.Round(16 * ratio)),
                26.875 / ratio, 287, 131);
            if (source.PixelWidth >= 128)
                DrawSlice(dc, source, (int)Math.Round(80 * ratio), (int)Math.Round(16 * ratio),
                    Math.Max(1, (int)Math.Round(16 * ratio)), Math.Max(1, (int)Math.Round(16 * ratio)),
                    30.8125 / ratio, 254, 107);
        }
        canvas.Render(visual);
        return CropToContent(canvas, outputSize, 96 * dpiScale);
    }

    public static BitmapSource RenderMinimalBody(BitmapSource skin, int outputSize = 180, double dpiScale = 1.0)
    {
        var old = skin.PixelHeight * 2 == skin.PixelWidth;
        var source = NormalizeSkin(skin, old, out var ratio);
        var canvas = new RenderTargetBitmap(CanvasSize, CanvasSize, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        using (var dc = visual.RenderOpen())
        {
            var ops = old ? OldBody : NewBody;
            for (var i = 0; i < ops.Length - 2; i++)
            {
                var op = ops[i];
                DrawSlice(dc, source,
                    (int)Math.Round(op.X * ratio), (int)Math.Round(op.Y * ratio),
                    Math.Max(1, (int)Math.Round(op.W * ratio)), Math.Max(1, (int)Math.Round(op.H * ratio)),
                    op.Factor / ratio, op.PasteX, op.PasteY);
            }
        }
        canvas.Render(visual);
        return CropToContent(canvas, outputSize, 96 * dpiScale);
    }

    public static BitmapSource RenderMinimalFull(BitmapSource skin, int scale = 100, int shadow = 50, double dpiScale = 1.0)
    {
        var old = skin.PixelHeight * 2 == skin.PixelWidth;
        var source = NormalizeSkin(skin, old, out var ratio);
        var baseCanvas = new RenderTargetBitmap(CanvasSize, CanvasSize, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        using (var dc = visual.RenderOpen())
        {
            foreach (var op in old ? OldBody : NewBody)
                DrawSlice(dc, source,
                    (int)Math.Round(op.X * ratio), (int)Math.Round(op.Y * ratio),
                    Math.Max(1, (int)Math.Round(op.W * ratio)), Math.Max(1, (int)Math.Round(op.H * ratio)),
                    op.Factor / ratio, op.PasteX, op.PasteY);
        }
        baseCanvas.Render(visual);
        return CropToContent(Compose(baseCanvas, scale, shadow), 180, 96 * dpiScale);
    }

    private static BitmapSource CropToContent(BitmapSource source, int outputSize, double dpi = 96)
    {
        var converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var w = converted.PixelWidth;
        var h = converted.PixelHeight;
        var stride = w * 4;
        var pixels = new byte[stride * h];
        converted.CopyPixels(pixels, stride, 0);

        var minX = w; var minY = h; var maxX = -1; var maxY = -1;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (pixels[y * stride + x * 4 + 3] <= 24) continue;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }
        if (maxX < minX || maxY < minY) return source;

        var cropW = maxX - minX + 1;
        var cropH = maxY - minY + 1;
        var crop = new CroppedBitmap(converted, new Int32Rect(minX, minY, cropW, cropH));
        var padding = Math.Max(2, outputSize / 14);
        var target = new RenderTargetBitmap(outputSize, outputSize, dpi, dpi, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(visual, EdgeMode.Aliased);
        using (var dc = visual.RenderOpen())
        {
            var available = outputSize - padding * 2;
            var ratio = Math.Min((double)available / cropW, (double)available / cropH);
            var drawW = cropW * ratio;
            var drawH = cropH * ratio;
            dc.DrawImage(crop, new Rect((outputSize - drawW) / 2, (outputSize - drawH) / 2, drawW, drawH));
        }
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static readonly (int X, int Y, int W, int H, double Factor, int PasteX, int PasteY)[] NewBody =
    {
        (40, 40, 16, 24, 8.0625, 437, 561),
        (40, 72, 16, 24, 8.6575, 432, 555),
        (8, 40, 8, 24, 8.375, 434, 751),
        (8, 72, 8, 24, 9.375, 428, 737),
        (40, 104, 8, 24, 8.375, 505, 751),
        (8, 104, 8, 24, 9.375, 503, 737),
        (86, 40, 6, 24, 8.167, 388, 561),
        (88, 72, 6, 24, 9.5, 382, 538),
        (74, 104, 6, 24, 8.167, 566, 561),
        (104, 104, 6, 24, 9.5, 564, 538),
        (16, 16, 16, 16, 26.875, 287, 131),
        (80, 16, 16, 16, 30.8125, 254, 107),
    };

    private static readonly (int X, int Y, int W, int H, double Factor, int PasteX, int PasteY)[] OldBody =
    {
        (40, 40, 16, 24, 8.0625, 437, 561),
        (8, 40, 8, 24, 8.375, 434, 751),
        (8, 40, 8, 24, 8.375, 505, 751),
        (86, 40, 6, 24, 8.167, 388, 561),
        (86, 40, 6, 24, 8.167, 566, 561),
        (16, 16, 16, 16, 26.875, 287, 131),
        (80, 16, 16, 16, 30.8125, 254, 107),
    };

    private static BitmapSource Compose(BitmapSource baseCanvas, int scale, int shadow)
    {
        var shadowBitmap = MakeShadow(baseCanvas, shadow);
        var output = new RenderTargetBitmap(CanvasSize, CanvasSize, 96, 96, PixelFormats.Pbgra32);
        var compose = new DrawingVisual();
        using (var dc = compose.RenderOpen())
        {
            dc.DrawImage(shadowBitmap, new Rect(0, 0, CanvasSize, CanvasSize));
            dc.DrawImage(baseCanvas, new Rect(0, 0, CanvasSize, CanvasSize));
        }
        output.Render(compose);
        output.Freeze();

        if (scale == 100) return output;
        var scaled = new RenderTargetBitmap(Math.Max(1, 10 * scale), Math.Max(1, 10 * scale),
            96, 96, PixelFormats.Pbgra32);
        var scaleVisual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(scaleVisual, BitmapScalingMode.HighQuality);
        using (var dc = scaleVisual.RenderOpen())
            dc.DrawImage(output, new Rect(0, 0, 10 * scale, 10 * scale));
        scaled.Render(scaleVisual);
        scaled.Freeze();
        return scaled;
    }

    public static BitmapSource RenderMinimalHead(BitmapSource skin, int scale = 100, int shadow = 50)
    {
        var source = NormalizeSkin(skin, false, out _);
        var baseCanvas = new RenderTargetBitmap(CanvasSize, CanvasSize, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        using (var dc = visual.RenderOpen())
        {
            DrawSlice(dc, source, 16, 16, 16, 16, 37.5, 200, 200);
            if (source.PixelWidth >= 128)
                DrawSlice(dc, source, 80, 16, 16, 16, 41, 175, 175);
        }
        baseCanvas.Render(visual); // 先画主体，阴影在下面单独合成

        var shadowBitmap = MakeShadow(baseCanvas, shadow);
        var output = new RenderTargetBitmap(CanvasSize, CanvasSize, 96, 96, PixelFormats.Pbgra32);
        var compose = new DrawingVisual();
        using (var dc = compose.RenderOpen())
        {
            dc.DrawImage(shadowBitmap, new Rect(0, 0, CanvasSize, CanvasSize));
            dc.DrawImage(baseCanvas, new Rect(0, 0, CanvasSize, CanvasSize));
        }
        output.Render(compose);
        output.Freeze();

        if (scale == 100) return output;
        var scaled = new RenderTargetBitmap(Math.Max(1, 10 * scale), Math.Max(1, 10 * scale),
            96, 96, PixelFormats.Pbgra32);
        var scaleVisual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(scaleVisual, BitmapScalingMode.HighQuality);
        using (var dc = scaleVisual.RenderOpen())
            dc.DrawImage(output, new Rect(0, 0, 10 * scale, 10 * scale));
        scaled.Render(scaleVisual);
        scaled.Freeze();
        return scaled;
    }

    private static BitmapSource MakeShadow(BitmapSource source, int strength)
    {
        // 用 WPF 的 BlurEffect 复刻 Canvas 的 shadowBlur；完全透明的像素不会产生阴影。
        var image = new Image
        {
            Source = source,
            Width = CanvasSize,
            Height = CanvasSize,
            Effect = new BlurEffect { Radius = 15 },
            Opacity = Math.Clamp(strength / 100.0, 0, 1)
        };
        image.Measure(new Size(CanvasSize, CanvasSize));
        image.Arrange(new Rect(0, 0, CanvasSize, CanvasSize));
        var bitmap = new RenderTargetBitmap(CanvasSize, CanvasSize, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(image);
        bitmap.Freeze();
        return bitmap;
    }

    private static void DrawSlice(DrawingContext dc, BitmapSource skin,
        int x, int y, int w, int h, double factor, double pasteX, double pasteY)
    {
        var crop = new CroppedBitmap(skin, new Int32Rect(x, y, w, h));
        dc.DrawImage(crop, new Rect(pasteX, pasteY, w * factor, h * factor));
    }

    private static BitmapSource NormalizeSkin(BitmapSource skin, bool old, out double ratio)
    {
        var targetW = 128;
        var targetH = old ? 64 : 128;
        if (skin.PixelWidth >= targetW && skin.PixelHeight >= targetH)
        {
            // 高清皮肤（128/256/2048）保留原始像素，按 128 基准换算裁剪坐标。
            ratio = skin.PixelWidth / 128.0;
            return skin;
        }
        ratio = 1;

        var source = skin.Format == PixelFormats.Bgra32
            ? skin
            : new FormatConvertedBitmap(skin, PixelFormats.Bgra32, null, 0);
        var sw = source.PixelWidth;
        var sh = source.PixelHeight;
        var srcStride = sw * 4;
        var srcPixels = new byte[srcStride * sh];
        source.CopyPixels(srcPixels, srcStride, 0);

        var dstPixels = new byte[targetW * 4 * targetH];
        for (var y = 0; y < targetH; y++)
        {
            var sy = Math.Min(sh - 1, y * sh / targetH);
            for (var x = 0; x < targetW; x++)
            {
                var sx = Math.Min(sw - 1, x * sw / targetW);
                var si = sy * srcStride + sx * 4;
                var di = y * targetW * 4 + x * 4;
                dstPixels[di] = srcPixels[si];
                dstPixels[di + 1] = srcPixels[si + 1];
                dstPixels[di + 2] = srcPixels[si + 2];
                dstPixels[di + 3] = srcPixels[si + 3];
            }
        }

        var bitmap = new WriteableBitmap(targetW, targetH, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, targetW, targetH), dstPixels, targetW * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }
}
