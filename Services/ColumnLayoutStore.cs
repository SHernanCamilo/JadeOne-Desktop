using System.IO;
using System.Text.Json;
using SaraBI.Models;

namespace SaraBI.Services;

public static class ColumnLayoutStore
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
        "columns");

    public static ColumnLayoutState? Load(LaunchSession session)
    {
        var path = PathFor(session);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<ColumnLayoutState>(json, JsonOptions);
            return state?.Columns is { Count: > 0 } ? state : null;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"No se pudo leer el diseño de columnas: {ex.Message}");
            return null;
        }
    }

    public static void Save(LaunchSession session, IReadOnlyList<ColumnOverride> columns)
    {
        var meaningful = columns
            .Where(c => !string.IsNullOrWhiteSpace(c.OriginalName)
                        && (!string.Equals(c.OriginalName, c.DisplayName, StringComparison.Ordinal)
                            || !string.IsNullOrWhiteSpace(c.Kind)))
            .ToList();
        if (meaningful.Count == 0)
        {
            Delete(session);
            return;
        }

        var path = PathFor(session);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var state = new ColumnLayoutState
        {
            Schema = session.Schema,
            View = session.View,
            SavedAt = DateTime.Now,
            Columns = meaningful,
        };
        var json = JsonSerializer.Serialize(state, JsonOptions);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Copy(tmp, path, overwrite: true);
        File.Delete(tmp);
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
        }
        catch (Exception ex)
        {
            AppLog.Warn($"No se pudo borrar el diseño de columnas: {ex.Message}");
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
