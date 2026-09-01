using System.Data;
using System.Globalization;
using SaraBI.Services;

namespace SaraBI.Models;

public enum DateGroupLevel
{
    None,
    Years,
    Quarters,
    Months,
    Days,
    Hours,
    Minutes,
    Seconds,
    Exact,
}

public sealed class PivotAxisField
{
    public PivotAxisField(string column, DateGroupLevel group = DateGroupLevel.None)
    {
        Column = column;
        Group = group;
    }

    public string Column { get; }
    public DateGroupLevel Group { get; }
    public string Label => DateGroup.FieldLabel(Column, Group);

    public bool Matches(string column, DateGroupLevel group) =>
        string.Equals(Column, column, StringComparison.OrdinalIgnoreCase) && Group == group;
}

public static class DateGroup
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-CO");

    public static string FieldLabel(string column, DateGroupLevel group) =>
        group is DateGroupLevel.None or DateGroupLevel.Exact
            ? column
            : $"{LevelLabel(group)} ({column})";

    public static string LevelLabel(DateGroupLevel group) => group switch
    {
        DateGroupLevel.Years => "Años",
        DateGroupLevel.Quarters => "Trimestres",
        DateGroupLevel.Months => "Meses",
        DateGroupLevel.Days => "Días",
        DateGroupLevel.Hours => "Horas",
        DateGroupLevel.Minutes => "Minutos",
        DateGroupLevel.Seconds => "Segundos",
        _ => "",
    };

    public static bool IsDateColumn(DataTable? table, string column)
    {
        if (string.IsNullOrEmpty(column))
        {
            return false;
        }

        if (table?.Columns.Contains(column) == true
            && table.Columns[column]!.DataType == typeof(DateTime))
        {
            return true;
        }

        return DataFileParser.IsFechaColumn(column);
    }

    public static bool HasTime(DataView? view, string column, int sample = 400)
    {
        if (view?.Table is null || !view.Table.Columns.Contains(column))
        {
            return false;
        }

        var n = 0;
        foreach (DataRowView row in view)
        {
            if (!TryGetDate(row, column, out var dt))
            {
                continue;
            }

            if (dt.TimeOfDay != TimeSpan.Zero)
            {
                return true;
            }

            if (++n >= sample)
            {
                break;
            }
        }

        return false;
    }

    public static bool SpansYears(DataView? view, string column, int sample = 800)
    {
        if (view?.Table is null || !view.Table.Columns.Contains(column))
        {
            return false;
        }

        int? min = null;
        int? max = null;
        var n = 0;
        foreach (DataRowView row in view)
        {
            if (!TryGetDate(row, column, out var dt))
            {
                continue;
            }

            min = min is null ? dt.Year : Math.Min(min.Value, dt.Year);
            max = max is null ? dt.Year : Math.Max(max.Value, dt.Year);
            if (min != max)
            {
                return true;
            }

            if (++n >= sample)
            {
                break;
            }
        }

        return false;
    }

    public static IReadOnlyList<DateGroupLevel> DefaultRowLevels(DataView? view, string column) =>
        HasTime(view, column)
            ? new[] { DateGroupLevel.Days, DateGroupLevel.Hours, DateGroupLevel.Minutes, DateGroupLevel.Exact }
            : new[] { DateGroupLevel.Years, DateGroupLevel.Quarters, DateGroupLevel.Months, DateGroupLevel.Days };

    public static DateGroupLevel DefaultColumnLevel(DataView? view, string column) =>
        HasTime(view, column) ? DateGroupLevel.Days : DateGroupLevel.Months;

    public static string Format(DateTime dt, DateGroupLevel group, bool includeYear = false) => group switch
    {
        DateGroupLevel.Years => dt.Year.ToString(CultureInfo.InvariantCulture),
        DateGroupLevel.Quarters => includeYear
            ? $"T{Quarter(dt)}-{dt.Year}"
            : $"T{Quarter(dt)}",
        DateGroupLevel.Months => includeYear
            ? $"{MonthShort(dt)}-{dt.Year}"
            : MonthShort(dt),
        DateGroupLevel.Days => includeYear
            ? $"{dt.Day}-{MonthShort(dt)}-{dt.Year}"
            : $"{dt.Day}-{MonthShort(dt)}",
        DateGroupLevel.Hours => FormatHour(dt),
        DateGroupLevel.Minutes => $":{dt:mm}",
        DateGroupLevel.Seconds => $":{dt:ss}",
        _ => DataFileParser.FormatFecha(dt),
    };

    public static string SortKey(DateTime dt, DateGroupLevel group) => group switch
    {
        DateGroupLevel.Years => dt.ToString("yyyy", CultureInfo.InvariantCulture),
        DateGroupLevel.Quarters => $"{dt:yyyy}-Q{Quarter(dt)}",
        DateGroupLevel.Months => dt.ToString("yyyy-MM", CultureInfo.InvariantCulture),
        DateGroupLevel.Days => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateGroupLevel.Hours => dt.ToString("yyyy-MM-dd-HH", CultureInfo.InvariantCulture),
        DateGroupLevel.Minutes => dt.ToString("yyyy-MM-dd-HH-mm", CultureInfo.InvariantCulture),
        DateGroupLevel.Seconds => dt.ToString("yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture),
        _ => dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
    };

    public static bool TryGetDate(DataRowView row, string column, out DateTime dt)
    {
        dt = default;
        if (string.IsNullOrEmpty(column) || !row.Row.Table.Columns.Contains(column))
        {
            return false;
        }

        var raw = row[column];
        if (raw is DateTime typed)
        {
            dt = typed;
            return true;
        }

        if (raw is DBNull or null)
        {
            return false;
        }

        return DataFileParser.TryParseFecha(Convert.ToString(raw, CultureInfo.InvariantCulture), out dt);
    }

    private static int Quarter(DateTime dt) => ((dt.Month - 1) / 3) + 1;

    private static string MonthShort(DateTime dt) =>
        dt.ToString("MMM", Es).TrimEnd('.');

    private static string FormatHour(DateTime dt)
    {
        var hour = dt.Hour % 12;
        if (hour == 0)
        {
            hour = 12;
        }

        var suffix = dt.Hour < 12 ? "a. m." : "p. m.";
        return $"{hour} {suffix}";
    }
}
