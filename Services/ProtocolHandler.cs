using Microsoft.Win32;
using System.IO;

namespace SaraBI.Services;

public static class ProtocolHandler
{
    public const string Scheme = "jadeone-desktop";
    public const string DisplayName = "JadeOne Desktop";
    private const string ClassesKey = @"Software\Classes\" + Scheme;
    private const string LegacyScheme = "sarabi";

    public static void EnsureRegistered()
    {
        try
        {
            TryRemoveLegacyProtocol();

            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            {
                return;
            }

            using var key = Registry.CurrentUser.CreateSubKey(ClassesKey);
            if (key is null)
            {
                return;
            }

            key.SetValue(null, "URL:JadeOne Desktop Protocol");
            key.SetValue("URL Protocol", "");

            using var icon = key.CreateSubKey("DefaultIcon");
            icon?.SetValue(null, $"\"{exe}\",0");

            using var command = key.CreateSubKey(@"shell\open\command");
            command?.SetValue(null, $"\"{exe}\" \"%1\"");
        }
        catch
        {
            // Sin permisos de registro: el usuario puede ejecutar install.ps1.
        }
    }

    private static void TryRemoveLegacyProtocol()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + LegacyScheme, throwOnMissingSubKey: false);
        }
        catch
        {
            // Ignorar: el protocolo antiguo puede no existir.
        }
    }

    public static bool TryParse(string? protocolUrl, out string ticket, out string env)
    {
        ticket = "";
        env = "";
        if (string.IsNullOrWhiteSpace(protocolUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(protocolUrl.Trim(), UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var query = uri.Query.TrimStart('?');
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2)
            {
                continue;
            }

            var name = Uri.UnescapeDataString(kv[0]);
            var value = Uri.UnescapeDataString(kv[1]);
            if (string.Equals(name, "ticket", StringComparison.OrdinalIgnoreCase))
            {
                ticket = value;
            }
            else if (string.Equals(name, "env", StringComparison.OrdinalIgnoreCase))
            {
                env = value.Trim();
            }
            else if (string.Equals(name, "api", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Warn("Parámetro api= del protocolo ignorado");
            }
        }

        if (ticket.Length != 32 || !ticket.All(static c => char.IsAsciiHexDigit(c)))
        {
            return false;
        }

        if (env.Length > 0
            && !string.Equals(env, "prod", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(env, "local", StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Warn($"env={env} no reconocido; se usa prod");
            env = "prod";
        }

        return true;
    }
}
