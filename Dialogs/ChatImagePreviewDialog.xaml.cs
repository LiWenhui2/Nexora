using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace NaiwaProxy.Dialogs;

public partial class ChatImagePreviewDialog : Window
{
    public ChatImagePreviewDialog(ImageSource source)
    {
        InitializeComponent();
        PreviewImage.Source = source;
    }

    private void Backdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == sender)
        {
            Close();
        }
    }

    private void Image_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            Close();
        }
    }
}
