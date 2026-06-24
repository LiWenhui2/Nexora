using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;

namespace NaiwaProxy.Dialogs;

public partial class DurationPromptDialog : Window
{
    public int Minutes { get; private set; }

    public DurationPromptDialog(string subscriptionName, int? currentMinutes)
    {
        InitializeComponent();
        TitleText.Text = $"自定义刷新间隔 · {subscriptionName}";
        if (currentMinutes is int minutes)
        {
            MinutesBox.Text = minutes.ToString();
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(MinutesBox.Text.Trim(), out var minutes) || minutes <= 0)
        {
            MessageBox.Show("请输入大于 0 的分钟数。", "Nexora", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Minutes = minutes;
        DialogResult = true;
        Close();
    }
}
