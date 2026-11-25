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

    public MainWindow()
    {
        InitializeComponent();
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

            StatusText.Text = "Extracting ProtectMe to C:\\Program Files\\ProtectMe...";
            InstallProgress.Value = 45;
            Directory.CreateDirectory(InstallDir);

            using Stream? resStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ProtectMe-Payload.zip");
            if (resStream == null) throw new Exception("Embedded installer payload missing.");

            using (var archive = new ZipArchive(resStream, ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    string destPath = Path.Combine(InstallDir, entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
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
