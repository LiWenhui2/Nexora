using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace NaiwaProxy.Services;

public static class ThemeService
{
    public const string DefaultAccentHex = "#2563EB";

    public static Color ParseAccentColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ColorFromHex(DefaultAccentHex);
        }

        var text = value.Trim();
        if (!text.StartsWith('#'))
        {
            text = $"#{text}";
        }

        try
        {
            return ColorFromHex(text);
        }
        catch
        {
            return ColorFromHex(DefaultAccentHex);
        }
    }

    public static string FormatHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public static void Apply(Color accent)
    {
        ApplyToResources(System.Windows.Application.Current.Resources, accent);
    }

    public static void ApplyToResources(ResourceDictionary resources, Color accent)
    {
        resources["AccentTextBrush"] = ToBrush(accent);
        resources["AccentGradientBrush"] = new LinearGradientBrush(
            Blend(accent, Colors.White, 0.72),
            Blend(accent, Colors.White, 0.18),
            new Point(0, 0),
            new Point(1, 1));
        resources["AccentLightBrush"] = ToBrush(Blend(accent, Colors.White, 0.9));
        resources["AccentMediumBrush"] = ToBrush(Blend(accent, Colors.White, 0.55));
        resources["AccentStrongBrush"] = ToBrush(Blend(accent, Colors.White, 0.15));
        resources["AccentSoftBorderBrush"] = ToBrush(Blend(accent, Colors.White, 0.68));
        resources["AccentNavBackgroundBrush"] = ToBrush(Blend(accent, Colors.White, 0.84));
        resources["AccentPrimaryBorderBrush"] = ToBrush(Blend(accent, Colors.White, 0.5));
        resources["AccentActiveNodeForegroundBrush"] = ToBrush(Darken(accent, 0.35));
    }

    private static SolidColorBrush ToBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color ColorFromHex(string hex)
    {
        var normalized = hex.TrimStart('#');
        if (normalized.Length == 6)
        {
            return (Color)System.Windows.Media.ColorConverter.ConvertFromString($"#{normalized}")!;
        }

        if (normalized.Length == 8 &&
            byte.TryParse(normalized[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var a) &&
            byte.TryParse(normalized.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) &&
            byte.TryParse(normalized.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) &&
            byte.TryParse(normalized.AsSpan(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return Color.FromArgb(a, r, g, b);
        }

        return (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!;
    }

    private static Color Blend(Color baseColor, Color target, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)(baseColor.R + (target.R - baseColor.R) * amount),
            (byte)(baseColor.G + (target.G - baseColor.G) * amount),
            (byte)(baseColor.B + (target.B - baseColor.B) * amount));
    }

    private static Color Darken(Color color, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)(color.R * (1 - amount)),
            (byte)(color.G * (1 - amount)),
            (byte)(color.B * (1 - amount)));
    }
}
