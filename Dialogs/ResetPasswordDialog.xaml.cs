using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using NaiwaProxy.Services;
using MessageBox = System.Windows.MessageBox;

namespace NaiwaProxy.Dialogs;

public partial class ResetPasswordDialog : Window
{
    private readonly AuthService _authService;
    private readonly string _email;
    private readonly DispatcherTimer _codeCooldownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _codeCooldownSeconds;

    public ResetPasswordDialog(AuthService authService, string email)
    {
        InitializeComponent();
        _authService = authService;
        _email = email.Trim();
        EmailText.Text = _email;
        _codeCooldownTimer.Tick += CodeCooldownTimer_Tick;
        Closed += (_, _) =>
        {
            _codeCooldownTimer.Stop();
            _codeCooldownTimer.Tick -= CodeCooldownTimer_Tick;
        };
    }

    private async void SendCodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_codeCooldownSeconds > 0)
        {
            return;
        }

        SendCodeButton.IsEnabled = false;
        SendCodeButton.Content = "发送中…";
        try
        {
            var result = await _authService.SendResetPasswordCodeAsync(_email);
            if (!result.Success)
            {
                ShowMessage(result.Message, isSuccess: false);
                return;
            }

            ShowMessage(result.Message, isSuccess: true);
            StartCodeCooldown();
        }
        finally
        {
            if (_codeCooldownSeconds <= 0)
            {
                SendCodeButton.IsEnabled = true;
                SendCodeButton.Content = "发送验证码";
            }
        }
    }

    private async void SubmitButton_Click(object sender, RoutedEventArgs e)
    {
        SubmitButton.IsEnabled = false;
        try
        {
            var result = await _authService.ResetPasswordAsync(
                _email,
                CodeBox.Text,
                NewPasswordBox.Password,
                ConfirmPasswordBox.Password);
            if (!result.Success)
            {
                ShowMessage(result.Message, isSuccess: false);
                return;
            }

            var message = string.IsNullOrWhiteSpace(result.Message) ? "密码已重置成功。" : result.Message;
            MessageBox.Show(message, "重置密码", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }
        finally
        {
            SubmitButton.IsEnabled = true;
        }
    }

    private void StartCodeCooldown()
    {
        _codeCooldownSeconds = 60;
        SendCodeButton.IsEnabled = false;
        SendCodeButton.Content = $"{_codeCooldownSeconds}s";
        _codeCooldownTimer.Start();
    }

    private void CodeCooldownTimer_Tick(object? sender, EventArgs e)
    {
        _codeCooldownSeconds--;
        if (_codeCooldownSeconds > 0)
        {
            SendCodeButton.Content = $"{_codeCooldownSeconds}s";
            return;
        }

        _codeCooldownTimer.Stop();
        SendCodeButton.IsEnabled = true;
        SendCodeButton.Content = "发送验证码";
    }

    private void ShowMessage(string message, bool isSuccess)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            MessageText.Visibility = Visibility.Collapsed;
            MessageText.Text = "";
            return;
        }

        MessageText.Text = message;
        MessageText.Foreground = new SolidColorBrush(isSuccess
            ? System.Windows.Media.Color.FromRgb(22, 163, 74)
            : System.Windows.Media.Color.FromRgb(220, 38, 38));
        MessageText.Visibility = Visibility.Visible;
    }
}
