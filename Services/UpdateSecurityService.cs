using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NaiwaProxy.Services;

public static class UpdateSecurityService
{
    public static void VerifyAuthenticode(string filePath, string expectedThumbprint)
    {
        if (string.IsNullOrWhiteSpace(expectedThumbprint))
        {
            DiagnosticLogService.Warning("Update metadata does not require an Authenticode signer; SHA-256 was verified.");
            return;
        }

        try
        {
#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
#pragma warning restore SYSLIB0057
            var actual = certificate.Thumbprint?.Replace(" ", "", StringComparison.Ordinal) ?? "";
            var expected = expectedThumbprint.Replace(" ", "", StringComparison.Ordinal);
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("安装包数字签名与发布者指纹不匹配。");
            }
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException("安装包没有有效的 Authenticode 数字签名。", ex);
        }
    }

    public static string PrepareRollbackSnapshot()
    {
        var rollbackDirectory = Path.Combine(ConfigurationBackupService.DataDirectory, "rollback", "last-version");
        Directory.CreateDirectory(rollbackDirectory);
        var executable = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executable) && File.Exists(executable))
        {
            File.Copy(executable, Path.Combine(rollbackDirectory, Path.GetFileName(executable)), true);
        }

        var sourceCores = Path.Combine(AppContext.BaseDirectory, "cores");
        var targetCores = Path.Combine(rollbackDirectory, "cores");
        Directory.CreateDirectory(targetCores);
        foreach (var name in new[] { "xray.exe", "sing-box.exe", "wintun.dll", "geoip.dat", "geosite.dat" })
        {
            var source = Path.Combine(sourceCores, name);
            if (File.Exists(source)) File.Copy(source, Path.Combine(targetCores, name), true);
        }

        File.WriteAllText(Path.Combine(rollbackDirectory, "version.txt"), AppVersionHelper.GetCurrentVersionName());
        DiagnosticLogService.Info($"Rollback snapshot prepared: {rollbackDirectory}");
        return rollbackDirectory;
    }

    public static ProcessStartInfo CreateInstallerGuardStartInfo(string installerPath)
    {
        var rollbackDirectory = Path.Combine(ConfigurationBackupService.DataDirectory, "rollback", "last-version");
        var scriptPath = Path.Combine(ConfigurationBackupService.DataDirectory, "updates", "install-with-rollback.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        static string Quote(string value) => value.Replace("'", "''", StringComparison.Ordinal);
        var installDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var executableName = Path.GetFileName(Environment.ProcessPath ?? "Nexora.exe");
        var script = $$"""
$ErrorActionPreference = 'Stop'
$installer = '{{Quote(installerPath)}}'
$rollback = '{{Quote(rollbackDirectory)}}'
$target = '{{Quote(installDirectory)}}'
$exeName = '{{Quote(executableName)}}'
try {
    $process = Start-Process -FilePath $installer -ArgumentList '/SP-','/SUPPRESSMSGBOXES','/CLOSEAPPLICATIONS' -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Installer exit code $($process.ExitCode)" }
    exit 0
}
catch {
    Start-Sleep -Seconds 2
    $oldExe = Join-Path $rollback $exeName
    if (Test-Path -LiteralPath $oldExe) { Copy-Item -LiteralPath $oldExe -Destination (Join-Path $target $exeName) -Force }
    $oldCores = Join-Path $rollback 'cores'
    if (Test-Path -LiteralPath $oldCores) {
        New-Item -ItemType Directory -Path (Join-Path $target 'cores') -Force | Out-Null
        Copy-Item -Path (Join-Path $oldCores '*') -Destination (Join-Path $target 'cores') -Recurse -Force
    }
    if (Test-Path -LiteralPath (Join-Path $target $exeName)) { Start-Process -FilePath (Join-Path $target $exeName) }
    exit 1
}
""";
        File.WriteAllText(scriptPath, script);
        return new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
    }
}
