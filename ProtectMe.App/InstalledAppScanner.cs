using System.Diagnostics;
using System.IO;
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
                    IsEnabled = e.IsEnabled
                };
            }
        }

        // Common Windows apps & browsers users often want to control
        var common = new (string Proc, string Name)[]
        {
            ("chrome", "Google Chrome"),
            ("msedge", "Microsoft Edge"),
            ("firefox", "Mozilla Firefox"),
            ("brave", "Brave Browser"),
            ("opera", "Opera Browser"),
            ("discord", "Discord"),
            ("steam", "Steam"),
            ("epicgameslauncher", "Epic Games Launcher"),
            ("spotify", "Spotify"),
            ("telegram", "Telegram"),
            ("whatsapp", "WhatsApp"),
            ("vlc", "VLC Media Player"),
            ("robloxplayerbeta", "Roblox")
        };

        foreach (var (proc, name) in common)
        {
            if (!map.ContainsKey(proc))
            {
                map[proc] = new BlockedAppEntry
                {
                    ProcessName = proc,
                    DisplayName = name,
                    IsEnabled = false
                };
            }
        }

        // Also include currently running user apps with visible windows
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle == IntPtr.Zero) continue;
                string procName = p.ProcessName.ToLowerInvariant();
                if (procName is "protectme" or "explorer" or "applicationframehost" or "textinputhost") continue;

                if (!map.ContainsKey(procName))
                {
                    string display = string.IsNullOrWhiteSpace(p.MainWindowTitle) ? p.ProcessName : p.ProcessName;
                    string path = "";
                    try { path = p.MainModule?.FileName ?? ""; } catch { }
                    map[procName] = new BlockedAppEntry
                    {
                        ProcessName = procName,
                        DisplayName = display,
                        ExecutablePath = path,
                        IsEnabled = false
                    };
                }
            }
            catch { }
            finally { p.Dispose(); }
        }

        return map.Values
            .OrderByDescending(a => a.IsEnabled)
            .ThenBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
