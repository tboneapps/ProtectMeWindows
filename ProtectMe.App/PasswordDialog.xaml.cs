using System.Windows;

namespace ProtectMe.App;

public partial class PasswordDialog : Window
{
    public string EnteredPassword => PasswordInput.Password;

    public PasswordDialog(string title, string prompt)
    {
        InitializeComponent();
        TitleText.Text = title.ToUpperInvariant();
        PromptText.Text = prompt;
        Loaded += (_, _) => PasswordInput.Focus();
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
