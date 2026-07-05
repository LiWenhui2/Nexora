using System.Reflection;
using System.Text.RegularExpressions;

namespace NaiwaProxy.Services;

public static partial class AppVersionHelper
{
    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)]
    private static partial Regex VersionNumberRegex();

    public static string GetCurrentVersionName()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";
        var plusIndex = version.IndexOf('+', StringComparison.Ordinal);
        return plusIndex >= 0 ? version[..plusIndex] : version;
    }

    public static int GetCurrentVersionCode() => ParseVersionCode(GetCurrentVersionName());

    public static int ParseVersionCode(string versionName)
    {
        var parts = ExtractVersionParts(versionName);
        var major = parts.Length > 0 ? parts[0] : 0;
        var minor = parts.Length > 1 ? parts[1] : 0;
        var patch = parts.Length > 2 ? parts[2] : 0;
        return major * 10000 + minor * 100 + patch;
    }

    public static bool IsUpdateAvailable(int latestVersionCode, string? latestVersionName = null)
    {
        if (!string.IsNullOrWhiteSpace(latestVersionName))
        {
            var nameCompare = CompareVersionNames(latestVersionName, GetCurrentVersionName());
            if (nameCompare > 0)
            {
                return true;
            }

            if (nameCompare < 0)
            {
                return false;
            }
        }

        return latestVersionCode > GetCurrentVersionCode();
    }

    public static int CompareVersionNames(string left, string right)
    {
        var leftParts = ExtractVersionParts(left);
        var rightParts = ExtractVersionParts(right);
        var maxLength = Math.Max(leftParts.Length, rightParts.Length);
        for (var i = 0; i < maxLength; i++)
        {
            var leftPart = i < leftParts.Length ? leftParts[i] : 0;
            var rightPart = i < rightParts.Length ? rightParts[i] : 0;
            if (leftPart != rightPart)
            {
                return leftPart.CompareTo(rightPart);
            }
        }

        return 0;
    }

    public static int[] ExtractVersionParts(string value)
    {
        return VersionNumberRegex()
            .Matches(value)
            .Select(match => int.Parse(match.Value))
            .Take(3)
            .ToArray();
    }
}
