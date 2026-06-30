using System.Text;
using System.Windows;

namespace NaiwaProxy.Dialogs;

public partial class UpdateConfirmDialog : Window
{
    public UpdateConfirmDialog(string tagName, string currentVersion, string? releaseNotes, string installerName, long installerSize)
    {
        InitializeComponent();
        TitleText.Text = $"发现新版本 {tagName}";
        SummaryText.Text = $"当前版本：{currentVersion}    最新版本：{tagName}";
        ReleaseNotesText.Text = FormatReleaseNotes(releaseNotes);
        InstallerText.Text = $"安装包：{installerName}（{FormatBytes(installerSize)}）";
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private static string FormatReleaseNotes(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return "暂无更新说明。";
        }

        var builder = new StringBuilder();
        var inCodeBlock = false;
        foreach (var rawLine in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            var trimmed = line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inCodeBlock = !inCodeBlock;
                continue;
            }

            if (inCodeBlock)
            {
                continue;
            }

            if (trimmed.StartsWith('#'))
            {
                var heading = trimmed.TrimStart('#').Trim();
                if (heading.Length == 0)
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.AppendLine().AppendLine();
                }

                builder.Append(heading);
                continue;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(line);
        }

        var result = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(result) ? "暂无更新说明。" : result;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }
}
