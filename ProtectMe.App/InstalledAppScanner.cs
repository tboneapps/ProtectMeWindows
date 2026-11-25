using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using ProtectMe.Core;

namespace ProtectMe.App;

public static class InstalledAppScanner
{
    public static List<BlockedAppEntry> ScanInstalledApps(IEnumerable<BlockedAppEntry> existing)
    {
        var map = new Dictionary<string, BlockedAppEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in existing)
        {
            if (!string.IsNullOrWhiteSpace(e.ProcessName))
            {
                map[e.ProcessName.ToLowerInvariant()] = new BlockedAppEntry
                {
                    ProcessName = e.ProcessName.ToLowerInvariant(),
                    DisplayName = string.IsNullOrWhiteSpace(e.DisplayName) ? e.ProcessName : e.DisplayName,
                    ExecutablePath = e.ExecutablePath,
                    IsEnabled = e.IsEnabled,
                    GraceLogin10s = e.GraceLogin10s
                };
            }
        }

        // 1. Scan Windows Uninstall Registry keys for all installed desktop apps (real user apps only)
        ScanUninstallRegistry(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", map);
        ScanUninstallRegistry(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", map);
        ScanUninstallRegistry(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", map);

        // 2. Also include currently running user apps with visible windows
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle == IntPtr.Zero) continue;
                string procName = p.ProcessName.ToLowerInvariant();
                if (procName is "protectme" or "explorer" or "applicationframehost" or "textinputhost" or "shellexperiencehost") continue;

                string path = "";
                try { path = p.MainModule?.FileName ?? ""; } catch { }

                if (!map.ContainsKey(procName))
                {
                    map[procName] = new BlockedAppEntry
                    {
                        ProcessName = procName,
                        DisplayName = p.ProcessName,
                        ExecutablePath = path,
                        IsEnabled = false
                    };
                }
                else if (string.IsNullOrWhiteSpace(map[procName].ExecutablePath) && !string.IsNullOrWhiteSpace(path))
                {
                    map[procName].ExecutablePath = path;
                }
            }
            catch { }
            finally { p.Dispose(); }
        }

        // Deduplicate by DisplayName (prefer entries with an executable path)
        return map.Values
            .GroupBy(a => a.DisplayName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(a => a.IsEnabled).ThenByDescending(a => !string.IsNullOrWhiteSpace(a.ExecutablePath)).First())
            .OrderByDescending(a => a.IsEnabled)
            .ThenBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ScanUninstallRegistry(RegistryKey root, string subKeyPath, Dictionary<string, BlockedAppEntry> map)
    {
        try
        {
            using var key = root.OpenSubKey(subKeyPath);
            if (key == null) return;

            foreach (string name in key.GetSubKeyNames())
            {
                try
                {
                    using var appKey = key.OpenSubKey(name);
                    if (appKey == null) continue;

                    string? displayName = appKey.GetValue("DisplayName")?.ToString();
                    if (string.IsNullOrWhiteSpace(displayName) || displayName.Contains("ProtectMe", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string? exePath = ExtractExePath(appKey.GetValue("DisplayIcon")?.ToString());
                    if (string.IsNullOrWhiteSpace(exePath))
                    {
                        string? installLoc = appKey.GetValue("InstallLocation")?.ToString()?.Trim('"');
                        if (!string.IsNullOrWhiteSpace(installLoc) && Directory.Exists(installLoc))
                        {
                            exePath = Directory.EnumerateFiles(installLoc, "*.exe", SearchOption.TopDirectoryOnly)
                                .FirstOrDefault(f => !f.Contains("unins", StringComparison.OrdinalIgnoreCase));
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(exePath))
                    {
                        AddExeIfValid(exePath, displayName, map);
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    private static string? ExtractExePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string cleaned = raw.Trim().Trim('"');
        int comma = cleaned.IndexOf(',');
        if (comma > 0) cleaned = cleaned[..comma].Trim().Trim('"');
        return cleaned.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(cleaned) ? cleaned : null;
    }

    private static void AddExeIfValid(string exePath, string displayName, Dictionary<string, BlockedAppEntry> map)
    {
        string procName = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(procName) ||
            procName.Contains("unins") ||
            procName.Contains("setup") ||
            procName.Contains("update") ||
            procName.Contains("crash") ||
            procName is "protectme" or "protectme.service")
        {
            return;
        }

        if (!map.TryGetValue(procName, out var existing))
        {
            map[procName] = new BlockedAppEntry
            {
                ProcessName = procName,
                DisplayName = displayName,
                ExecutablePath = exePath,
                IsEnabled = false
            };
        }
        else if (string.IsNullOrWhiteSpace(existing.ExecutablePath))
        {
            existing.ExecutablePath = exePath;
        }
    }
}
