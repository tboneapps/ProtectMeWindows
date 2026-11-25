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
                            (cfg.WhitelistOnlyWebsites || cfg.SafeBrowsingEnabled || cfg.BlockPornEnabled || cfg.BlockImageVideoSearch);
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
    private bool? _lastPornPolicy;
    private bool? _lastImageSearchPolicy;
    private int _lastWhitelistHash;

    private void EnforceBrowserPolicies(ProtectConfig cfg)
    {
        bool safeOn = cfg.IsProtectionLocked && cfg.SafeBrowsingEnabled;
        bool pornOn = cfg.IsProtectionLocked && cfg.BlockPornEnabled;
        bool imgBlockOn = cfg.IsProtectionLocked && cfg.BlockImageVideoSearch;
        int wlHash = string.Join("|", cfg.WebsiteWhitelist.Select(w => w.Value)).GetHashCode();

        if (_lastSafeBrowsingPolicy == safeOn && _lastPornPolicy == pornOn && _lastImageSearchPolicy == imgBlockOn && _lastWhitelistHash == wlHash)
            return;

        _lastSafeBrowsingPolicy = safeOn;
        _lastPornPolicy = pornOn;
        _lastImageSearchPolicy = imgBlockOn;
        _lastWhitelistHash = wlHash;

        string[] browserPolicyRoots =
        {
            @"SOFTWARE\Policies\BraveSoftware\Brave",
            @"SOFTWARE\Policies\Google\Chrome",
            @"SOFTWARE\Policies\Microsoft\Edge",
            @"SOFTWARE\Policies\Opera Software\Opera Stable",
            @"SOFTWARE\Policies\Opera Software\Opera GX Stable",
            @"SOFTWARE\Policies\Vivaldi",
            @"SOFTWARE\Policies\Chromium",
            @"SOFTWARE\Policies\YandexBrowser",
            @"SOFTWARE\Policies\The Browser Company\Arc",
            @"SOFTWARE\Policies\Thorium",
            @"SOFTWARE\Policies\Comodo\Dragon"
        };

        // Valid Chromium URLBlocklist patterns (must start with host/domain, not leading '*')
        string[] imageVideoPatterns =
        {
            // Google (global & UK) Images & Videos
            "google.com/search?*udm=2*", "google.com/search?*udm=7*",
            "google.com/search?*tbm=isch*", "google.com/search?*tbm=vid*",
            "google.com/imghp*", "google.com/imgres*", "google.com/videohp*",
            "google.co.uk/search?*udm=2*", "google.co.uk/search?*udm=7*",
            "google.co.uk/search?*tbm=isch*", "google.co.uk/search?*tbm=vid*",
            "google.co.uk/imghp*", "google.co.uk/imgres*",
            // Bing Images & Videos
            "bing.com/images*", "bing.com/videos*",
            // Brave Search Images & Videos
            "search.brave.com/images*", "search.brave.com/videos*",
            // DuckDuckGo Images & Videos
            "duckduckgo.com/*iax=images*", "duckduckgo.com/*iax=videos*",
            "duckduckgo.com/*ia=images*", "duckduckgo.com/*ia=videos*",
            // Yahoo, Yandex, Ecosia, Qwant, Startpage, Baidu
            "images.search.yahoo.com", "video.search.yahoo.com",
            "yandex.com/images*", "yandex.com/video*", "yandex.ru/images*", "yandex.ru/video*",
            "ecosia.org/images*", "ecosia.org/videos*",
            "qwant.com/*t=images*", "qwant.com/*t=videos*",
            "startpage.com/*cat=images*", "startpage.com/*cat=videos*",
            "image.baidu.com", "v.baidu.com"
        };

        foreach (string root in browserPolicyRoots)
        {
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(root, writable: true);
                if (key == null) continue;

                // Safe Browsing:
                // 1. Force Google SafeSearch + YouTube Strict Restricted Mode
                key.SetValue("ForceGoogleSafeSearch", safeOn ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("ForceYouTubeRestrict", safeOn ? 2 : 0, RegistryValueKind.DWord);

                // 2. Block ALL images on non-whitelisted websites (DefaultImagesSetting = 2)
                //    and allow images on whitelisted websites (ImagesAllowedForUrls)
                key.SetValue("DefaultImagesSetting", safeOn ? 2 : 1, RegistryValueKind.DWord);
                key.DeleteSubKeyTree("ImagesAllowedForUrls", throwOnMissingSubKey: false);
                if (safeOn && cfg.WebsiteWhitelist.Count > 0)
                {
                    using var allowImgKey = key.CreateSubKey("ImagesAllowedForUrls", writable: true);
                    if (allowImgKey != null)
                    {
                        int idx = 1;
                        foreach (var w in cfg.WebsiteWhitelist)
                        {
                            string dom = SecurityHelper.NormalizeDomain(w.Value);
                            if (!string.IsNullOrWhiteSpace(dom))
                            {
                                allowImgKey.SetValue(idx++.ToString(), $"[*.]{dom}", RegistryValueKind.String);
                            }
                        }
                    }
                }

                // 3. Block ALL file downloads in Brave/Chrome/Edge (DownloadRestrictions = 3)
                key.SetValue("DownloadRestrictions", safeOn ? 3 : 0, RegistryValueKind.DWord);
                // Ensure browser reloads policies cleanly on restart (don't stay stuck in background)
                key.SetValue("BackgroundModeEnabled", 0, RegistryValueKind.DWord);

                // 4. Auto-install & lock ProtectMe Companion Extension when Safe Browsing is ON
                //    (renders images & videos as clean solid black boxes — zero clicks for customer)
                key.DeleteSubKeyTree("ExtensionInstallForcelist", throwOnMissingSubKey: false);
                if (safeOn)
                {
                    using var extKey = key.CreateSubKey("ExtensionInstallForcelist", writable: true);
                    extKey?.SetValue("1", "kalbgbihhjjlejdfejimdkiemfaijobi;https://clients2.google.com/service/update2/crx", RegistryValueKind.String);
                }

                // Combine URLBlocklist patterns (Image/Video Search + Porn domains & searches)
                var blockPatterns = new List<string>();
                if (imgBlockOn) blockPatterns.AddRange(imageVideoPatterns);
                if (pornOn)
                {
                    blockPatterns.AddRange(new[]
                    {
                        "pornhub.com", "xvideos.com", "xnxx.com", "xhamster.com", "redtube.com", "youporn.com",
                        "tube8.com", "spankbang.com", "brazzers.com", "onlyfans.com", "chaturbate.com",
                        "stripchat.com", "bongacams.com", "cam4.com", "livejasmin.com", "myfreecams.com",
                        "eporner.com", "hqporner.com", "tnaflix.com", "rule34.xxx", "nhentai.net", "e-hentai.org",
                        "sex.com", "xxx.com",
                        "google.com/search?*porn*", "google.co.uk/search?*porn*", "bing.com/search?*porn*",
                        "duckduckgo.com/*porn*", "search.brave.com/search?*porn*", "search.yahoo.com/*porn*"
                    });
                }

                key.DeleteSubKeyTree("URLBlocklist", throwOnMissingSubKey: false);
                key.DeleteSubKeyTree("URLAllowlist", throwOnMissingSubKey: false);
                if (blockPatterns.Count > 0)
                {
                    using var blockKey = key.CreateSubKey("URLBlocklist", writable: true);
                    if (blockKey != null)
                    {
                        for (int i = 0; i < blockPatterns.Count; i++)
                            blockKey.SetValue((i + 1).ToString(), blockPatterns[i], RegistryValueKind.String);
                    }
                    if (pornOn)
                    {
                        using var allowKey = key.CreateSubKey("URLAllowlist", writable: true);
                        allowKey?.SetValue("1", "jw.org", RegistryValueKind.String);
                    }
                }
            }
            catch { }
        }

        // Also enforce Firefox / LibreWolf / Waterfox policies
        string[] geckoRoots =
        {
            @"SOFTWARE\Policies\Mozilla\Firefox",
            @"SOFTWARE\Policies\LibreWolf",
            @"SOFTWARE\Policies\Waterfox"
        };
        foreach (string geckoRoot in geckoRoots)
        {
            try
            {
                using var ffKey = Registry.LocalMachine.CreateSubKey(geckoRoot, writable: true);
                if (ffKey == null) continue;
                ffKey.SetValue("DisablePrivateBrowsing", safeOn ? 1 : 0, RegistryValueKind.DWord);
                ffKey.SetValue("BlockAboutConfig", safeOn ? 1 : 0, RegistryValueKind.DWord);
                ffKey.DeleteSubKeyTree("WebsiteFilter", throwOnMissingSubKey: false);
                if (imgBlockOn)
                {
                    using var blockKey = ffKey.CreateSubKey(@"WebsiteFilter\Block", writable: true);
                    if (blockKey != null)
                    {
                        for (int i = 0; i < imageVideoPatterns.Length; i++)
                            blockKey.SetValue((i + 1).ToString(), imageVideoPatterns[i], RegistryValueKind.String);
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
