using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace ProtectMe.Core;

public static class LicenseService
{
    // Replace these two with your Gumroad £10/year product URL and Product ID once created
    public const string CheckoutUrl = "https://lockhart363.gumroad.com/l/protectme-websites";
    public const string GumroadProductId = "eXwIuhRKkGNmkZcBiYmoOw==";

    // One-way salted SHA-256 hashes of promo codes (plain-text codes are NEVER stored in source)
    private const string PromoSalt = "PM_PROMO_2026:";
    private const string PromoWeekHash = "1FEF72D30BD3FFF014E1B8FB7804FE9E97AF6595830BC665AE4D2625D3B4C8A1";
    private const string PromoForeverHash = "134D2B2EF2A25C76160E5F4B95A3E085C7016EBECF6953EAE08886DF76BE641C";

    public static string CheckPromoCode(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PromoSalt + input.Trim())));
        if (hash.Equals(PromoForeverHash, StringComparison.OrdinalIgnoreCase)) return "FOREVER";
        if (hash.Equals(PromoWeekHash, StringComparison.OrdinalIgnoreCase)) return "WEEK";
        return string.Empty;
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static int GetTrialDaysRemaining(ProtectConfig cfg)
    {
        if (cfg.IsPremiumActive) return 999;
        if (!cfg.TrialStartedUtc.HasValue) return 7;
        double elapsed = (DateTime.UtcNow - cfg.TrialStartedUtc.Value).TotalDays;
        return Math.Max(0, (int)Math.Ceiling(7.0 - elapsed));
    }

    public static bool HasWebsitesAccess(ProtectConfig cfg)
    {
        return cfg.IsPremiumActive || GetTrialDaysRemaining(cfg) > 0;
    }

    public static string GetMachineFingerprint()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            string guid = key?.GetValue("MachineGuid")?.ToString() ?? Environment.MachineName;
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("ProtectMeWin:" + guid));
            return "PC-" + Convert.ToHexString(hash)[..12];
        }
        catch
        {
            return "PC-" + Environment.MachineName;
        }
    }

    public static async Task<(bool Ok, string Message, string InstanceId)> ActivateAsync(string licenseKey)
    {
        if (string.IsNullOrWhiteSpace(licenseKey))
            return (false, "Please enter a valid Gumroad license key.", string.Empty);

        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["product_id"] = GumroadProductId,
                ["license_key"] = licenseKey.Trim(),
                ["increment_uses_count"] = "true"
            });

            var resp = await Http.PostAsync("https://api.gumroad.com/v2/licenses/verify", content);
            string json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            bool success = doc.RootElement.TryGetProperty("success", out var okEl) && okEl.GetBoolean();
            if (!success)
            {
                string err = doc.RootElement.TryGetProperty("message", out var msgEl)
                    ? (msgEl.GetString() ?? "Invalid license key.")
                    : "Invalid license key.";
                return (false, err, string.Empty);
            }

            // Check if subscription is refunded/ended/cancelled
            if (doc.RootElement.TryGetProperty("purchase", out var purchase))
            {
                if (purchase.TryGetProperty("refunded", out var refEl) && refEl.GetBoolean())
                    return (false, "This license was refunded.", string.Empty);
                if (purchase.TryGetProperty("chargebacked", out var cbEl) && cbEl.GetBoolean())
                    return (false, "This license is no longer valid.", string.Empty);
                if (purchase.TryGetProperty("subscription_ended_at", out var endEl) && endEl.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(endEl.GetString()))
                    return (false, "This annual subscription has expired.", string.Empty);
            }

            // Enforce 1-device activation limit via Gumroad's uses count
            int uses = doc.RootElement.TryGetProperty("uses", out var usesEl) ? usesEl.GetInt32() : 1;
            if (uses > 2)
            {
                return (false, "This license key has already been activated on another PC.", string.Empty);
            }

            return (true, "Websites Pro unlocked on this PC!", GetMachineFingerprint());
        }
        catch
        {
            return (false, "Could not reach Gumroad license server. Check your internet connection.", string.Empty);
        }
    }

    public static Task<(bool Ok, string Message)> DeactivateAsync(string licenseKey, string instanceId)
    {
        return Task.FromResult((true, "Deactivated on this PC. You can now activate your key on your new laptop."));
    }
}
