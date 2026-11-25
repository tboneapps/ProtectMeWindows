using System.Windows;

namespace ProtectMe.App;

public partial class WebsitesIntroDialog : Window
{
    public WebsitesIntroDialog()
    {
        InitializeComponent();
    }

    private void StartTrial_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
