using System.Data;

namespace SaraBI.Models;

public enum PivotOp
{
    Sum,
    Avg,
    Count,
    Min,
    Max,
    Distinct,
}

public sealed class PivotValueField
{
    public string Column { get; set; } = "";
    public PivotOp Operation { get; set; } = PivotOp.Sum;

    public string Label => $"{OpLabel(Operation)} {Column}";

    public static string OpLabel(PivotOp op) => op switch
    {
        PivotOp.Avg => "PROMEDIO",
        PivotOp.Count => "CONTAR",
        PivotOp.Min => "MIN",
        PivotOp.Max => "MAX",
        PivotOp.Distinct => "DISTINTOS",
        _ => "SUMA",
    };
}

public sealed class PivotConfig
{
    public List<PivotAxisField> Rows { get; } = new();
    public List<PivotAxisField> Columns { get; } = new();
    public List<PivotValueField> Values { get; } = new();
    public List<string> Filters { get; } = new();
    public Dictionary<string, string> FilterSelections { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ExpandedPaths { get; } = new(StringComparer.Ordinal);

    public bool CanBuild => Rows.Count > 0 || Columns.Count > 0 || Values.Count > 0;

    public void Clear()
    {
        Rows.Clear();
        Columns.Clear();
        Values.Clear();
        Filters.Clear();
        FilterSelections.Clear();
        ExpandedPaths.Clear();
    }

    public void ToggleExpand(string path)
    {
        if (!ExpandedPaths.Remove(path))
        {
            ExpandedPaths.Add(path);
        }
    }

    public void ExpandBareDateFields(DataTable table, DataView view)
    {
        ExpandBare(Rows, table, view, rows: true);
        ExpandBare(Columns, table, view, rows: false);
    }

    private static void ExpandBare(
        List<PivotAxisField> fields,
        DataTable table,
        DataView view,
        bool rows)
    {
        if (fields.Count == 0)
        {
            return;
        }

        var columns = fields.Select(f => f.Column).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var next = new List<PivotAxisField>();
        foreach (var col in columns)
        {
            var parts = fields
                .Where(f => string.Equals(f.Column, col, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (parts.All(p => p.Group == DateGroupLevel.None) && DateGroup.IsDateColumn(table, col))
            {
                if (rows)
                {
                    next.AddRange(DateGroup.DefaultRowLevels(view, col)
                        .Select(g => new PivotAxisField(col, g)));
                }
                else
                {
                    next.Add(new PivotAxisField(col, DateGroup.DefaultColumnLevel(view, col)));
                }
            }
            else
            {
                next.AddRange(parts);
            }
        }

        fields.Clear();
        fields.AddRange(next);
    }

    public SavedPivotConfig ToSaved() => new()
    {
        Rows = Rows.Select(r => r.Column).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        Columns = Columns.Select(c => c.Column).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        RowFields = Rows.Select(ToAxis).ToList(),
        ColumnFields = Columns.Select(ToAxis).ToList(),
        Values = Values.Select(v => new SavedPivotValue
        {
            Column = v.Column,
            Operation = v.Operation.ToString(),
        }).ToList(),
        Filters = Filters.ToList(),
        FilterSelections = new Dictionary<string, string>(FilterSelections, StringComparer.OrdinalIgnoreCase),
    };

    public void ApplySaved(SavedPivotConfig saved, Func<string, string?> mapColumn)
    {
        Clear();
        ApplyAxis(saved.RowFields, saved.Rows, Rows, mapColumn);
        ApplyAxis(saved.ColumnFields, saved.Columns, Columns, mapColumn);

        foreach (var value in saved.Values)
        {
            if (mapColumn(value.Column) is not { } name)
            {
                continue;
            }

            var op = Enum.TryParse<PivotOp>(value.Operation, ignoreCase: true, out var parsed)
                ? parsed
                : PivotOp.Sum;
            Values.Add(new PivotValueField { Column = name, Operation = op });
        }

        foreach (var filter in saved.Filters)
        {
            if (mapColumn(filter) is not { } name)
            {
                continue;
            }

            Filters.Add(name);
            if (saved.FilterSelections.TryGetValue(filter, out var selected)
                || saved.FilterSelections.TryGetValue(name, out selected))
            {
                FilterSelections[name] = string.IsNullOrWhiteSpace(selected) ? "(Todos)" : selected;
            }
            else
            {
                FilterSelections[name] = "(Todos)";
            }
        }
    }

    private static SavedPivotAxis ToAxis(PivotAxisField field) => new()
    {
        Column = field.Column,
        Group = field.Group.ToString(),
    };

    private static void ApplyAxis(
        List<SavedPivotAxis>? structured,
        List<string> legacy,
        List<PivotAxisField> target,
        Func<string, string?> mapColumn)
    {
        if (structured is { Count: > 0 })
        {
            foreach (var item in structured)
            {
                if (mapColumn(item.Column) is not { } name)
                {
                    continue;
                }

                var group = Enum.TryParse<DateGroupLevel>(item.Group, ignoreCase: true, out var parsed)
                    ? parsed
                    : DateGroupLevel.None;
                target.Add(new PivotAxisField(name, group));
            }

            return;
        }

        foreach (var column in legacy)
        {
            if (mapColumn(column) is { } name)
            {
                target.Add(new PivotAxisField(name));
            }
        }
    }
}
