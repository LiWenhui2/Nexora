using System.Text.Json;
using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public static class XpanelMetadataHelper
{
    public static void ApplyFromJson(VmessProfile profile, JsonElement root)
    {
        if (!root.TryGetProperty("xpanel", out var xpanel) || xpanel.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        Apply(profile, xpanel);
    }

    public static void ApplyFromQuery(VmessProfile profile, IReadOnlyDictionary<string, string> query)
    {
        if (!query.ContainsKey("xpanel_expiry") &&
            !query.ContainsKey("xpanel_total_bytes") &&
            !query.ContainsKey("xpanel_remaining_bytes") &&
            !query.ContainsKey("xpanel_used_bytes"))
        {
            return;
        }

        if (query.TryGetValue("xpanel_expiry", out var expiryText) &&
            TryParseExpiry(expiryText, out var expiry))
        {
            profile.XpanelExpiryTime = expiry;
        }

        if (query.TryGetValue("xpanel_total_bytes", out var totalText) && TryParseInt64(totalText, out var total))
        {
            profile.XpanelTotalBytes = total;
        }

        if (query.TryGetValue("xpanel_used_bytes", out var usedText) && TryParseInt64(usedText, out var used))
        {
            profile.XpanelUsedBytes = used;
        }

        if (query.TryGetValue("xpanel_remaining_bytes", out var remainingText) && TryParseInt64(remainingText, out var remaining))
        {
            profile.XpanelRemainingBytes = remaining;
        }
    }

    private static void Apply(VmessProfile profile, JsonElement xpanel)
    {
        if (TryGetString(xpanel, "expiryTime", out var expiryText) && TryParseExpiry(expiryText, out var expiry))
        {
            profile.XpanelExpiryTime = expiry;
        }

        if (TryGetInt64(xpanel, "totalBytes", out var total))
        {
            profile.XpanelTotalBytes = total;
        }

        if (TryGetInt64(xpanel, "usedBytes", out var used))
        {
            profile.XpanelUsedBytes = used;
        }

        if (TryGetInt64(xpanel, "remainingBytes", out var remaining))
        {
            profile.XpanelRemainingBytes = remaining;
        }
    }

    private static bool TryParseExpiry(string? text, out DateTime expiryUtc)
    {
        expiryUtc = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(text, out var parsed))
        {
            return false;
        }

        expiryUtc = parsed.UtcDateTime;
        return true;
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = "";
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetInt64(JsonElement element, string name, out long value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out value))
        {
            return true;
        }

        return property.ValueKind == JsonValueKind.String && TryParseInt64(property.GetString(), out value);
    }

    private static bool TryParseInt64(string? text, out long value)
    {
        value = 0;
        return !string.IsNullOrWhiteSpace(text) && long.TryParse(text, out value);
    }
}
