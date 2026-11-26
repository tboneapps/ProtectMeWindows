using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ProtectMe.Core;

namespace ProtectMe.Service;

public sealed class PipeServerWorker : BackgroundService
{
    private readonly ProtectionStateManager _state;
    private readonly ILogger<PipeServerWorker> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public PipeServerWorker(ProtectionStateManager state, ILogger<PipeServerWorker> logger)
    {
        _state = state;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var security = new PipeSecurity();
                security.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                    PipeAccessRights.ReadWrite,
                    AccessControlType.Allow));
                security.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    PipeAccessRights.FullControl,
                    AccessControlType.Allow));

                using var server = NamedPipeServerStreamAcl.Create(
                    PipeConstants.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    4096,
                    4096,
                    security);

                await server.WaitForConnectionAsync(stoppingToken);
                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                string? line = await reader.ReadLineAsync(stoppingToken);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    var req = JsonSerializer.Deserialize<PipeRequest>(line, JsonOptions);
                    var resp = HandleRequest(req);
                    byte[] respBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(resp, JsonOptions) + "\n");
                    await server.WriteAsync(respBytes, stoppingToken);
                    await server.FlushAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Pipe server error");
                await Task.Delay(250, stoppingToken);
            }
        }
    }

    private PipeResponse HandleRequest(PipeRequest? req)
    {
        if (req == null) return new PipeResponse { Success = false, Message = "Empty request." };

        var cfg = _state.Current;

        switch (req.Action.ToLowerInvariant())
        {
            case "get_config":
                return new PipeResponse { Success = true, Config = _state.GetSnapshot(), ServiceRunning = true };

            case "set_password":
                if (cfg.HasPassword && !_state.VerifyPassword(req.Password))
                    return new PipeResponse { Success = false, Message = "Current password is incorrect." };
                if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 4)
                    return new PipeResponse { Success = false, Message = "Password must be at least 4 characters." };

                var (hash, salt) = SecurityHelper.HashPassword(req.NewPassword);
                _state.Update(c =>
                {
                    c.PasswordHash = hash;
                    c.PasswordSalt = salt;
                });
                return new PipeResponse { Success = true, Message = "Master password updated.", Config = _state.GetSnapshot() };

            case "lock":
                _state.Update(c =>
                {
                    c.IsProtectionLocked = true;
                    c.LockedUntilUtc = req.TimerMinutes is > 0 ? DateTime.UtcNow.AddMinutes(req.TimerMinutes.Value) : null;
                });
                return new PipeResponse { Success = true, Message = "Protection locked.", Config = _state.GetSnapshot() };

            case "unlock":
                if (!_state.VerifyPassword(req.Password))
                    return new PipeResponse { Success = false, Message = "Incorrect master password." };

                if (cfg.LockedUntilUtc.HasValue && cfg.LockedUntilUtc.Value > DateTime.UtcNow)
                {
                    var rem = cfg.LockedUntilUtc.Value - DateTime.UtcNow;
                    return new PipeResponse
                    {
                        Success = false,
                        Message = $"Timer lock active ({(int)rem.TotalMinutes}m {rem.Seconds}s remaining)."
                    };
                }

                _state.Update(c =>
                {
                    c.IsProtectionLocked = false;
                    c.LockedUntilUtc = null;
                });
                return new PipeResponse { Success = true, Message = "Protection unlocked.", Config = _state.GetSnapshot() };

            case "update_config":
                if (req.Config == null)
                    return new PipeResponse { Success = false, Message = "Missing config." };

                // Allow tightening protection (turning features ON) while locked without password;
                // require password only when weakening protection while locked.
                bool weakening =
                    (cfg.WhitelistOnlyWebsites && !req.Config.WhitelistOnlyWebsites) ||
                    (cfg.SafeBrowsingEnabled && !req.Config.SafeBrowsingEnabled) ||
                    (cfg.BlockPornEnabled && !req.Config.BlockPornEnabled) ||
                    (cfg.BlockImageVideoSearch && !req.Config.BlockImageVideoSearch) ||
                    (cfg.BlockNewInstalledApps && !req.Config.BlockNewInstalledApps);

                if (cfg.IsProtectionLocked && cfg.HasPassword && weakening && !_state.VerifyPassword(req.Password))
                    return new PipeResponse { Success = false, Message = "Password required while locked." };

                _state.Update(c =>
                {
                    c.WhitelistOnlyWebsites = req.Config.WhitelistOnlyWebsites;
                    c.SafeBrowsingEnabled = req.Config.SafeBrowsingEnabled;
                    c.BlockPornEnabled = req.Config.BlockPornEnabled;
                    c.BlockImageVideoSearch = req.Config.BlockImageVideoSearch;
                    c.TrialStartedUtc = req.Config.TrialStartedUtc;
                    c.StrictPopupShown = req.Config.StrictPopupShown;
                    c.WebsitesIntroShown = req.Config.WebsitesIntroShown;
                    c.IsPremiumActive = req.Config.IsPremiumActive;
                    c.LicenseKey = req.Config.LicenseKey;
                    c.LicenseInstanceId = req.Config.LicenseInstanceId;
                    c.AntiTamper = req.Config.AntiTamper;
                    c.BlockNewInstalledApps = req.Config.BlockNewInstalledApps;
                    c.BlockedApps = req.Config.BlockedApps;
                    c.OfflineApps = req.Config.OfflineApps;
                    c.SetTimeEnabled = req.Config.SetTimeEnabled;
                    c.SetTimeAt = req.Config.SetTimeAt;
                    c.ScheduleEnabled = req.Config.ScheduleEnabled;
                    c.WeeklyDays = req.Config.WeeklyDays;
                    c.ScheduleStart = req.Config.ScheduleStart;
                    c.ScheduleEnd = req.Config.ScheduleEnd;
                    c.WebsiteWhitelist = req.Config.WebsiteWhitelist;
                });
                return new PipeResponse { Success = true, Message = "Saved.", Config = _state.GetSnapshot() };

            case "add_whitelist":
                if (cfg.IsProtectionLocked && !_state.VerifyPassword(req.Password))
                    return new PipeResponse { Success = false, Message = "Password required while locked." };
                if (req.WhitelistEntry == null || string.IsNullOrWhiteSpace(req.WhitelistEntry.Value))
                    return new PipeResponse { Success = false, Message = "Enter a website or URL." };

                string norm = req.WhitelistEntry.Scope == WhitelistScope.WholeWebsite
                    ? SecurityHelper.NormalizeDomain(req.WhitelistEntry.Value)
                    : SecurityHelper.NormalizeWebpageUrl(req.WhitelistEntry.Value);

                _state.Update(c =>
                {
                    if (!c.WebsiteWhitelist.Any(e => e.Scope == req.WhitelistEntry.Scope && e.Value.Equals(norm, StringComparison.OrdinalIgnoreCase)))
                    {
                        c.WebsiteWhitelist.Insert(0, new WhitelistEntry
                        {
                            Value = norm,
                            Scope = req.WhitelistEntry.Scope,
                            AddedUtc = DateTime.UtcNow
                        });
                    }
                    // Clear any queued RecentBlocked entries for this domain so popup never re-triggers
                    string normDom = SecurityHelper.NormalizeDomain(norm);
                    c.RecentBlocked.RemoveAll(r => r.Domain.Equals(normDom, StringComparison.OrdinalIgnoreCase));
                });
                return new PipeResponse { Success = true, Message = "Added to whitelist.", Config = _state.GetSnapshot() };

            case "remove_whitelist":
                if (cfg.IsProtectionLocked && !_state.VerifyPassword(req.Password))
                    return new PipeResponse { Success = false, Message = "Password required while locked." };

                _state.Update(c => c.WebsiteWhitelist.RemoveAll(e => e.Id == req.EntryId));
                return new PipeResponse { Success = true, Message = "Removed.", Config = _state.GetSnapshot() };

            case "prepare_upgrade":
                // Keeps IsProtectionLocked untouched so protection NEVER turns off during an update!
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "sc.exe",
                        Arguments = "sdset ProtectMeService \"D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)\"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    })?.WaitForExit(3000);
                }
                catch { }
                return new PipeResponse { Success = true, Message = "Ready for upgrade (protection state preserved)." };

            case "verify_uninstall":
                if (!_state.VerifyPassword(req.Password))
                    return new PipeResponse { Success = false, Message = "Incorrect master password. Uninstall denied." };
                _state.Update(c => c.IsProtectionLocked = false);
                try
                {
                    // Restore Administrator full control on ProtectMeService so the uninstaller can stop & delete it
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "sc.exe",
                        Arguments = "sdset ProtectMeService \"D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)\"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    })?.WaitForExit(3000);
                }
                catch { }
                return new PipeResponse { Success = true, Message = "Uninstall authorized." };

            default:
                return new PipeResponse { Success = false, Message = "Unknown action." };
        }
    }
}
