using System.Data;

namespace SaraBI.Models;

public sealed class WorkbookSheet
{
    public string Name { get; set; } = "Hoja1";
    public bool IsPivot { get; set; }
    public bool IsExtraView { get; set; }
    public bool IsActive { get; set; }
    public bool CanClose => IsPivot || IsExtraView;
    public string? Schema { get; set; }
    public string? ViewName { get; set; }
    public string? LastJobId { get; set; }
    public DataTable? SourceTable { get; set; }
    public List<FabricColumn> Columns { get; } = new();
    public DateRangeFilter? DateFilter { get; set; }
    public Dictionary<string, ColumnAutoFilter> Filters { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ColumnOverride> Overrides { get; } = new();
    public WorkbookSheet? PivotSource { get; set; }
    public PivotConfig Config { get; } = new();
    public DataTable? Result { get; set; }
    public PivotSnapshot? Snapshot { get; set; }
    public Dictionary<string, string> Captions { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ColumnDataKind> ColumnKinds { get; } = new(StringComparer.Ordinal);
}
