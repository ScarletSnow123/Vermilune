using System.Windows;
using System.Windows.Media;

namespace Vermilune.UI;

public static class ThemeManager
{
    public static Color Accent { get; private set; } = Color.FromRgb(0xE5, 0xB5, 0x67);

    public static void Apply(string accentHex, double opacity, bool hasBackgroundImage, bool light = false)
    {
        var accent = ParseColor(accentHex, Color.FromRgb(0xE5, 0xB5, 0x67));
        Accent = accent;
        opacity = Math.Clamp(opacity, 0.25, 1.0);

        var res = Application.Current.Resources;
        res["AccentBrush"] = Solid(accent);
        res["AccentHoverBrush"] = Solid(Lighten(accent, 0.18));
        res["AccentPressedBrush"] = Solid(Darken(accent, 0.14));
        res["AccentSoftBrush"] = Solid(WithAlpha(accent, 0x2A));
        res["AccentGradientBrush"] = new LinearGradientBrush(Lighten(accent, 0.14), Darken(accent, 0.03), new Point(0, 0), new Point(1, 1));

        var windowAlpha = hasBackgroundImage
            ? (byte)Math.Clamp((0.55 + 0.45 * opacity) * 255, 40, 255)
            : (byte)Math.Clamp(opacity * 255, 45, 255);
        var surfaceAlpha = (byte)Math.Clamp((0.86 + 0.14 * opacity) * 255, 180, 255);
        var surface2Alpha = (byte)Math.Clamp((0.90 + 0.10 * opacity) * 255, 200, 255);
        var surface3Alpha = (byte)Math.Clamp((0.94 + 0.06 * opacity) * 255, 220, 255);

        if (light)
        {
            res["WindowBgBrush"] = Solid(Color.FromArgb(windowAlpha, 0xF3, 0xF1, 0xF5));
            res["SurfaceBrush"] = Solid(Color.FromArgb(surfaceAlpha, 0xFB, 0xFA, 0xFD));
            res["Surface2Brush"] = Solid(Color.FromArgb(surface2Alpha, 0xF0, 0xEE, 0xF4));
            res["Surface3Brush"] = Solid(Color.FromArgb(surface3Alpha, 0xE7, 0xE4, 0xEC));
            res["SurfaceHoverBrush"] = Solid(Color.FromArgb(0x10, 0x00, 0x00, 0x00));
            res["StrokeBrush"] = Solid(Color.FromArgb(hasBackgroundImage ? (byte)0x24 : (byte)0x1A, 0x00, 0x00, 0x00));
            res["StrokeStrongBrush"] = Solid(Color.FromArgb(hasBackgroundImage ? (byte)0x38 : (byte)0x2A, 0x00, 0x00, 0x00));
            res["TextBrush"] = Solid(Color.FromRgb(0x24, 0x21, 0x2A));
            res["TextSubBrush"] = Solid(Color.FromRgb(0x5E, 0x59, 0x66));
            res["TextMutedBrush"] = Solid(Color.FromRgb(0x8A, 0x84, 0x92));
            res["DangerBrush"] = Solid(Color.FromRgb(0xC9, 0x3F, 0x45));
            res["WarnBrush"] = Solid(Color.FromRgb(0x9A, 0x6C, 0x00));
            res["SuccessBrush"] = Solid(Color.FromRgb(0x2E, 0x8B, 0x57));
            res["InfoBrush"] = Solid(Color.FromRgb(0x2A, 0x6D, 0xB8));
            res["OverlayBrush"] = Solid(Color.FromArgb(0x0D, 0x00, 0x00, 0x00));
            res["ScrimBrush"] = Solid(Color.FromArgb(0x55, 0x20, 0x1D, 0x26));
            res["TrackBrush"] = Solid(Color.FromArgb(0x1C, 0x00, 0x00, 0x00));
            res["ScrollThumbBrush"] = Solid(Color.FromArgb(0x35, 0x00, 0x00, 0x00));
        }
        else
        {
            res["WindowBgBrush"] = Solid(Color.FromArgb(windowAlpha, 0x0B, 0x0B, 0x10));
            res["SurfaceBrush"] = Solid(Color.FromArgb(surfaceAlpha, 0x16, 0x16, 0x1D));
            res["Surface2Brush"] = Solid(Color.FromArgb(surface2Alpha, 0x1C, 0x1C, 0x24));
            res["Surface3Brush"] = Solid(Color.FromArgb(surface3Alpha, 0x22, 0x22, 0x2C));
            res["SurfaceHoverBrush"] = Solid(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
            res["StrokeBrush"] = Solid(Color.FromArgb(hasBackgroundImage ? (byte)0x2E : (byte)0x24, 0xFF, 0xFF, 0xFF));
            res["StrokeStrongBrush"] = Solid(Color.FromArgb(hasBackgroundImage ? (byte)0x4A : (byte)0x3C, 0xFF, 0xFF, 0xFF));
            res["TextBrush"] = Solid(Color.FromRgb(0xF5, 0xF3, 0xF8));
            res["TextSubBrush"] = Solid(Color.FromRgb(0xA8, 0xA5, 0xB4));
            res["TextMutedBrush"] = Solid(Color.FromRgb(0x71, 0x6E, 0x7D));
            res["DangerBrush"] = Solid(Color.FromRgb(0xEB, 0x6B, 0x6B));
            res["WarnBrush"] = Solid(Color.FromRgb(0xF0, 0xC8, 0x6A));
            res["SuccessBrush"] = Solid(Color.FromRgb(0x7B, 0xD8, 0x8F));
            res["InfoBrush"] = Solid(Color.FromRgb(0x7F, 0xB5, 0xF0));
            res["OverlayBrush"] = Solid(Color.FromArgb(0x66, 0x00, 0x00, 0x00));
            res["ScrimBrush"] = Solid(Color.FromArgb(0x88, 0x00, 0x00, 0x00));
            res["TrackBrush"] = Solid(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
            res["ScrollThumbBrush"] = Solid(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        }
    }

    public static Color ParseColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try
        {
            hex = hex.Trim();
            if (!hex.StartsWith('#')) hex = "#" + hex;
            if (hex.Length == 7) hex = "#FF" + hex[1..];
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch { return fallback; }
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Color Lighten(Color c, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)(c.R + (255 - c.R) * amount),
            (byte)(c.G + (255 - c.G) * amount),
            (byte)(c.B + (255 - c.B) * amount));
    }

    public static Color Darken(Color c, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb((byte)(c.R * (1 - amount)), (byte)(c.G * (1 - amount)), (byte)(c.B * (1 - amount)));
    }

    public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static SolidColorBrush Solid(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    public static readonly string[] Presets =
    {
        "#E5B567", "#F4A6C0", "#7FB5F0", "#7BD88F", "#B79CFF", "#F08A6B", "#69D6D0", "#C9D06A"
    };
}
