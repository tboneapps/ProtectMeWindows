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
                EnforceProcessBlocks(cfg);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Blocker loop iteration error");
            }

            await Task.Delay(600, stoppingToken);
        }
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
        bool shouldEnable = cfg.IsProtectionLocked && cfg.WhitelistOnlyWebsites && cfg.AntiTamper.EnforceSystemProxy;
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
