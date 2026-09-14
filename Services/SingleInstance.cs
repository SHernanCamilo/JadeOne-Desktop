using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace SaraBI.Services;

/// <summary>
/// Garantiza una sola instancia de JadeOne Desktop. Si ya hay una corriendo,
/// una segunda invocación (p. ej. otro clic en "abrir desktop" desde la web)
/// reenvía su URL de protocolo a la instancia existente por un named pipe, en
/// vez de abrir otra ventana. La instancia primaria decide qué hacer con la URL.
/// </summary>
public static class SingleInstance
{
    private const string MutexName = "JadeOneDesktop.SingleInstance.Mutex";
    private const string PipeName = "JadeOneDesktop.SingleInstance.Pipe";

    private static Mutex? _mutex;

    /// <summary>
    /// Intenta tomar la titularidad de instancia primaria.
    /// Devuelve true si esta es la primaria; false si ya había otra.
    /// </summary>
    public static bool TryBecomePrimary()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        return createdNew;
    }

    /// <summary>
    /// Envía la URL de protocolo a la instancia primaria ya en ejecución.
    /// Devuelve true si se pudo entregar.
    /// </summary>
    public static bool SendToPrimary(string? protocolUrl)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            var bytes = Encoding.UTF8.GetBytes(protocolUrl ?? "");
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"No se pudo contactar la instancia primaria: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Arranca el servidor de pipe en la instancia primaria. Cada vez que llega
    /// una URL, invoca <paramref name="onUrlReceived"/> en un hilo de fondo
    /// (el llamador debe marshalizar al hilo UI).
    /// </summary>
    public static void StartServer(Action<string> onUrlReceived)
    {
        var thread = new Thread(() => ServerLoop(onUrlReceived))
        {
            IsBackground = true,
            Name = "SingleInstancePipe",
        };
        thread.Start();
    }

    private static void ServerLoop(Action<string> onUrlReceived)
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.None);
                server.WaitForConnection();

                using var reader = new StreamReader(server, Encoding.UTF8);
                var url = reader.ReadToEnd();
                onUrlReceived(url ?? "");
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Pipe de instancia única falló: {ex.Message}");
                Thread.Sleep(500);
            }
        }
    }

    public static void Release()
    {
        try
        {
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }
        catch
        {
            // best effort
        }
    }
}
