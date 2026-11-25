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
                if (!cfg.HasPassword)
                    return new PipeResponse { Success = false, Message = "Set a master password before locking." };

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
                if (cfg.IsProtectionLocked && !_state.VerifyPassword(req.Password))
                    return new PipeResponse { Success = false, Message = "Password required while locked." };
                if (req.Config == null)
                    return new PipeResponse { Success = false, Message = "Missing config." };

                _state.Update(c =>
                {
                    c.WhitelistOnlyWebsites = req.Config.WhitelistOnlyWebsites;
                    c.SafeBrowsingEnabled = req.Config.SafeBrowsingEnabled;
                    c.BlockImageVideoSearch = req.Config.BlockImageVideoSearch;
                    c.TrialStartedUtc = req.Config.TrialStartedUtc;
                    c.IsPremiumActive = req.Config.IsPremiumActive;
                    c.LicenseKey = req.Config.LicenseKey;
                    c.LicenseInstanceId = req.Config.LicenseInstanceId;
                    c.AntiTamper = req.Config.AntiTamper;
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
                });
                return new PipeResponse { Success = true, Message = "Added to whitelist.", Config = _state.GetSnapshot() };

            case "remove_whitelist":
                if (cfg.IsProtectionLocked && !_state.VerifyPassword(req.Password))
                    return new PipeResponse { Success = false, Message = "Password required while locked." };

                _state.Update(c => c.WebsiteWhitelist.RemoveAll(e => e.Id == req.EntryId));
                return new PipeResponse { Success = true, Message = "Removed.", Config = _state.GetSnapshot() };

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
