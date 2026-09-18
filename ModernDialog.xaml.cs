using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MediaColor = System.Windows.Media.Color;

namespace INTViewer;

public partial class ModernDialog : Window
{
    private readonly MessageBoxResult _confirmResult;
    private readonly MessageBoxResult _cancelResult;

    public ModernDialog(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;

        if (buttons == MessageBoxButton.YesNo || buttons == MessageBoxButton.YesNoCancel)
        {
            ConfirmButton.Content = "예";
            CancelButton.Content = "아니오";
            _confirmResult = MessageBoxResult.Yes;
            _cancelResult = MessageBoxResult.No;
        }
        else
        {
            ConfirmButton.Content = "확인";
            CancelButton.Content = "취소";
            _confirmResult = MessageBoxResult.OK;
            _cancelResult = MessageBoxResult.Cancel;
        }

        CancelButton.Visibility = buttons == MessageBoxButton.OK ? Visibility.Collapsed : Visibility.Visible;
        ConfigureIcon(image);
    }

    public static MessageBoxResult Show(Window owner, string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
    {
        var dialog = new ModernDialog(message, title, buttons, image) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog._confirmResult : dialog._cancelResult;
    }

    private void ConfigureIcon(MessageBoxImage image)
    {
        (string icon, MediaColor color) = image switch
        {
            MessageBoxImage.Question => ("?", MediaColor.FromRgb(79, 70, 229)),
            MessageBoxImage.Warning => ("!", MediaColor.FromRgb(217, 119, 6)),
            MessageBoxImage.Error => ("×", MediaColor.FromRgb(220, 38, 38)),
            _ => ("i", MediaColor.FromRgb(37, 99, 235))
        };
        IconText.Text = icon;
        IconBorder.Background = new SolidColorBrush(color);
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Dialog_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        DialogResult = false;
        Close();
        e.Handled = true;
    }

    private void Dialog_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
