using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace NaiwaProxy.Services;

public static class DialogThemeService
{
    public static void Apply(Window window, Color accent)
    {
        var light = SystemThemeService.IsLightMode();
        var resources = window.Resources;

        resources["BgBrush"] = CreateBrush(light ? "#F4F6FA" : "#1E1E1E");
        resources["PanelBrush"] = CreateBrush(light ? "#FFFFFF" : "#252526");
        resources["Panel2Brush"] = CreateBrush(light ? "#F8FAFC" : "#2D2D30");
        resources["LineBrush"] = CreateBrush(light ? "#E2E8F0" : "#3F3F46");
        resources["TextBrush"] = CreateBrush(light ? "#0F172A" : "#F1F5F9");
        resources["MutedBrush"] = CreateBrush(light ? "#64748B" : "#94A3B8");
        resources["InfoIconBrush"] = CreateBrush(light ? "#2563EB" : "#60A5FA");
        resources["InfoIconBackgroundBrush"] = CreateBrush(light ? "#EFF6FF" : "#1E3A5F");
        resources["InfoIconBorderBrush"] = CreateBrush(light ? "#BFDBFE" : "#1D4ED8");
        resources["WarningIconBrush"] = CreateBrush(light ? "#D97706" : "#FBBF24");
        resources["WarningIconBackgroundBrush"] = CreateBrush(light ? "#FFFBEB" : "#422006");
        resources["WarningIconBorderBrush"] = CreateBrush(light ? "#FDE68A" : "#92400E");
        resources["ErrorIconBrush"] = CreateBrush(light ? "#DC2626" : "#F87171");
        resources["ErrorIconBackgroundBrush"] = CreateBrush(light ? "#FEF2F2" : "#450A0A");
        resources["ErrorIconBorderBrush"] = CreateBrush(light ? "#FECACA" : "#991B1B");
        resources["PrimaryButtonForegroundBrush"] = CreateBrush(light ? "#111827" : "#F8FAFC");

        ThemeService.ApplyToResources(resources, accent);
        window.Background = (System.Windows.Media.Brush)resources["BgBrush"];
    }

    public static Color ResolveAccent(Window? owner)
    {
        if (owner?.TryFindResource("AccentTextBrush") is SolidColorBrush brush)
        {
            return brush.Color;
        }

        if (System.Windows.Application.Current?.TryFindResource("AccentTextBrush") is SolidColorBrush appBrush)
        {
            return appBrush.Color;
        }

        return ThemeService.ParseAccentColor(ThemeService.DefaultAccentHex);
    }

    private static SolidColorBrush CreateBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }
}
