using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using MessageBox = System.Windows.MessageBox;

namespace NaiwaProxy.Dialogs;

public partial class ProfileEditDialog : Window
{
    private const long MaxAvatarBytes = 5 * 1024 * 1024;
    private readonly string? _originalNickname;
    private string? _selectedAvatarFilePath;

    public bool ShouldUpdateNickname { get; private set; }

    public string Nickname { get; private set; } = "";

    public string? SelectedAvatarFilePath => _selectedAvatarFilePath;

    public bool HasAvatarChange => !string.IsNullOrWhiteSpace(_selectedAvatarFilePath);

    public ProfileEditDialog(string email, string? nickname, string? avatarUrl)
    {
        InitializeComponent();
        _originalNickname = nickname;
        EmailText.Text = email;
        NicknameBox.Text = nickname ?? "";
        LoadAvatarPreview(avatarUrl);
    }

    private void ChooseAvatarButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择头像图片",
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.webp",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var validationError = ValidateAvatarFile(dialog.FileName);
        if (validationError is not null)
        {
            MessageBox.Show(validationError, "Nexora", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _selectedAvatarFilePath = dialog.FileName;
        LoadAvatarPreview(dialog.FileName);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var nickname = NicknameBox.Text.Trim();
        ShouldUpdateNickname = !string.Equals(nickname, _originalNickname ?? "", StringComparison.Ordinal);
        var hasAvatarChange = HasAvatarChange;

        if (!ShouldUpdateNickname && !hasAvatarChange)
        {
            MessageBox.Show("请修改昵称或选择新头像。", "Nexora", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (ShouldUpdateNickname)
        {
            if (string.IsNullOrWhiteSpace(nickname))
            {
                MessageBox.Show("昵称不能为空。", "Nexora", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (nickname.Length > 50)
            {
                MessageBox.Show("昵称最多 50 个字符。", "Nexora", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        Nickname = nickname;
        DialogResult = true;
        Close();
    }

    private void LoadAvatarPreview(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            AvatarPreviewImage.Visibility = Visibility.Collapsed;
            AvatarPlaceholderIcon.Visibility = Visibility.Visible;
            AvatarPreviewImage.Source = null;
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(source, UriKind.Absolute);
            bitmap.EndInit();
            AvatarPreviewImage.Source = bitmap;
            AvatarPreviewImage.Visibility = Visibility.Visible;
            AvatarPlaceholderIcon.Visibility = Visibility.Collapsed;
        }
        catch
        {
            AvatarPreviewImage.Visibility = Visibility.Collapsed;
            AvatarPlaceholderIcon.Visibility = Visibility.Visible;
            AvatarPreviewImage.Source = null;
        }
    }

    private static string? ValidateAvatarFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return "头像文件不存在。";
        }

        var extension = Path.GetExtension(filePath);
        if (!extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".png", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".webp", StringComparison.OrdinalIgnoreCase))
        {
            return "头像仅支持 JPG、PNG、WebP 格式。";
        }

        if (new FileInfo(filePath).Length > MaxAvatarBytes)
        {
            return "头像文件不能超过 5MB。";
        }

        return null;
    }
}
