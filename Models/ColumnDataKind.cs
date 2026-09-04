using System.Data;

namespace SaraBI.Models;

public enum ColumnDataKind
{
    Texto,
    Numero,
    Moneda,
    Entero,
    Fecha,
    Logico,
}

public sealed class ColumnOverride
{
    public string OriginalName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Kind { get; set; }
}

public sealed class ColumnLayoutState
{
    public string Schema { get; set; } = "";
    public string View { get; set; } = "";
    public DateTime SavedAt { get; set; }
    public List<ColumnOverride> Columns { get; set; } = new();
}

public static class ColumnDataKinds
{
    public static string Label(ColumnDataKind kind) => kind switch
    {
        ColumnDataKind.Numero => "Número",
        ColumnDataKind.Moneda => "Moneda",
        ColumnDataKind.Entero => "Entero",
        ColumnDataKind.Fecha => "Fecha",
        ColumnDataKind.Logico => "Verdadero/Falso",
        _ => "Texto",
    };

    public static Type ClrType(ColumnDataKind kind) => kind switch
    {
        ColumnDataKind.Numero => typeof(double),
        ColumnDataKind.Moneda => typeof(decimal),
        ColumnDataKind.Entero => typeof(long),
        ColumnDataKind.Fecha => typeof(DateTime),
        ColumnDataKind.Logico => typeof(bool),
        _ => typeof(string),
    };

    public static ColumnDataKind FromType(Type type)
    {
        if (type == typeof(DateTime))
        {
            return ColumnDataKind.Fecha;
        }

        if (type == typeof(bool))
        {
            return ColumnDataKind.Logico;
        }

        if (type == typeof(decimal))
        {
            return ColumnDataKind.Moneda;
        }

        if (type == typeof(double) || type == typeof(float))
        {
            return ColumnDataKind.Numero;
        }

        if (type == typeof(byte) || type == typeof(short) || type == typeof(int) || type == typeof(long)
            || type == typeof(ushort) || type == typeof(uint) || type == typeof(ulong))
        {
            return ColumnDataKind.Entero;
        }

        return ColumnDataKind.Texto;
    }

    public static bool TryParse(string? value, out ColumnDataKind kind) =>
        Enum.TryParse(value, ignoreCase: true, out kind);
}
