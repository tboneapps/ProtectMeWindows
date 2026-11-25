using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ProtectMe.Core;

namespace ProtectMe.Service;

public sealed class ProcessBlockerWorker : BackgroundService
{
    private readonly ProtectionStateManager _state;
    private readonly ILogger<ProcessBlockerWorker> _logger;
    private bool _lastProxyEnabled;
    private HashSet<string>? _knownInstalledExes;
    private DateTime _lastAppScanUtc = DateTime.MinValue;

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_REFRESH = 37;

    private static readonly string[] TaskManagerProcs = { "taskmgr", "procexp", "procexp64", "processhacker", "systeminformer" };
    private static readonly string[] ServicesRegProcs = { "mmc", "regedit", "reg" };
    private static readonly string[] CliProcs = { "cmd", "powershell", "pwsh", "wt", "windowsterminal" };
    private static readonly string[] UninstallProcs = { "msiexec" };

    public ProcessBlockerWorker(ProtectionStateManager state, ILogger<ProcessBlockerWorker> logger)
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
                var cfg = _state.Current;
                EnforceSystemProxy(cfg);
                EnforceBrowserPolicies(cfg);
                EnforceProcessBlocks(cfg);
                if (cfg.BlockNewInstalledApps && (DateTime.UtcNow - _lastAppScanUtc).TotalSeconds >= 15)
                {
                    _lastAppScanUtc = DateTime.UtcNow;
                    DetectNewlyInstalledApps();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Blocker loop iteration error");
            }

            await Task.Delay(600, stoppingToken);
        }
    }

    private void DetectNewlyInstalledApps()
    {
        var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] roots =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };

        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    if (dir.Contains("ProtectMe", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        foreach (var exe in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
                        {
                            string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                            if (!string.IsNullOrWhiteSpace(name) && !name.Contains("unins") && !name.Contains("setup"))
                                current.Add(name);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        if (_knownInstalledExes == null)
        {
            _knownInstalledExes = current;
            return;
        }

        var newlyAdded = current.Except(_knownInstalledExes).ToList();
        _knownInstalledExes = current;

        if (newlyAdded.Count == 0) return;

        _state.Update(c =>
        {
            foreach (var procName in newlyAdded)
            {
                var existing = c.BlockedApps.FirstOrDefault(a => a.ProcessName.Equals(procName, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.IsEnabled = true;
                }
                else
                {
                    c.BlockedApps.Insert(0, new BlockedAppEntry
                    {
                        ProcessName = procName,
                        DisplayName = procName,
                        IsEnabled = true
                    });
                }
            }
        });
    }

    private void EnforceProcessBlocks(ProtectConfig cfg)
    {
        var blockedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (cfg.IsProtectionLocked)
        {
            foreach (var app in cfg.BlockedApps.Where(a => a.IsEnabled && !string.IsNullOrWhiteSpace(a.ProcessName)))
            {
                blockedNames.Add(app.ProcessName.Trim().ToLowerInvariant());
            }
        }

        if (blockedNames.Count == 0) return;

        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                string name = proc.ProcessName.ToLowerInvariant();
                if (blockedNames.Contains(name))
                {
                    proc.Kill(entireProcessTree: true);
                    _state.RecordBlockedEvent("App", proc.ProcessName, proc.ProcessName);
                }
            }
            catch
            {
                // Process may have already exited or be protected
            }
            finally
            {
                proc.Dispose();
            }
        }
    }

    private void EnforceSystemProxy(ProtectConfig cfg)
    {
        bool shouldEnable = cfg.IsProtectionLocked &&
                            cfg.AntiTamper.EnforceSystemProxy &&
                            (cfg.WhitelistOnlyWebsites || cfg.SafeBrowsingEnabled || cfg.BlockImageVideoSearch);
        string proxyServer = $"127.0.0.1:{cfg.ProxyPort}";

        // Update all loaded user hives (works when running as LocalSystem)
        foreach (string sid in Registry.Users.GetSubKeyNames())
        {
            if (sid.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase) || sid.Length < 10)
                continue;

            try
            {
                using var key = Registry.Users.OpenSubKey($@"{sid}\Software\Microsoft\Windows\CurrentVersion\Internet Settings", writable: true);
                if (key == null) continue;

                if (shouldEnable)
                {
                    key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                    key.SetValue("ProxyServer", proxyServer, RegistryValueKind.String);
                    key.SetValue("ProxyOverride", "localhost;127.0.0.1;<local>", RegistryValueKind.String);
                }
                else if (_lastProxyEnabled)
                {
                    key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                }
            }
            catch
            {
                // Ignore inaccessible hives
            }
        }

        if (shouldEnable != _lastProxyEnabled)
        {
            _lastProxyEnabled = shouldEnable;
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        }
    }

    private bool? _lastSafeBrowsingPolicy;
    private bool? _lastImageSearchPolicy;

    private void EnforceBrowserPolicies(ProtectConfig cfg)
    {
        bool safeOn = cfg.IsProtectionLocked && cfg.SafeBrowsingEnabled;
        bool imgBlockOn = cfg.IsProtectionLocked && cfg.BlockImageVideoSearch;

        if (_lastSafeBrowsingPolicy == safeOn && _lastImageSearchPolicy == imgBlockOn)
            return;

        _lastSafeBrowsingPolicy = safeOn;
        _lastImageSearchPolicy = imgBlockOn;

        string[] browserPolicyRoots =
        {
            @"SOFTWARE\Policies\BraveSoftware\Brave",
            @"SOFTWARE\Policies\Google\Chrome",
            @"SOFTWARE\Policies\Microsoft\Edge"
        };

        string[] imageVideoPatterns =
        {
            "*tbm=isch*", "*tbm=vid*", "*udm=2*", "*udm=7*",
            "*iax=images*", "*iax=videos*", "*ia=images*", "*ia=videos*",
            "*/images/search*", "*/videos/search*", "*/imghp*", "*/videohp*"
        };

        foreach (string root in browserPolicyRoots)
        {
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(root, writable: true);
                if (key == null) continue;

                // Safe Browsing: Force Google SafeSearch + YouTube Strict Restricted Mode
                key.SetValue("ForceGoogleSafeSearch", safeOn ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("ForceYouTubeRestrict", safeOn ? 2 : 0, RegistryValueKind.DWord);

                // Block Image & Video Search across HTTPS search engines
                key.DeleteSubKeyTree("URLBlocklist", throwOnMissingSubKey: false);
                if (imgBlockOn)
                {
                    using var blockKey = key.CreateSubKey("URLBlocklist", writable: true);
                    if (blockKey != null)
                    {
                        for (int i = 0; i < imageVideoPatterns.Length; i++)
                        {
                            blockKey.SetValue((i + 1).ToString(), imageVideoPatterns[i], RegistryValueKind.String);
                        }
                    }
                }
            }
            catch { }
        }
    }

    private void EnforceTaskManagerPolicy(ProtectConfig cfg)
    {
        bool disableTaskMgr = cfg.IsProtectionLocked && cfg.AntiTamper.BlockTaskManager;

        foreach (string sid in Registry.Users.GetSubKeyNames())
        {
            if (sid.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase) || sid.Length < 10)
                continue;

            try
            {
                using var key = Registry.Users.CreateSubKey($@"{sid}\Software\Microsoft\Windows\CurrentVersion\Policies\System", writable: true);
                if (key == null) continue;

                if (disableTaskMgr)
                {
                    key.SetValue("DisableTaskMgr", 1, RegistryValueKind.DWord);
                }
                else
                {
                    if (key.GetValue("DisableTaskMgr") != null)
                        key.SetValue("DisableTaskMgr", 0, RegistryValueKind.DWord);
                }
            }
            catch
            {
                // Ignore inaccessible hives
            }
        }
    }
}
