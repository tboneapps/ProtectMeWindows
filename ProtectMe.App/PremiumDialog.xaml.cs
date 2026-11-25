using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using ProtectMe.Core;

namespace ProtectMe.App;

public partial class PremiumDialog : Window
{
    public bool LicenseActivated { get; private set; }
    public bool PromoWeekGranted { get; private set; }
    public string ActivatedKey { get; private set; } = string.Empty;
    public string ActivatedInstanceId { get; private set; } = string.Empty;

    public PremiumDialog(int trialDaysLeft)
    {
        InitializeComponent();
        SubtitleText.Text = trialDaysLeft > 0
            ? $"You have {trialDaysLeft} {(trialDaysLeft == 1 ? "day" : "days")} left in your 7-day free trial."
            : "Your 7-day free trial of Website Protection has ended.";
    }

    private void BuyNow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = LicenseService.CheckoutUrl,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private async void Activate_Click(object sender, RoutedEventArgs e)
    {
        ActivateBtn.IsEnabled = false;
        string raw = LicenseKeyBox.Text.Trim();

        // 1. Check hashed promo codes first (never stored in plain text)
        string promo = LicenseService.CheckPromoCode(raw);
        if (promo == "FOREVER")
        {
            LicenseActivated = true;
            ActivatedKey = "PROMO-LIFETIME";
            ActivatedInstanceId = LicenseService.GetMachineFingerprint();
            StatusText.Foreground = (Brush)FindResource("GoldLightBrush");
            StatusText.Text = "Promo accepted — Websites Pro unlocked forever!";
            await Task.Delay(700);
            DialogResult = true;
            Close();
            return;
        }
        if (promo == "WEEK")
        {
            PromoWeekGranted = true;
            StatusText.Foreground = (Brush)FindResource("GoldLightBrush");
            StatusText.Text = "Promo accepted — 7 extra days of Websites Pro unlocked!";
            await Task.Delay(700);
            DialogResult = true;
            Close();
            return;
        }

        // 2. Otherwise verify as a Gumroad License Key
        StatusText.Foreground = (Brush)FindResource("MutedBrush");
        StatusText.Text = "Activating license on this PC...";

        var (ok, msg, instanceId) = await LicenseService.ActivateAsync(raw);
        if (ok)
        {
            LicenseActivated = true;
            ActivatedKey = raw;
            ActivatedInstanceId = instanceId;
            StatusText.Foreground = (Brush)FindResource("GoldLightBrush");
            StatusText.Text = msg;
            await Task.Delay(700);
            DialogResult = true;
            Close();
        }
        else
        {
            StatusText.Foreground = (Brush)FindResource("DangerBrush");
            StatusText.Text = msg;
            ActivateBtn.IsEnabled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
