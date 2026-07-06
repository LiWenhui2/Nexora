using System.IO;
using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public static class ClientDeviceInfoProvider
{
    private static readonly object DeviceIdLock = new();
    private static string? _cachedDeviceId;

    public static ClientDeviceInfo CreatePayload() =>
        new()
        {
            ClientType = "WINDOWS",
            DeviceId = GetOrCreateDeviceId(),
            DeviceName = TrimToMaxLength(GetDeviceName(), 100),
            OsName = "Windows",
            OsVersion = TrimToMaxLength(Environment.OSVersion.Version.ToString(), 50),
            AppVersion = TrimToMaxLength(AppVersionHelper.GetCurrentVersionName(), 50)
        };

    private static string GetOrCreateDeviceId()
    {
        lock (DeviceIdLock)
        {
            if (!string.IsNullOrWhiteSpace(_cachedDeviceId))
            {
                return _cachedDeviceId;
            }

            var path = GetDeviceIdPath();
            if (File.Exists(path))
            {
                try
                {
                    var existing = File.ReadAllText(path).Trim();
                    if (!string.IsNullOrWhiteSpace(existing))
                    {
                        _cachedDeviceId = TrimToMaxLength(existing, 100);
                        return _cachedDeviceId;
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticLogService.Warning($"Failed to load device id: {ex.Message}");
                }
            }

            var deviceId = TrimToMaxLength(Guid.NewGuid().ToString("D"), 100);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tempPath = $"{path}.tmp";
                File.WriteAllText(tempPath, deviceId);
                File.Move(tempPath, path, overwrite: true);
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Warning($"Failed to persist device id: {ex.Message}");
            }

            _cachedDeviceId = deviceId;
            return deviceId;
        }
    }

    private static string GetDeviceIdPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Nexora",
            "device-id.txt");

    private static string GetDeviceName()
    {
        var machineName = Environment.MachineName?.Trim();
        return string.IsNullOrWhiteSpace(machineName) ? "Windows Desktop" : machineName;
    }

    private static string TrimToMaxLength(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
