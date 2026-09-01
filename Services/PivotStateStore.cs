using System.IO;
using System.Text.Json;
using SaraBI.Models;

namespace SaraBI.Services;

public static class PivotStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static string RootDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JadeOneDesktop",
        "pivots");

    public static bool Exists(LaunchSession session) => File.Exists(PathFor(session));

    public static SavedPivotState? Load(LaunchSession session)
    {
        var path = PathFor(session);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<SavedPivotState>(json, JsonOptions);
            if (state?.PivotSheets is null || state.PivotSheets.Count == 0)
            {
                return null;
            }

            return state;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"No se pudo leer la tabla dinámica local: {ex.Message}");
            return null;
        }
    }

    public static void Save(LaunchSession session, SavedPivotState state)
    {
        var path = PathFor(session);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(state, JsonOptions);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Copy(tmp, path, overwrite: true);
        File.Delete(tmp);
        AppLog.Info($"Tabla dinámica guardada local {session.Schema}.{session.View} sheets={state.PivotSheets.Count}");
    }

    public static void Delete(LaunchSession session)
    {
        var path = PathFor(session);
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
            AppLog.Info($"Tabla dinámica local eliminada {session.Schema}.{session.View}");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"No se pudo borrar la tabla dinámica local: {ex.Message}");
        }
    }

    public static string PathFor(LaunchSession session)
    {
        var user = Sanitize(string.IsNullOrWhiteSpace(session.User) ? "_shared" : session.User!);
        var file = $"{Sanitize(session.Schema)}.{Sanitize(session.View)}.json";
        return Path.Combine(RootDir, user, file);
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray();
        return new string(chars);
    }
}
