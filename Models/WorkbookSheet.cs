using System.Data;

namespace SaraBI.Models;

public sealed class WorkbookSheet
{
    public string Name { get; set; } = "Hoja1";
    public bool IsPivot { get; set; }
    public bool IsActive { get; set; }
    public PivotConfig Config { get; } = new();
    public DataTable? Result { get; set; }
    public PivotSnapshot? Snapshot { get; set; }
    public Dictionary<string, string> Captions { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ColumnDataKind> ColumnKinds { get; } = new(StringComparer.Ordinal);
}
