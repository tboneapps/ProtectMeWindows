using System.Windows;

namespace ProtectMe.App;

public partial class StrictByDesignDialog : Window
{
    public StrictByDesignDialog()
    {
        InitializeComponent();
    }

    private void GotIt_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
