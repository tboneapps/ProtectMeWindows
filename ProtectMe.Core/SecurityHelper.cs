using System.Security.Cryptography;

namespace ProtectMe.Core;

public static class SecurityHelper
{
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const int Iterations = 100_000;

    public static (string Hash, string Salt) HashPassword(string password)
    {
        byte[] saltBytes = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hashBytes = Rfc2898DeriveBytes.Pbkdf2(
            password,
            saltBytes,
            Iterations,
            HashAlgorithmName.SHA256,
            KeySize);

        return (Convert.ToBase64String(hashBytes), Convert.ToBase64String(saltBytes));
    }

    public static bool VerifyPassword(string password, string storedHash, string storedSalt)
    {
        if (string.IsNullOrWhiteSpace(password) ||
            string.IsNullOrWhiteSpace(storedHash) ||
            string.IsNullOrWhiteSpace(storedSalt))
        {
            return false;
        }

        try
        {
            byte[] saltBytes = Convert.FromBase64String(storedSalt);
            byte[] expectedHash = Convert.FromBase64String(storedHash);
            byte[] actualHash = Rfc2898DeriveBytes.Pbkdf2(
                password,
                saltBytes,
                Iterations,
                HashAlgorithmName.SHA256,
                KeySize);

            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        catch
        {
            return false;
        }
    }

    public static string NormalizeDomain(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        string trimmed = input.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "https://" + trimmed;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            string host = uri.Host.ToLowerInvariant();
            if (host.StartsWith("www.", StringComparison.Ordinal))
                host = host[4..];
            return host;
        }

        string fallback = input.Trim().ToLowerInvariant();
        if (fallback.StartsWith("www.", StringComparison.Ordinal))
            fallback = fallback[4..];
        int slash = fallback.IndexOf('/');
        if (slash >= 0) fallback = fallback[..slash];
        int colon = fallback.IndexOf(':');
        if (colon >= 0) fallback = fallback[..colon];
        return fallback;
    }

    public static string NormalizeWebpageUrl(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        string trimmed = input.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "https://" + trimmed;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            string host = uri.Host.ToLowerInvariant();
            if (host.StartsWith("www.", StringComparison.Ordinal))
                host = host[4..];
            string path = uri.AbsolutePath.TrimEnd('/');
            if (string.IsNullOrEmpty(path)) path = "/";
            return $"{host}{path}{uri.Query}";
        }

        return trimmed.ToLowerInvariant();
    }

    public static bool IsUrlWhitelisted(string rawUrl, string host, IEnumerable<WhitelistEntry> whitelist)
    {
        string normalizedHost = NormalizeDomain(host);
        if (string.IsNullOrEmpty(normalizedHost)) return false;

        // Always allow localhost / loopback so the block page and local API work
        if (normalizedHost is "localhost" or "127.0.0.1" or "::1")
            return true;

        string normalizedPage = NormalizeWebpageUrl(string.IsNullOrWhiteSpace(rawUrl) ? host : rawUrl);

        foreach (var entry in whitelist)
        {
            if (entry.Scope == WhitelistScope.WholeWebsite)
            {
                string allowedDomain = NormalizeDomain(entry.Value);
                if (string.IsNullOrEmpty(allowedDomain)) continue;
                if (normalizedHost.Equals(allowedDomain, StringComparison.OrdinalIgnoreCase) ||
                    normalizedHost.EndsWith("." + allowedDomain, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else
            {
                string allowedPage = NormalizeWebpageUrl(entry.Value);
                if (string.IsNullOrEmpty(allowedPage)) continue;

                // For HTTPS CONNECT tunnels we only see host:443 unless MITM is used,
                // so if a specific webpage on that host is whitelisted, allow the host tunnel
                // and exact-match on HTTP requests.
                if (string.IsNullOrWhiteSpace(rawUrl) || !rawUrl.Contains('/'))
                {
                    string allowedDomain = NormalizeDomain(entry.Value);
                    if (normalizedHost.Equals(allowedDomain, StringComparison.OrdinalIgnoreCase) ||
                        normalizedHost.EndsWith("." + allowedDomain, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                else if (normalizedPage.Equals(allowedPage, StringComparison.OrdinalIgnoreCase) ||
                         normalizedPage.StartsWith(allowedPage + "/", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
