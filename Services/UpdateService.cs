using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SaraBI.Services;

/// <summary>
/// El backend rechazó el claim porque la app está desactualizada y exige
/// actualizar antes de continuar. Lleva la URL de descarga y la versión mínima.
/// </summary>
public sealed class UpdateRequiredException : Exception
{
    public UpdateRequiredException(string message, string? downloadUrl, string? minVersion)
        : base(message)
    {
        DownloadUrl = downloadUrl;
        MinVersion = minVersion;
    }

    public string? DownloadUrl { get; }
    public string? MinVersion { get; }
}

/// <summary>
/// Auto-actualización de JadeOne Desktop.
///
/// Un .exe self-contained single-file no puede sobrescribirse mientras corre.
/// Patrón: se descarga la versión nueva a un archivo temporal y se lanza un
/// pequeño script .cmd que espera a que el proceso actual cierre, reemplaza el
/// .exe y relanza la app. Sin dependencias externas.
/// </summary>
public static class UpdateService
{
    public sealed class VersionInfo
    {
        public bool Success { get; set; }
        public bool Available { get; set; }
        public string? Version { get; set; }

        [JsonPropertyName("download_url")]
        public string? DownloadUrl { get; set; }

        public long Size { get; set; }
    }

    /// <summary>Versión de esta app (del assembly, definida en el .csproj).</summary>
    public static Version Current =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    /// <summary>
    /// Consulta la última versión publicada. Devuelve null si no hay red,
    /// el endpoint no responde o no hay versión declarada.
    /// </summary>
    public static async Task<VersionInfo?> CheckAsync(string apiBaseUrl, HttpClient http, CancellationToken ct)
    {
        try
        {
            var url = apiBaseUrl.TrimEnd('/') + "/fabric/viewer/desktop/version";
            using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<VersionInfo>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Chequeo de actualización falló: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// True si la versión del servidor es mayor que la actual.
    /// </summary>
    public static bool IsNewer(string? serverVersion)
    {
        if (string.IsNullOrWhiteSpace(serverVersion)
            || !Version.TryParse(serverVersion.Trim(), out var remote))
        {
            return false;
        }

        // Comparar por Major.Minor.Build (ignora Revision indefinida = -1).
        var current = Current;
        var a = new Version(Math.Max(0, current.Major), Math.Max(0, current.Minor), Math.Max(0, current.Build));
        var b = new Version(Math.Max(0, remote.Major), Math.Max(0, remote.Minor), Math.Max(0, remote.Build));
        return b > a;
    }

    /// <summary>
    /// Descarga la versión nueva a un temporal y lanza el updater que hará el
    /// swap y relanzará la app. Devuelve true si arrancó el updater (el llamador
    /// debe cerrar la app inmediatamente después).
    /// </summary>
    public static async Task<bool> DownloadAndApplyAsync(
        string downloadUrl,
        HttpClient http,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
        {
            AppLog.Warn("No se pudo determinar la ruta del ejecutable actual.");
            return false;
        }

        var dir = Path.GetDirectoryName(currentExe)!;
        var newExe = Path.Combine(dir, "JadeOneDesktop.new.exe");

        // ── Descargar a temporal con progreso ────────────────────────────────
        using (var response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                   .ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;

            await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = new FileStream(newExe, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);

            var buffer = new byte[128 * 1024];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                read += n;
                if (total > 0)
                {
                    progress?.Report((double)read / total);
                }
            }
        }

        // Guarda: no aplicar un archivo vacío/corrupto.
        if (new FileInfo(newExe).Length < 1024)
        {
            AppLog.Warn("La actualización descargada es demasiado pequeña; se descarta.");
            TryDelete(newExe);
            return false;
        }

        LaunchUpdater(currentExe, newExe);
        return true;
    }

    /// <summary>
    /// Crea y lanza un .cmd que espera a que el proceso cierre, reemplaza el
    /// .exe por el nuevo y relanza la app. El script se autoelimina al final.
    /// </summary>
    private static void LaunchUpdater(string currentExe, string newExe)
    {
        var pid = Environment.ProcessId;
        var scriptPath = Path.Combine(Path.GetTempPath(), $"jadeone_update_{Guid.NewGuid():N}.cmd");

        // El script:
        //  1. Espera a que el PID actual termine (timeout de guarda).
        //  2. Reemplaza el exe viejo por el nuevo (con reintentos por si el
        //     archivo sigue bloqueado unos ms).
        //  3. Relanza la app.
        //  4. Se borra a sí mismo.
        var script = $@"@echo off
setlocal
set ""OLD={currentExe}""
set ""NEW={newExe}""

rem Esperar a que el proceso actual (PID {pid}) cierre
for /L %%i in (1,1,50) do (
    tasklist /FI ""PID eq {pid}"" 2>nul | find ""{pid}"" >nul
    if errorlevel 1 goto :swap
    timeout /t 1 /nobreak >nul
)

:swap
rem Reemplazar el exe (reintentos por bloqueo residual)
for /L %%i in (1,1,20) do (
    del ""%OLD%"" >nul 2>&1
    if not exist ""%OLD%"" goto :move
    timeout /t 1 /nobreak >nul
)

:move
move /Y ""%NEW%"" ""%OLD%"" >nul 2>&1

rem Relanzar la app actualizada
start """" ""%OLD%""

rem Autolimpieza
del ""%~f0"" >nul 2>&1
";

        File.WriteAllText(scriptPath, script);
        AppLog.Info($"Updater lanzado: {scriptPath}");

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{scriptPath}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best effort
        }
    }
}
