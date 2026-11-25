using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ProtectMe.Core;

namespace ProtectMe.App;

public partial class MainWindow : Window
{
    private ProtectConfig _config = new();
    private bool _serviceRunning;
    private readonly DispatcherTimer _refreshTimer;

    // Animated Hold-to-Protect Pill State
    private readonly DispatcherTimer _holdTimer;
    private double _holdProgress;
    private bool _isHolding;
    private const double HoldDurationSeconds = 1.4;

    private readonly BitmapSource _goldLogo;
    private readonly BitmapSource _grayLogo;
    private readonly BitmapSource _safeBrowsingGold;
    private readonly BitmapSource _safeBrowsingGray;
    private readonly BitmapSource _blockImagesGold;
    private readonly BitmapSource _blockImagesGray;
    private bool _auraAnimating;
    private DateTime _lastNotifiedBlockUtc = DateTime.UtcNow;
    private bool _showingBlockPopup;

    public MainWindow()
    {
        InitializeComponent();

        _goldLogo = new BitmapImage(new Uri("pack://application:,,,/Assets/Images/protectme_icon.png"));
        _grayLogo = new FormatConvertedBitmap(_goldLogo, PixelFormats.Gray8, null, 0);

        _safeBrowsingGold = new BitmapImage(new Uri("pack://application:,,,/Assets/Images/tb_safe_browsing.png"));
        _safeBrowsingGray = new FormatConvertedBitmap(_safeBrowsingGold, PixelFormats.Gray8, null, 0);
        _blockImagesGold = new BitmapImage(new Uri("pack://application:,,,/Assets/Images/tb_block_images.png"));
        _blockImagesGray = new FormatConvertedBitmap(_blockImagesGold, PixelFormats.Gray8, null, 0);

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _refreshTimer.Tick += async (_, _) => await RefreshStateAsync();

        _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _holdTimer.Tick += HoldTimer_Tick;

        Loaded += async (_, _) =>
        {
            await RefreshStateAsync();
            if (!_serviceRunning)
            {
                var permWin = new PermissionsWindow { Owner = this };
                permWin.ShowDialog();
                await RefreshStateAsync();
            }
            _refreshTimer.Start();
        };
    }

    private async Task RefreshStateAsync()
    {
        var resp = await PipeClient.SendAsync(new PipeRequest { Action = "get_config" });
        if (resp is { Success: true, Config: not null })
        {
            _config = resp.Config;
            _serviceRunning = true;
        }
        else
        {
            _config = ConfigStore.Load();
            _serviceRunning = false;
        }

        if (!_config.TrialStartedUtc.HasValue)
        {
            _config.TrialStartedUtc = DateTime.UtcNow;
            await PushConfigAsync();
        }

        _config.BlockedApps = InstalledAppScanner.ScanInstalledApps(_config.BlockedApps);
        _config.OfflineApps = InstalledAppScanner.ScanInstalledApps(_config.OfflineApps);

        BindUi();
        CheckForNewBlockedSitePopup();
    }

    private async void CheckForNewBlockedSitePopup()
    {
        if (_showingBlockPopup || !_config.IsProtectionLocked || !_config.WhitelistOnlyWebsites) return;
        var newest = _config.RecentBlocked.FirstOrDefault(e => e.Kind == "Website" && e.TimestampUtc > _lastNotifiedBlockUtc);
        if (newest == null) return;

        _lastNotifiedBlockUtc = newest.TimestampUtc;
        _showingBlockPopup = true;
        try
        {
            var popup = new SiteBlockedPopup(newest.Domain);
            if (popup.ShowDialog() == true && popup.SelectedScope.HasValue)
            {
                string val = popup.SelectedScope.Value == WhitelistScope.WholeWebsite ? newest.Domain : newest.Target;
                await AddWhitelistItemAsync(val, popup.SelectedScope.Value);
            }
        }
        finally
        {
            _showingBlockPopup = false;
        }
    }

    private void BindUi()
    {
        bool locked = _config.IsProtectionLocked;

        CenterLogoImage.Source = locked ? _goldLogo : _grayLogo;
        HoldHintTitle.Text = locked ? "P R O T E C T E D" : "U N P R O T E C T E D";
        HoldHintTitle.Foreground = (Brush)FindResource(locked ? "TitleGradientBrush" : "MutedBrush");
        HoldHintSubtitle.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        HoldPillButton.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;

        if (locked)
        {
            StartAuraAnimation();
        }
        else
        {
            StopAuraAnimation();
        }

        ServiceWarningCard.Visibility = _serviceRunning ? Visibility.Collapsed : Visibility.Visible;

        WhitelistOnlyCheck.IsChecked = _config.WhitelistOnlyWebsites;
        BindTboneWebsiteCards();

        int trialLeft = LicenseService.GetTrialDaysRemaining(_config);
        if (_config.IsPremiumActive)
        {
            TrialBannerText.Text = "★  Websites Pro Active";
            TrialBannerBtn.Content = "Deactivate PC";
        }
        else
        {
            TrialBannerText.Text = trialLeft > 0
                ? $"7-Day Free Trial · {trialLeft} {(trialLeft == 1 ? "day" : "days")} left"
                : "7-Day Free Trial Ended";
            TrialBannerBtn.Content = "Upgrade / Key";
        }

        var wholeSites = _config.WebsiteWhitelist.Where(w => w.Scope == WhitelistScope.WholeWebsite).ToList();
        var specificPages = _config.WebsiteWhitelist.Where(w => w.Scope == WhitelistScope.SpecificWebpage).ToList();
        WhitelistSitesCountText.Text = $"{wholeSites.Count} {(wholeSites.Count == 1 ? "site" : "sites")}";
        WhitelistPagesCountText.Text = $"{specificPages.Count} {(specificPages.Count == 1 ? "page" : "pages")}";

        WhitelistWebsitesControl.ItemsSource = null;
        WhitelistWebsitesControl.ItemsSource = wholeSites;
        WhitelistPagesControl.ItemsSource = null;
        WhitelistPagesControl.ItemsSource = specificPages;

        PasswordToggleSwitch.IsChecked = _config.HasPassword;
        PasswordStatusText.Text = _config.HasPassword
            ? "Master password is active. Required to unlock or uninstall."
            : "Toggle ON or tap Change Password to set your master password.";

        BindBlockedAppsGrid();
        BindOfflineAppsGrid();

        int blockedCount = _config.BlockedApps.Count(a => a.IsEnabled);
        int offlineCount = _config.OfflineApps.Count(a => a.IsEnabled);
        BlockedAppsCountText.Text = $"{blockedCount} {(blockedCount == 1 ? "app" : "apps")}";
        OfflineAppsCountText.Text = $"{offlineCount} {(offlineCount == 1 ? "app" : "apps")}";

        BindScheduleUi();
        BlockNewAppsToggle.IsChecked = _config.BlockNewInstalledApps;
    }

    private void StartAuraAnimation()
    {
        if (_auraAnimating) return;
        _auraAnimating = true;

        var opacityAnim = new DoubleAnimation(0.55, 1.0, TimeSpan.FromSeconds(1.8))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        var scaleAnim = new DoubleAnimation(0.96, 1.08, TimeSpan.FromSeconds(1.8))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };

        GoldAuraEllipse.BeginAnimation(OpacityProperty, opacityAnim);
        GoldAuraScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        GoldAuraScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
    }

    private void StopAuraAnimation()
    {
        if (!_auraAnimating) return;
        _auraAnimating = false;
        GoldAuraEllipse.BeginAnimation(OpacityProperty, null);
        GoldAuraScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        GoldAuraScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        GoldAuraEllipse.Opacity = 0;
    }

    // ---- Tap the Logo when PROTECTED to show Master Password popup ----
    private async void LogoTile_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_config.IsProtectionLocked) return;

        LogoTileScale.ScaleX = 0.94;
        LogoTileScale.ScaleY = 0.94;
        await UnlockProtectionAsync();
        LogoTileScale.ScaleX = 1.0;
        LogoTileScale.ScaleY = 1.0;
    }

    // ---- Hold "Hold to Protect" pill button to turn ON ----
    private void HoldPill_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_config.IsProtectionLocked) return;
        _isHolding = true;
        _holdProgress = 0;
        HoldPillFill.Width = 0;
        _holdTimer.Start();
    }

    private void HoldPill_MouseUp(object sender, MouseButtonEventArgs e) => CancelHold();
    private void HoldPill_MouseLeave(object sender, MouseEventArgs e) => CancelHold();

    private void CancelHold()
    {
        if (!_isHolding) return;
        _isHolding = false;
        _holdTimer.Stop();
        _holdProgress = 0;
        HoldPillFill.Width = 0;
        HoldPillText.Foreground = (Brush)FindResource("GoldLightBrush");
    }

    private async void HoldTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isHolding) return;
        _holdProgress += 0.016 / HoldDurationSeconds;
        double maxW = HoldPillButton.ActualWidth > 0 ? HoldPillButton.ActualWidth : 310;

        if (_holdProgress >= 1.0)
        {
            _isHolding = false;
            _holdTimer.Stop();
            HoldPillFill.Width = maxW;
            await LockProtectionAsync();
            HoldPillFill.Width = 0;
        }
        else
        {
            HoldPillFill.Width = maxW * _holdProgress;
            HoldPillText.Foreground = _holdProgress > 0.55
                ? new SolidColorBrush(Color.FromRgb(10, 10, 10))
                : (Brush)FindResource("GoldLightBrush");
        }
    }

    private async Task LockProtectionAsync()
    {
        if (_serviceRunning)
        {
            var resp = await PipeClient.SendAsync(new PipeRequest { Action = "lock" });
            if (resp is not { Success: true })
            {
                MessageBox.Show(resp?.Message ?? "Failed to lock.", "ProtectMe");
                return;
            }
        }
        else
        {
            _config.IsProtectionLocked = true;
            ConfigStore.Save(_config);
        }
        await RefreshStateAsync();
    }

    private async Task UnlockProtectionAsync()
    {
        string? pw = null;
        if (_config.HasPassword)
        {
            pw = PromptPassword("Unlock Protection", "Enter your Master Password to turn off ProtectMe.");
            if (pw == null) return;
        }

        if (_serviceRunning)
        {
            var resp = await PipeClient.SendAsync(new PipeRequest { Action = "unlock", Password = pw ?? string.Empty });
            if (resp is not { Success: true })
            {
                MessageBox.Show(resp?.Message ?? "Unlock failed.", "ProtectMe", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        else
        {
            if (_config.HasPassword && !SecurityHelper.VerifyPassword(pw ?? string.Empty, _config.PasswordHash, _config.PasswordSalt))
            {
                MessageBox.Show("Incorrect master password.", "ProtectMe", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _config.IsProtectionLocked = false;
            _config.LockedUntilUtc = null;
            ConfigStore.Save(_config);
        }
        await RefreshStateAsync();
    }

    private string? PromptPassword(string title, string prompt)
    {
        var dlg = new PasswordDialog(title, prompt) { Owner = this };
        return dlg.ShowDialog() == true ? dlg.EnteredPassword : null;
    }

    private async Task<bool> PushConfigAsync(string? password = null)
    {
        if (_serviceRunning)
        {
            var resp = await PipeClient.SendAsync(new PipeRequest
            {
                Action = "update_config",
                Password = password,
                Config = _config
            });

            if (resp is not { Success: true })
            {
                MessageBox.Show(resp?.Message ?? "Could not update service.", "ProtectMe", MessageBoxButton.OK, MessageBoxImage.Warning);
                await RefreshStateAsync();
                return false;
            }
            if (resp.Config != null) _config = resp.Config;
            BindUi();
            return true;
        }
        else
        {
            ConfigStore.Save(_config);
            BindUi();
            return true;
        }
    }

    private void BindTboneWebsiteCards()
    {
        bool sbOn = _config.SafeBrowsingEnabled;
        SafeBrowsingIcon.Source = sbOn ? _safeBrowsingGold : _safeBrowsingGray;
        SafeBrowsingCard.BorderBrush = (Brush)FindResource(sbOn ? "GoldBrush" : "CardBorderBrush");
        SafeBrowsingCard.Background = new SolidColorBrush(sbOn ? Color.FromRgb(31, 26, 15) : Color.FromRgb(18, 18, 18));
        SafeBrowsingPill.Background = sbOn ? (Brush)FindResource("GoldGradientBrush") : new SolidColorBrush(Color.FromRgb(34, 32, 28));
        SafeBrowsingPillText.Text = sbOn ? "On" : "Off";
        SafeBrowsingPillText.Foreground = sbOn ? new SolidColorBrush(Color.FromRgb(10, 10, 10)) : (Brush)FindResource("MutedBrush");

        bool imgOn = _config.BlockImageVideoSearch;
        BlockImagesIcon.Source = imgOn ? _blockImagesGold : _blockImagesGray;
        BlockImagesCard.BorderBrush = (Brush)FindResource(imgOn ? "GoldBrush" : "CardBorderBrush");
        BlockImagesCard.Background = new SolidColorBrush(imgOn ? Color.FromRgb(31, 26, 15) : Color.FromRgb(18, 18, 18));
        BlockImagesPill.Background = imgOn ? (Brush)FindResource("GoldGradientBrush") : new SolidColorBrush(Color.FromRgb(34, 32, 28));
        BlockImagesPillText.Text = imgOn ? "On" : "Off";
        BlockImagesPillText.Foreground = imgOn ? new SolidColorBrush(Color.FromRgb(10, 10, 10)) : (Brush)FindResource("MutedBrush");
    }

    private async void SafeBrowsingCard_Click(object sender, MouseButtonEventArgs e)
    {
        // Can turn ON anytime; CANNOT turn OFF while Protection is ON
        if (_config.SafeBrowsingEnabled && _config.IsProtectionLocked)
        {
            MessageBox.Show("Safe Browsing is locked while Protection is ON. Turn off Protection first.", "ProtectMe", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _config.SafeBrowsingEnabled = !_config.SafeBrowsingEnabled;
        await PushConfigAsync();
    }

    private async void BlockImagesCard_Click(object sender, MouseButtonEventArgs e)
    {
        // Can turn ON anytime; CANNOT turn OFF while Protection is ON
        if (_config.BlockImageVideoSearch && _config.IsProtectionLocked)
        {
            MessageBox.Show("Block Image & Video Search is locked while Protection is ON. Turn off Protection first.", "ProtectMe", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _config.BlockImageVideoSearch = !_config.BlockImageVideoSearch;
        await PushConfigAsync();
    }

    private async void WhitelistOnlyCheck_Click(object sender, RoutedEventArgs e)
    {
        bool turningOn = WhitelistOnlyCheck.IsChecked == true;
        // Can switch ON while protection is ON, but CANNOT switch OFF until Protection is OFF!
        if (!turningOn && _config.IsProtectionLocked)
        {
            WhitelistOnlyCheck.IsChecked = true;
            MessageBox.Show("Website Protection cannot be turned OFF while Protection is ON. Turn off Protection first.", "ProtectMe", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _config.WhitelistOnlyWebsites = turningOn;
        await PushConfigAsync();
    }

    private void OpenWhitelistWebsites_Click(object sender, RoutedEventArgs e)
    {
        PageHeaderTitle.Text = "W E B S I T E S";
        WebsitesHubView.Visibility = Visibility.Collapsed;
        WhitelistWebsitesSubView.Visibility = Visibility.Visible;
        WhitelistPagesSubView.Visibility = Visibility.Collapsed;
    }

    private void OpenWhitelistPages_Click(object sender, RoutedEventArgs e)
    {
        PageHeaderTitle.Text = "W E B P A G E S";
        WebsitesHubView.Visibility = Visibility.Collapsed;
        WhitelistWebsitesSubView.Visibility = Visibility.Collapsed;
        WhitelistPagesSubView.Visibility = Visibility.Visible;
    }

    private void BackToWebsites_Click(object sender, RoutedEventArgs e)
    {
        PageHeaderTitle.Text = "W E B S I T E S";
        WebsitesHubView.Visibility = Visibility.Visible;
        WhitelistWebsitesSubView.Visibility = Visibility.Collapsed;
        WhitelistPagesSubView.Visibility = Visibility.Collapsed;
    }

    private void OpenWhitelistLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string raw || string.IsNullOrWhiteSpace(raw)) return;
        string url = raw.Contains("://", StringComparison.Ordinal) ? raw : "https://" + raw;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private async void AddWholeWebsite_Click(object sender, RoutedEventArgs e)
    {
        await AddWhitelistItemAsync(NewSiteInput.Text, WhitelistScope.WholeWebsite);
        NewSiteInput.Text = string.Empty;
    }

    private async void AddSpecificWebpage_Click(object sender, RoutedEventArgs e)
    {
        await AddWhitelistItemAsync(NewPageInput.Text, WhitelistScope.SpecificWebpage);
        NewPageInput.Text = string.Empty;
    }

    private async void AllowRecentSite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is BlockedEvent ev)
            await AddWhitelistItemAsync(ev.Domain, WhitelistScope.WholeWebsite);
    }

    private async void AllowRecentPage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is BlockedEvent ev)
            await AddWhitelistItemAsync(ev.Target, WhitelistScope.SpecificWebpage);
    }

    private async Task AddWhitelistItemAsync(string rawValue, WhitelistScope scope)
    {
        if (string.IsNullOrWhiteSpace(rawValue)) return;
        bool needPw = _config.IsProtectionLocked && _config.HasPassword;
        string? pw = needPw ? PromptPassword("Whitelist Site", "Enter Master Password to add to whitelist.") : null;
        if (needPw && pw == null) return;

        var entry = new WhitelistEntry { Value = rawValue.Trim(), Scope = scope };

        if (_serviceRunning)
        {
            var resp = await PipeClient.SendAsync(new PipeRequest
            {
                Action = "add_whitelist",
                Password = pw,
                WhitelistEntry = entry
            });
            if (resp is not { Success: true })
            {
                MessageBox.Show(resp?.Message ?? "Failed to add.", "ProtectMe", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        else
        {
            entry.Value = scope == WhitelistScope.WholeWebsite
                ? SecurityHelper.NormalizeDomain(entry.Value)
                : SecurityHelper.NormalizeWebpageUrl(entry.Value);
            _config.WebsiteWhitelist.Insert(0, entry);
            ConfigStore.Save(_config);
        }

        await RefreshStateAsync();
    }

    private async void RemoveWhitelist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id) return;
        bool needPw = _config.IsProtectionLocked && _config.HasPassword;
        string? pw = needPw ? PromptPassword("Remove Whitelist", "Enter Master Password to remove this site.") : null;
        if (needPw && pw == null) return;

        if (_serviceRunning)
        {
            var resp = await PipeClient.SendAsync(new PipeRequest { Action = "remove_whitelist", Password = pw, EntryId = id });
            if (resp is not { Success: true })
            {
                MessageBox.Show(resp?.Message ?? "Failed to remove.", "ProtectMe", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        else
        {
            _config.WebsiteWhitelist.RemoveAll(x => x.Id == id);
            ConfigStore.Save(_config);
        }
        await RefreshStateAsync();
    }

    private void PasswordToggle_Click(object sender, RoutedEventArgs e)
    {
        if (PasswordToggleSwitch.IsChecked == true && !_config.HasPassword)
        {
            SetPassword_Click(sender, e);
        }
        else if (PasswordToggleSwitch.IsChecked == false && _config.HasPassword)
        {
            // Keep password enabled for safety; prompt to change instead
            PasswordToggleSwitch.IsChecked = true;
            MessageBox.Show("Master Password stays enabled while ProtectMe is installed. Use Change Password to update it.", "ProtectMe", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void SetPassword_Click(object sender, RoutedEventArgs e)
    {
        string? currentPw = null;
        if (_config.HasPassword)
        {
            currentPw = PromptPassword("Current Password", "Enter your current Master Password.");
            if (currentPw == null) return;
        }

        string? newPw = PromptPassword("New Master Password", "Enter a new Master Password (at least 4 characters).");
        if (string.IsNullOrWhiteSpace(newPw)) return;

        if (_serviceRunning)
        {
            var resp = await PipeClient.SendAsync(new PipeRequest
            {
                Action = "set_password",
                Password = currentPw,
                NewPassword = newPw
            });
            MessageBox.Show(resp?.Message ?? "Done.", "ProtectMe");
        }
        else
        {
            if (_config.HasPassword && !SecurityHelper.VerifyPassword(currentPw ?? "", _config.PasswordHash, _config.PasswordSalt))
            {
                MessageBox.Show("Current password is incorrect.", "ProtectMe", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var (hash, salt) = SecurityHelper.HashPassword(newPw);
            _config.PasswordHash = hash;
            _config.PasswordSalt = salt;
            ConfigStore.Save(_config);
            MessageBox.Show("Master password saved.", "ProtectMe");
        }

        await RefreshStateAsync();
    }

    private async void AddCustomApp_Click(object sender, RoutedEventArgs e)
    {
        string raw = CustomAppInput.Text.Trim().ToLowerInvariant();
        if (raw.EndsWith(".exe")) raw = raw[..^4];
        if (string.IsNullOrWhiteSpace(raw)) return;

        string? pw = _config.IsProtectionLocked ? PromptPassword("Add Blocked App", "Enter Master Password to update blocked apps.") : null;
        if (_config.IsProtectionLocked && pw == null) return;

        var existing = _config.BlockedApps.FirstOrDefault(a => a.ProcessName.Equals(raw, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            existing.IsEnabled = true;
        else
            _config.BlockedApps.Insert(0, new BlockedAppEntry { ProcessName = raw, DisplayName = raw, IsEnabled = true });

        CustomAppInput.Text = string.Empty;
        await PushConfigAsync(pw);
    }

    private async void AppToggle_Click(object sender, RoutedEventArgs e)
    {
        string? pw = _config.IsProtectionLocked ? PromptPassword("Update Blocked App", "Enter Master Password to change blocked apps.") : null;
        if (_config.IsProtectionLocked && pw == null) { await RefreshStateAsync(); return; }

        await PushConfigAsync(pw);
    }

    private void BindBlockedAppsGrid()
    {
        string q = AppSearchBox?.Text?.Trim() ?? string.Empty;
        var blocked = _config.BlockedApps.Where(a => a.IsEnabled).ToList();
        var unblocked = _config.BlockedApps
            .Where(a => !a.IsEnabled &&
                        (string.IsNullOrEmpty(q) ||
                         a.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                         a.ProcessName.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        BlockedTrayCountPill.Text = $"{blocked.Count} {(blocked.Count == 1 ? "app" : "apps")}";
        BlockedTrayItems.ItemsSource = null;
        BlockedTrayItems.ItemsSource = blocked;

        AddToBlockedHeader.Text = $"ADD TO BLOCKED · {unblocked.Count}";
        UnblockedGridItems.ItemsSource = null;
        UnblockedGridItems.ItemsSource = unblocked;
    }

    private void AppSearchBox_TextChanged(object sender, TextChangedEventArgs e) => BindBlockedAppsGrid();

    private async void BlockAppTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not BlockedAppEntry app) return;
        // Same rule as ProtectMe Android: adding an app to Blocked is allowed with 1 tap!
        app.IsEnabled = true;
        await PushConfigAsync();
    }

    private async void UnblockAppTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not BlockedAppEntry app) return;
        // Same rule as ProtectMe Android: unblocking while protected requires Master Password!
        string? pw = _config.IsProtectionLocked ? PromptPassword("Unblock App", $"Enter Master Password to unblock {app.DisplayName}.") : null;
        if (_config.IsProtectionLocked && pw == null) return;

        app.IsEnabled = false;
        await PushConfigAsync(pw);
    }

    private void OpenBlockedApps_Click(object sender, RoutedEventArgs e)
    {
        PageHeaderTitle.Text = "B L O C K E D   A P P S";
        RulesHubView.Visibility = Visibility.Collapsed;
        BlockedAppsSubView.Visibility = Visibility.Visible;
        OfflineAppsSubView.Visibility = Visibility.Collapsed;
        ScheduleSubView.Visibility = Visibility.Collapsed;
        BindBlockedAppsGrid();
    }

    private void BindOfflineAppsGrid()
    {
        string q = OfflineSearchBox?.Text?.Trim() ?? string.Empty;
        var offline = _config.OfflineApps.Where(a => a.IsEnabled).ToList();
        var available = _config.OfflineApps
            .Where(a => !a.IsEnabled &&
                        (string.IsNullOrEmpty(q) ||
                         a.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                         a.ProcessName.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        OfflineTrayCountPill.Text = $"{offline.Count} {(offline.Count == 1 ? "app" : "apps")}";
        OfflineTrayItems.ItemsSource = null;
        OfflineTrayItems.ItemsSource = offline;

        AddToOfflineHeader.Text = $"ADD TO OFFLINE · {available.Count}";
        OfflineGridItems.ItemsSource = null;
        OfflineGridItems.ItemsSource = available;
    }

    private void OfflineSearchBox_TextChanged(object sender, TextChangedEventArgs e) => BindOfflineAppsGrid();

    private async void AddOfflineTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not BlockedAppEntry app) return;
        app.IsEnabled = true;

        var dlg = new GraceLoginDialog(app.DisplayName) { Owner = this };
        dlg.ShowDialog();
        app.GraceLogin10s = dlg.Add10Seconds;

        await PushConfigAsync();
    }

    private async void OfflineTrayTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not BlockedAppEntry app) return;

        var res = MessageBox.Show(
            $"Remove {app.DisplayName} from Offline?\n\nYes = Remove from Offline\nNo = Toggle 10-second login window ({(app.GraceLogin10s ? "Currently ON" : "Currently OFF")})\nCancel = Keep as is",
            app.DisplayName,
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (res == MessageBoxResult.Cancel) return;

        string? pw = _config.IsProtectionLocked ? PromptPassword("Offline App Options", $"Enter Master Password to update {app.DisplayName}.") : null;
        if (_config.IsProtectionLocked && pw == null) return;

        if (res == MessageBoxResult.Yes)
        {
            app.IsEnabled = false;
            app.GraceLogin10s = false;
        }
        else if (res == MessageBoxResult.No)
        {
            app.GraceLogin10s = !app.GraceLogin10s;
        }

        await PushConfigAsync(pw);
    }

    private void OpenOfflineApps_Click(object sender, RoutedEventArgs e)
    {
        PageHeaderTitle.Text = "A P P S   O F F L I N E";
        RulesHubView.Visibility = Visibility.Collapsed;
        BlockedAppsSubView.Visibility = Visibility.Collapsed;
        OfflineAppsSubView.Visibility = Visibility.Visible;
        ScheduleSubView.Visibility = Visibility.Collapsed;
        BindOfflineAppsGrid();
    }

    private void BindScheduleUi()
    {
        bool setOn = _config.SetTimeEnabled;
        bool weekOn = _config.ScheduleEnabled;

        SetTimeToggle.IsChecked = setOn;
        SetTimeStateLabel.Text = setOn ? "ON" : "OFF";
        SetTimeStateLabel.Foreground = (Brush)FindResource(setOn ? "GoldBrush" : "MutedBrush");
        SetTimeCard.BorderBrush = (Brush)FindResource(setOn ? "GoldBrush" : "CardBorderBrush");
        SetTimeSubText.Text = weekOn ? "Off while weekly is on" : "Protection switches on by itself";
        SetTimeAtBox.Text = _config.SetTimeAt;

        ScheduleEnabledToggle.IsChecked = weekOn;
        WeeklyStateLabel.Text = weekOn ? "ON" : "OFF";
        WeeklyStateLabel.Foreground = (Brush)FindResource(weekOn ? "GoldBrush" : "MutedBrush");
        WeeklyCard.BorderBrush = (Brush)FindResource(weekOn ? "GoldBrush" : "CardBorderBrush");
        WeeklySubText.Text = setOn ? "Off while set time is on" : "On for set days and hours";
        ScheduleStartBox.Text = _config.ScheduleStart;
        ScheduleEndBox.Text = _config.ScheduleEnd;

        ScheduleSummaryText.Text = setOn
            ? $"Every day at {_config.SetTimeAt}"
            : (weekOn ? $"Weekly {_config.ScheduleStart} – {_config.ScheduleEnd}" : "Off");

        if (_config.WeeklyDays == null || _config.WeeklyDays.Length != 7)
            _config.WeeklyDays = new[] { true, true, true, true, true, false, false };

        Button[] dayBtns = { DayBtn0, DayBtn1, DayBtn2, DayBtn3, DayBtn4, DayBtn5, DayBtn6 };
        for (int i = 0; i < 7; i++)
        {
            bool active = _config.WeeklyDays[i];
            dayBtns[i].Background = active
                ? (weekOn ? (Brush)FindResource("GoldGradientBrush") : new SolidColorBrush(Color.FromRgb(74, 70, 62)))
                : new SolidColorBrush(Color.FromRgb(20, 20, 20));
            dayBtns[i].Foreground = active
                ? new SolidColorBrush(Color.FromRgb(10, 10, 10))
                : (Brush)FindResource("MutedBrush");
            dayBtns[i].BorderBrush = new SolidColorBrush(Color.FromRgb(45, 42, 34));
            dayBtns[i].FontWeight = FontWeights.Bold;
        }
    }

    private async void SetTimeToggle_Click(object sender, RoutedEventArgs e)
    {
        bool turningOn = SetTimeToggle.IsChecked == true;
        string? pw = (!turningOn && _config.IsProtectionLocked) ? PromptPassword("Turn Off Schedule", "Enter Master Password to turn off Set Time.") : null;
        if (!turningOn && _config.IsProtectionLocked && pw == null) { BindScheduleUi(); return; }

        _config.SetTimeEnabled = turningOn;
        if (turningOn) _config.ScheduleEnabled = false; // Mutually exclusive
        _config.SetTimeAt = SetTimeAtBox.Text.Trim();
        await PushConfigAsync(pw);
    }

    private async void WeeklyToggle_Click(object sender, RoutedEventArgs e)
    {
        bool turningOn = ScheduleEnabledToggle.IsChecked == true;
        string? pw = (!turningOn && _config.IsProtectionLocked) ? PromptPassword("Turn Off Weekly", "Enter Master Password to turn off Weekly schedule.") : null;
        if (!turningOn && _config.IsProtectionLocked && pw == null) { BindScheduleUi(); return; }

        _config.ScheduleEnabled = turningOn;
        if (turningOn) _config.SetTimeEnabled = false; // Mutually exclusive
        _config.ScheduleStart = ScheduleStartBox.Text.Trim();
        _config.ScheduleEnd = ScheduleEndBox.Text.Trim();
        await PushConfigAsync(pw);
    }

    private async void DayBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || !int.TryParse(btn.Tag?.ToString(), out int idx)) return;
        string? pw = _config.IsProtectionLocked ? PromptPassword("Edit Schedule", "Enter Master Password to edit schedule days.") : null;
        if (_config.IsProtectionLocked && pw == null) return;

        _config.WeeklyDays[idx] = !_config.WeeklyDays[idx];
        await PushConfigAsync(pw);
    }

    private async void SaveSchedule_LostFocus(object sender, RoutedEventArgs e)
    {
        _config.SetTimeAt = SetTimeAtBox.Text.Trim();
        _config.ScheduleStart = ScheduleStartBox.Text.Trim();
        _config.ScheduleEnd = ScheduleEndBox.Text.Trim();
        if (!_config.IsProtectionLocked)
            await PushConfigAsync();
    }

    private void OpenSchedule_Click(object sender, MouseButtonEventArgs e)
    {
        PageHeaderTitle.Text = "S C H E D U L E";
        RulesHubView.Visibility = Visibility.Collapsed;
        BlockedAppsSubView.Visibility = Visibility.Collapsed;
        OfflineAppsSubView.Visibility = Visibility.Collapsed;
        ScheduleSubView.Visibility = Visibility.Visible;
        BindScheduleUi();
        BlockNewAppsToggle.IsChecked = _config.BlockNewInstalledApps;
    }

    private async void BlockNewAppsToggle_Click(object sender, RoutedEventArgs e)
    {
        bool turningOn = BlockNewAppsToggle.IsChecked == true;
        if (!turningOn && _config.IsProtectionLocked)
        {
            string? pw = _config.HasPassword ? PromptPassword("Auto-Block New Apps", "Enter Master Password to turn this off.") : null;
            if (_config.HasPassword && pw == null) { BlockNewAppsToggle.IsChecked = true; return; }
            _config.BlockNewInstalledApps = false;
            await PushConfigAsync(pw);
            return;
        }

        _config.BlockNewInstalledApps = turningOn;
        await PushConfigAsync();
    }

    private void BackToRules_Click(object sender, RoutedEventArgs e)
    {
        PageHeaderTitle.Text = "R U L E S";
        RulesHubView.Visibility = Visibility.Visible;
        BlockedAppsSubView.Visibility = Visibility.Collapsed;
        OfflineAppsSubView.Visibility = Visibility.Collapsed;
        ScheduleSubView.Visibility = Visibility.Collapsed;
    }

    private async void OfflineAppToggle_Click(object sender, RoutedEventArgs e)
    {
        string? pw = _config.IsProtectionLocked ? PromptPassword("Update Offline App", "Enter Master Password to change offline apps.") : null;
        if (_config.IsProtectionLocked && pw == null) { await RefreshStateAsync(); return; }
        await PushConfigAsync(pw);
    }

    private async void SaveSchedule_Click(object sender, RoutedEventArgs e)
    {
        string? pw = _config.IsProtectionLocked ? PromptPassword("Update Schedule", "Enter Master Password to change schedule.") : null;
        if (_config.IsProtectionLocked && pw == null) { await RefreshStateAsync(); return; }
        _config.ScheduleEnabled = ScheduleEnabledToggle.IsChecked == true;
        _config.ScheduleStart = ScheduleStartBox.Text.Trim();
        _config.ScheduleEnd = ScheduleEndBox.Text.Trim();
        await PushConfigAsync(pw);
    }

    private async void RescanApps_Click(object sender, RoutedEventArgs e)
    {
        _config.BlockedApps = InstalledAppScanner.ScanInstalledApps(_config.BlockedApps);
        BindUi();
        await Task.CompletedTask;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshStateAsync();

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (_config.IsProtectionLocked)
        {
            MessageBox.Show("Protection is currently ON. Unlock ProtectMe with your Master Password first before uninstalling.", "ProtectMe", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string? pw = PromptPassword("Uninstall ProtectMe", "Enter your Master Password to authorize uninstalling ProtectMe.");
        if (pw == null) return;

        if (_serviceRunning)
        {
            var resp = await PipeClient.SendAsync(new PipeRequest { Action = "verify_uninstall", Password = pw });
            if (resp is not { Success: true })
            {
                MessageBox.Show(resp?.Message ?? "Incorrect password. Uninstall denied.", "ProtectMe", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }
        else if (_config.HasPassword && !SecurityHelper.VerifyPassword(pw, _config.PasswordHash, _config.PasswordSalt))
        {
            MessageBox.Show("Incorrect password. Uninstall denied.", "ProtectMe", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        MessageBox.Show("Password verified! Now run C:\\Program Files\\ProtectMe\\Uninstall-ProtectMe.ps1 as Administrator to finish uninstalling.", "ProtectMe", MessageBoxButton.OK, MessageBoxImage.Information);
        await RefreshStateAsync();
    }

    private void SwitchTab(int index)
    {
        TopHeader.Visibility = index == 0 ? Visibility.Collapsed : Visibility.Visible;
        PageHeaderTitle.Text = index switch
        {
            1 => "P A S S W O R D",
            2 => "R U L E S",
            3 => "W E B S I T E S",
            _ => "P R O T E C T M E"
        };
        HomePanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        PasswordPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        AppsPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        WebsitesPanel.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;

        var gold = (Brush)FindResource("GoldBrush");
        var muted = (Brush)FindResource("MutedBrush");
        NavHome.Foreground = index == 0 ? gold : muted;
        NavPassword.Foreground = index == 1 ? gold : muted;
        NavApps.Foreground = index == 2 ? gold : muted;
        NavWebsites.Foreground = index == 3 ? gold : muted;

        DotHome.Visibility = index == 0 ? Visibility.Visible : Visibility.Hidden;
        DotPassword.Visibility = index == 1 ? Visibility.Visible : Visibility.Hidden;
        DotRules.Visibility = index == 2 ? Visibility.Visible : Visibility.Hidden;
        DotWebsites.Visibility = index == 3 ? Visibility.Visible : Visibility.Hidden;
    }

    private async void OpenPremiumDialog_Click(object sender, RoutedEventArgs e)
    {
        if (_config.IsPremiumActive)
        {
            var confirm = MessageBox.Show(
                "Deactivate Websites Pro on this PC so you can move your license key to a new laptop?",
                "Deactivate This PC",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            var (ok, msg) = await LicenseService.DeactivateAsync(_config.LicenseKey, _config.LicenseInstanceId);
            if (ok)
            {
                _config.IsPremiumActive = false;
                _config.LicenseKey = string.Empty;
                _config.LicenseInstanceId = string.Empty;
                await PushConfigAsync();
            }
            MessageBox.Show(msg, "ProtectMe", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new PremiumDialog(LicenseService.GetTrialDaysRemaining(_config)) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            if (dlg.LicenseActivated)
            {
                _config.IsPremiumActive = true;
                _config.LicenseKey = dlg.ActivatedKey;
                _config.LicenseInstanceId = dlg.ActivatedInstanceId;
                await PushConfigAsync();
            }
            else if (dlg.PromoWeekGranted)
            {
                _config.TrialStartedUtc = DateTime.UtcNow;
                await PushConfigAsync();
            }
        }
    }

    private void NavHome_Click(object sender, RoutedEventArgs e) => SwitchTab(0);
    private void NavPassword_Click(object sender, RoutedEventArgs e) => SwitchTab(1);
    private void NavApps_Click(object sender, RoutedEventArgs e) => SwitchTab(2);
    private async void NavWebsites_Click(object sender, RoutedEventArgs e)
    {
        if (!LicenseService.HasWebsitesAccess(_config))
        {
            var dlg = new PremiumDialog(0) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                if (dlg.LicenseActivated)
                {
                    _config.IsPremiumActive = true;
                    _config.LicenseKey = dlg.ActivatedKey;
                    _config.LicenseInstanceId = dlg.ActivatedInstanceId;
                    await PushConfigAsync();
                    SwitchTab(3);
                }
                else if (dlg.PromoWeekGranted)
                {
                    _config.TrialStartedUtc = DateTime.UtcNow;
                    await PushConfigAsync();
                    SwitchTab(3);
                }
            }
            return;
        }
        SwitchTab(3);
    }
}
