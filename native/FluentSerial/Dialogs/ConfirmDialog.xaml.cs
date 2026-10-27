using System.Windows;

namespace FluentSerial.Dialogs;

public partial class ConfirmDialog : Window
{
    private ConfirmDialog()
    {
        InitializeComponent();
    }

    /// <summary>Styled confirmation used for destructive actions such as deleting a profile or clearing data.</summary>
    public static bool Show(Window? owner, string title, string message,
        string confirmLabel = "确定", bool danger = false)
    {
        var dialog = new ConfirmDialog
        {
            Owner = owner,
            Title = title
        };
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.ConfirmButton.Content = confirmLabel;
        if (danger)
        {
            dialog.ConfirmButton.Style = (Style)dialog.FindResource("DangerButtonStyle");
        }
        return dialog.ShowDialog() == true;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
