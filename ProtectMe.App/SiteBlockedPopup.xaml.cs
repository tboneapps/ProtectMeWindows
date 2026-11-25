using System.Windows;
using ProtectMe.Core;

namespace ProtectMe.App;

public partial class SiteBlockedPopup : Window
{
    public WhitelistScope? SelectedScope { get; private set; }

    public SiteBlockedPopup(string domain)
    {
        InitializeComponent();
        DomainText.Text = domain;
        AllowSiteBtn.Content = $"Allow Whole Website ({domain})";
    }

    private void AllowSite_Click(object sender, RoutedEventArgs e)
    {
        SelectedScope = WhitelistScope.WholeWebsite;
        DialogResult = true;
        Close();
    }

    private void AllowPage_Click(object sender, RoutedEventArgs e)
    {
        SelectedScope = WhitelistScope.SpecificWebpage;
        DialogResult = true;
        Close();
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        SelectedScope = null;
        DialogResult = false;
        Close();
    }
}
