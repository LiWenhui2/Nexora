using System.Security.Cryptography;
using System.Text;

namespace NaiwaProxy.Services;

internal static class SessionTokenProtection
{
    private static readonly byte[] Entropy = "Nexora.AuthSession.v1"u8.ToArray();

    public static string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return "";
        }

        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plainText),
            Entropy,
            DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public static string Unprotect(string protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64))
        {
            return "";
        }

        var protectedBytes = Convert.FromBase64String(protectedBase64);
        var plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plainBytes);
    }
}
