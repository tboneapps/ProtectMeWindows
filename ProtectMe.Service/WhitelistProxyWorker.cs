using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ProtectMe.Core;

namespace ProtectMe.Service;

public sealed class WhitelistProxyWorker : BackgroundService
{
    private readonly ProtectionStateManager _state;
    private readonly ILogger<WhitelistProxyWorker> _logger;

    public WhitelistProxyWorker(ProtectionStateManager state, ILogger<WhitelistProxyWorker> logger)
    {
        _state = state;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int port = _state.Current.ProxyPort;
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _logger.LogInformation("ProtectMe Whitelist Proxy listening on 127.0.0.1:{Port}", port);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                _ = Task.Run(() => HandleClientAsync(client, stoppingToken), stoppingToken);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var headerBytes = await ReadHttpHeadersAsync(stream, ct);
                if (headerBytes.Length == 0) return;

                string headerText = Encoding.ASCII.GetString(headerBytes);
                string[] lines = headerText.Split("\r\n", StringSplitOptions.None);
                if (lines.Length == 0) return;

                string[] requestLine = lines[0].Split(' ');
                if (requestLine.Length < 3) return;

                string method = requestLine[0].ToUpperInvariant();
                string target = requestLine[1];

                // Handle local ProtectMe Block Page API (Allow Whole Website / Allow Webpage)
                if (method == "POST" && target.StartsWith("/api/whitelist", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleWhitelistApiAsync(stream, lines, ct);
                    return;
                }

                var cfg = _state.Current;

                if (method == "CONNECT")
                {
                    string[] hostPort = target.Split(':');
                    string host = hostPort[0];
                    int port = hostPort.Length > 1 && int.TryParse(hostPort[1], out int p) ? p : 443;

                    bool isWhitelisted = SecurityHelper.IsUrlWhitelisted(string.Empty, host, cfg.WebsiteWhitelist);

                    if (cfg.IsProtectionLocked && !isWhitelisted)
                    {
                        if (cfg.WhitelistOnlyWebsites ||
                            (cfg.SafeBrowsingEnabled && IsUnsafeOrAdultHost(host)) ||
                            (cfg.BlockImageVideoSearch && IsImageOrVideoSearchHost(host)))
                        {
                            string domain = SecurityHelper.NormalizeDomain(host);
                            _state.RecordBlockedEvent("Website", $"https://{host}", domain);
                            await SendBlockedHtmlResponseAsync(stream, $"https://{host}", domain, cfg.IsProtectionLocked, ct);
                            return;
                        }
                    }

                    using var remote = new TcpClient();
                    await remote.ConnectAsync(host, port, ct);
                    byte[] ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
                    await stream.WriteAsync(ok, ct);

                    var remoteStream = remote.GetStream();
                    await Task.WhenAny(
                        stream.CopyToAsync(remoteStream, ct),
                        remoteStream.CopyToAsync(stream, ct));
                }
                else
                {
                    string host = ExtractHostHeader(lines);
                    if (string.IsNullOrEmpty(host) && Uri.TryCreate(target, UriKind.Absolute, out var parsedUri))
                    {
                        host = parsedUri.Host;
                    }

                    bool isWhitelisted = SecurityHelper.IsUrlWhitelisted(target, host, cfg.WebsiteWhitelist);

                    // Whitelisted websites are NEVER affected by image/video/download/search limitations
                    if (cfg.IsProtectionLocked && !isWhitelisted)
                    {
                        if (cfg.WhitelistOnlyWebsites)
                        {
                            string domain = SecurityHelper.NormalizeDomain(host);
                            _state.RecordBlockedEvent("Website", target, domain);
                            await SendBlockedHtmlResponseAsync(stream, target, domain, cfg.IsProtectionLocked, ct);
                            return;
                        }

                        if (cfg.SafeBrowsingEnabled && IsUnsafeOrAdultHost(host))
                        {
                            string domain = SecurityHelper.NormalizeDomain(host);
                            _state.RecordBlockedEvent("Website", target, domain);
                            await SendBlockedHtmlResponseAsync(stream, target, domain, cfg.IsProtectionLocked, ct);
                            return;
                        }

                        if (cfg.BlockImageVideoSearch && IsImageOrVideoSearchUrl(host, target))
                        {
                            string domain = SecurityHelper.NormalizeDomain(host);
                            _state.RecordBlockedEvent("Website", target, domain);
                            await SendBlockedHtmlResponseAsync(stream, target, domain, cfg.IsProtectionLocked, ct);
                            return;
                        }

                        if (IsImageUrl(target))
                        {
                            // Render images as solid black (#000000)
                            await SendSolidBlackPngAsync(stream, ct);
                            return;
                        }

                        if (IsVideoUrl(target) || IsDownloadFileUrl(target))
                        {
                            // Videos cannot be played & files cannot be downloaded
                            await WriteJsonResponseAsync(stream, 403, "{\"blocked\":true,\"reason\":\"Media playback and file downloads are blocked by ProtectMe.\"}", ct);
                            return;
                        }
                    }

                    int port = 80;
                    string connectHost = host;
                    if (host.Contains(':'))
                    {
                        var hp = host.Split(':');
                        connectHost = hp[0];
                        if (int.TryParse(hp[1], out int p)) port = p;
                    }

                    using var remote = new TcpClient();
                    await remote.ConnectAsync(connectHost, port, ct);
                    var remoteStream = remote.GetStream();
                    await remoteStream.WriteAsync(headerBytes, ct);

                    await Task.WhenAny(
                        stream.CopyToAsync(remoteStream, ct),
                        remoteStream.CopyToAsync(stream, ct));
                }
            }
            catch
            {
                // Connection reset / client closed
            }
        }
    }

    private async Task HandleWhitelistApiAsync(NetworkStream stream, string[] headerLines, CancellationToken ct)
    {
        int contentLength = 0;
        foreach (var line in headerLines)
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(line["Content-Length:".Length..].Trim(), out contentLength);
            }
        }

        byte[] bodyBytes = new byte[Math.Min(contentLength, 16384)];
        int read = 0;
        while (read < bodyBytes.Length)
        {
            int n = await stream.ReadAsync(bodyBytes.AsMemory(read, bodyBytes.Length - read), ct);
            if (n <= 0) break;
            read += n;
        }

        string body = Encoding.UTF8.GetString(bodyBytes, 0, read);
        try
        {
            using var doc = JsonDocument.Parse(body);
            string value = doc.RootElement.GetProperty("value").GetString() ?? "";
            string scopeStr = doc.RootElement.GetProperty("scope").GetString() ?? "WholeWebsite";
            string password = doc.RootElement.TryGetProperty("password", out var pw) ? (pw.GetString() ?? "") : "";

            if (_state.Current.IsProtectionLocked && !_state.VerifyPassword(password))
            {
                await WriteJsonResponseAsync(stream, 403, "{\"ok\":false,\"error\":\"Incorrect master password.\"}", ct);
                return;
            }

            var scope = scopeStr.Equals("SpecificWebpage", StringComparison.OrdinalIgnoreCase)
                ? WhitelistScope.SpecificWebpage
                : WhitelistScope.WholeWebsite;

            string normalized = scope == WhitelistScope.WholeWebsite
                ? SecurityHelper.NormalizeDomain(value)
                : SecurityHelper.NormalizeWebpageUrl(value);

            _state.Update(cfg =>
            {
                if (!cfg.WebsiteWhitelist.Any(e => e.Scope == scope && e.Value.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    cfg.WebsiteWhitelist.Add(new WhitelistEntry
                    {
                        Value = normalized,
                        Scope = scope,
                        AddedUtc = DateTime.UtcNow
                    });
                }
            });

            await WriteJsonResponseAsync(stream, 200, "{\"ok\":true}", ct);
        }
        catch
        {
            await WriteJsonResponseAsync(stream, 400, "{\"ok\":false,\"error\":\"Invalid request.\"}", ct);
        }
    }

    private static async Task WriteJsonResponseAsync(NetworkStream stream, int statusCode, string json, CancellationToken ct)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        string status = statusCode == 200 ? "200 OK" : statusCode == 403 ? "403 Forbidden" : "400 Bad Request";
        string resp = $"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(resp), ct);
        await stream.WriteAsync(body, ct);
    }

    private static async Task SendBlockedHtmlResponseAsync(NetworkStream stream, string url, string domain, bool requiresPassword, CancellationToken ct)
    {
        string safeUrl = WebUtility.HtmlEncode(url);
        string safeDomain = WebUtility.HtmlEncode(domain);
        string jsUrl = JsonSerializer.Serialize(url);
        string jsDomain = JsonSerializer.Serialize(domain);

        string html = $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8""/>
<meta name=""viewport"" content=""width=device-width, initial-scale=1""/>
<title>ProtectMe — Site Blocked</title>
<style>
  body {{ margin:0; background:#0A0A0A; color:#F3E7C4; font-family:'Segoe UI',sans-serif; display:flex; align-items:center; justify-content:center; min-height:100vh; }}
  .card {{ background:linear-gradient(180deg,#181818,#101010); border:1px solid #25221D; border-radius:20px; padding:36px; max-width:520px; width:90%; text-align:center; box-shadow:0 20px 60px rgba(0,0,0,0.8); }}
  .title {{ color:#D4AF37; letter-spacing:0.22em; font-size:26px; font-weight:700; margin-bottom:8px; }}
  .sub {{ color:#8C8472; font-size:14px; margin-bottom:24px; word-break:break-all; }}
  input {{ width:100%; box-sizing:border-box; padding:14px; border-radius:12px; border:1px solid #25221D; background:#0A0A0A; color:#F3E7C4; font-size:15px; margin-bottom:16px; }}
  .btn {{ width:100%; padding:14px; border-radius:12px; border:none; font-size:15px; font-weight:700; cursor:pointer; margin-bottom:10px; }}
  .btn-gold {{ background:linear-gradient(180deg,#F0CE63,#C99F2B); color:#0A0A0A; }}
  .btn-outline {{ background:transparent; border:1px solid #D4AF37; color:#F3E7C4; }}
  .msg {{ color:#E06A5A; font-size:13px; margin-top:8px; min-height:20px; }}
</style>
</head>
<body>
  <div class=""card"">
    <div class=""title"">PROTECTME</div>
    <div class=""sub"">This website is blocked by Whitelist-Only Protection.<br/><strong>{safeDomain}</strong><br/><small>{safeUrl}</small></div>
    {(requiresPassword ? @"<input id=""pw"" type=""password"" placeholder=""Enter Master Password to Whitelist"" />" : "")}
    <button class=""btn btn-gold"" onclick=""allowSite('WholeWebsite')"">Allow Whole Website ({safeDomain})</button>
    <button class=""btn btn-outline"" onclick=""allowSite('SpecificWebpage')"">Allow This Webpage Only</button>
    <div id=""msg"" class=""msg""></div>
  </div>
<script>
async function allowSite(scope) {{
  const pwEl = document.getElementById('pw');
  const password = pwEl ? pwEl.value : '';
  const value = scope === 'WholeWebsite' ? {jsDomain} : {jsUrl};
  const res = await fetch('/api/whitelist', {{
    method: 'POST',
    headers: {{ 'Content-Type': 'application/json' }},
    body: JSON.stringify({{ value, scope, password }})
  }});
  const data = await res.json();
  if (data.ok) {{
    document.getElementById('msg').style.color = '#D4AF37';
    document.getElementById('msg').textContent = 'Saved to ProtectMe whitelist! Reloading...';
    setTimeout(() => location.reload(), 600);
  }} else {{
    document.getElementById('msg').textContent = data.error || 'Could not whitelist.';
  }}
}}
</script>
</body>
</html>";

        byte[] body = Encoding.UTF8.GetBytes(html);
        string header = $"HTTP/1.1 403 Forbidden\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
        await stream.WriteAsync(body, ct);
    }

    private static readonly byte[] SolidBlack1x1Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private static async Task SendSolidBlackPngAsync(NetworkStream stream, CancellationToken ct)
    {
        string header = $"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {SolidBlack1x1Png.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
        await stream.WriteAsync(SolidBlack1x1Png, ct);
    }

    private static bool IsUnsafeOrAdultHost(string host)
    {
        string h = host.ToLowerInvariant();
        string[] keywords =
        {
            "porn", "xxx", "xvideos", "xnxx", "xhamster", "redtube", "youporn", "tube8",
            "spankbang", "brazzers", "onlyfans", "chaturbate", "stripchat", "bongacams",
            "cam4", "livejasmin", "myfreecams", "eporner", "hqporner", "tnaflix", "hentai",
            "rule34", "nhentai", "e-hentai", "pornhub", "xv-videos", "sex.com", "adult"
        };
        return keywords.Any(k => h.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsImageOrVideoSearchHost(string host)
    {
        string h = host.ToLowerInvariant();
        return h.StartsWith("images.") || h.StartsWith("image.") || h.StartsWith("img.") ||
               h.StartsWith("videos.") || h.StartsWith("video.") ||
               h.Contains("images.search.yahoo") || h.Contains("video.search.yahoo") ||
               h.Contains("image.baidu") || h.Contains("v.baidu") ||
               // HTTPS image & video search thumbnail/media CDNs (Google, Bing, DuckDuckGo, YouTube, GIFs)
               h.Contains("gstatic.com") && h.Contains("tbn") ||
               h.Contains("mm.bing.net") || h.Equals("th.bing.com") ||
               h.Contains("ytimg.com") || h.Contains("googlevideo.com") ||
               h.Contains("vimeocdn.com") || h.Contains("giphy.com") || h.Contains("tenor.com") ||
               h.Contains("external-content.duckduckgo.com");
    }

    private static bool IsImageOrVideoSearchUrl(string host, string rawUrl)
    {
        if (IsImageOrVideoSearchHost(host)) return true;
        string u = Uri.UnescapeDataString(rawUrl).ToLowerInvariant();

        // Multi-engine & multi-language image/video search query & path detection
        string[] patterns =
        {
            "tbm=isch", "tbm=vid", "udm=2", "udm=7", "/imghp", "/imgres", "/videohp",
            "iax=images", "iax=videos", "ia=images", "ia=videos",
            "t=images", "t=videos", "cat=images", "cat=videos",
            "/images/search", "/videos/search", "/images?", "/videos?", "/video/search",
            "/imagenes", "/fotos", "/bilder", "/vidéos", "/immagini", "/afbeeldingen",
            "/obrazy", "/صور", "/فيديو", "/画像", "/動画", "/이미지", "/동영상"
        };

        return patterns.Any(p => u.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsImageUrl(string url)
    {
        string path = url.Split('?')[0].ToLowerInvariant();
        return path.EndsWith(".jpg") || path.EndsWith(".jpeg") || path.EndsWith(".png") ||
               path.EndsWith(".gif") || path.EndsWith(".webp") || path.EndsWith(".bmp") ||
               path.EndsWith(".svg") || path.EndsWith(".avif");
    }

    private static bool IsVideoUrl(string url)
    {
        string path = url.Split('?')[0].ToLowerInvariant();
        return path.EndsWith(".mp4") || path.EndsWith(".webm") || path.EndsWith(".mkv") ||
               path.EndsWith(".mov") || path.EndsWith(".avi") || path.EndsWith(".m3u8") ||
               path.EndsWith(".mpd") || path.EndsWith(".ts") || path.EndsWith(".flv") || path.EndsWith(".m4v");
    }

    private static bool IsDownloadFileUrl(string url)
    {
        string path = url.Split('?')[0].ToLowerInvariant();
        string[] exts =
        {
            ".exe", ".msi", ".zip", ".rar", ".7z", ".tar", ".gz", ".iso", ".dmg", ".apk",
            ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
            ".torrent", ".bat", ".ps1", ".cmd", ".vbs", ".jar", ".dll"
        };
        return exts.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    private static string ExtractHostHeader(string[] lines)
    {
        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
            {
                return line["Host:".Length..].Trim();
            }
        }
        return string.Empty;
    }

    private static async Task<byte[]> ReadHttpHeadersAsync(NetworkStream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        byte[] buffer = new byte[1];
        int match = 0;
        byte[] target = { (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n' };

        while (ms.Length < 16384)
        {
            int n = await stream.ReadAsync(buffer, ct);
            if (n <= 0) break;
            ms.WriteByte(buffer[0]);
            if (buffer[0] == target[match])
            {
                match++;
                if (match == 4) break;
            }
            else
            {
                match = buffer[0] == target[0] ? 1 : 0;
            }
        }

        return ms.ToArray();
    }
}
