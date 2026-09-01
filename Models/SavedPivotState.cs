namespace SaraBI.Models;

public sealed class SavedPivotState
{
    public string Schema { get; set; } = "";
    public string View { get; set; } = "";
    public string ViewLabel { get; set; } = "";
    public string? User { get; set; }
    public DateTime SavedAt { get; set; }
    public string? ActiveSheetName { get; set; }
    public List<SavedPivotSheet> PivotSheets { get; set; } = new();
}

public sealed class SavedPivotSheet
{
    public string Name { get; set; } = "";
    public SavedPivotConfig Config { get; set; } = new();
}

public sealed class SavedPivotConfig
{
    public List<string> Rows { get; set; } = new();
    public List<string> Columns { get; set; } = new();
    public List<SavedPivotAxis> RowFields { get; set; } = new();
    public List<SavedPivotAxis> ColumnFields { get; set; } = new();
    public List<SavedPivotValue> Values { get; set; } = new();
    public List<string> Filters { get; set; } = new();
    public Dictionary<string, string> FilterSelections { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SavedPivotAxis
{
    public string Column { get; set; } = "";
    public string Group { get; set; } = nameof(DateGroupLevel.None);
}

public sealed class SavedPivotValue
{
    public string Column { get; set; } = "";
    public string Operation { get; set; } = nameof(PivotOp.Sum);
}
