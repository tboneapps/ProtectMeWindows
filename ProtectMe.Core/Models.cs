using System.Text.Json.Serialization;

namespace ProtectMe.Core;

public enum WhitelistScope
{
    WholeWebsite,
    SpecificWebpage
}

public sealed class WhitelistEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Value { get; set; } = string.Empty; // domain (e.g. "google.com") or URL (e.g. "https://example.com/page")
    public WhitelistScope Scope { get; set; } = WhitelistScope.WholeWebsite;
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class BlockedAppEntry
{
    public string ProcessName { get; set; } = string.Empty; // without .exe, lowercase (e.g. "chrome", "discord")
    public string DisplayName { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public bool GraceLogin10s { get; set; } = false;
}

public sealed class BlockedEvent
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string Kind { get; set; } = "Website"; // "Website" or "App"
    public string Target { get; set; } = string.Empty; // URL or ProcessName
    public string Domain { get; set; } = string.Empty;
}

public sealed class AntiTamperRules
{
    public bool BlockTaskManager { get; set; } = true;
    public bool BlockServicesAndRegistry { get; set; } = true;
    public bool BlockCommandLineTools { get; set; } = true;
    public bool BlockUninstallAndControlPanel { get; set; } = true;
    public bool BlockClockTampering { get; set; } = true;
    public bool EnforceSystemProxy { get; set; } = true;
    public bool RequirePasswordToUninstall { get; set; } = true;
}

public sealed class ProtectConfig
{
    public bool IsProtectionLocked { get; set; } = false;
    public bool WhitelistOnlyWebsites { get; set; } = true;
    public bool SafeBrowsingEnabled { get; set; } = true;
    public bool BlockImageVideoSearch { get; set; } = true;
    public int ProxyPort { get; set; } = 8787;

    // 7-Day Free Trial + Lemon Squeezy £10/year Websites Pro License
    public DateTime? TrialStartedUtc { get; set; }
    public bool IsPremiumActive { get; set; } = false;
    public string LicenseKey { get; set; } = string.Empty;
    public string LicenseInstanceId { get; set; } = string.Empty;

    // Salted PBKDF2-SHA256 hash of the master password
    public string PasswordHash { get; set; } = string.Empty;
    public string PasswordSalt { get; set; } = string.Empty;

    // Optional timer lock (UTC). If in the future, cannot unlock even with password unless emergency override is disabled
    public DateTime? LockedUntilUtc { get; set; }

    public AntiTamperRules AntiTamper { get; set; } = new();
    public bool BlockNewInstalledApps { get; set; } = false;
    public List<WhitelistEntry> WebsiteWhitelist { get; set; } = new();
    public List<BlockedAppEntry> BlockedApps { get; set; } = new();
    public List<BlockedAppEntry> OfflineApps { get; set; } = new();
    public bool SetTimeEnabled { get; set; } = false;
    public string SetTimeAt { get; set; } = "23:00";
    public bool ScheduleEnabled { get; set; } = false;
    public bool[] WeeklyDays { get; set; } = new[] { true, true, true, true, true, false, false }; // M T W T F S S
    public string ScheduleStart { get; set; } = "09:00";
    public string ScheduleEnd { get; set; } = "17:00";
    public List<BlockedEvent> RecentBlocked { get; set; } = new();

    [JsonIgnore]
    public bool HasPassword => !string.IsNullOrWhiteSpace(PasswordHash) && !string.IsNullOrWhiteSpace(PasswordSalt);
}
