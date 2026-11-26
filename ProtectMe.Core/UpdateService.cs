using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace ProtectMe.Core;

public static class UpdateService
{
    public const string CurrentVersion = "1.0.4";
    private const string LatestReleaseApi = "https://api.github.com/repos/tboneapps/ProtectMeWindows/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ProtectMeWindows/1.0");
        return client;
    }

    public static async Task<(bool Available, string LatestVersion, string DownloadUrl)> CheckForUpdateAsync()
    {
        try
        {
            string json = await Http.GetStringAsync(LatestReleaseApi);
            using var doc = JsonDocument.Parse(json);

            string tag = doc.RootElement.TryGetProperty("tag_name", out var tagEl)
                ? (tagEl.GetString() ?? string.Empty).TrimStart('v', 'V')
                : string.Empty;

            if (string.IsNullOrWhiteSpace(tag)) return (false, CurrentVersion, string.Empty);

            if (Version.TryParse(tag, out var latest) && Version.TryParse(CurrentVersion, out var current))
            {
                if (latest <= current) return (false, CurrentVersion, string.Empty);
            }
            else if (tag.Equals(CurrentVersion, StringComparison.OrdinalIgnoreCase))
            {
                return (false, CurrentVersion, string.Empty);
            }

            string downloadUrl = string.Empty;
            if (doc.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string name = asset.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? "") : "";
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = asset.TryGetProperty("browser_download_url", out var uEl) ? (uEl.GetString() ?? "") : "";
                        break;
                    }
                }
            }

            return (!string.IsNullOrWhiteSpace(downloadUrl), tag, downloadUrl);
        }
        catch
        {
            return (false, CurrentVersion, string.Empty);
        }
    }

    public static async Task<bool> DownloadAndLaunchInstallerAsync(
        string downloadUrl,
        IProgress<(long Downloaded, long Total)>? progress = null)
    {
        try
        {
            string tempExe = Path.Combine(Path.GetTempPath(), "ProtectMe-Setup-Update.exe");
            using var resp = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();

            long total = resp.Content.Headers.ContentLength ?? -1;
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using (var fs = new FileStream(tempExe, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[81920];
                long downloaded = 0;
                int read;
                while ((read = await src.ReadAsync(buffer)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read));
                    downloaded += read;
                    progress?.Report((downloaded, total));
                }
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = tempExe,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
