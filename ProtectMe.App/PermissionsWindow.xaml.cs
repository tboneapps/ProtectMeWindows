using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using ProtectMe.Core;

namespace ProtectMe.App;

public partial class PermissionsWindow : Window
{
    public bool PasswordWasSet { get; private set; }

    public PermissionsWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshChecksAsync();
    }

    private async Task RefreshChecksAsync()
    {
        var resp = await PipeClient.SendAsync(new PipeRequest { Action = "get_config" });
        bool serviceRunning = resp is { Success: true };
        var cfg = resp?.Config ?? ConfigStore.Load();

        var gold = (Brush)FindResource("GoldBrush");
        var danger = (Brush)FindResource("DangerBrush");

        ServiceStatusBadge.Text = serviceRunning ? "GRANTED" : "MISSING";
        ServiceStatusBadge.Foreground = serviceRunning ? gold : danger;
        GrantServiceBtn.Visibility = serviceRunning ? Visibility.Collapsed : Visibility.Visible;

        ProxyStatusBadge.Text = serviceRunning ? "GRANTED" : "NEEDS SERVICE";
        ProxyStatusBadge.Foreground = serviceRunning ? gold : danger;

        PasswordStatusBadge.Text = cfg.HasPassword ? "SET" : "MISSING";
        PasswordStatusBadge.Foreground = cfg.HasPassword ? gold : danger;
        SetPasswordBtn.Visibility = cfg.HasPassword ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void GrantService_Click(object sender, RoutedEventArgs e)
    {
        // Locate the installer script next to the app or in parent directory
        string baseDir = AppContext.BaseDirectory;
        string? script = FindInstallerScript(baseDir);

        if (script == null)
        {
            MessageBox.Show("Run Install-ProtectMe-Tablet.bat as Administrator to grant the SYSTEM Service permission.", "ProtectMe", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"",
                UseShellExecute = true,
                Verb = "runas"
            };
            var proc = Process.Start(psi);
            if (proc != null)
            {
                await proc.WaitForExitAsync();
            }
        }
        catch
        {
            MessageBox.Show("Administrator permission was canceled.", "ProtectMe", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        await RefreshChecksAsync();
    }

    private static string? FindInstallerScript(string baseDir)
    {
        string[] candidates =
        {
            Path.Combine(baseDir, "Install-ProtectMe-Tablet.ps1"),
            Path.Combine(baseDir, "..", "Install-ProtectMe-Tablet.ps1"),
            Path.Combine(baseDir, "..", "..", "Install-ProtectMe.ps1"),
            @"C:\Users\troyl\Documents\Codex\ProtectMeWindows\TabletPackage\Install-ProtectMe-Tablet.ps1"
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private async void SetPassword_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new PasswordDialog("Set Master Password", "Create a Master Password (at least 4 characters).") { Owner = this };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.EnteredPassword) || dlg.EnteredPassword.Length < 4)
            return;

        var resp = await PipeClient.SendAsync(new PipeRequest
        {
            Action = "set_password",
            NewPassword = dlg.EnteredPassword
        });

        if (resp == null)
        {
            var cfg = ConfigStore.Load();
            var (hash, salt) = SecurityHelper.HashPassword(dlg.EnteredPassword);
            cfg.PasswordHash = hash;
            cfg.PasswordSalt = salt;
            ConfigStore.Save(cfg);
        }

        PasswordWasSet = true;
        await RefreshChecksAsync();
    }

    private async void Recheck_Click(object sender, RoutedEventArgs e) => await RefreshChecksAsync();

    private void Continue_Click(object sender, RoutedEventArgs e) => Close();
}
