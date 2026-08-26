using System.Windows;
using SaraBI.Services;

namespace SaraBI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ProtocolHandler.EnsureRegistered();
        AppLog.Info($"Inicio args={string.Join(' ', e.Args ?? Array.Empty<string>())}");

        var args = e.Args ?? Array.Empty<string>();
        if (args.Any(a => string.Equals(a, "--register-protocol", StringComparison.OrdinalIgnoreCase)))
        {
            Shutdown();
            return;
        }

        var protocolUrl = args.FirstOrDefault(a =>
            a.StartsWith(ProtocolHandler.Scheme + ":", StringComparison.OrdinalIgnoreCase));

        var window = new MainWindow(protocolUrl);
        MainWindow = window;
        window.Show();
    }
}
