using Microsoft.Win32;

namespace NaiwaProxy.Services;

public static class SystemThemeService
{
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool IsLightMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
            var value = key?.GetValue("AppsUseLightTheme");
            return value is not int intValue || intValue != 0;
        }
        catch
        {
            return true;
        }
    }
}
