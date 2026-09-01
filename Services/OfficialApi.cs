using System.IO;
using System.Net;
using System.Text.Json;

namespace SaraBI.Services;

/// <summary>
/// El protocolo no elige el host. Solo ticket + env=prod|local.
/// </summary>
public static class OfficialApi
{
    public const string Production = "https://jade-api.medilaser.com.co/api";
    public const string Local = "http://127.0.0.1:8000/api";

    private static readonly string OverridePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JadeOneDesktop",
        "api.json");

    public static string Resolve(string? env)
    {
        if (IsLocalEnv(env))
        {
            if (TryReadOverride(out var over) && IsPrivateOrBuiltIn(over))
            {
                return Normalize(over);
            }

            return Local;
        }

        return Production;
    }

    public static bool IsTrustedBase(string? url)
    {
        if (!TryNormalize(url, out var n))
        {
            return false;
        }

        return IsBuiltIn(n) || IsApprovedOverride(n);
    }

    public static string BindSessionUrl(string claimedAgainst, string? serverSaid)
    {
        var fallback = Normalize(claimedAgainst);
        if (!TryNormalize(serverSaid, out var said))
        {
            return fallback;
        }

        if (string.Equals(said, fallback, StringComparison.OrdinalIgnoreCase) || IsBuiltIn(said))
        {
            return said;
        }

        AppLog.Warn($"api_url del claim rechazada host={said}");
        return fallback;
    }

    public static string Normalize(string url)
    {
        TryNormalize(url, out var n);
        return n;
    }

    private static bool IsLocalEnv(string? env) =>
        string.Equals(env?.Trim(), "local", StringComparison.OrdinalIgnoreCase);

    private static bool IsBuiltIn(string normalized)
    {
        return string.Equals(normalized, Production, StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, Local, StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, "http://localhost:8000/api", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsApprovedOverride(string normalized) =>
        TryReadOverride(out var over)
        && TryNormalize(over, out var n)
        && string.Equals(n, normalized, StringComparison.OrdinalIgnoreCase)
        && IsPrivateOrBuiltIn(n);

    private static bool IsPrivateOrBuiltIn(string url)
    {
        if (!TryNormalize(url, out var n))
        {
            return false;
        }

        if (IsBuiltIn(n))
        {
            return true;
        }

        if (!Uri.TryCreate(n, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        var host = uri.IdnHost;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(host, out var ip))
        {
            return false;
        }

        return IPAddress.IsLoopback(ip) || IsPrivateIPv4(ip);
    }

    private static bool IsPrivateIPv4(IPAddress ip)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        var b = ip.GetAddressBytes();
        return b[0] == 10
               || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
               || (b[0] == 192 && b[1] == 168);
    }

    private static bool TryReadOverride(out string url)
    {
        url = "";
        try
        {
            if (!File.Exists(OverridePath))
            {
                return false;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(OverridePath));
            if (!doc.RootElement.TryGetProperty("apiUrl", out var prop))
            {
                return false;
            }

            var raw = prop.GetString();
            if (string.IsNullOrWhiteSpace(raw) || !TryNormalize(raw, out var n))
            {
                return false;
            }

            url = n;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"No se pudo leer api.json local: {ex.Message}");
            return false;
        }
    }

    private static bool TryNormalize(string? raw, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var s = raw.Trim().TrimEnd('/');
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        var builder = new UriBuilder(uri)
        {
            Host = uri.IdnHost.ToLowerInvariant(),
            Fragment = "",
            Query = "",
            UserName = "",
            Password = "",
        };
        normalized = builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }
}
