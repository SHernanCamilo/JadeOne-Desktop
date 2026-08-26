using System.IO;

namespace SaraBI.Services;

public static class AppLog
{
    private static readonly object Gate = new();
    private static readonly string FilePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JadeOneDesktop",
        "jadeone-desktop.log");

    public static string LogFile => FilePath;

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}";
            lock (Gate)
            {
                File.AppendAllText(FilePath, line);
            }
        }
        catch
        {
            // No interrumpir la carga por un fallo de log.
        }
    }
}
