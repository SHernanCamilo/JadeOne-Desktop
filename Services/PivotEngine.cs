using System.Data;
using System.Globalization;
using SaraBI.Models;

namespace SaraBI.Services;

public static class PivotEngine
{
    public const int MaxCrossColumns = 50;
    public const string OutlineColumn = "Etiquetas de fila";
    public const string PathColumn = "_pivotPath";
    public const string KidsColumn = "_pivotHasChildren";

    public static PivotBuild Build(DataView source, PivotConfig config)
    {
        var empty = new DataTable("Pivot");
        if (source is null || !config.CanBuild)
        {
            return new PivotBuild { Table = empty };
        }

        var snapshot = BuildSnapshot(source, config);
        return new PivotBuild
        {
            Table = Flatten(snapshot, config.ExpandedPaths),
            Snapshot = snapshot,
        };
    }

    public static DataTable Flatten(PivotSnapshot snapshot, ISet<string> expanded)
    {
        var table = new DataTable("Pivot");
        var hasCross = snapshot.Cross.Count > 0;
        var outline = snapshot.Outline;

        if (outline)
        {
            table.Columns.Add(OutlineColumn, typeof(string));
            table.Columns.Add(PathColumn, typeof(string));
            table.Columns.Add(KidsColumn, typeof(bool));
        }
        else
        {
            foreach (var field in snapshot.RowFields)
            {
                table.Columns.Add(UniqueName(table, field.Label), typeof(string));
            }
        }

        var crossNames = new Dictionary<string, string>(StringComparer.Ordinal);
        string? totalName = null;
        string? valueName = null;
        if (hasCross)
        {
            foreach (var c in snapshot.Cross)
            {
                var name = UniqueName(table, c);
                table.Columns.Add(name, typeof(double));
                crossNames[c] = name;
            }

            totalName = UniqueName(table, "Total");
            table.Columns.Add(totalName, typeof(double));
        }
        else
        {
            valueName = UniqueName(table, snapshot.Value.Column.Length == 0 ? "Conteo" : snapshot.Value.Label);
            table.Columns.Add(valueName, typeof(double));
        }

        if (outline)
        {
            EmitOutline(table, snapshot, snapshot.Leaves, 0, "", expanded, crossNames, totalName, valueName);
        }
        else
        {
            EmitFlat(table, snapshot, crossNames, totalName, valueName);
        }

        return table;
    }

    private static PivotSnapshot BuildSnapshot(DataView source, PivotConfig config)
    {
        var hasCross = config.Columns.Count > 0;
        var value = config.Values.Count > 0
            ? config.Values[0]
            : new PivotValueField { Column = config.Rows.FirstOrDefault()?.Column ?? "", Operation = PivotOp.Count };

        var yearFlags = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool IncludeYear(string column)
        {
            if (yearFlags.TryGetValue(column, out var known))
            {
                return known;
            }

            var spans = DateGroup.SpansYears(source, column);
            yearFlags[column] = spans;
            return spans;
        }

        var groups = new Dictionary<string, PivotLeaf>(StringComparer.Ordinal);
        var colValues = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (DataRowView row in source)
        {
            if (!PassesFilters(row, config))
            {
                continue;
            }

            var labels = new string[config.Rows.Count];
            var sorts = new string[config.Rows.Count];
            for (var i = 0; i < config.Rows.Count; i++)
            {
                var axis = AxisValue(row, config.Rows[i], IncludeYear);
                sorts[i] = axis.Sort;
                labels[i] = axis.Label;
            }

            var rowKey = config.Rows.Count == 0 ? "Total" : string.Join("\u001f", sorts);
            var colAxis = hasCross ? AxisValue(row, config.Columns[0], IncludeYear) : default;
            var colKey = hasCross ? colAxis.Label : "";
            if (hasCross)
            {
                colValues[colKey] = colAxis.Sort;
            }

            if (!groups.TryGetValue(rowKey, out var leaf))
            {
                leaf = new PivotLeaf
                {
                    Labels = config.Rows.Count == 0 ? new[] { "Total" } : labels,
                    SortKeys = config.Rows.Count == 0 ? new[] { "Total" } : sorts,
                    Cols = new Dictionary<string, PivotAgg>(StringComparer.Ordinal),
                };
                groups[rowKey] = leaf;
            }

            if (!leaf.Cols.TryGetValue(colKey, out var agg))
            {
                agg = new PivotAgg();
                leaf.Cols[colKey] = agg;
            }

            Add(agg, row, value);
        }

        var cross = colValues
            .OrderBy(kv => kv.Value, StringComparer.Ordinal)
            .Select(kv => kv.Key)
            .Take(MaxCrossColumns)
            .ToList();

        return new PivotSnapshot
        {
            Leaves = groups.Values.ToList(),
            Cross = cross,
            CrossSort = colValues,
            Value = value,
            RowFields = config.Rows.ToList(),
            Outline = config.Rows.Count > 1,
        };
    }

    private static void EmitFlat(
        DataTable table,
        PivotSnapshot snapshot,
        Dictionary<string, string> crossNames,
        string? totalName,
        string? valueName)
    {
        var ordered = snapshot.Leaves.OrderBy(l => string.Join("\u001f", l.SortKeys), StringComparer.Ordinal);
        foreach (var leaf in ordered)
        {
            var dataRow = table.NewRow();
            for (var i = 0; i < snapshot.RowFields.Count && i < table.Columns.Count; i++)
            {
                if (table.Columns[i].ColumnName.StartsWith("_pivot", StringComparison.Ordinal))
                {
                    continue;
                }

                dataRow[i] = i < leaf.Labels.Length ? leaf.Labels[i] : "";
            }

            WriteValues(dataRow, MergeCols(new[] { leaf }), snapshot, crossNames, totalName, valueName);
            table.Rows.Add(dataRow);
        }
    }

    private static void EmitOutline(
        DataTable table,
        PivotSnapshot snapshot,
        IEnumerable<PivotLeaf> leaves,
        int level,
        string parentPath,
        ISet<string> expanded,
        Dictionary<string, string> crossNames,
        string? totalName,
        string? valueName)
    {
        var fieldCount = snapshot.RowFields.Count;
        var groups = leaves
            .GroupBy(l => (l.SortKeys.ElementAtOrDefault(level) ?? "") + "\0" + (l.Labels.ElementAtOrDefault(level) ?? ""))
            .OrderBy(g => g.First().SortKeys.ElementAtOrDefault(level) ?? "", StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var first = group.First();
            var sort = first.SortKeys.ElementAtOrDefault(level) ?? "";
            var label = first.Labels.ElementAtOrDefault(level) ?? "";
            var path = parentPath.Length == 0 ? sort : parentPath + "\u001f" + sort;
            var hasKids = level < fieldCount - 1;
            var isOpen = hasKids && expanded.Contains(path);
            var indent = new string('\u00A0', level * 3);
            var glyph = !hasKids ? "   " : isOpen ? "▼  " : "▶  ";

            var dataRow = table.NewRow();
            dataRow[OutlineColumn] = indent + glyph + label;
            dataRow[PathColumn] = path;
            dataRow[KidsColumn] = hasKids;
            WriteValues(dataRow, MergeCols(group), snapshot, crossNames, totalName, valueName);
            table.Rows.Add(dataRow);

            if (isOpen)
            {
                EmitOutline(table, snapshot, group, level + 1, path, expanded, crossNames, totalName, valueName);
            }
        }
    }

    private static Dictionary<string, PivotAgg> MergeCols(IEnumerable<PivotLeaf> leaves)
    {
        var merged = new Dictionary<string, PivotAgg>(StringComparer.Ordinal);
        foreach (var leaf in leaves)
        {
            foreach (var kv in leaf.Cols)
            {
                if (!merged.TryGetValue(kv.Key, out var agg))
                {
                    agg = new PivotAgg();
                    merged[kv.Key] = agg;
                }

                Merge(agg, kv.Value);
            }
        }

        return merged;
    }

    private static void Merge(PivotAgg dest, PivotAgg src)
    {
        dest.Sum += src.Sum;
        dest.Count += src.Count;
        dest.NumCount += src.NumCount;
        if (src.Min < dest.Min)
        {
            dest.Min = src.Min;
        }

        if (src.Max > dest.Max)
        {
            dest.Max = src.Max;
        }

        if (src.Distinct is null)
        {
            return;
        }

        dest.Distinct ??= new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in src.Distinct)
        {
            dest.Distinct.Add(item);
        }
    }

    private static void WriteValues(
        DataRow dataRow,
        Dictionary<string, PivotAgg> cols,
        PivotSnapshot snapshot,
        Dictionary<string, string> crossNames,
        string? totalName,
        string? valueName)
    {
        if (snapshot.Cross.Count > 0)
        {
            double total = 0;
            foreach (var c in snapshot.Cross)
            {
                var n = Finish(cols.TryGetValue(c, out var a) ? a : null, snapshot.Value.Operation);
                dataRow[crossNames[c]] = n;
                total += n;
            }

            dataRow[totalName!] = total;
            return;
        }

        var value = Finish(cols.TryGetValue("", out var only) ? only : null, snapshot.Value.Operation);
        dataRow[valueName!] = value;
    }

    private static void Add(PivotAgg agg, DataRowView row, PivotValueField value)
    {
        agg.Count++;
        var raw = CellText(row, value.Column);
        if (value.Operation == PivotOp.Distinct)
        {
            agg.Distinct ??= new HashSet<string>(StringComparer.Ordinal);
            agg.Distinct.Add(raw);
            return;
        }

        if (value.Operation == PivotOp.Count)
        {
            return;
        }

        if (!TryNumber(row, value.Column, out var n))
        {
            return;
        }

        agg.Sum += n;
        agg.NumCount++;
        if (n < agg.Min)
        {
            agg.Min = n;
        }

        if (n > agg.Max)
        {
            agg.Max = n;
        }
    }

    private static double Finish(PivotAgg? agg, PivotOp op)
    {
        if (agg is null)
        {
            return 0;
        }

        return op switch
        {
            PivotOp.Avg => agg.NumCount == 0 ? 0 : agg.Sum / agg.NumCount,
            PivotOp.Count => agg.Count,
            PivotOp.Min => agg.NumCount == 0 ? 0 : agg.Min,
            PivotOp.Max => agg.NumCount == 0 ? 0 : agg.Max,
            PivotOp.Distinct => agg.Distinct?.Count ?? 0,
            _ => agg.Sum,
        };
    }

    public static string FormatCell(DataRowView row, string column) => CellText(row, column);

    public static bool IsHiddenColumn(string name) =>
        name.StartsWith("_pivot", StringComparison.Ordinal);

    private static bool PassesFilters(DataRowView row, PivotConfig config)
    {
        foreach (var field in config.Filters)
        {
            if (!config.FilterSelections.TryGetValue(field, out var selected)
                || string.IsNullOrEmpty(selected)
                || selected == "(Todos)")
            {
                continue;
            }

            if (!string.Equals(CellText(row, field), selected, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private readonly record struct AxisText(string Sort, string Label);

    private static AxisText AxisValue(DataRowView row, PivotAxisField field, Func<string, bool> includeYear)
    {
        if (DateGroup.TryGetDate(row, field.Column, out var dt))
        {
            var group = field.Group == DateGroupLevel.None ? DateGroupLevel.Exact : field.Group;
            var year = includeYear(field.Column) && group is DateGroupLevel.Days
                or DateGroupLevel.Months
                or DateGroupLevel.Quarters;
            return new AxisText(DateGroup.SortKey(dt, group), DateGroup.Format(dt, group, year));
        }

        if (field.Group != DateGroupLevel.None)
        {
            return new AxisText("~", "(vacío)");
        }

        var text = CellText(row, field.Column);
        return new AxisText(text, text);
    }

    private static string CellText(DataRowView row, string column)
    {
        if (string.IsNullOrEmpty(column) || !row.Row.Table.Columns.Contains(column))
        {
            return "(vacío)";
        }

        var raw = row[column];
        if (raw is DBNull or null)
        {
            return "(vacío)";
        }

        if (raw is DateTime dt)
        {
            return DataFileParser.FormatFecha(dt);
        }

        var s = Convert.ToString(raw, CultureInfo.CurrentCulture)?.Trim();
        return string.IsNullOrEmpty(s) ? "(vacío)" : s;
    }

    private static bool TryNumber(DataRowView row, string column, out double n)
    {
        n = 0;
        if (string.IsNullOrEmpty(column) || !row.Row.Table.Columns.Contains(column))
        {
            return false;
        }

        var raw = row[column];
        if (raw is DBNull or null)
        {
            return false;
        }

        if (raw is IConvertible and not string and not DateTime)
        {
            try
            {
                n = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        return double.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out n)
               || double.TryParse(Convert.ToString(raw, CultureInfo.CurrentCulture), NumberStyles.Any, CultureInfo.CurrentCulture, out n);
    }

    private static string UniqueName(DataTable table, string name)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "Columna" : name.Replace(".", "_");
        if (baseName.Length > 64)
        {
            baseName = baseName[..64];
        }

        var candidate = baseName;
        var i = 2;
        while (table.Columns.Contains(candidate))
        {
            candidate = $"{baseName}_{i++}";
        }

        return candidate;
    }
}
