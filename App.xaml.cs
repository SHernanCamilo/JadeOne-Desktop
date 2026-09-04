using System.Windows;
using System.Windows.Threading;
using SaraBI.Services;

namespace SaraBI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                AppLog.Error("Excepción no controlada", ex);
            }
        };

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

        try
        {
            var window = new MainWindow(protocolUrl);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            AppLog.Error("No se pudo abrir la ventana principal", ex);
            MessageBox.Show(
                "JadeOne Desktop no pudo iniciar.\n\n" + ex.Message,
                "JadeOne Desktop",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Excepción en la interfaz", e.Exception);
        MessageBox.Show(
            e.Exception.Message,
            "JadeOne Desktop",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
