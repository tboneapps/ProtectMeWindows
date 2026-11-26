using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows;

namespace ProtectMe.Installer;

public partial class MainWindow : Window
{
    private const string InstallDir = @"C:\Program Files\ProtectMe";
    private bool _installed;
    private readonly bool _autoMode;

    public MainWindow()
    {
        InitializeComponent();
        _autoMode = Environment.GetCommandLineArgs().Any(a => a.Equals("--auto", StringComparison.OrdinalIgnoreCase));
        if (_autoMode)
        {
            Loaded += (_, _) => ActionButton_Click(this, new RoutedEventArgs());
        }
    }

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_installed)
        {
            string appExe = Path.Combine(InstallDir, "App", "ProtectMe.exe");
            if (File.Exists(appExe))
            {
                Process.Start(new ProcessStartInfo { FileName = appExe, WorkingDirectory = Path.GetDirectoryName(appExe), UseShellExecute = true });
            }
            Close();
            return;
        }

        ActionButton.IsEnabled = false;
        try
        {
            StatusText.Text = "Stopping any previous ProtectMe service...";
            InstallProgress.Value = 15;
            // Ask running service to allow an in-place upgrade WITHOUT turning off protection
            try
            {
                using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", "ProtectMeWindowsServicePipe", System.IO.Pipes.PipeDirection.InOut);
                await pipe.ConnectAsync(1500);
                byte[] req = System.Text.Encoding.UTF8.GetBytes("{\"Action\":\"prepare_upgrade\"}\n");
                await pipe.WriteAsync(req);
                await pipe.FlushAsync();
                using var sr = new StreamReader(pipe);
                await sr.ReadLineAsync();
            }
            catch { }

            await RunCmdAsync("sc.exe", "failure ProtectMeService reset= 0 actions= \"\"");
            await RunCmdAsync("sc.exe", "stop ProtectMeService");
            await RunCmdAsync("taskkill.exe", "/F /IM ProtectMe.exe");
            await RunCmdAsync("taskkill.exe", "/F /IM ProtectMe.Service.exe");
            await RunCmdAsync("sc.exe", "delete ProtectMeService");
            await Task.Delay(1500);

            StatusText.Text = "Cleaning up old versions...";
            InstallProgress.Value = 40;
            Directory.CreateDirectory(InstallDir);

            // Clean up any leftover .old.* backups or stray root executables to save disk space
            try
            {
                foreach (string oldFile in Directory.EnumerateFiles(InstallDir, "*.old.*", SearchOption.AllDirectories))
                {
                    try { File.Delete(oldFile); } catch { }
                }
                foreach (string stray in new[] { "ProtectMe.exe", "ProtectMe.Service.exe" })
                {
                    string strayPath = Path.Combine(InstallDir, stray);
                    if (File.Exists(strayPath)) { try { File.Delete(strayPath); } catch { } }
                }
            }
            catch { }

            StatusText.Text = "Extracting ProtectMe to C:\\Program Files\\ProtectMe...";
            InstallProgress.Value = 45;

            using Stream? resStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ProtectMe-Payload.zip");
            if (resStream == null) throw new Exception("Embedded installer payload missing.");

            using (var archive = new ZipArchive(resStream, ZipArchiveMode.Read))
            {
                string stamp = DateTime.UtcNow.Ticks.ToString();
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    string destPath = Path.Combine(InstallDir, entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    if (File.Exists(destPath))
                    {
                        try
                        {
                            File.Delete(destPath);
                        }
                        catch
                        {
                            // Windows always allows renaming a running .exe so the new version can extract cleanly!
                            File.Move(destPath, destPath + ".old." + stamp, overwrite: true);
                        }
                    }
                    entry.ExtractToFile(destPath, overwrite: true);
                }
            }

            StatusText.Text = "Installing LocalSystem service & anti-tamper lock...";
            InstallProgress.Value = 75;
            string serviceExe = Path.Combine(InstallDir, "Service", "ProtectMe.Service.exe");
            await RunCmdAsync("sc.exe", $"create ProtectMeService binPath= \"\\\"{serviceExe}\\\"\" start= auto obj= LocalSystem DisplayName= \"ProtectMe Protection Service\"");
            await RunCmdAsync("sc.exe", "failure ProtectMeService reset= 0 actions= restart/1000/restart/1000/restart/1000");
            await RunCmdAsync("sc.exe", "failureflag ProtectMeService 1");
            await RunCmdAsync("sc.exe", "start ProtectMeService");
            await RunCmdAsync("sc.exe", "sdset ProtectMeService \"D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCLCSWLOCRRC;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)\"");

            StatusText.Text = "Creating Desktop shortcut...";
            InstallProgress.Value = 92;
            string appExe = Path.Combine(InstallDir, "App", "ProtectMe.exe");
            string desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "ProtectMe.lnk");
            await RunCmdAsync("powershell.exe", $"-NoProfile -Command \"$ws=New-Object -ComObject WScript.Shell; $s=$ws.CreateShortcut('{desktopLnk}'); $s.TargetPath='{appExe}'; $s.WorkingDirectory='{Path.GetDirectoryName(appExe)}'; $s.Save()\"");

            InstallProgress.Value = 100;
            StatusText.Text = "ProtectMe is installed and protected! Click below to launch.";
            ActionButton.Content = "Launch ProtectMe";
            ActionButton.IsEnabled = true;
            _installed = true;

            if (_autoMode)
            {
                if (File.Exists(appExe))
                {
                    Process.Start(new ProcessStartInfo { FileName = appExe, WorkingDirectory = Path.GetDirectoryName(appExe), UseShellExecute = true });
                }
                Close();
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Install error: " + ex.Message;
            ActionButton.IsEnabled = true;
        }
    }

    private static async Task RunCmdAsync(string file, string args)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                CreateNoWindow = true,
                UseShellExecute = false
            });
            if (p != null) await p.WaitForExitAsync();
        }
        catch { }
    }
}
