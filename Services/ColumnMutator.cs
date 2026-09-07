using System.Data;
using System.Globalization;
using SaraBI.Models;

namespace SaraBI.Services;

public readonly record struct ColumnConversionResult(int Converted, int Failed, int Total);

public static class ColumnMutator
{
    public const string OriginalNameKey = "OriginalName";
    public const string KindKey = "DataKind";

    public static void StampOriginalNames(DataTable table)
    {
        foreach (DataColumn col in table.Columns)
        {
            if (!col.ExtendedProperties.Contains(OriginalNameKey))
            {
                col.ExtendedProperties[OriginalNameKey] = col.ColumnName;
            }
        }
    }

    public static string OriginalNameOf(DataColumn col) =>
        col.ExtendedProperties[OriginalNameKey] as string ?? col.ColumnName;

    public static string SanitizeName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return "Columna";
        }

        var chars = trimmed.Select(c => c is '.' or '[' or ']' or '/' or '\\' ? '_' : c).ToArray();
        return new string(chars);
    }

    public static string UniqueName(DataTable table, string name, string? skip = null)
    {
        var candidate = name;
        var i = 2;
        while (table.Columns.Contains(candidate)
               && !string.Equals(candidate, skip, StringComparison.OrdinalIgnoreCase))
        {
            candidate = $"{name}_{i++}";
        }

        return candidate;
    }

    public static string Rename(DataTable table, string oldName, string newName)
    {
        if (!table.Columns.Contains(oldName))
        {
            throw new InvalidOperationException($"No existe la columna '{oldName}'.");
        }

        var col = table.Columns[oldName]!;
        var sanitized = SanitizeName(newName);
        if (string.Equals(col.ColumnName, sanitized, StringComparison.Ordinal))
        {
            col.Caption = sanitized;
            return col.ColumnName;
        }

        var unique = UniqueName(table, sanitized, col.ColumnName);
        col.ColumnName = unique;
        col.Caption = unique;
        return unique;
    }

    public static ColumnConversionResult ChangeType(DataTable table, string columnName, ColumnDataKind kind)
    {
        if (!table.Columns.Contains(columnName))
        {
            throw new InvalidOperationException($"No existe la columna '{columnName}'.");
        }

        var old = table.Columns[columnName]!;
        var target = ColumnDataKinds.ClrType(kind);
        if (old.DataType == target)
        {
            StampKind(old, kind);
            return new ColumnConversionResult(table.Rows.Count, 0, table.Rows.Count);
        }

        using var suspend = SuspendIndexes(table);
        var temp = "_tmp_" + Guid.NewGuid().ToString("N")[..8];
        var neu = new DataColumn(temp, target)
        {
            AllowDBNull = true,
            Caption = string.IsNullOrEmpty(old.Caption) ? old.ColumnName : old.Caption,
        };
        var original = OriginalNameOf(old);
        var ordinal = old.Ordinal;
        table.Columns.Add(neu);

        try
        {
            var converted = 0;
            var failed = 0;
            foreach (DataRow row in table.Rows)
            {
                if (row.RowState == DataRowState.Deleted)
                {
                    continue;
                }

                var raw = row[old];
                if (raw is DBNull or null)
                {
                    row[neu] = DBNull.Value;
                    converted++;
                    continue;
                }

                if (TryConvert(raw, kind, out var value))
                {
                    row[neu] = value is null ? DBNull.Value : value;
                    converted++;
                }
                else
                {
                    row[neu] = DBNull.Value;
                    failed++;
                }
            }

            table.Columns.Remove(old);
            neu.ColumnName = columnName;
            neu.Caption = columnName;
            neu.ExtendedProperties[OriginalNameKey] = original;
            StampKind(neu, kind);
            neu.SetOrdinal(ordinal);
            return new ColumnConversionResult(converted, failed, converted + failed);
        }
        catch
        {
            if (table.Columns.Contains(temp))
            {
                table.Columns.Remove(neu);
            }

            throw;
        }
    }

    /// <summary>
    /// Quita Sort del DataView mientras se mutan celdas. Si el índice sigue activo,
    /// ADO.NET lanza NullReferenceException en Index.CompareRecords.
    /// </summary>
    private static ViewIndexSuspend SuspendIndexes(DataTable table) => new(table);

    private sealed class ViewIndexSuspend : IDisposable
    {
        private readonly DataView _view;
        private readonly string _filter;
        private bool _disposed;

        public ViewIndexSuspend(DataTable table)
        {
            _view = table.DefaultView;
            _filter = _view.RowFilter ?? string.Empty;
            _view.Sort = string.Empty;
            _view.RowFilter = string.Empty;
            table.BeginLoadData();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _view.Table?.EndLoadData();
            }
            catch
            {
                // Constraints al reactivar índices: la conversión ya aplicó.
            }

            try
            {
                _view.RowFilter = _filter;
            }
            catch
            {
                _view.RowFilter = string.Empty;
            }
        }
    }

    public static void StampKind(DataColumn col, ColumnDataKind kind) =>
        col.ExtendedProperties[KindKey] = kind.ToString();

    public static bool TryGetKind(DataColumn col, out ColumnDataKind kind)
    {
        kind = ColumnDataKind.Texto;
        return col.ExtendedProperties[KindKey] is string raw
               && ColumnDataKinds.TryParse(raw, out kind);
    }

    public static bool TryConvert(object raw, ColumnDataKind kind, out object? value)
    {
        value = null;
        switch (kind)
        {
            case ColumnDataKind.Texto:
                value = raw is DateTime dt
                    ? DataFileParser.FormatFecha(dt)
                    : Convert.ToString(raw, CultureInfo.CurrentCulture) ?? "";
                return true;

            case ColumnDataKind.Numero:
            case ColumnDataKind.Porcentaje:
                if (TryNumber(raw, out var n))
                {
                    value = n;
                    return true;
                }

                return false;

            case ColumnDataKind.Moneda:
                if (raw is decimal money)
                {
                    value = money;
                    return true;
                }

                if (TryNumber(raw, out var amount)
                    && amount >= (double)decimal.MinValue
                    && amount <= (double)decimal.MaxValue)
                {
                    value = Math.Round((decimal)amount, 2, MidpointRounding.AwayFromZero);
                    return true;
                }

                return false;

            case ColumnDataKind.Entero:
                if (TryNumber(raw, out var num))
                {
                    value = Convert.ToInt64(Math.Round(num, MidpointRounding.AwayFromZero));
                    return true;
                }

                return false;

            case ColumnDataKind.Fecha:
                if (raw is DateTime date)
                {
                    value = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
                    return true;
                }

                if (DataFileParser.TryParseFecha(Convert.ToString(raw, CultureInfo.InvariantCulture), out var parsed))
                {
                    value = parsed;
                    return true;
                }

                return false;

            case ColumnDataKind.Logico:
                if (raw is bool b)
                {
                    value = b;
                    return true;
                }

                if (TryNumber(raw, out var flag))
                {
                    value = Math.Abs(flag) > double.Epsilon;
                    return true;
                }

                var s = (Convert.ToString(raw, CultureInfo.CurrentCulture) ?? "").Trim();
                if (s.Length == 0)
                {
                    return false;
                }

                if (s.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("sí", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("si", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("verdadero", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("v", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("x", StringComparison.OrdinalIgnoreCase)
                    || s == "1")
                {
                    value = true;
                    return true;
                }

                if (s.Equals("false", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("no", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("falso", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("f", StringComparison.OrdinalIgnoreCase)
                    || s == "0")
                {
                    value = false;
                    return true;
                }

                return false;

            default:
                return false;
        }
    }

    public static bool TryNumber(object raw, out double value)
    {
        value = 0;
        switch (raw)
        {
            case double d:
                value = d;
                return !double.IsNaN(d) && !double.IsInfinity(d);
            case float f:
                value = f;
                return true;
            case decimal m:
                value = (double)m;
                return true;
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                value = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                return true;
            case bool b:
                value = b ? 1 : 0;
                return true;
            case DateTime:
                return false;
        }

        var s = (Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "").Trim();
        if (s.Length == 0)
        {
            return false;
        }

        s = StripMoneyNoise(s);
        if (s.Length == 0)
        {
            return false;
        }

        if (double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value)
            || double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.GetCultureInfo("es-CO"), out value)
            || double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value))
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        return false;
    }

    private static string StripMoneyNoise(string s)
    {
        s = s.Replace("\u00A0", "").Replace(" ", "").Replace("$", "").Replace("€", "");
        if (s.StartsWith("COP", StringComparison.OrdinalIgnoreCase))
        {
            s = s[3..];
        }

        if (s.EndsWith("COP", StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^3];
        }

        return s.Trim();
    }
}
