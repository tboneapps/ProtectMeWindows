using System.Windows;

namespace ProtectMe.App;

public partial class GraceLoginDialog : Window
{
    public bool Add10Seconds { get; private set; }

    public GraceLoginDialog(string appName)
    {
        InitializeComponent();
        AppNameText.Text = appName;
        AppBadgeText.Text = appName;
    }

    private void Add10s_Click(object sender, RoutedEventArgs e)
    {
        Add10Seconds = true;
        DialogResult = true;
        Close();
    }

    private void NotNow_Click(object sender, RoutedEventArgs e)
    {
        Add10Seconds = false;
        DialogResult = false;
        Close();
    }
}
