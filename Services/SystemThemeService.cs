using Microsoft.Win32;

namespace NaiwaProxy.Services;

public static class SystemThemeService
{
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private static string _appThemeMode = "System";

    public static void SetAppThemeMode(string? mode)
    {
        _appThemeMode = mode is "Light" or "Dark" ? mode : "System";
    }

    /// <summary>
    /// Reads the Windows app theme preference. Dialogs may adapt using this value.
    /// Main window chrome should call <see cref="UseLightChrome"/> instead.
    /// </summary>
    public static bool IsLightMode()
    {
        if (_appThemeMode == "Light")
        {
            return true;
        }

        if (_appThemeMode == "Dark")
        {
            return false;
        }

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

    /// <summary>
    /// Resolves the selected application mode, including the Windows preference when
    /// the user selects follow-system.
    /// </summary>
    public static bool UseLightChrome => IsLightMode();
}
